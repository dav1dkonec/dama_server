using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Threading.Tasks;
using System.Windows.Input;
using System.Timers;
using Avalonia.Threading;
using Avalonia.Media;
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
    private readonly Action<string?> _returnToLogin;
    private readonly int _roomId;

    // Příznak, že deska už byla načtena (pro změnu textu na tahu).
    private bool _initializedBoard;
    private bool _isBusy;

    // Texty a statusy do UI.
    private string _roomName;
    private string _playerColor;
    private string _opponentName = "Soupeř";
    private string _currentTurn = "NONE";
    private string _currentTurnDisplay = "Čekám na soupeře";
    private string _statusMessage = "Čekám na start hry...";
    private string _playerTimer = "60s";
    private string _opponentTimer = "60s";
    private string _serverStatusText = "Server neznámý";
    private IBrush _serverStatusBrush = Brushes.Gray;

    private BoardCellViewModel? _selectedCell;
    private LegalMovesResult? _lastLegalMoves;
    private (int Row, int Col)? _lastMoveTarget;
    private bool _awaitingCaptureChain;
    private TimeSpan _turnDuration = TimeSpan.FromSeconds(60);
    private DateTime _turnDeadline;
    private bool _isMyTurn;
    private readonly Timer _turnTimer;
    private bool _isPaused;
    private long _resumeByMs;
    private bool _awaitingReconnect;
    private DispatcherTimer? _reconnectTimer;
    private bool _showingPause;
    private bool _serverOutage;
    private long _serverOutageByMs;
    private string? _lastBoard;
    private BoardCellViewModel[] _cellsByServer = Array.Empty<BoardCellViewModel>();
    private bool _boardBuilt;
    private bool _timeExpired;
    private string _lastTurn = "NONE";
    private bool _showLeaveConfirm;
    private bool _isFinished;
    private bool _isMyClockActive;
    private bool _isOpponentClockActive;
    private bool _showPauseOverlay;
    private readonly TimeSpan _reconnectWindow = TimeSpan.FromSeconds(60);
    private bool _localDisconnect;

    public GameViewModel(IGameClient gameClient, RoomInfo room, string playerNickname, Action returnToLobby, Action<string?> returnToLogin)
    {
        GameClient = gameClient;
        _returnToLobby = returnToLobby;
        _returnToLogin = returnToLogin;
        _roomId = int.TryParse(room.Id, out var rid) ? rid : 0;
        _roomName = room.Name;
        _playerColor = "NEZNÁMÁ";
        PlayerNickname = playerNickname;
        Cells = new ObservableCollection<BoardCellViewModel>();
        _turnTimer = new Timer(1000);
        _turnTimer.Elapsed += OnTurnTimerElapsed;

        RequestLeaveCommand = new RelayCommand(OnLeaveRequested);
        ConfirmLeaveCommand = new AsyncCommand(LeaveAsync);
        CancelLeaveCommand = new RelayCommand(() => ShowLeaveConfirm = false);

        GameClient.GameStarted += OnGameStarted;
        GameClient.GameStateUpdated += OnGameStateUpdated;
        GameClient.GameEnded += OnGameEnded;
        GameClient.Disconnected += OnDisconnected;
        GameClient.GamePaused += OnGamePaused;
        GameClient.TokenInvalidated += OnTokenInvalidated;
        GameClient.ServerStatusChanged += OnServerStatusChanged;
        UpdateServerStatus(GameClient.ServerStatus);
        _ = GameClient.TryReconnectAsync();

        // build default grid on UI thread; will rebuild with flip after GAME_START
        Dispatcher.UIThread.Post(() =>
        {
            BuildBoard(false);
            _boardBuilt = true;
        });

        // If GAME_START/STATE already arrived before we subscribed, replay them.
        if (GameClient.TryGetLastGameStart(_roomId, out var cachedStart))
        {
            OnGameStarted(this, cachedStart);
        }
        if (GameClient.TryGetLastGameState(_roomId, out var cachedState))
        {
            OnGameStateUpdated(this, cachedState);
        }
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
        set
        {
            if (SetField(ref _playerColor, value))
            {
                OnPropertyChanged(nameof(OpponentColor));
                OnPropertyChanged(nameof(OpponentFill));
                OnPropertyChanged(nameof(OpponentStroke));
                OnPropertyChanged(nameof(PlayerFill));
                OnPropertyChanged(nameof(PlayerStroke));
            }
        }
    }

    public string OpponentName
    {
        get => _opponentName;
        set => SetField(ref _opponentName, value);
    }

    public string OpponentColor => PlayerColor.Equals("WHITE", StringComparison.OrdinalIgnoreCase) ? "BLACK" : "WHITE";

    public string OpponentFill => OpponentColor.Equals("WHITE", StringComparison.OrdinalIgnoreCase) ? "#f8f8f8" : "#2d2d2d";

    public string OpponentStroke => OpponentColor.Equals("WHITE", StringComparison.OrdinalIgnoreCase) ? "#d0d0d0" : "#101010";

    public string PlayerFill => PlayerColor.Equals("WHITE", StringComparison.OrdinalIgnoreCase) ? "#f8f8f8" : "#2d2d2d";

    public string PlayerStroke => PlayerColor.Equals("WHITE", StringComparison.OrdinalIgnoreCase) ? "#d0d0d0" : "#101010";

    public bool IsMyClockActive
    {
        get => _isMyClockActive;
        set => SetField(ref _isMyClockActive, value);
    }

    public bool IsOpponentClockActive
    {
        get => _isOpponentClockActive;
        set => SetField(ref _isOpponentClockActive, value);
    }

    public bool IsBoardEnabled => !_isPaused;

    public bool ShowPauseOverlay
    {
        get => _showPauseOverlay;
        set => SetField(ref _showPauseOverlay, value);
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

    public ICommand RequestLeaveCommand { get; }
    public ICommand ConfirmLeaveCommand { get; }
    public ICommand CancelLeaveCommand { get; }

    public bool ShowLeaveConfirm
    {
        get => _showLeaveConfirm;
        set => SetField(ref _showLeaveConfirm, value);
    }

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
        finally
        {
            if (_roomId > 0)
            {
                GameClient.ClearRoomCache(_roomId);
            }
        }

        StopReconnectLoop();
        Unsubscribe();
        ShowLeaveConfirm = false;
        _returnToLobby();
    }

    // Vytvoří prázdnou 8x8 mřížku políček, s volitelným otočením pro černého.
    private void BuildBoard(bool flipForBlack)
    {
        Cells.Clear();
        _cellsByServer = new BoardCellViewModel[BoardSize * BoardSize];

        // mapujeme serverové souřadnice na UI podle flipu
        var temp = new BoardCellViewModel[BoardSize * BoardSize];
        for (var srvRow = 0; srvRow < BoardSize; srvRow++)
        {
            for (var srvCol = 0; srvCol < BoardSize; srvCol++)
            {
                var uiRow = flipForBlack ? (BoardSize - 1 - srvRow) : srvRow;
                var uiCol = flipForBlack ? (BoardSize - 1 - srvCol) : srvCol;
                var cell = new BoardCellViewModel(uiRow, uiCol, srvRow, srvCol);
                cell.ClickCommand = new AsyncCommand(() => OnCellClickedAsync(cell));
                temp[uiRow * BoardSize + uiCol] = cell;
                _cellsByServer[srvRow * BoardSize + srvCol] = cell;
            }
        }

        foreach (var cell in temp)
        {
            Cells.Add(cell);
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
            var cell = _cellsByServer[index];
            if (cell == null) continue;
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
        var totalPieces = Cells.Count(c => c.HasPiece);
        if (totalPieces <= BoardSize)
        {
            StatusMessage = "Čeká se na soupeře...";
        }
    }
    
    private void ClearHighlights()
    {
        foreach (var cell in Cells)
        {
            cell.IsHighlighted = false;
        }
    }

    private void HighlightCaptures()
    {
        if (_lastLegalMoves == null)
        {
            return;
        }

        foreach (var dest in _lastLegalMoves.Destinations)
        {
            var cell = GetCellByServer(dest.Row, dest.Col);
            if (cell != null)
            {
                cell.IsHighlighted = true;
            }
        }
    }

    private void ClearSelection()
    {
        _selectedCell = null;
        _lastLegalMoves = null;
        ClearHighlights();
    }

    // Ošetření GAME_START: uloží roli a status.
    private void OnGameStarted(object? sender, GameStartInfo info)
    {
        if (info.RoomId != _roomId)
        {
            return;
        }

        ClearSelection();
        _awaitingCaptureChain = false;
        _lastMoveTarget = null;
        PlayerColor = info.Role;
        _isFinished = false;
        Dispatcher.UIThread.Post(() =>
        {
            var flip = PlayerColor.Equals("BLACK", StringComparison.OrdinalIgnoreCase);
            BuildBoard(flip);
            _boardBuilt = true;
        });
        var timeoutMs = GameClient.TurnTimeoutMs > 0 ? GameClient.TurnTimeoutMs : 60000;
        _turnDuration = TimeSpan.FromMilliseconds(timeoutMs);
        Dispatcher.UIThread.Post(() =>
        {
            StatusMessage = $"Hra spuštěna, vaše barva: {PlayerColor}";
            CurrentTurnDisplay = "Čekám na stav hry...";
            ApplyBoard(_lastBoard ?? string.Empty);
        });
        AppServices.Logger.Info($"GAME_START room={info.RoomId} role={info.Role} timeoutMs={timeoutMs}");
    }

    // Ošetření GAME_STATE: aplikuje desku a text o tom, kdo je na tahu.
    private void OnGameStateUpdated(object? sender, GameStateInfo info)
    {
        if (info.RoomId != _roomId)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(info.Board) || info.Board.Length < BoardSize * BoardSize)
        {
            return;
        }
        _lastBoard = info.Board;
        if (string.Equals(PlayerColor, "NEZNÁMÁ", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }
        Dispatcher.UIThread.Post(() =>
        {
            if (!_boardBuilt)
            {
                var flipNow = PlayerColor.Equals("BLACK", StringComparison.OrdinalIgnoreCase);
                BuildBoard(flipNow);
                _boardBuilt = true;
            }
            ApplyBoard(info.Board);
            ClearSelection();
            SetPaused(false);
            _serverOutage = false;
            _serverOutageByMs = 0;
            _resumeByMs = 0;
            _localDisconnect = false;
            _awaitingReconnect = false;
            _showingPause = false;
            StopReconnectLoop();
            _timeExpired = false;
            ShowPauseOverlay = false;
            if (!_lastMoveTarget.HasValue)
            {
                _awaitingCaptureChain = false;
            }
            CurrentTurn = info.Turn;
            CurrentTurnDisplay = DeriveTurnLabel(info.Turn);
            _lastTurn = info.Turn;
            var myTurn = IsMyTurn(info.Turn);
            if (info.LockRow.HasValue && info.LockCol.HasValue)
            {
                _lastMoveTarget = (info.LockRow.Value, info.LockCol.Value);
                _awaitingCaptureChain = myTurn;
            }
            else
            {
                _awaitingCaptureChain = false;
                _lastMoveTarget = null;
            }
            StartTurnTimer(info.Turn, info.RemainingMs);
            if (!myTurn)
            {
                _awaitingCaptureChain = false;
                _lastMoveTarget = null;
                StatusMessage = "Hra probíhá.";
            }
            else if (_awaitingCaptureChain && _lastMoveTarget.HasValue)
            {
                StatusMessage = "Pokračuj v braní tou samou figurkou.";
                _ = ContinueCaptureAsync(_lastMoveTarget.Value);
            }
            if (Cells.Count(c => c.HasPiece) <= BoardSize) // heuristika: prázdná místa = čekání na soupeře na startu
            {
                StatusMessage = "Čeká se na soupeře...";
            }
            else
            {
                if (_timeExpired)
                {
                    StatusMessage = _isMyTurn
                        ? "Vypršel ti čas, čekám na vyhodnocení."
                        : "Soupeři vypršel čas, čekám na vyhodnocení.";
                }
                else
                {
                    StatusMessage = "Hra probíhá.";
                }
            }
            AppServices.Logger.Info($"GAME_STATE room={info.RoomId} turn={info.Turn} pieces={Cells.Count(c => c.HasPiece)}");
        });
    }

    // Ošetření GAME_END: vypíše důvod/vítěze a odpojí odběry.
    private async void OnGameEnded(object? sender, GameEndInfo info)
    {
        if (info.RoomId != _roomId)
        {
            return;
        }

        StatusMessage = BuildEndMessage(info);
        CurrentTurnDisplay = "Ukončeno";
        ClearSelection();
        StopTurnTimer();
        SetPaused(false);
        _resumeByMs = 0;
        _showingPause = false;
        _awaitingCaptureChain = false;
        _lastMoveTarget = null;
        StopReconnectLoop();
        Unsubscribe();
        _isFinished = true;
        _serverOutage = false;
        _serverOutageByMs = 0;
        _localDisconnect = false;
        AppServices.Logger.Info($"GAME_END room={info.RoomId} reason={info.Reason} winner={info.Winner}");

        var reason = info.Reason?.ToUpperInvariant() ?? string.Empty;
        var winner = info.Winner?.ToUpperInvariant() ?? "NONE";
        if (reason == "OPPONENT_TIMEOUT" && !winner.Equals(PlayerColor, StringComparison.OrdinalIgnoreCase))
        {
            await ReturnToLoginAsync();
            return;
        }

        // pokud v reconnect flow, klient se pošle rovnou na login
        if (_awaitingReconnect)
        {
            await ReturnToLoginAsync();
            return;
        }

        // necháme vítězi krátký čas na zobrazení výsledku
        await Task.Delay(TimeSpan.FromSeconds(3));
        _returnToLobby();
    }

    private string BuildEndMessage(GameEndInfo info)
    {
        var reason = info.Reason?.ToUpperInvariant() ?? string.Empty;
        var winner = info.Winner?.ToUpperInvariant() ?? "NONE";
        string winnerLabel = winner switch
        {
            "WHITE" => "Bílý",
            "BLACK" => "Černý",
            _ => "Nikdo"
        };

        if (reason == "OPPONENT_LEFT" && winner == "NONE")
        {
            winnerLabel = PlayerColor.Equals("WHITE", StringComparison.OrdinalIgnoreCase) ? "Bílý" : "Černý";
            winner = PlayerColor.ToUpperInvariant();
        }

        return reason switch
        {
            "TURN_TIMEOUT" => $"Konec hry: vypršel čas. Vítěz: {winnerLabel}.",
            "OPPONENT_TIMEOUT" => $"Konec hry: soupeř se nevrátil včas. Vítěz: {winnerLabel}.",
            "OPPONENT_LEFT" => $"Konec hry: soupeř se vzdal. Vítěz: {winnerLabel}.",
            "DRAW" => "Konec hry: remíza.",
            _ => $"Konec hry. Vítěz: {winnerLabel}."
        };
    }

    private string DeriveTurnLabel(string turn)
    {
        // Turn z PROTOCOL.md: PLAYER1/PLAYER2/NONE – mapuje podle barvy při startu.
        if (!_initializedBoard)
        {
            return "Čeká se na data";
        }

        return turn switch
        {
            "NONE" => "Nikdo",
            "PLAYER1" => PlayerColor.Equals("WHITE", StringComparison.OrdinalIgnoreCase) ? "Ty" : "Soupeř",
            "PLAYER2" => PlayerColor.Equals("BLACK", StringComparison.OrdinalIgnoreCase) ? "Ty" : "Soupeř",
            _ => "Neznámý"
        };
    }

    private void StartTurnTimer(string turn, int remainingMs = -1)
    {
        var timeoutMs = GameClient.TurnTimeoutMs > 0 ? GameClient.TurnTimeoutMs : 60000;
        _turnDuration = TimeSpan.FromMilliseconds(timeoutMs);
        var remaining = remainingMs > 0 ? TimeSpan.FromMilliseconds(remainingMs) : _turnDuration;
        _turnDeadline = DateTime.UtcNow + remaining;
        _isMyTurn = IsMyTurn(turn);
        IsMyClockActive = _isMyTurn;
        IsOpponentClockActive = !_isMyTurn;
        _timeExpired = false;
        UpdateTimers(remaining);
        _turnTimer.Start();
        StatusMessage = _isMyTurn ? "Jsi na tahu." : "Čeká se na tah soupeře.";
    }

    private void OnLeaveRequested()
    {
        if (_isFinished)
        {
            _ = LeaveAsync();
            return;
        }

        // pokud hra ještě nezačala (čísla figurek <= 8), potvrzení není potřeba
        var hasStarted = Cells.Count(c => c.HasPiece) > BoardSize;
        if (!hasStarted)
        {
            _ = LeaveAsync();
            return;
        }

        ShowLeaveConfirm = true;
    }

    private void StopTurnTimer()
    {
        _turnTimer.Stop();
    }

    private void OnTurnTimerElapsed(object? sender, ElapsedEventArgs e)
    {
        var now = DateTime.UtcNow;
        var remaining = _turnDeadline - now;
        if (remaining < TimeSpan.Zero)
        {
            remaining = TimeSpan.Zero;
        }

        Dispatcher.UIThread.Post(() => UpdateTimers(remaining));
    }

    private void UpdateTimers(TimeSpan remaining)
    {
        if (remaining <= TimeSpan.Zero && !_timeExpired)
        {
            _timeExpired = true;
            _turnTimer.Stop();
            StatusMessage = _isMyTurn
                ? "Vypršel ti čas, čekám na vyhodnocení."
                : "Soupeři vypršel čas, čekám na vyhodnocení.";
        }

        var formatted = $"{Math.Max(0, (int)remaining.TotalSeconds)}s";
        if (_isMyTurn)
        {
            PlayerTimer = formatted;
            OpponentTimer = FormatFullDuration();
        }
        else
        {
            OpponentTimer = formatted;
            PlayerTimer = FormatFullDuration();
        }
    }

    private bool IsMyTurn(string turn)
    {
        var expected = PlayerColor.Equals("WHITE", StringComparison.OrdinalIgnoreCase) ? "PLAYER1" : "PLAYER2";
        return turn.Equals(expected, StringComparison.OrdinalIgnoreCase);
    }

    private string FormatFullDuration()
    {
        return $"{(int)_turnDuration.TotalSeconds}s";
    }

    private async Task ContinueCaptureAsync((int Row, int Col) targetCell)
    {
        var cell = GetCellByServer(targetCell.Row, targetCell.Col);
        if (cell == null)
        {
            _awaitingCaptureChain = false;
            _lastMoveTarget = null;
            return;
        }

        _awaitingCaptureChain = true;
        await FetchLegalMovesAsync(cell);
        if (_lastLegalMoves != null && _lastLegalMoves.Destinations.Count > 0)
        {
            StatusMessage = "Pokračujte v braní tou samou figurkou.";
        }
        else
        {
            _awaitingCaptureChain = false;
            _lastMoveTarget = null;
        }
    }

    private void OnDisconnected(object? sender, EventArgs e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            HandleConnectivityLoss();
        });
    }

    private void OnServerStatusChanged(object? sender, ServerStatus status)
    {
        Dispatcher.UIThread.Post(() => HandleServerStatus(status));
    }

    private void OnTokenInvalidated(object? sender, EventArgs e)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => OnTokenInvalidated(sender, e));
            return;
        }

        _isFinished = true;
        StopTurnTimer();
        StopReconnectLoop();
        SetPaused(false);
        _ = ReturnToLoginAsync();
    }

    private void HandleServerStatus(ServerStatus status)
    {
        UpdateServerStatus(status);
        if (status == ServerStatus.Offline)
        {
            HandleConnectivityLoss();
        }
        else if (status == ServerStatus.Online)
        {
            HandleServerOnline();
        }
    }

    private void HandleServerOffline()
    {
        if (_isFinished || _serverOutage)
        {
            return;
        }

        _localDisconnect = false;
        _serverOutage = true;
        _serverOutageByMs = DateTimeOffset.UtcNow.AddSeconds(20).ToUnixTimeMilliseconds();
        StatusMessage = "Spojení se serverem bylo přerušeno, čekám na obnovení...";
        SetPaused(true);
        _showingPause = false;
        _awaitingReconnect = true;
        StopTurnTimer();
        IsMyClockActive = false;
        IsOpponentClockActive = false;
        CurrentTurnDisplay = "Přerušeno";
        ShowPauseOverlay = true;
        StartReconnectLoop();
    }

    private void HandleLocalNetworkLoss()
    {
        if (_isFinished || _localDisconnect)
        {
            return;
        }

        _localDisconnect = true;
        _serverOutage = false;
        _serverOutageByMs = 0;
        _resumeByMs = DateTimeOffset.UtcNow.Add(_reconnectWindow).ToUnixTimeMilliseconds();
        StatusMessage = "Ztracené spojení. Obnovuji připojení...";
        SetPaused(true);
        _showingPause = false;
        _awaitingReconnect = true;
        StopTurnTimer();
        IsMyClockActive = false;
        IsOpponentClockActive = false;
        CurrentTurnDisplay = "Přerušeno";
        ShowPauseOverlay = true;
        StartReconnectLoop();
    }

    private void HandleServerOnline()
    {
        if (!_serverOutage || _isFinished)
        {
            return;
        }

        _serverOutage = false;
        _serverOutageByMs = 0;
        StatusMessage = "Obnovuji spojení se serverem...";
        _awaitingReconnect = true;
        StartReconnectLoop();
        _ = GameClient.TryReconnectAsync();
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

    private void OnGamePaused(object? sender, (int RoomId, long ResumeBy) payload)
    {
        if (payload.RoomId != _roomId)
        {
            return;
        }

        Dispatcher.UIThread.Post(() =>
        {
            SetPaused(true);
            if (!_showingPause || _resumeByMs == 0)
            {
                _resumeByMs = payload.ResumeBy;
            }
            StopTurnTimer();
            IsMyClockActive = false;
            IsOpponentClockActive = false;
            StatusMessage = "Soupeř odpojen, čekám na návrat...";
            CurrentTurnDisplay = "Přerušeno";
            _awaitingReconnect = false;
            _showingPause = true;
            _serverOutage = false;
            _serverOutageByMs = 0;
            _localDisconnect = false;
            ShowPauseOverlay = true;
        });
        StartReconnectLoop();
    }

    private async Task OnCellClickedAsync(BoardCellViewModel cell)
    {
        if (_isBusy)
        {
            return;
        }

        if (_awaitingReconnect)
        {
            StatusMessage = "Obnovuji spojení se serverem...";
            return;
        }

        if (_isPaused)
        {
            StatusMessage = "Hra je pozastavená, čeká se na soupeře.";
            return;
        }

        if (_timeExpired)
        {
            StatusMessage = _isMyTurn
                ? "Vypršel ti čas, čekám na vyhodnocení."
                : "Soupeři vypršel čas, čekám na vyhodnocení.";
            return;
        }

        if (_awaitingCaptureChain && _lastMoveTarget.HasValue)
        {
            var (r, c) = _lastMoveTarget.Value;
            if (cell.HasPiece && (cell.ServerRow != r || cell.ServerCol != c))
            {
                StatusMessage = "Pokračuj v braní tou samou figurkou.";
                return;
            }

            // pokud není vybraná figurka (např. po novém GAME_STATE), znovu načte tahy pro zámek
            if ((_selectedCell == null || _lastLegalMoves == null) && cell.ServerRow == r && cell.ServerCol == c && cell.Piece != null)
            {
                await FetchLegalMovesAsync(cell);
            }
        }

        if (!_initializedBoard)
        {
            StatusMessage = "Čekám na stav hry...";
            return;
        }

        if (_selectedCell != null &&
            _lastLegalMoves != null &&
            cell.IsHighlighted &&
            cell != _selectedCell)
        {
            await ExecuteMoveAsync(cell);
            return;
        }

        if (cell.Piece == null)
        {
            ClearSelection();
            StatusMessage = "Vyberte svou figurku.";
            return;
        }

        if (!IsPlayersPiece(cell.Piece))
        {
            ClearSelection();
            return; // žádná hláška při kliknutí na soupeře
        }

        if (!IsMyTurn(CurrentTurn))
        {
            ClearSelection();
            StatusMessage = "Nyní jste mimo tah.";
            return;
        }

        await FetchLegalMovesAsync(cell);
    }

    private async Task FetchLegalMovesAsync(BoardCellViewModel cell)
    {
        try
        {
            _isBusy = true;
            ClearSelection();

            _selectedCell = cell;
            cell.IsHighlighted = true;
            StatusMessage = "Načítám možné tahy...";

            var legal = await GameClient.GetLegalMovesAsync(_roomId, cell.ServerRow, cell.ServerCol);
            _lastLegalMoves = legal;

            if (legal.Destinations.Count == 0)
            {
                StatusMessage = legal.MustCapture
                    ? "Jedna z tvých figurek musí brát – táhni s ní."
                    : "Žádný platný tah.";
                return;
            }

            HighlightCaptures();

            var suffix = legal.MustCapture ? " (povinné braní)" : string.Empty;
            StatusMessage = $"Vyberte cílové pole{suffix}.";
        }
        catch (Exception ex)
        {
            if (ex is TimeoutException || ex.Message.Contains("No response", StringComparison.OrdinalIgnoreCase))
            {
                HandleLocalDisconnect();
                return;
            }
            StatusMessage = $"Načtení tahů selhalo: {ex.Message}";
            ClearSelection();
            AppServices.Logger.Error($"LEGAL_MOVES failed: {ex.Message}");
        }
        finally
        {
            _isBusy = false;
        }
    }

    private async Task ExecuteMoveAsync(BoardCellViewModel target)
    {
        var selected = _selectedCell;
        var legal = _lastLegalMoves;
        if (selected == null || legal == null)
        {
            ClearSelection();
            return;
        }
        
        var isAllowed = legal.Destinations.Any(d => d.Row == target.ServerRow && d.Col == target.ServerCol);
        if (!isAllowed)
        {
            ClearSelection();
            return;
        }

        var moveSucceeded = false;
        try
        {
            _isBusy = true;
            StatusMessage = $"Tah: {selected.Row},{selected.Col} → {target.Row},{target.Col}";

            var move = new Move(_roomId, (selected.ServerRow, selected.ServerCol), (target.ServerRow, target.ServerCol));
            await GameClient.SendMoveAsync(move);

            StatusMessage = "Tah odeslán.";
            AppServices.Logger.Info($"MOVE room={_roomId} from={selected.Row},{selected.Col} to={target.Row},{target.Col}");
            moveSucceeded = true;
        }
        catch (Exception ex)
        {
            if (ex is TimeoutException || ex.Message.Contains("No response", StringComparison.OrdinalIgnoreCase))
            {
                HandleLocalDisconnect();
                return;
            }
            StatusMessage = $"Tah selhal: {ex.Message}";
            AppServices.Logger.Error($"Move failed: {ex.Message}");
        }
        finally
        {
            _isBusy = false;
            if (!moveSucceeded)
            {
                ClearSelection();
            }
        }
    }

    private bool IsPlayersPiece(PieceViewModel piece)
    {
        var isWhite = PlayerColor.Equals("WHITE", StringComparison.OrdinalIgnoreCase);
        return (isWhite && piece.Color.Equals("White", StringComparison.OrdinalIgnoreCase)) ||
               (!isWhite && piece.Color.Equals("Black", StringComparison.OrdinalIgnoreCase));
    }

    private BoardCellViewModel? GetCellByServer(int serverRow, int serverCol)
    {
        var idx = serverRow * BoardSize + serverCol;
        if (idx < 0 || idx >= _cellsByServer.Length)
        {
            return null;
        }
        return _cellsByServer[idx];
    }

    private void StartReconnectLoop()
    {
        if (_reconnectTimer == null)
        {
            _reconnectTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            _reconnectTimer.Tick += OnReconnectTick;
        }
        if (!_reconnectTimer.IsEnabled)
        {
            _reconnectTimer.Start();
        }
        if (_awaitingReconnect)
        {
            _ = GameClient.TryReconnectAsync();
        }
    }

    private void OnReconnectTick(object? sender, EventArgs e)
    {
        if (_serverOutage)
        {
            var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var remainingMs = _serverOutageByMs - nowMs;
            var seconds = Math.Max(0, (int)Math.Ceiling(remainingMs / 1000.0));
            StatusMessage = remainingMs > 0
                ? $"Spojení se serverem přerušeno, čekám {seconds}s..."
                : "Spojení se serverem je stále přerušeno.";
            if (remainingMs <= 0 && !_isFinished)
            {
                _isFinished = true;
                _ = ReturnToLoginAsync("Spojení se serverem bylo přerušeno, byli jste odhlášeni.");
            }
        }
        else if (_resumeByMs > 0)
        {
            var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var remainingMs = _resumeByMs - nowMs;
            var seconds = Math.Max(0, (int)Math.Ceiling(remainingMs / 1000.0));
            if (_showingPause)
            {
                StatusMessage = remainingMs > 0
                    ? $"Soupeř odpojen, čekám {seconds}s..."
                    : "Čekám na odpověď serveru...";
                if (remainingMs <= 0 && !_isFinished)
                {
                    _ = EndWaitingAfterTimeoutAsync();
                }
            }
            else if (_awaitingReconnect)
            {
                StatusMessage = remainingMs > 0
                    ? "Ztracené spojení. Obnovuji připojení..."
                    : "Spojení se nepodařilo obnovit.";
                if (remainingMs <= 0 && !_isFinished)
                {
                    _isFinished = true;
                    _ = ReturnToLoginAsync();
                }
            }
        }

        if (_awaitingReconnect)
        {
            _ = GameClient.TryReconnectAsync();
        }
    }

    private void SetPaused(bool paused)
    {
        if (_isPaused == paused)
        {
            return;
        }

        _isPaused = paused;
        OnPropertyChanged(nameof(IsBoardEnabled));
    }

    private void StopReconnectLoop()
    {
        if (_reconnectTimer != null)
        {
            _reconnectTimer.Stop();
        }
        _awaitingReconnect = false;
        _resumeByMs = 0;
    }

    private void HandleLocalDisconnect()
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(HandleLocalDisconnect);
            return;
        }

        HandleConnectivityLoss();
    }

    private void HandleConnectivityLoss()
    {
        if (!IsNetworkLikelyAvailable())
        {
            HandleLocalNetworkLoss();
            return;
        }

        HandleServerOffline();
    }

    private static bool IsNetworkLikelyAvailable()
    {
        try
        {
            if (!NetworkInterface.GetIsNetworkAvailable())
            {
                return false;
            }

            /*foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up)
                {
                    continue;
                }
                if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback ||
                    nic.NetworkInterfaceType == NetworkInterfaceType.Tunnel ||
                    nic.NetworkInterfaceType == NetworkInterfaceType.Unknown)
                {
                    continue;
                }

                var props = nic.GetIPProperties();
                if (props.GatewayAddresses.Count == 0)
                {
                    continue;
                }

                var hasUsableAddress = props.UnicastAddresses.Any(addr =>
                    !IPAddress.IsLoopback(addr.Address) &&
                    !addr.Address.IsIPv6LinkLocal &&
                    !addr.Address.ToString().StartsWith("169.254.", StringComparison.Ordinal));

                if (hasUsableAddress)
                {
                    return true;
                }
            }}*/
        }
        catch
        {
            // fall-back na "available", pokud nemůžeme detekovat připojení k sítí
            return true;
        }

        return false;
    }

    private async Task ReturnToLoginAsync(string? reason = null)
    {
        await Task.Delay(TimeSpan.FromSeconds(2));
        _returnToLogin(reason);
    }

    private async Task EndWaitingAfterTimeoutAsync()
    {
        if (_isFinished)
        {
            return;
        }

        _isFinished = true;
        StatusMessage = "Soupeř se nevrátil, vyhráváš.";
        CurrentTurnDisplay = "Ukončeno";
        StopTurnTimer();
        StopReconnectLoop();
        Unsubscribe();
        await Task.Delay(TimeSpan.FromSeconds(2));
        _returnToLobby();
    }

    // Odhlášení z eventů klienta (prevence leaků a double-notifikací).
    private void Unsubscribe()
    {
        GameClient.GameStarted -= OnGameStarted;
        GameClient.GameStateUpdated -= OnGameStateUpdated;
        GameClient.GameEnded -= OnGameEnded;
        GameClient.Disconnected -= OnDisconnected;
        GameClient.GamePaused -= OnGamePaused;
        GameClient.TokenInvalidated -= OnTokenInvalidated;
        GameClient.ServerStatusChanged -= OnServerStatusChanged;
        StopTurnTimer();
        StopReconnectLoop();
    }
}
