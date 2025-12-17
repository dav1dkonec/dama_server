namespace dama_klient_app.Models;

// Popis místnosti v lobby (id podle serveru, název, obsazenost).
public record RoomInfo(string Id, string Name, int PlayerCount, int Capacity);
