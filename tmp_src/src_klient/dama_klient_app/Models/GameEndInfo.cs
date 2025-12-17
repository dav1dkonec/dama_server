namespace dama_klient_app.Models;

// Konec hry oznámený serverem: důvod (viz PROTOCOL) a vítěz (WHITE/BLACK/NONE).
public record GameEndInfo(int RoomId, string Reason, string Winner);
