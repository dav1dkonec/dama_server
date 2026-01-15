using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using dama_klient_app.Models;
using System.Linq;

namespace dama_klient_app.Services;

/// <summary>
/// UDP klient dle PROTOCOL.md – stará se o korelaci ID, heartbeat a rozesílání serverových push zpráv do UI.
/// </summary>
public class GameClient : IGameClient, IAsyncDisposable
{
    // Parametry připojení a časování dle serveru/protokolu.
    private string _host;
    private int _port;
    private readonly TimeSpan _requestTimeout = TimeSpan.FromSeconds(5);
    private readonly TimeSpan _roomsCollectWindow = TimeSpan.FromMilliseconds(150);
    private readonly TimeSpan _heartbeatInterval = TimeSpan.FromSeconds(5);
    private readonly TimeSpan _disconnectThreshold = TimeSpan.FromSeconds(20);
    private readonly TimeSpan _invalidWindow = TimeSpan.FromSeconds(30);
    private readonly TimeSpan _phaseGraceWindow = TimeSpan.FromSeconds(3);
    private readonly TimeSpan _resyncCooldown = TimeSpan.FromSeconds(3);
    private readonly TimeSpan _serverOfflineThreshold = TimeSpan.FromSeconds(12);

    // UDP socket a tabulka rozpracovaných požadavků (dle ID).
    private UdpClient _udpClient;
    private readonly ConcurrentDictionary<int, PendingRequest> _pending = new();
    private readonly ConcurrentDictionary<int, GameStartInfo> _lastGameStarts = new();
    private readonly ConcurrentDictionary<int, GameStateInfo> _lastGameStates = new();

    // Řízení životního cyklu klienta (rušení smyček, serializace ConnectAsync).
    private readonly CancellationTokenSource _cts = new();
    private readonly object _connectLock = new();

    // Stavové proměnné – generování ID, připojení a běžící smyčky.
    private int _nextId = 1;
    private bool _isConnected;
    private Task? _receiveLoopTask;
    private Task? _heartbeatTask;
    private int _turnTimeoutMs = 60000;
    private string _token = string.Empty;
    private readonly object _reconnectLock = new();
    private bool _reconnecting;
    private DateTimeOffset _lastReceiveAt = DateTimeOffset.UtcNow;
    private int _disconnectNotified;
    private int _invalidServerCount;
    private DateTimeOffset _invalidWindowStart;
    private DateTimeOffset _lastResyncRequest;
    private ClientPhase _phase = ClientPhase.LoggedOut;
    private int? _activeRoomId;
    private DateTimeOffset _phaseChangedAt = DateTimeOffset.UtcNow;
    private ServerStatus _serverStatus = ServerStatus.Unknown;
    private bool _sessionActive;
    private string _connectedHost = string.Empty;
    private int _connectedPort;

    public GameClient(string host = "127.0.0.1", int port = 5000)
    {
        _host = host;
        _port = port;
        _udpClient = new UdpClient();
    }

    public void ConfigureEndpoint(string host, int port)
    {
        if (string.IsNullOrWhiteSpace(host) || port <= 0 || port > 65535)
        {
            return;
        }

        _host = host;
        _port = port;
    }

    public bool IsConnected => _isConnected;
    public int TurnTimeoutMs => _turnTimeoutMs;
    public string Token => _token;
    public ServerStatus ServerStatus => _serverStatus;

    public event EventHandler? Disconnected;
    public event EventHandler<ServerStatus>? ServerStatusChanged;
    public event EventHandler? TokenInvalidated;
    public event EventHandler<IReadOnlyList<RoomInfo>>? LobbyUpdated;
    public event EventHandler<GameStartInfo>? GameStarted;
    public event EventHandler<GameStateInfo>? GameStateUpdated;
    public event EventHandler<GameEndInfo>? GameEnded;
    public event EventHandler<(int RoomId, long ResumeBy)>? GamePaused;

    public bool TryGetLastGameStart(int roomId, out GameStartInfo info) => _lastGameStarts.TryGetValue(roomId, out info!);

