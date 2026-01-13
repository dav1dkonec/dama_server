using System;
using dama_klient_app.Models;
using dama_klient_app.Services;

namespace dama_klient_app.ViewModels;

/// <summary>
/// Kořenová VM řídící navigaci mezi Login, Lobby a Game.
/// </summary>
public class MainWindowViewModel : ViewModelBase
{
    private ViewModelBase _currentViewModel;
    private string _nickname = string.Empty;
    private string _loginNotice = string.Empty;

    public MainWindowViewModel(IGameClient gameClient)
    {
        GameClient = gameClient;
        _currentViewModel = new LoginViewModel(gameClient, OnLoginCompleted, _loginNotice);
    }

    public IGameClient GameClient { get; }

    public ViewModelBase CurrentViewModel
    {
        get => _currentViewModel;
        private set => SetField(ref _currentViewModel, value);
    }

    // Login -> uloží nick a otevře lobby.
    private void OnLoginCompleted(string nickname)
    {
        _nickname = nickname;
        ShowLobby();
    }

    // Odhlášení zpět na login.
    private void ReturnToLogin(string? notice = null)
    {
        _nickname = string.Empty;
        _loginNotice = notice ?? string.Empty;
        CurrentViewModel = new LoginViewModel(GameClient, OnLoginCompleted, _loginNotice);
        _loginNotice = string.Empty;
    }

    // Přepnutí do lobby.
    private void ShowLobby()
    {
        CurrentViewModel = new LobbyViewModel(GameClient, _nickname, ReturnToLogin, StartGame);
    }

    // Zahájení hry v dané místnosti.
    private void StartGame(RoomInfo room)
    {
        CurrentViewModel = new GameViewModel(GameClient, room, _nickname, ShowLobby, ReturnToLogin);
    }
}
