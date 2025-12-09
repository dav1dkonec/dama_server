namespace dama_klient_app.Models;

// Stav hry poslaný serverem: místnost, kdo je na tahu (PLAYER1/PLAYER2/NONE), a 64-znakový popis desky.
public record GameStateInfo(int RoomId, string Turn, string Board);
