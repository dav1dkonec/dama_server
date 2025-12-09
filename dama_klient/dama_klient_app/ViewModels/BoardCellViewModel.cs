namespace dama_klient_app.ViewModels;

/// <summary>
/// VM jednoho políčka na desce.
/// </summary>
public class BoardCellViewModel : ViewModelBase
{
    private PieceViewModel? _piece;
    private bool _isHighlighted;

    public BoardCellViewModel(int row, int col)
    {
        Row = row;
        Col = col;
        IsDark = (row + col) % 2 == 1;
    }

    public int Row { get; }

    public int Col { get; }

    public bool IsDark { get; }

    public PieceViewModel? Piece
    {
        get => _piece;
        set
        {
            if (SetField(ref _piece, value))
            {
                OnPropertyChanged(nameof(HasPiece));
            }
        }
    }

    public bool HasPiece => Piece != null;

    public bool IsHighlighted
    {
        get => _isHighlighted;
        set
        {
            if (SetField(ref _isHighlighted, value))
            {
                OnPropertyChanged(nameof(Background));
            }
        }
    }

    public string Background => IsHighlighted ? "#F6D55C" : IsDark ? "#B58863" : "#F0D9B5";
}

/// <summary>
/// VM figury (barva/dáma) pro vykreslení.
/// </summary>
public class PieceViewModel
{
    public PieceViewModel(string color, bool isKing)
    {
        Color = color;
        IsKing = isKing;
        Fill = color == "White" ? "#f8f8f8" : "#2d2d2d";
        Stroke = color == "White" ? "#d0d0d0" : "#101010";
    }

    public string Color { get; }

    public bool IsKing { get; }

    public string Fill { get; }

    public string Stroke { get; }
}
