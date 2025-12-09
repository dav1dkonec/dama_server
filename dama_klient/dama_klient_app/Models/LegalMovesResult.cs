using System.Collections.Generic;

namespace dama_klient_app.Models;

// Výsledek LEGAL_MOVES z protokolu: odkud, seznam povolených cílů a příznak povinného braní.
public record LegalMovesResult(int RoomId, (int Row, int Col) From, IReadOnlyList<(int Row, int Col)> Destinations, bool MustCapture);
