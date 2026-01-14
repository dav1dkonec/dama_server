using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using dama_klient_app.Models;

namespace dama_klient_app.Services;

public interface IGameClient
{
    bool IsConnected { get; }
    int TurnTimeoutMs { get; }
    string Token { get; }
    ServerStatus ServerStatus { get; }
    void ConfigureEndpoint(string host, int port);
    Task TryReconnectAsync(CancellationToken cancellationToken = default);

    event EventHandler? Disconnected;
    event EventHandler<ServerStatus>? ServerStatusChanged;
    event EventHandler? TokenInvalidated;
    event EventHandler<IReadOnlyList<RoomInfo>>? LobbyUpdated;
    event EventHandler<GameStartInfo>? GameStarted;
    event EventHandler<GameStateInfo>? GameStateUpdated;
    event EventHandler<GameEndInfo>? GameEnded;
    event EventHandler<(int RoomId, long ResumeBy)>? GamePaused;

    // Latest push snapshots (helps when VM subscribes after push arrives).
    bool TryGetLastGameStart(int roomId, out GameStartInfo info);
    bool TryGetLastGameState(int roomId, out GameStateInfo info);

    Task ConnectAsync(CancellationToken cancellationToken = default);

    Task LoginAsync(string nickname, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<RoomInfo>> GetRoomsAsync(CancellationToken cancellationToken = default);

    Task<RoomInfo> CreateRoomAsync(string name, CancellationToken cancellationToken = default);

    Task JoinRoomAsync(string roomId, CancellationToken cancellationToken = default);

    Task LeaveRoomAsync(int roomId, CancellationToken cancellationToken = default);
    void ClearRoomCache(int roomId);

    Task SendMoveAsync(Move move, CancellationToken cancellationToken = default);

    Task<LegalMovesResult> GetLegalMovesAsync(int roomId, int row, int col, CancellationToken cancellationToken = default);

    Task SendByeAsync(CancellationToken cancellationToken = default);

    ValueTask DisposeAsync();
}
