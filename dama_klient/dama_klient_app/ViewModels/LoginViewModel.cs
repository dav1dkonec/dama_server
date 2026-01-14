using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using dama_klient_app.Models;
using dama_klient_app.Services;
using System.Net.Sockets;
using Avalonia.Threading;
using Avalonia.Media;

namespace dama_klient_app.ViewModels;

/// <summary>
/// VM pro přihlášení: drží přezdívku, běh/error a volá Login/Connect na klientovi.
/// </summary>
public class LoginViewModel : ViewModelBase
{
    private readonly Func<string, Task>? _onLoginAsync;
    private readonly Func<Task<(string Host, int Port)?>> _discoverAsync;
    private string _nickname = string.Empty;
    private string? _errorMessage;
    private bool _isBusy;
    private const int MaxNicknameLength = 64;
    private string _host = "127.0.0.1";
    private int _port = 5000;
    private string _statusMessage = "Zkouším autodetekci serveru...";
    private bool _isDiscovering;
    private bool _discoveryAttempted;
    private IBrush _statusBrush = Brushes.Gray;
    private string _serverStatusText = "Server neznámý";
    private IBrush _serverStatusBrush = Brushes.Gray;
    private CancellationTokenSource? _probeCts;

    public LoginViewModel(IGameClient gameClient, Action<string> onLoginCompleted, string? loginNotice)
    {
        GameClient = gameClient;
        _onLoginAsync = nickname =>
        {
            onLoginCompleted(nickname);
            return Task.CompletedTask;
        };
        _discoverAsync = () => DiscoveryClient.DiscoverAsync();
        ConnectCommand = new AsyncCommand(ConnectAsync, () => !IsBusy && !string.IsNullOrWhiteSpace(Nickname));
        UpdateServerStatus(GameClient.ServerStatus);
        GameClient.ServerStatusChanged += OnServerStatusChanged;
        if (!string.IsNullOrWhiteSpace(loginNotice))
        {
            ErrorMessage = loginNotice;
        }
        _ = AutoDiscoverAsync();
        StartProbeLoop();
    }

    public IGameClient GameClient { get; }

    public string Nickname
    {
        get => _nickname;
        set
        {
            if (SetField(ref _nickname, value))
            {
                (ConnectCommand as AsyncCommand)?.RaiseCanExecuteChanged();
            }
        }
    }

    public string? ErrorMessage
    {
        get => _errorMessage;
        private set
        {
            if (SetField(ref _errorMessage, value))
            {
                OnPropertyChanged(nameof(HasError));
            }
        }
    }

    public string Host
    {
        get => _host;
        set => SetField(ref _host, value);
    }

    public int Port
    {
        get => _port;
        set => SetField(ref _port, value);
    }

