namespace dama_klient_app.Models;

// Notifikace o startu hry: id místnosti a role (WHITE/BLACK) přiřazená serverem.
public record GameStartInfo(int RoomId, string Role);
