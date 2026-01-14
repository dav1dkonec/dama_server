using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using dama_klient_app.Models;
using dama_klient_app.Services;
using Avalonia.Threading;
using Avalonia.Media;

namespace dama_klient_app.ViewModels;

/// <summary>
/// VM pro lobby: načítá seznam místností, vytváří/ničí join a předává vybranou místnost do hry.
/// </summary>
public class LobbyViewModel : ViewModelBase
{
    private readonly Action<string?> _backToLogin;
    private readonly Action<RoomInfo> _startGame;
    private RoomInfo? _selectedRoom;
    private string? _statusMessage;
    private bool _isBusy;
    private bool _isLoadingRooms;
    private string _serverStatusText = "Server neznámý";
    private IBrush _serverStatusBrush = Brushes.Gray;
    private CancellationTokenSource? _serverOfflineCts;

    public LobbyViewModel(IGameClient gameClient, string nickname, Action<string?> backToLogin, Action<RoomInfo> startGame)
    {
        GameClient = gameClient;
        Nickname = nickname;
        _backToLogin = backToLogin;
        _startGame = startGame;
        Rooms = new ObservableCollection<RoomInfo>();

        RefreshCommand = new AsyncCommand(LoadRoomsAsync, () => !IsBusy);
        CreateRoomCommand = new AsyncCommand(CreateRoomAsync, () => !IsBusy);
        JoinRoomCommand = new AsyncCommand(JoinRoomAsync, () => !IsBusy && SelectedRoom != null);
        LogoutCommand = new AsyncCommand(LogoutAsync, () => !IsBusy);

        GameClient.LobbyUpdated += OnLobbyUpdated;
        GameClient.Disconnected += OnDisconnected;
        GameClient.TokenInvalidated += OnTokenInvalidated;
        GameClient.ServerStatusChanged += OnServerStatusChanged;
        UpdateServerStatus(GameClient.ServerStatus);
        _ = LoadRoomsAsync();
        AppServices.Logger.Info($"Entered lobby as '{nickname}'.");
    }

    public IGameClient GameClient { get; }

    public string Nickname { get; }

    public ObservableCollection<RoomInfo> Rooms { get; }

    public RoomInfo? SelectedRoom
    {
        get => _selectedRoom;
        set
        {
            if (SetField(ref _selectedRoom, value))
            {
                (JoinRoomCommand as AsyncCommand)?.RaiseCanExecuteChanged();
            }
        }
    }

