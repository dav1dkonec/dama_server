using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using dama_klient_app.Models;

namespace dama_klient_app.Services;

/// <summary>
/// UDP klient dle PROTOCOL.md – stará se o korelaci ID, heartbeat a rozesílání serverových push zpráv do UI.
/// </summary>
public class GameClient : IGameClient, IAsyncDisposable
{
    // Parametry připojení a časování dle serveru/protokolu.
    private readonly string _host;
    private readonly int _port;
    private readonly TimeSpan _requestTimeout = TimeSpan.FromSeconds(3);
    private readonly TimeSpan _roomsCollectWindow = TimeSpan.FromMilliseconds(150);
    private readonly TimeSpan _heartbeatInterval = TimeSpan.FromSeconds(10);

    // UDP socket a tabulka rozpracovaných požadavků (dle ID).
    private readonly UdpClient _udpClient;
    private readonly ConcurrentDictionary<int, PendingRequest> _pending = new();

    // Řízení životního cyklu klienta (rušení smyček, serializace ConnectAsync).
    private readonly CancellationTokenSource _cts = new();
    private readonly object _connectLock = new();

    // Stavové proměnné – generování ID, připojení a běžící smyčky.
    private int _nextId = 1;
    private bool _isConnected;
    private Task? _receiveLoopTask;
    private Task? _heartbeatTask;

    public GameClient(string host = "127.0.0.1", int port = 5000)
    {
        _host = host;
        _port = port;
        _udpClient = new UdpClient();
    }

    public bool IsConnected => _isConnected;

    public event EventHandler? Disconnected;
    public event EventHandler<IReadOnlyList<RoomInfo>>? LobbyUpdated;
    public event EventHandler<GameStartInfo>? GameStarted;
    public event EventHandler<GameStateInfo>? GameStateUpdated;
    public event EventHandler<GameEndInfo>? GameEnded;

    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        // Naváže socket a spustí přijímací/heartbeat smyčky.
        lock (_connectLock)
        {
            if (_isConnected)
            {
                return;
            }
            _isConnected = true;
        }