    public bool HasError => !string.IsNullOrWhiteSpace(ErrorMessage);

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetField(ref _isBusy, value))
            {
                (ConnectCommand as AsyncCommand)?.RaiseCanExecuteChanged();
                OnPropertyChanged(nameof(CanEditEndpoint));
            }
        }
    }

    public bool IsDiscovering
    {
        get => _isDiscovering;
        private set
        {
            if (SetField(ref _isDiscovering, value))
            {
                OnPropertyChanged(nameof(CanEditEndpoint));
            }
        }
    }

    public bool CanEditEndpoint => !IsBusy && !IsDiscovering;

    public ICommand ConnectCommand { get; }

    public string StatusMessage
    {
        get => _statusMessage;
        set => SetField(ref _statusMessage, value);
    }

    public IBrush StatusBrush
    {
        get => _statusBrush;
        private set => SetField(ref _statusBrush, value);
    }

    public string ServerStatusText
    {
        get => _serverStatusText;
        private set => SetField(ref _serverStatusText, value);
    }

    public IBrush ServerStatusBrush
    {
        get => _serverStatusBrush;
        private set => SetField(ref _serverStatusBrush, value);
    }

    // Připojí se na server, provede LOGIN a předá nick do parent VM.
    private async Task ConnectAsync()
    {
        try
        {
            IsBusy = true;
            ErrorMessage = null;
            var trimmed = Nickname.Trim();
            if (string.IsNullOrWhiteSpace(trimmed))
            {
                ErrorMessage = "Zadejte přezdívku.";
                return;
            }

            if (trimmed.Length > MaxNicknameLength)
            {
                ErrorMessage = $"Přezdívka je příliš dlouhá (max {MaxNicknameLength} znaků).";
                return;
            }

            if (!_discoveryAttempted)
            {
                await AutoDiscoverAsync();
            }

            GameClient.ConfigureEndpoint(Host, Port);
            await GameClient.ConnectAsync();
            AppServices.Logger.Info($"Connected to {Host}:{Port}");
            await GameClient.LoginAsync(trimmed);
            AppServices.Logger.Info($"Login OK as '{trimmed}'");
            if (_onLoginAsync != null)
            {
                StopProbeLoop();
                GameClient.ServerStatusChanged -= OnServerStatusChanged;
                await _onLoginAsync(trimmed);
            }
        }
        catch (Exception ex)
        {
            ErrorMessage = FormatLoginError(ex.Message);
            AppServices.Logger.Error($"Login failed: {ex.Message}");
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void OnServerStatusChanged(object? sender, ServerStatus status)
    {
        Dispatcher.UIThread.Post(() => UpdateServerStatus(status));
    }

    private void UpdateServerStatus(ServerStatus status)
    {
        switch (status)
        {
            case ServerStatus.Online:
                ServerStatusText = "Server online";
                ServerStatusBrush = Brushes.ForestGreen;
                break;
            case ServerStatus.Offline:
                ServerStatusText = "Spojení přerušeno";
                ServerStatusBrush = Brushes.IndianRed;
                break;
            default:
                ServerStatusText = "Server neznámý";
                ServerStatusBrush = Brushes.Gray;
                break;
        }
    }

    private async Task AutoDiscoverAsync()
    {
        _discoveryAttempted = true;
        try
        {
            OnUi(() => { IsDiscovering = true; UpdateStatus("Zkouším autodetekci serveru...", Brushes.Gray); });
            var discovered = await _discoverAsync();
            if (discovered.HasValue)
            {
                var (host, port) = discovered.Value;
                OnUi(() =>
                {
                    Host = host;
                    Port = port;
                    UpdateStatus($"Nalezen server {host}:{port}", Brushes.ForestGreen);
                });
                AppServices.Logger.Info($"Discovery succeeded: {host}:{port}");
            }
            else
            {
                OnUi(() => UpdateStatus("Autodetekce se nezdařila, vyplňte adresu ručně.", Brushes.DarkOrange));
                AppServices.Logger.Info("Discovery failed, using default endpoint.");
            }
        }
        catch (SocketException)
        {
            OnUi(() => UpdateStatus("Autodetekce selhala (síť), vyplňte adresu ručně.", Brushes.DarkOrange));
            AppServices.Logger.Error("Discovery failed with socket exception; using default endpoint.");
        }
        catch
        {
            OnUi(() => UpdateStatus("Autodetekce selhala, vyplňte adresu ručně.", Brushes.DarkOrange));
            AppServices.Logger.Error("Discovery failed; using default endpoint.");
        }
        finally
        {
            OnUi(() => IsDiscovering = false);
        }
    }

    private static void OnUi(Action action)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            action();
        }
        else
        {
            Dispatcher.UIThread.Post(action);
        }
    }

    private void UpdateStatus(string message, IBrush brush)
    {
        StatusMessage = message;
        StatusBrush = brush;
    }

    private static string FormatLoginError(string message)
    {
        if (message.Contains("SERVER_FULL", StringComparison.OrdinalIgnoreCase))
        {
            return "Server full";
        }

        return message;
    }

    private void StartProbeLoop()
    {
        if (_probeCts != null)
        {
            return;
        }

        _probeCts = new CancellationTokenSource();
        var token = _probeCts.Token;
        _ = Task.Run(async () =>
        {
            while (!token.IsCancellationRequested)
            {
                if (!IsBusy && !IsDiscovering)
                {
                    try
                    {
                        GameClient.ConfigureEndpoint(Host, Port);
                        await GameClient.ConnectAsync(token);
                        await GameClient.ProbeServerAsync(token);
                    }
                    catch
                    {
                        // best effort
                    }
                }

                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(5), token);
                }
                catch (TaskCanceledException)
                {
                    return;
                }
            }
        }, token);
    }

    private void StopProbeLoop()
    {
        if (_probeCts == null)
        {
            return;
        }
        _probeCts.Cancel();
        _probeCts = null;
    }
}
