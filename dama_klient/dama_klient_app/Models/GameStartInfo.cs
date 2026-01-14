namespace dama_klient_app.Models;

// Notifikace o startu hry: id místnosti, role (WHITE/BLACK) a přezdívka soupeře.
public record GameStartInfo(int RoomId, string Role, string OpponentName);