        _udpClient.Connect(_host, _port);
        _receiveLoopTask = Task.Run(() => ReceiveLoopAsync(_cts.Token));
        _heartbeatTask = Task.Run(() => HeartbeatLoopAsync(_cts.Token));
        await Task.CompletedTask;
    }

    public async Task LoginAsync(string nickname, CancellationToken cancellationToken = default)
    {
        // LOGIN → čekáme na LOGIN_OK nebo ERROR.
        var id = NextId();
        var pending = new PendingRequest(RequestKind.Single);
        _pending[id] = pending;
        await SendAsync($"{id};LOGIN;{nickname}\n", cancellationToken);
        var resp = await AwaitResponseAsync(id, pending, cancellationToken);
        if (resp.Type == "ERROR")
        {
            throw new InvalidOperationException(resp.Raw);
        }
    }

    public async Task<IReadOnlyList<RoomInfo>> GetRoomsAsync(CancellationToken cancellationToken = default)
    {
        // LIST_ROOMS → sbíráme ROOM/ROOMS_EMPTY v krátkém okně, pak vracíme snapshot.
        var id = NextId();
        var pending = new PendingRequest(RequestKind.Rooms);
        _pending[id] = pending;
        await SendAsync($"{id};LIST_ROOMS\n", cancellationToken);
        var rooms = await pending.RoomsTask(_roomsCollectWindow, cancellationToken);
        _pending.TryRemove(id, out _);
        LobbyUpdated?.Invoke(this, rooms);
        return rooms;
    }

    public async Task<RoomInfo> CreateRoomAsync(string name, CancellationToken cancellationToken = default)
    {
        // CREATE_ROOM → čekáme na CREATE_ROOM_OK nebo ERROR.
        var id = NextId();
        var pending = new PendingRequest(RequestKind.Single);
        _pending[id] = pending;
        await SendAsync($"{id};CREATE_ROOM;{name}\n", cancellationToken);
        var resp = await AwaitResponseAsync(id, pending, cancellationToken);
        if (resp.Type == "ERROR")
        {
            throw new InvalidOperationException(resp.Raw);
        }

        var roomId = resp.Params.TryGetValue("room", out var val) ? val : Guid.NewGuid().ToString("N");
        var room = new RoomInfo(roomId, name, 1, 2);
        return room;
    }

    public async Task JoinRoomAsync(string roomId, CancellationToken cancellationToken = default)
    {
        // JOIN_ROOM → čekáme na JOIN_ROOM_OK nebo ERROR (GAME_START a GAME_STATE přijdou jako push).
        var id = NextId();
        var pending = new PendingRequest(RequestKind.Single);
        _pending[id] = pending;
        await SendAsync($"{id};JOIN_ROOM;{roomId}\n", cancellationToken);
        var resp = await AwaitResponseAsync(id, pending, cancellationToken);
        if (resp.Type == "ERROR")
        {
            throw new InvalidOperationException(resp.Raw);
        }
    }

    public async Task LeaveRoomAsync(int roomId, CancellationToken cancellationToken = default)
    {
        // LEAVE_ROOM → potvrzení LEAVE_ROOM_OK, případné GAME_END přijde jako push soupeři.
        var id = NextId();
        var pending = new PendingRequest(RequestKind.Single);
        _pending[id] = pending;
        await SendAsync($"{id};LEAVE_ROOM;{roomId}\n", cancellationToken);
        var resp = await AwaitResponseAsync(id, pending, cancellationToken);
        if (resp.Type == "ERROR")
        {
            throw new InvalidOperationException(resp.Raw);
        }
    }

    public async Task SendMoveAsync(Move move, CancellationToken cancellationToken = default)
    {
        // MOVE → server odpoví ERROR nebo pošle GAME_STATE/GAME_END push všem v místnosti; držíme pending kvůli případnému ERROR.
        var id = NextId();
        var pending = new PendingRequest(RequestKind.Single);
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
        var pending = new PendingRequest(RequestKind.Single);
        _pending[id] = pending;
        await SendAsync($"{id};LEGAL_MOVES;{roomId};{row};{col}\n", cancellationToken);
        var resp = await AwaitResponseAsync(id, pending, cancellationToken);
        if (resp.Type == "ERROR")
        {
            throw new InvalidOperationException(resp.Raw);
        }

        var from = ParseCoords(resp.Params.TryGetValue("from", out var fromStr) ? fromStr : $"{row},{col}");
        var dests = new List<(int Row, int Col)>();
        if (resp.Params.TryGetValue("to", out var toStr) && !string.IsNullOrWhiteSpace(toStr))
        {
            var parts = toStr.Split('|', StringSplitOptions.RemoveEmptyEntries);
            foreach (var part in parts)
            {
                dests.Add(ParseCoords(part));
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

    private async Task SendAsync(string payload, CancellationToken cancellationToken)
    {
        var buffer = Encoding.UTF8.GetBytes(payload);
        await _udpClient.SendAsync(buffer, buffer.Length);
    }

    private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
    {
        // Nekonečná smyčka na příjem datagramů, parsuje zprávy a buď je přiřadí pending žádosti (dle ID), nebo zavolá push eventy.
        while (!cancellationToken.IsCancellationRequested)
        {
            UdpReceiveResult result;
            try
            {
                result = await _udpClient.ReceiveAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception)
            {
                Disconnected?.Invoke(this, EventArgs.Empty);
                break;
            }

            var line = Encoding.UTF8.GetString(result.Buffer).TrimEnd('\n', '\r');
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var msg = ParseMessage(line);
            if (msg == null)
            {
                continue;
            }

            DispatchPush(msg);

            if (_pending.TryGetValue(msg.Id, out var pending))
            {
                pending.Handle(msg);
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
                    GameStarted?.Invoke(this, new GameStartInfo(roomId, role));
                }
                break;
            case "GAME_STATE":
                if (msg.Params.TryGetValue("room", out var rStr) &&
                    int.TryParse(rStr, out var rId) &&
                    msg.Params.TryGetValue("turn", out var turn) &&
                    msg.Params.TryGetValue("board", out var board))
                {
                    GameStateUpdated?.Invoke(this, new GameStateInfo(rId, turn, board));
                }
                break;
            case "GAME_END":
                if (msg.Params.TryGetValue("room", out var reStr) &&
                    int.TryParse(reStr, out var reId))
                {
                    msg.Params.TryGetValue("reason", out var reason);
                    msg.Params.TryGetValue("winner", out var winner);
                    GameEnded?.Invoke(this, new GameEndInfo(reId, reason ?? string.Empty, winner ?? "NONE"));
                }
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
        // Server po ~30s bez provozu odpojuje; posíláme PING každých 10s.
        while (!cancellationToken.IsCancellationRequested)
        {
            await Task.Delay(_heartbeatInterval, cancellationToken);
            try
            {
                var id = NextId();
                await SendAsync($"{id};PING\n", cancellationToken);
            }
            catch
            {
                // ignorujeme heartbear errory; receive loop se odpojí
            }
        }
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

    private static (int Row, int Col) ParseCoords(string value)
    {
        var parts = value.Split(',');
        if (parts.Length == 2 &&
            int.TryParse(parts[0], out var r) &&
            int.TryParse(parts[1], out var c))
        {
            return (r, c);
        }
        return (0, 0);
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
        private bool _completed;

        public PendingRequest(RequestKind kind)
        {
            Kind = kind;
        }

        public RequestKind Kind { get; }
        public Task<ParsedMessage> SingleTask => _singleTcs.Task;
        public bool IsTerminal => _completed;

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
