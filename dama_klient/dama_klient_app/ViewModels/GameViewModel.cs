using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using System.Windows.Input;
using dama_klient_app.Models;
using dama_klient_app.Services;

namespace dama_klient_app.ViewModels;

/// <summary>
/// VM herní obrazovky – poslouchá GAME_START/STATE/END, drží desku a popisuje, kdo je na tahu.
/// Interaktivní tahy (LEGAL_MOVES/MOVE) se doplní v další fázi.
/// </summary>
public class GameViewModel : ViewModelBase
{
    // Identifikace místnosti a návratová akce do lobby.
    private readonly Action _returnToLobby;
    private readonly int _roomId;

    // Příznak, že deska už byla načtena (pro derivaci textu na tahu).
    private bool _initializedBoard;

    // Texty a statusy do UI.
    private string _roomName;
    private string _playerColor;
    private string _opponentName = "Soupeř";
    private string _currentTurn = "NONE";
    private string _currentTurnDisplay = "Čekám na data";
    private string _statusMessage = "Čekám na start hry...";
    private string _playerTimer = "60s";
    private string _opponentTimer = "60s";

    public GameViewModel(IGameClient gameClient, RoomInfo room, string playerNickname, Action returnToLobby)
    {
        GameClient = gameClient;
        _returnToLobby = returnToLobby;
        _roomId = int.TryParse(room.Id, out var rid) ? rid : 0;
        _roomName = room.Name;
        _playerColor = "NEZNÁMÁ";
        PlayerNickname = playerNickname;
        Cells = new ObservableCollection<BoardCellViewModel>();

        LeaveGameCommand = new AsyncCommand(LeaveAsync);

        GameClient.GameStarted += OnGameStarted;
        GameClient.GameStateUpdated += OnGameStateUpdated;
        GameClient.GameEnded += OnGameEnded;

        BuildBoard();
    }

    public IGameClient GameClient { get; }

    public int BoardSize { get; } = 8;

    public ObservableCollection<BoardCellViewModel> Cells { get; }

    public string RoomName
    {
        get => _roomName;
        set => SetField(ref _roomName, value);
    }

    public string PlayerNickname { get; }

    public string PlayerColor
    {
        get => _playerColor;
        set => SetField(ref _playerColor, value);
    }

    public string OpponentName
    {
        get => _opponentName;
        set => SetField(ref _opponentName, value);
    }

    public string CurrentTurn
    {
        get => _currentTurn;
        set => SetField(ref _currentTurn, value);
    }

    public string CurrentTurnDisplay
    {
        get => _currentTurnDisplay;
        private set => SetField(ref _currentTurnDisplay, value);
    }

    public string StatusMessage
    {
        get => _statusMessage;
        set => SetField(ref _statusMessage, value);
    }

    public string PlayerTimer
    {
        get => _playerTimer;
        set => SetField(ref _playerTimer, value);
    }

    public string OpponentTimer
    {
        get => _opponentTimer;
        set => SetField(ref _opponentTimer, value);
    }

    public ICommand LeaveGameCommand { get; }

    private async Task LeaveAsync()
    {
        try
        {
            if (_roomId > 0)
            {
                await GameClient.LeaveRoomAsync(_roomId);
            }
        }
        catch
        {
            // Chyba se neřeší – server je autoritativní, UI jen opouští obrazovku.
        }

        Unsubscribe();
        _returnToLobby();
    }

    // Vytvoří prázdnou 8x8 mřížku políček.
    private void BuildBoard()
    {
        Cells.Clear();
        for (var row = 0; row < BoardSize; row++)
        {
            for (var col = 0; col < BoardSize; col++)
            {
                Cells.Add(new BoardCellViewModel(row, col));
            }
        }
    }

    // Vykreslí stav desky podle 64 znaků z GAME_STATE (., w/W, b/B).
    private void ApplyBoard(string board)
    {
        if (board.Length < BoardSize * BoardSize)
        {
            return;
        }

        for (var index = 0; index < BoardSize * BoardSize; index++)
        {
            var cell = Cells[index];
            var symbol = board[index];
            cell.Piece = symbol switch
            {
                'w' => new PieceViewModel("White", false),
                'W' => new PieceViewModel("White", true),
                'b' => new PieceViewModel("Black", false),
                'B' => new PieceViewModel("Black", true),
                _ => null
            };
        }
        _initializedBoard = true;
    }

    // Reset zvýraznění (zatím nevyužito – připraveno pro interaktivní tahy).
    private void ClearHighlights()
    {
        foreach (var cell in Cells)
        {
            cell.IsHighlighted = false;
        }
    }

    // Ošetření GAME_START: uloží roli a status.
    private void OnGameStarted(object? sender, GameStartInfo info)
    {
        if (info.RoomId != _roomId)
        {
            return;
        }

        PlayerColor = info.Role;
        StatusMessage = $"Hra spuštěna, vaše barva: {PlayerColor}";
        CurrentTurnDisplay = "Čekám na stav hry...";
    }

    // Ošetření GAME_STATE: aplikuje desku a text o tom, kdo je na tahu.
    private void OnGameStateUpdated(object? sender, GameStateInfo info)
    {
        if (info.RoomId != _roomId)
        {
            return;
        }

        ApplyBoard(info.Board);
        CurrentTurn = info.Turn;
        CurrentTurnDisplay = DeriveTurnLabel(info.Turn);
        StatusMessage = $"Na tahu: {CurrentTurnDisplay}";
    }

    // Ošetření GAME_END: vypíše důvod/vítěze a odpojí odběry.
    private void OnGameEnded(object? sender, GameEndInfo info)
    {
        if (info.RoomId != _roomId)
        {
            return;
        }

        StatusMessage = $"Konec hry: {info.Reason}, vítěz: {info.Winner}";
        CurrentTurnDisplay = "Ukončeno";
        Unsubscribe();
    }

    private string DeriveTurnLabel(string turn)
    {
        // Turn z PROTOCOL.md: PLAYER1/PLAYER2/NONE – mapuje podle barvy při startu.
        if (!_initializedBoard)
        {
            return "Čekám na data";
        }

        return turn switch
        {
            "NONE" => "Nikdo",
            "PLAYER1" => PlayerColor.Equals("WHITE", StringComparison.OrdinalIgnoreCase) ? "Ty" : "Soupeř",
            "PLAYER2" => PlayerColor.Equals("BLACK", StringComparison.OrdinalIgnoreCase) ? "Ty" : "Soupeř",
            _ => "Neznámý"
        };
    }

    // Odhlášení z eventů klienta (prevence leaků a double-notifikací).
    private void Unsubscribe()
    {
        GameClient.GameStarted -= OnGameStarted;
        GameClient.GameStateUpdated -= OnGameStateUpdated;
        GameClient.GameEnded -= OnGameEnded;
    }
}
