using System.Collections.Generic;

namespace dama_klient_app.Models;

public class GameState
{
    public int BoardSize { get; init; } = 8;

    // Mapování na pozici: hodnota je znak z protokolu ('w' bílý pěšák, 'W' bílá dáma, 'b' černý pěšák, 'B' černá dáma).
    public Dictionary<(int Row, int Col), string> Pieces { get; init; } = new();

    public string ActivePlayerId { get; init; } = string.Empty;

    public int TurnNumber { get; init; }
}
