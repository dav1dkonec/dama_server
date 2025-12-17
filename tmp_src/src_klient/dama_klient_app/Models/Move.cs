namespace dama_klient_app.Models;

// Jeden tah: odkud, kam a v jaké místnosti (RoomId je z protokolu).
public record Move(int RoomId, (int Row, int Col) From, (int Row, int Col) To, bool IsCapture = false);