    public string? StatusMessage
    {
        get => _statusMessage;
        private set => SetField(ref _statusMessage, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetField(ref _isBusy, value))
            {
                AppServices.Logger.Info($"Lobby IsBusy -> {value}");
                RaiseCommandStates();
            }
        }
    }

    public ICommand RefreshCommand { get; }

    public ICommand CreateRoomCommand { get; }

    public ICommand JoinRoomCommand { get; }

    public ICommand LogoutCommand { get; }

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

    private void RaiseCommandStates()
    {
        Dispatcher.UIThread.Post(() =>
        {
            (RefreshCommand as AsyncCommand)?.RaiseCanExecuteChanged();
            (CreateRoomCommand as AsyncCommand)?.RaiseCanExecuteChanged();
            (JoinRoomCommand as AsyncCommand)?.RaiseCanExecuteChanged();
            (LogoutCommand as AsyncCommand)?.RaiseCanExecuteChanged();
        });
    }

    // Načte seznam místností pomocí LIST_ROOMS.
    private async Task LoadRoomsAsync()
    {
        if (_isLoadingRooms)
        {
            return;
        }

        _isLoadingRooms = true;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(4));
        try
        {
            IsBusy = true;
            StatusMessage = "Loading rooms...";
            AppServices.Logger.Info("LIST_ROOMS start");
            var rooms = await GameClient.GetRoomsAsync(cts.Token);
            AppServices.Logger.Info($"LIST_ROOMS received {rooms.Count} rooms");

            Dispatcher.UIThread.Post(() =>
            {
                Rooms.Clear();
                foreach (var room in rooms)
                {
                    Rooms.Add(room);
                }
                
                var msg = Rooms.Count == 1 ? $"{Rooms.Count} stůl k dispozici" : $"{Rooms.Count} stoly k dispozici";
                if (rooms.Count >= 5 || rooms.Count < 1)
                {
                    msg = $"{Rooms.Count} stolů k dispozici";
                }

                StatusMessage = msg;
                AppServices.Logger.Info($"Loaded {Rooms.Count} rooms.");
            });
        }
        catch (Exception ex)
        {
            StatusMessage = $"Načtení stolů selhalo: {ex.Message}";
            AppServices.Logger.Error($"Load rooms failed: {ex.Message}");
        }
        finally
        {
            IsBusy = false;
            _isLoadingRooms = false;
            AppServices.Logger.Info($"LIST_ROOMS end (IsBusy={IsBusy})");
        }
    }

    // Vytvoří novou místnost (CREATE_ROOM).
    private async Task CreateRoomAsync()
    {
        try
        {
            IsBusy = true;
            StatusMessage = "Creating room...";
            var created = await GameClient.CreateRoomAsync("Stůl");
            StatusMessage = $"Vytvořen {created.Name}";
            AppServices.Logger.Info($"Created room {created.Id} ({created.Name}).");
            await LoadRoomsAsync();
        }
        catch (Exception ex)
        {
            StatusMessage = $"Vytvoření stolu selhalo: {ex.Message}";
            AppServices.Logger.Error($"Create room failed: {ex.Message}");
        }
        finally
        {
            IsBusy = false;
        }
    }

    // Připojí se do zvolené místnosti (JOIN_ROOM) a předá dál do Game flow.
    private async Task JoinRoomAsync()
    {
        if (SelectedRoom is null)
        {
            return;
        }

        try
        {
            IsBusy = true;
            StatusMessage = $"Joining {SelectedRoom.Name}...";
            var joinedCount = await GameClient.JoinRoomAsync(SelectedRoom.Id);
            StatusMessage = $"Joined {SelectedRoom.Name}";
            GameClient.LobbyUpdated -= OnLobbyUpdated;
            GameClient.Disconnected -= OnDisconnected;
            GameClient.TokenInvalidated -= OnTokenInvalidated;
            GameClient.ServerStatusChanged -= OnServerStatusChanged;
            StopServerOfflineCountdown();
            var roomForGame = joinedCount >= 0
                ? new RoomInfo(SelectedRoom.Id, SelectedRoom.Name, joinedCount, SelectedRoom.Capacity)
                : SelectedRoom;
            _startGame(roomForGame);
            AppServices.Logger.Info($"Joined room {SelectedRoom.Id} ({SelectedRoom.Name}).");
        }
        catch (Exception ex)
        {
            StatusMessage = $"Failed to join: {ex.Message}";
            AppServices.Logger.Error($"Join room failed: {ex.Message}");
        }
        finally
        {
            IsBusy = false;
        }
    }

    // Odhlášení zpět na login (bez volání serveru – server nezná logout).
    private async Task LogoutAsync()
    {
        try
        {
            await GameClient.SendByeAsync();
        }
        catch
        {
            // best-effort
        }
        GameClient.LobbyUpdated -= OnLobbyUpdated;
        GameClient.Disconnected -= OnDisconnected;
        GameClient.TokenInvalidated -= OnTokenInvalidated;
        GameClient.ServerStatusChanged -= OnServerStatusChanged;
        StopServerOfflineCountdown();
        _backToLogin(null);
    }

    // Aktualizace Rooms při pushu z klienta (ROOM/ROOMS_EMPTY po LIST_ROOMS).
    private void OnLobbyUpdated(object? sender, IReadOnlyList<RoomInfo> rooms)
    {
        Dispatcher.UIThread.Post(() =>
        {
            Rooms.Clear();
            foreach (var room in rooms)
            {
                Rooms.Add(room);
            }

            StatusMessage = $"{Rooms.Count} rooms available";
            AppServices.Logger.Info($"Lobby push update: {Rooms.Count} rooms.");
        });
    }

    private void OnDisconnected(object? sender, EventArgs e)
    {
        Dispatcher.UIThread.Post(HandleServerOffline);
    }

    private void OnTokenInvalidated(object? sender, EventArgs e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            StatusMessage = "Přihlášení vypršelo, přihlaste se znovu.";
            GameClient.LobbyUpdated -= OnLobbyUpdated;
            GameClient.Disconnected -= OnDisconnected;
            GameClient.TokenInvalidated -= OnTokenInvalidated;
            GameClient.ServerStatusChanged -= OnServerStatusChanged;
            StopServerOfflineCountdown();
            _backToLogin(null);
        });
    }

    private void OnServerStatusChanged(object? sender, ServerStatus status)
    {
        Dispatcher.UIThread.Post(() => HandleServerStatus(status));
    }

    private void HandleServerStatus(ServerStatus status)
    {
        UpdateServerStatus(status);
        if (status == ServerStatus.Offline)
        {
            HandleServerOffline();
            return;
        }

        StopServerOfflineCountdown();
        if (!_isLoadingRooms)
        {
            _ = LoadRoomsAsync();
        }
    }

    private void HandleServerOffline()
    {
        StartServerOfflineCountdown();
    }

    private void StartServerOfflineCountdown()
    {
        if (_serverOfflineCts != null)
        {
            return;
        }

        _serverOfflineCts = new CancellationTokenSource();
        var token = _serverOfflineCts.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(20), token);
            }
            catch (TaskCanceledException)
            {
                return;
            }

            Dispatcher.UIThread.Post(() =>
            {
                GameClient.LobbyUpdated -= OnLobbyUpdated;
                GameClient.Disconnected -= OnDisconnected;
                GameClient.TokenInvalidated -= OnTokenInvalidated;
                GameClient.ServerStatusChanged -= OnServerStatusChanged;
                _backToLogin("Spojení se serverem bylo přerušeno, byli jste odhlášeni.");
            });
        }, token);
    }

    private void StopServerOfflineCountdown()
    {
        if (_serverOfflineCts == null)
        {
            return;
        }
        _serverOfflineCts.Cancel();
        _serverOfflineCts = null;
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
}