    public bool TryGetLastGameState(int roomId, out GameStateInfo info) => _lastGameStates.TryGetValue(roomId, out info!);

    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        // Naváže socket a spustí přijímací/heartbeat smyčky.
        lock (_connectLock)
        {
            var endpointChanged = !string.Equals(_connectedHost, _host, StringComparison.Ordinal) ||
                                  _connectedPort != _port;
            if (!_isConnected || endpointChanged)
            {
                RebindSocket();
            }

            _isConnected = true;
            if (_receiveLoopTask == null || _receiveLoopTask.IsCompleted || endpointChanged)
            {
                _receiveLoopTask = Task.Run(() => ReceiveLoopAsync(_cts.Token, _udpClient));
            }
            if (_heartbeatTask == null || _heartbeatTask.IsCompleted)
            {
                _heartbeatTask = Task.Run(() => HeartbeatLoopAsync(_cts.Token));
            }
        }

        await Task.CompletedTask;
    }

    public async Task TryReconnectAsync(CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(_token))
        {
            return;
        }

        lock (_reconnectLock)
        {
            if (_reconnecting) return;
            _reconnecting = true;
        }

        try
        {
            var id = NextId();
            var pending = new PendingRequest(RequestKind.Single, new[] { "RECONNECT_OK", "ERROR" });
            _pending[id] = pending;
            var payload = $"{id};RECONNECT;{_token}\n";
            await SendAsync(payload, cancellationToken);
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(_requestTimeout, _cts.Token);
                }
                catch (TaskCanceledException)
                {
                    // shutdown
                }
                _pending.TryRemove(id, out _);
            }, _cts.Token);
        }
        finally
        {
            lock (_reconnectLock)
            {
                _reconnecting = false;
            }
        }
    }

    public async Task LoginAsync(string nickname, CancellationToken cancellationToken = default)
    {
        // LOGIN → čekáme na LOGIN_OK nebo ERROR.
        var id = NextId();
        var pending = new PendingRequest(RequestKind.Single, new[] { "LOGIN_OK", "ERROR" });
        _pending[id] = pending;
        await SendAsync($"{id};LOGIN;{nickname}\n", cancellationToken);
        var resp = await AwaitResponseAsync(id, pending, cancellationToken);
        if (resp.Type == "ERROR")
        {
            throw new InvalidOperationException(resp.Raw);
        }
        if (resp.Params.TryGetValue("token", out var tkn))
        {
            ClearSessionCaches();
            _token = tkn;
            SetPhase(ClientPhase.Lobby);
            _sessionActive = true;
        }
    }

    public async Task<bool> ProbeServerAsync(CancellationToken cancellationToken = default)
    {
        var id = NextId();
        var pending = new PendingRequest(RequestKind.Single, new[] { "PONG", "ERROR" });
        _pending[id] = pending;
        try
        {
            await SendAsync($"{id};PING\n", cancellationToken);
            var resp = await AwaitResponseAsync(id, pending, cancellationToken);
            return resp.Type == "PONG";
        }
        catch
        {
            return false;
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    public async Task SendByeAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var id = NextId();
            await SendAsync($"{id};BYE\n", cancellationToken);
        }
        catch
        {
            // best-effort
        }
        _token = string.Empty;
        ClearSessionCaches();
        SetPhase(ClientPhase.LoggedOut);
        _sessionActive = false;
    }

    public async Task<IReadOnlyList<RoomInfo>> GetRoomsAsync(CancellationToken cancellationToken = default)
    {
        // LIST_ROOMS → sbíráme ROOM/ROOMS_EMPTY v krátkém okně, pak vracíme snapshot.
        var id = NextId();
        var pending = new PendingRequest(RequestKind.Rooms, new[] { "ROOM", "ROOMS_EMPTY", "ERROR" });
        _pending[id] = pending;
        await SendAsync($"{id};LIST_ROOMS\n", cancellationToken);
        var rooms = await pending.RoomsTask(_roomsCollectWindow, cancellationToken);
        _pending.TryRemove(id, out _);
        LobbyUpdated?.Invoke(this, rooms);
        return rooms;
    }

    public async Task<RoomInfo> CreateRoomAsync(string name, CancellationToken cancellationToken = default)
    {
        // CREATE_ROOM → čeká na CREATE_ROOM_OK nebo ERROR.
        var id = NextId();
        var pending = new PendingRequest(RequestKind.Single, new[] { "CREATE_ROOM_OK", "ERROR" });
        _pending[id] = pending;
        await SendAsync($"{id};CREATE_ROOM;{name}\n", cancellationToken);
        var resp = await AwaitResponseAsync(id, pending, cancellationToken);
        if (resp.Type == "ERROR")
        {
            throw new InvalidOperationException(resp.Raw);
        }

        var roomId = resp.Params.TryGetValue("room", out var val) ? val : Guid.NewGuid().ToString("N");
        var roomName = resp.Params.TryGetValue("name", out var n) ? n : name;
        var room = new RoomInfo(roomId, roomName, 0, 2);
        return room;
    }

    public async Task JoinRoomAsync(string roomId, CancellationToken cancellationToken = default)
    {
        // JOIN_ROOM → čeká na JOIN_ROOM_OK nebo ERROR (GAME_START a GAME_STATE přijdou jako push).
        var parsedRoomId = TryParseRoomId(roomId);
        SetPhase(ClientPhase.InRoom, parsedRoomId);
        try
        {
            var id = NextId();
            var pending = new PendingRequest(RequestKind.Single, new[] { "JOIN_ROOM_OK", "ERROR" });
            _pending[id] = pending;
            await SendAsync($"{id};JOIN_ROOM;{roomId}\n", cancellationToken);
            var resp = await AwaitResponseAsync(id, pending, cancellationToken);
            if (resp.Type == "ERROR")
            {
                if (_phase == ClientPhase.InRoom && (!_activeRoomId.HasValue || _activeRoomId == parsedRoomId))
                {
                    SetPhase(ClientPhase.Lobby);
                }
                throw new InvalidOperationException(resp.Raw);
            }
        }
        catch
        {
            if (_phase == ClientPhase.InRoom && (!_activeRoomId.HasValue || _activeRoomId == parsedRoomId))
            {
                SetPhase(ClientPhase.Lobby);
            }
            throw;
        }
    }

    public async Task LeaveRoomAsync(int roomId, CancellationToken cancellationToken = default)
    {
        // LEAVE_ROOM → potvrzení LEAVE_ROOM_OK, případné GAME_END přijde jako push soupeři.
        var id = NextId();
        var pending = new PendingRequest(RequestKind.Single, new[] { "LEAVE_ROOM_OK", "ERROR" });
        _pending[id] = pending;
        await SendAsync($"{id};LEAVE_ROOM;{roomId}\n", cancellationToken);
        var resp = await AwaitResponseAsync(id, pending, cancellationToken);
        if (resp.Type == "ERROR")
        {
            throw new InvalidOperationException(resp.Raw);
        }
        if (!_activeRoomId.HasValue || _activeRoomId == roomId)
        {
            SetPhase(ClientPhase.Lobby);
        }
    }

    public void ClearRoomCache(int roomId)
    {
        _lastGameStarts.TryRemove(roomId, out _);
        _lastGameStates.TryRemove(roomId, out _);
        if (_activeRoomId.HasValue && _activeRoomId.Value == roomId)
        {
            SetPhase(ClientPhase.Lobby);
        }
    }

    public async Task SendMoveAsync(Move move, CancellationToken cancellationToken = default)
    {
        // MOVE → server odpoví ERROR nebo pošle GAME_STATE/GAME_END push všem v místnosti; držíme pending kvůli případnému ERROR.
        var id = NextId();
        var pending = new PendingRequest(RequestKind.Single, new[] { "GAME_STATE", "GAME_END", "ERROR" });
        _pending[id] = pending;
        var payload = $"{id};MOVE;{move.RoomId};{move.From.Row};{move.From.Col};{move.To.Row};{move.To.Col}\n";
        await SendAsync(payload, cancellationToken);
        var resp = await AwaitResponseAsync(id, pending, cancellationToken);
        if (resp.Type == "ERROR")
        {
            throw new InvalidOperationException(resp.Raw);
        }
    }

    public async Task<LegalMovesResult> GetLegalMovesAsync(int roomId, int row, int col, CancellationToken cancellationToken = default)
    {
        // LEGAL_MOVES → odpověď LEGAL_MOVES nebo ERROR, obsahuje seznam cílových polí a mustCapture.
        var id = NextId();
        var pending = new PendingRequest(RequestKind.Single, new[] { "LEGAL_MOVES", "ERROR" });
        _pending[id] = pending;
        await SendAsync($"{id};LEGAL_MOVES;{roomId};{row};{col}\n", cancellationToken);
        var resp = await AwaitResponseAsync(id, pending, cancellationToken);
        if (resp.Type == "ERROR")
        {
            throw new InvalidOperationException(resp.Raw);
        }

        var fromValue = resp.Params.TryGetValue("from", out var fromStr) ? fromStr : $"{row},{col}";
        if (!TryParseCoords(fromValue, out var fromRow, out var fromCol))
        {
            fromRow = 0;
            fromCol = 0;
        }
        var from = (fromRow, fromCol);
        var dests = new List<(int Row, int Col)>();
        if (resp.Params.TryGetValue("to", out var toStr) && !string.IsNullOrWhiteSpace(toStr))
        {
            var parts = toStr.Split('|', StringSplitOptions.RemoveEmptyEntries);
            foreach (var part in parts)
            {
                if (!TryParseCoords(part, out var toRow, out var toCol))
                {
                    toRow = 0;
                    toCol = 0;
                }
                dests.Add((toRow, toCol));
            }
        }
        var mustCapture = resp.Params.TryGetValue("mustCapture", out var mc) && mc == "1";
        return new LegalMovesResult(roomId, from, dests, mustCapture);
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _udpClient.Dispose();
        if (_receiveLoopTask != null) await _receiveLoopTask;
        if (_heartbeatTask != null) await _heartbeatTask;
    }

    // --- Pomocné metody ---

    private int NextId() => Interlocked.Increment(ref _nextId);

    private void RebindSocket()
    {
        var old = _udpClient;
        _udpClient = new UdpClient();
        _udpClient.Connect(_host, _port);
        _connectedHost = _host;
        _connectedPort = _port;
        _lastReceiveAt = DateTimeOffset.UtcNow;
        Interlocked.Exchange(ref _disconnectNotified, 0);
        try
        {
            old.Dispose();
        }
        catch
        {
            // best effort
        }
    }

    private async Task SendAsync(string payload, CancellationToken cancellationToken)
    {
        var buffer = Encoding.UTF8.GetBytes(payload);
        await _udpClient.SendAsync(buffer, buffer.Length);
        AppServices.Logger.Info($"TX: {payload.Trim()}");
    }

    private async Task ReceiveLoopAsync(CancellationToken cancellationToken, UdpClient udpClient)
    {
        // Nekonečná smyčka na příjem datagramů, parsuje zprávy a buď je přiřadí pending žádosti (dle ID), nebo zavolá push eventy.
        while (!cancellationToken.IsCancellationRequested)
        {
            if (!ReferenceEquals(udpClient, _udpClient))
            {
                break;
            }
            UdpReceiveResult result;
            try
            {
                result = await udpClient.ReceiveAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                continue;
            }
            catch (SocketException ex) when (ex.SocketErrorCode == SocketError.ConnectionReset ||
                                             ex.SocketErrorCode == SocketError.ConnectionRefused)
            {
                // Ignore ICMP port unreachable when server isn't up yet.
                NotifyDisconnected();
                UpdateServerStatus(ServerStatus.Offline);
                continue;
            }
            catch (Exception)
            {
                NotifyDisconnected();
                break;
            }

            _lastReceiveAt = DateTimeOffset.UtcNow;
            Interlocked.Exchange(ref _disconnectNotified, 0);
            UpdateServerStatus(ServerStatus.Online);

            if (HasBinaryPayload(result.Buffer))
            {
                RegisterInvalidServerMessage("BINARY_DATA");
                continue;
            }

            var line = Encoding.UTF8.GetString(result.Buffer).TrimEnd('\n', '\r');
            AppServices.Logger.Info($"RX: {line}");
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var msg = ParseMessage(line);
            if (msg == null)
            {
                RegisterInvalidServerMessage("PARSE_ERROR");
                continue;
            }

            if (!IsServerMessageValid(msg))
            {
                if (msg.Type == "GAME_STATE")
                {
                    RequestGameStateResyncIfNeeded("INVALID_GAME_STATE");
                }
                RegisterInvalidServerMessage("INVALID_MESSAGE");
                continue;
            }

            if (!IsMessageAllowedByPhase(msg))
            {
                RegisterInvalidServerMessage("UNEXPECTED_PHASE");
                continue;
            }

            var hasPending = _pending.TryGetValue(msg.Id, out var pending);
            var isPushType = IsPushType(msg.Type) || (msg.Type == "ERROR" && msg.Id == 0);
            var allowedForPending = hasPending && pending!.IsAllowedResponseType(msg.Type);

            if (!hasPending && !isPushType)
            {
                RegisterInvalidServerMessage("UNEXPECTED_ID");
                continue;
            }
            if (hasPending && !allowedForPending && !isPushType)
            {
                RegisterInvalidServerMessage("UNEXPECTED_RESPONSE");
                continue;
            }

            DispatchPush(msg);

            if (hasPending && allowedForPending)
            {
                pending!.Handle(msg);
                if (pending.IsTerminal)
                {
                    _pending.TryRemove(msg.Id, out _);
                }
            }
        }
    }

    private void DispatchPush(ParsedMessage msg)
    {
        // Zpracování push zpráv, které nemají pending čekání (GAME_START/STATE/END). ROOM/ROOMS_EMPTY řeší pending LIST_ROOMS.
        switch (msg.Type)
        {
            case "ROOM":
            case "ROOMS_EMPTY":
                // ROOM/ROOMS_EMPTY se řeší přes pending při LIST_ROOMS
                break;
            case "GAME_START":
                if (msg.Params.TryGetValue("room", out var roomIdStr) &&
                    int.TryParse(roomIdStr, out var roomId) &&
                    msg.Params.TryGetValue("you", out var role))
                {
                    if (!_activeRoomId.HasValue || _activeRoomId == roomId)
                    {
                        SetPhase(ClientPhase.InGame, roomId);
                    }
                    msg.Params.TryGetValue("opponent", out var opponent);
                    var start = new GameStartInfo(roomId, role, opponent ?? string.Empty);
                    _lastGameStarts[roomId] = start;
                    GameStarted?.Invoke(this, start);
                }
                break;
            case "CONFIG":
                if (msg.Params.TryGetValue("turnTimeoutMs", out var ttStr) &&
                    int.TryParse(ttStr, out var tt) &&
                    tt > 0)
                {
                    _turnTimeoutMs = tt;
                }
                _ = SendAsync($"{msg.Id};CONFIG_ACK\n", _cts.Token);
                break;
            case "GAME_STATE":
                if (msg.Params.TryGetValue("room", out var rStr) &&
                    int.TryParse(rStr, out var rId) &&
                    msg.Params.TryGetValue("turn", out var turn) &&
                    msg.Params.TryGetValue("board", out var board))
                {
                    if (!_activeRoomId.HasValue || _activeRoomId == rId)
                    {
                        SetPhase(ClientPhase.InGame, rId);
                    }
                    msg.Params.TryGetValue("remainingMs", out var remStr);
                    int remMs = 0;
                    int.TryParse(remStr, out remMs);
                    int? lockRow = null;
                    int? lockCol = null;
                    if (msg.Params.TryGetValue("lock", out var lockStr) &&
                        TryParseCoords(lockStr, out var lr, out var lc))
                    {
                        lockRow = lr;
                        lockCol = lc;
                    }
                    var state = new GameStateInfo(rId, turn, board, remMs, lockRow, lockCol);
                    _lastGameStates[rId] = state;
                    GameStateUpdated?.Invoke(this, state);
                }
                break;
            case "GAME_END":
                if (msg.Params.TryGetValue("room", out var reStr) &&
                    int.TryParse(reStr, out var reId))
                {
                    if (!_activeRoomId.HasValue || _activeRoomId == reId)
                    {
                        SetPhase(ClientPhase.Lobby);
                    }
                    msg.Params.TryGetValue("reason", out var reason);
                    msg.Params.TryGetValue("winner", out var winner);
                    GameEnded?.Invoke(this, new GameEndInfo(reId, reason ?? string.Empty, winner ?? "NONE"));
                    _lastGameStarts.TryRemove(reId, out _);
                    _lastGameStates.TryRemove(reId, out _);
                }
                break;
            case "GAME_PAUSED":
                if (msg.Params.TryGetValue("room", out var prStr) &&
                    int.TryParse(prStr, out var prId) &&
                    msg.Params.TryGetValue("resumeBy", out var resumeStr) &&
                    long.TryParse(resumeStr, out var resumeBy))
                {
                    if (!_activeRoomId.HasValue || _activeRoomId == prId)
                    {
                        SetPhase(ClientPhase.InGame, prId);
                    }
                    GamePaused?.Invoke(this, (prId, resumeBy));
                }
                break;
            case "ERROR":
                if (msg.Raw.Contains("TOKEN_NOT_FOUND", StringComparison.OrdinalIgnoreCase) ||
                    msg.Raw.Contains("TOKEN_EXPIRED", StringComparison.OrdinalIgnoreCase) ||
                    msg.Raw.Contains("NOT_LOGGED_IN", StringComparison.OrdinalIgnoreCase))
                {
                    AppServices.Logger.Info("TOKEN invalid, clearing token");
                    _token = string.Empty;
                    ClearSessionCaches();
                    SetPhase(ClientPhase.LoggedOut);
                    _sessionActive = false;
                    TokenInvalidated?.Invoke(this, EventArgs.Empty);
                }
                break;
            case "RECONNECT_OK":
                break;
        }
    }

    private async Task<ParsedMessage> AwaitResponseAsync(int id, PendingRequest pending, CancellationToken cancellationToken)
    {
        // Čeká na odpověď nebo timeout, uklízí pending z tabulky.
        var msgTask = pending.SingleTask;
        var completed = await Task.WhenAny(msgTask, Task.Delay(_requestTimeout, cancellationToken));
        _pending.TryRemove(id, out _);
        if (completed != msgTask)
        {
            throw new TimeoutException("No response from server.");
        }
        return await msgTask;
    }

    private async Task HeartbeatLoopAsync(CancellationToken cancellationToken)
    {
        // Server po ~30s bez provozu odpojuje; posíláme PING každých 5s.
        while (!cancellationToken.IsCancellationRequested)
        {
            await Task.Delay(_heartbeatInterval, cancellationToken);
            try
            {
                var elapsed = DateTimeOffset.UtcNow - _lastReceiveAt;
                if (elapsed > _serverOfflineThreshold)
                {
                    UpdateServerStatus(ServerStatus.Offline);
                }
                if (elapsed > _disconnectThreshold)
                {
                    NotifyDisconnected();
                }
                if (!_sessionActive)
                {
                    continue;
                }
                var id = NextId();
                await SendAsync($"{id};PING\n", cancellationToken);
            }
            catch
            {
                // ignoruje heartbear errory; receive loop se odpojí
            }
        }
    }

    private void NotifyDisconnected()
    {
        if (Interlocked.Exchange(ref _disconnectNotified, 1) == 1)
        {
            return;
        }
        Disconnected?.Invoke(this, EventArgs.Empty);
    }

    private void RegisterInvalidServerMessage(string reason)
    {
        var now = DateTimeOffset.UtcNow;
        if (_invalidWindowStart == default || now - _invalidWindowStart > _invalidWindow)
        {
            _invalidServerCount = 0;
            _invalidWindowStart = now;
        }

        _invalidServerCount++;
        AppServices.Logger.Info($"Invalid server message ({reason}), count={_invalidServerCount}.");

        if (_invalidServerCount >= 3)
        {
            AppServices.Logger.Error("Invalid server message limit reached, dropping session.");
            _token = string.Empty;
            ClearSessionCaches();
            SetPhase(ClientPhase.LoggedOut);
            TokenInvalidated?.Invoke(this, EventArgs.Empty);
        }
    }

    private void UpdateServerStatus(ServerStatus status)
    {
        if (_serverStatus == status)
        {
            return;
        }

        _serverStatus = status;
        ServerStatusChanged?.Invoke(this, status);
    }

    private void SetPhase(ClientPhase phase, int? roomId = null)
    {
        var phaseChanged = _phase != phase;
        var roomChanged = roomId.HasValue && (!_activeRoomId.HasValue || _activeRoomId.Value != roomId.Value);
        if (phaseChanged || roomChanged)
        {
            _phaseChangedAt = DateTimeOffset.UtcNow;
        }
        _phase = phase;
        if (roomId.HasValue)
        {
            _activeRoomId = roomId.Value;
        }
        else if (phase == ClientPhase.LoggedOut || phase == ClientPhase.Lobby)
        {
            _activeRoomId = null;
        }
    }

    private bool IsWithinPhaseGraceWindow()
    {
        return DateTimeOffset.UtcNow - _phaseChangedAt <= _phaseGraceWindow;
    }

    private bool IsMessageAllowedByPhase(ParsedMessage msg)
    {
        if (IsWithinPhaseGraceWindow())
        {
            return true;
        }

        switch (msg.Type)
        {
            case "ROOM":
            case "ROOMS_EMPTY":
                return _phase == ClientPhase.Lobby;
            case "GAME_START":
            case "GAME_STATE":
            case "GAME_END":
            case "GAME_PAUSED":
                if (_phase != ClientPhase.InRoom && _phase != ClientPhase.InGame)
                {
                    return false;
                }
                if (msg.Params.TryGetValue("room", out var roomStr) &&
                    int.TryParse(roomStr, out var roomId) &&
                    _activeRoomId.HasValue &&
                    _activeRoomId.Value != roomId)
                {
                    return false;
                }
                return true;
            default:
                return true;
        }
    }

    private void RequestGameStateResyncIfNeeded(string reason)
    {
        if (_phase != ClientPhase.InRoom && _phase != ClientPhase.InGame)
        {
            return;
        }
        if (string.IsNullOrWhiteSpace(_token))
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        if (now - _lastResyncRequest < _resyncCooldown)
        {
            return;
        }

        _lastResyncRequest = now;
        AppServices.Logger.Info($"Requesting game state resync ({reason}).");
        _ = TryReconnectAsync(_cts.Token);
    }

    private static bool IsPushType(string type)
    {
        return type is "CONFIG" or "GAME_START" or "GAME_STATE" or "GAME_END" or "GAME_PAUSED" or
               "PONG" or "RECONNECT_OK" or "BYE_OK" or "ROOM" or "ROOMS_EMPTY";
    }

    private static int? TryParseRoomId(string roomId)
    {
        return int.TryParse(roomId, out var value) ? value : null;
    }

    private static bool HasBinaryPayload(byte[] buffer)
    {
        for (var i = 0; i < buffer.Length; i++)
        {
            var b = buffer[i];
            if (b == 0x09 || b == 0x0A || b == 0x0D)
            {
                continue;
            }
            if (b < 0x20 || b == 0x7F)
            {
                return true;
            }
        }
        return false;
    }

    private static bool TryParseCoords(string value, out int row, out int col)
    {
        row = 0;
        col = 0;
        var parts = value.Split(',');
        return parts.Length == 2 &&
               int.TryParse(parts[0], out row) &&
               int.TryParse(parts[1], out col);
    }

    private static bool IsServerMessageValid(ParsedMessage msg)
    {
        switch (msg.Type)
        {
            case "ROOMS_EMPTY":
            case "PONG":
            case "RECONNECT_OK":
            case "BYE_OK":
                return true;
            case "ROOM":
                return msg.Params.ContainsKey("id") && msg.Params.ContainsKey("name");
            case "LOGIN_OK":
                return msg.Params.ContainsKey("token");
            case "CREATE_ROOM_OK":
                return msg.Params.ContainsKey("room") && msg.Params.ContainsKey("name");
            case "JOIN_ROOM_OK":
                return msg.Params.ContainsKey("room") && msg.Params.ContainsKey("players");
            case "LEAVE_ROOM_OK":
                return msg.Params.ContainsKey("room");
            case "CONFIG":
                return msg.Params.TryGetValue("turnTimeoutMs", out var ttStr) && int.TryParse(ttStr, out _);
            case "GAME_START":
                return msg.Params.ContainsKey("room") && msg.Params.ContainsKey("you");
            case "GAME_STATE":
                if (!msg.Params.TryGetValue("room", out var roomStr) || !int.TryParse(roomStr, out _)) return false;
                if (!msg.Params.TryGetValue("turn", out _)) return false;
                if (!msg.Params.TryGetValue("board", out var board) || board.Length != 64) return false;
                if (msg.Params.TryGetValue("remainingMs", out var remStr) && !int.TryParse(remStr, out _)) return false;
                if (msg.Params.TryGetValue("lock", out var lockStr) && !TryParseCoords(lockStr, out _, out _)) return false;
                return true;
            case "GAME_END":
                return msg.Params.ContainsKey("room");
            case "GAME_PAUSED":
                return msg.Params.TryGetValue("room", out var prStr) && int.TryParse(prStr, out _) &&
                       msg.Params.TryGetValue("resumeBy", out var resumeStr) && long.TryParse(resumeStr, out _);
            case "LEGAL_MOVES":
                if (!msg.Params.TryGetValue("room", out var lmRoom) || !int.TryParse(lmRoom, out _)) return false;
                if (!msg.Params.TryGetValue("from", out var fromStr) || !TryParseCoords(fromStr, out _, out _)) return false;
                if (!msg.Params.TryGetValue("mustCapture", out var mc) || (mc != "0" && mc != "1")) return false;
                if (msg.Params.TryGetValue("to", out var toStr) && !string.IsNullOrWhiteSpace(toStr))
                {
                    var parts = toStr.Split('|', StringSplitOptions.RemoveEmptyEntries);
                    foreach (var part in parts)
                    {
                        if (!TryParseCoords(part, out _, out _)) return false;
                    }
                }
                return true;
            case "ERROR":
                return msg.RawParams.Count > 0;
            default:
                return false;
        }
    }

    private void ClearSessionCaches()
    {
        _lastGameStarts.Clear();
        _lastGameStates.Clear();
    }


    private static ParsedMessage? ParseMessage(string line)
    {
        // Rozparsuje řádek "ID;TYPE;param;key=val;..." na strukturu s ID, typem, prostými a pojmenovanými parametry.
        var parts = line.Split(';');
        if (parts.Length < 2) return null;
        if (!int.TryParse(parts[0], out var id)) return null;
        var type = parts[1];
        var rawParams = new List<string>();
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        for (int i = 2; i < parts.Length; i++)
        {
            var part = parts[i];
            var kv = part.Split('=', 2);
            if (kv.Length == 2)
            {
                dict[kv[0]] = kv[1];
            }
            else
            {
                rawParams.Add(part);
            }
        }

        return new ParsedMessage(id, type, rawParams, dict, line);
    }

    private static RoomInfo ToRoomInfo(ParsedMessage msg)
    {
        // Převod ROOM řádku na RoomInfo (status aktuálně neuchováváme).
        msg.Params.TryGetValue("id", out var idStr);
        msg.Params.TryGetValue("name", out var name);
        msg.Params.TryGetValue("players", out var playersStr);
        msg.Params.TryGetValue("status", out _);
        int.TryParse(playersStr, out var count);
        return new RoomInfo(idStr ?? Guid.NewGuid().ToString("N"), name ?? "Room", count, 2);
    }

    // --- Pomocné vnitřní struktury ---

    private class ParsedMessage
    {
        public ParsedMessage(int id, string type, IReadOnlyList<string> raw, IReadOnlyDictionary<string, string> @params, string rawLine)
        {
            Id = id;
            Type = type;
            RawParams = raw;
            Params = @params;
            Raw = rawLine;
        }

        public int Id { get; }
        public string Type { get; }
        public IReadOnlyList<string> RawParams { get; }
        public IReadOnlyDictionary<string, string> Params { get; }
        public string Raw { get; }
    }

    private enum ClientPhase
    {
        LoggedOut,
        Lobby,
        InRoom,
        InGame
    }

    private enum RequestKind
    {
        Single,
        Rooms
    }

    private class PendingRequest
    {
        private readonly TaskCompletionSource<ParsedMessage> _singleTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly List<RoomInfo> _rooms = new();
        private readonly object _lock = new();
        private readonly HashSet<string> _allowedTypes;
        private bool _completed;

        public PendingRequest(RequestKind kind, IEnumerable<string> allowedTypes)
        {
            Kind = kind;
            _allowedTypes = new HashSet<string>(allowedTypes, StringComparer.OrdinalIgnoreCase);
        }

        public RequestKind Kind { get; }
        public Task<ParsedMessage> SingleTask => _singleTcs.Task;
        public bool IsTerminal => _completed;
        public bool IsAllowedResponseType(string type) => _allowedTypes.Contains(type);

        public void Handle(ParsedMessage msg)
        {
            // Single: hned ukončí TCS; Rooms: sbírá ROOM, uzavírá se při ROOMS_EMPTY nebo po timeout okně.
            if (Kind == RequestKind.Single)
            {
                if (!_singleTcs.Task.IsCompleted)
                {
                    _singleTcs.TrySetResult(msg);
                    _completed = true;
                }
                return;
            }

            if (Kind == RequestKind.Rooms)
            {
                if (msg.Type == "ROOMS_EMPTY")
                {
                    CompleteRooms();
                }
                else if (msg.Type == "ROOM")
                {
                    _rooms.Add(GameClient.ToRoomInfo(msg));
                }
            }
        }

        public async Task<IReadOnlyList<RoomInfo>> RoomsTask(TimeSpan window, CancellationToken cancellationToken)
        {
            // Krátké čekání na všechny ROOM zprávy, poté vrací snapshot.
            try
            {
                await Task.Delay(window, cancellationToken);
            }
            catch (TaskCanceledException)
            {
                // ignorovat
            }
            CompleteRooms();
            return _rooms.ToList();
        }

        private void CompleteRooms()
        {
            lock (_lock)
            {
                if (_completed) return;
                _completed = true;
            }
        }
    }
}
