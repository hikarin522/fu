namespace ShogiGame.Models;

public class Board
{
    private readonly Piece?[,] _squares = new Piece?[9, 9];

    public Piece? this[int col, int row]
    {
        get => _squares[col, row];
        set => _squares[col, row] = value;
    }

    public Piece? this[Position pos]
    {
        get => _squares[pos.Col, pos.Row];
        set => _squares[pos.Col, pos.Row] = value;
    }

    public Board() => Initialize();

    public void Initialize()
    {
        // 盤面をクリア
        for (var col = 0; col < 9; col++)
            for (var row = 0; row < 9; row++)
                _squares[col, row] = null;

        // 後手の駒配置 (上側)
        _squares[4, 0] = new(PieceType.King, Player.Gote);
        _squares[3, 0] = new(PieceType.Gold, Player.Gote);
        _squares[5, 0] = new(PieceType.Gold, Player.Gote);
        _squares[2, 0] = new(PieceType.Silver, Player.Gote);
        _squares[6, 0] = new(PieceType.Silver, Player.Gote);
        _squares[1, 0] = new(PieceType.Knight, Player.Gote);
        _squares[7, 0] = new(PieceType.Knight, Player.Gote);
        _squares[0, 0] = new(PieceType.Lance, Player.Gote);
        _squares[8, 0] = new(PieceType.Lance, Player.Gote);
        _squares[1, 1] = new(PieceType.Bishop, Player.Gote);
        _squares[7, 1] = new(PieceType.Rook, Player.Gote);
        for (var col = 0; col < 9; col++)
            _squares[col, 2] = new(PieceType.Pawn, Player.Gote);

        // 先手の駒配置 (下側)
        _squares[4, 8] = new(PieceType.King, Player.Sente);
        _squares[3, 8] = new(PieceType.Gold, Player.Sente);
        _squares[5, 8] = new(PieceType.Gold, Player.Sente);
        _squares[2, 8] = new(PieceType.Silver, Player.Sente);
        _squares[6, 8] = new(PieceType.Silver, Player.Sente);
        _squares[1, 8] = new(PieceType.Knight, Player.Sente);
        _squares[7, 8] = new(PieceType.Knight, Player.Sente);
        _squares[0, 8] = new(PieceType.Lance, Player.Sente);
        _squares[8, 8] = new(PieceType.Lance, Player.Sente);
        _squares[7, 7] = new(PieceType.Bishop, Player.Sente);
        _squares[1, 7] = new(PieceType.Rook, Player.Sente);
        for (var col = 0; col < 9; col++)
            _squares[col, 6] = new(PieceType.Pawn, Player.Sente);
    }

    public Board Clone()
    {
        var clone = new Board();
        for (var col = 0; col < 9; col++)
            for (var row = 0; row < 9; row++)
                clone._squares[col, row] = _squares[col, row]?.Clone();
        return clone;
    }

    public Position? FindKing(Player player)
    {
        for (var col = 0; col < 9; col++)
        {
            for (var row = 0; row < 9; row++)
            {
                // Pattern matching with property pattern
                if (_squares[col, row] is { Type: PieceType.King, Owner: var owner } && owner == player)
                    return new Position(col, row);
            }
        }
        return null;
    }

    public IEnumerable<(Position pos, Piece piece)> GetAllPieces(Player? player = null)
    {
        for (var col = 0; col < 9; col++)
        {
            for (var row = 0; row < 9; row++)
            {
                if (_squares[col, row] is { } piece && (player is null || piece.Owner == player))
                    yield return (new Position(col, row), piece);
            }
        }
    }

    public bool HasPawnInColumn(int col, Player player)
    {
        for (var row = 0; row < 9; row++)
        {
            // Pattern matching with property pattern
            if (_squares[col, row] is { Type: PieceType.Pawn, Owner: var owner } && owner == player)
                return true;
        }
        return false;
    }
}
