using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using System.Windows.Input;
using dama_klient_app.Models;
using dama_klient_app.Services;

namespace dama_klient_app.ViewModels;

/// <summary>
/// VM pro lobby: načítá seznam místností, vytváří/ničí join a předává vybranou místnost do hry.
/// </summary>
public class LobbyViewModel : ViewModelBase
{
    private readonly Action _backToLogin;
    private readonly Action<RoomInfo> _startGame;
    private RoomInfo? _selectedRoom;
    private string? _statusMessage;
    private bool _isBusy;

    public LobbyViewModel(IGameClient gameClient, string nickname, Action backToLogin, Action<RoomInfo> startGame)
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
        _ = LoadRoomsAsync();
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
                (RefreshCommand as AsyncCommand)?.RaiseCanExecuteChanged();
                (CreateRoomCommand as AsyncCommand)?.RaiseCanExecuteChanged();
                (JoinRoomCommand as AsyncCommand)?.RaiseCanExecuteChanged();
                (LogoutCommand as AsyncCommand)?.RaiseCanExecuteChanged();
            }
        }
    }

    public ICommand RefreshCommand { get; }

    public ICommand CreateRoomCommand { get; }

    public ICommand JoinRoomCommand { get; }

    public ICommand LogoutCommand { get; }

    // Načte seznam místností pomocí LIST_ROOMS.
    private async Task LoadRoomsAsync()
    {
        try
        {
            IsBusy = true;
            StatusMessage = "Loading rooms...";
            var rooms = await GameClient.GetRoomsAsync();

            Rooms.Clear();
            foreach (var room in rooms)
            {
                Rooms.Add(room);
            }

            StatusMessage = $"{Rooms.Count} rooms available";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Failed to load rooms: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    // Vytvoří novou místnost (CREATE_ROOM).
    private async Task CreateRoomAsync()
    {
        try
        {
            IsBusy = true;
            StatusMessage = "Creating room...";
            var created = await GameClient.CreateRoomAsync($"Table {Rooms.Count + 1}");
            SelectedRoom = created;
            StatusMessage = $"Created {created.Name}";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Failed to create room: {ex.Message}";
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
            await GameClient.JoinRoomAsync(SelectedRoom.Id);
            StatusMessage = $"Joined {SelectedRoom.Name}";
            GameClient.LobbyUpdated -= OnLobbyUpdated;
            _startGame(SelectedRoom);
        }
        catch (Exception ex)
        {
            StatusMessage = $"Failed to join: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    // Odhlášení zpět na login (bez volání serveru – server nezná logout).
    private Task LogoutAsync()
    {
        GameClient.LobbyUpdated -= OnLobbyUpdated;
        _backToLogin();
        return Task.CompletedTask;
    }

    // Aktualizace Rooms při pushu z klienta (ROOM/ROOMS_EMPTY po LIST_ROOMS).
    private void OnLobbyUpdated(object? sender, IReadOnlyList<RoomInfo> rooms)
    {
        Rooms.Clear();
        foreach (var room in rooms)
        {
            Rooms.Add(room);
        }

        StatusMessage = $"{Rooms.Count} rooms available";
    }
}
