namespace dama_klient_app.Models;

// Stav hry poslaný serverem: místnost, kdo je na tahu, 64-znakový popis desky,
// zbývající čas na tah (ms) a případný capture lock.
public record GameStateInfo(int RoomId, string Turn, string Board, int RemainingMs, int? LockRow, int? LockCol);
