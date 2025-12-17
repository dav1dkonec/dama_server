using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using dama_klient_app.Models;
using dama_klient_app.Services;
using Avalonia.Threading;

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
        GameClient.Disconnected += OnDisconnected;
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
            await GameClient.JoinRoomAsync(SelectedRoom.Id);
            StatusMessage = $"Joined {SelectedRoom.Name}";
            GameClient.LobbyUpdated -= OnLobbyUpdated;
            _startGame(SelectedRoom);
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
    private Task LogoutAsync()
    {
        GameClient.LobbyUpdated -= OnLobbyUpdated;
        GameClient.Disconnected -= OnDisconnected;
        _backToLogin();
        return Task.CompletedTask;
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
        Dispatcher.UIThread.Post(() =>
        {
            StatusMessage = "Spojení se serverem bylo ztraceno.";
            GameClient.LobbyUpdated -= OnLobbyUpdated;
            GameClient.Disconnected -= OnDisconnected;
            _backToLogin();
        });
    }
}
