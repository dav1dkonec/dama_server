using System;
using System.Threading.Tasks;
using System.Windows.Input;
using dama_klient_app.Services;

namespace dama_klient_app.ViewModels;

/// <summary>
/// VM pro přihlášení: drží přezdívku, běh/error a volá Login/Connect na klientovi.
/// </summary>
public class LoginViewModel : ViewModelBase
{
    private readonly Func<string, Task>? _onLoginAsync;
    private string _nickname = string.Empty;
    private string? _errorMessage;
    private bool _isBusy;

    public LoginViewModel(IGameClient gameClient, Action<string> onLoginCompleted)
    {
        GameClient = gameClient;
        _onLoginAsync = nickname =>
        {
            onLoginCompleted(nickname);
            return Task.CompletedTask;
        };
        ConnectCommand = new AsyncCommand(ConnectAsync, () => !IsBusy && !string.IsNullOrWhiteSpace(Nickname));
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

    public bool HasError => !string.IsNullOrWhiteSpace(ErrorMessage);

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetField(ref _isBusy, value))
            {
                (ConnectCommand as AsyncCommand)?.RaiseCanExecuteChanged();
            }
        }
    }

    public ICommand ConnectCommand { get; }

    // Připojí se na server, provede LOGIN a předá nick do parent VM.
    private async Task ConnectAsync()
    {
        try
        {
            IsBusy = true;
            ErrorMessage = null;
            await GameClient.ConnectAsync();
            await GameClient.LoginAsync(Nickname.Trim());
            if (_onLoginAsync != null)
            {
                await _onLoginAsync(Nickname.Trim());
            }
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }
}
