using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using dama_klient_app.Models;

namespace dama_klient_app.Services;

public interface IGameClient
{
    bool IsConnected { get; }

    event EventHandler? Disconnected;
    event EventHandler<IReadOnlyList<RoomInfo>>? LobbyUpdated;
    event EventHandler<GameStartInfo>? GameStarted;
    event EventHandler<GameStateInfo>? GameStateUpdated;
    event EventHandler<GameEndInfo>? GameEnded;

    Task ConnectAsync(CancellationToken cancellationToken = default);

    Task LoginAsync(string nickname, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<RoomInfo>> GetRoomsAsync(CancellationToken cancellationToken = default);

    Task<RoomInfo> CreateRoomAsync(string name, CancellationToken cancellationToken = default);

    Task JoinRoomAsync(string roomId, CancellationToken cancellationToken = default);

    Task LeaveRoomAsync(int roomId, CancellationToken cancellationToken = default);

    Task SendMoveAsync(Move move, CancellationToken cancellationToken = default);

    Task<LegalMovesResult> GetLegalMovesAsync(int roomId, int row, int col, CancellationToken cancellationToken = default);
}
