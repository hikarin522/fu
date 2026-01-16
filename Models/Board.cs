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

    public Board()
    {
        Initialize();
    }

    public void Initialize()
    {
        // 盤面をクリア
        for (int col = 0; col < 9; col++)
            for (int row = 0; row < 9; row++)
                _squares[col, row] = null;

        // 後手の駒配置 (上側)
        _squares[4, 0] = new Piece(PieceType.King, Player.Gote);
        _squares[3, 0] = new Piece(PieceType.Gold, Player.Gote);
        _squares[5, 0] = new Piece(PieceType.Gold, Player.Gote);
        _squares[2, 0] = new Piece(PieceType.Silver, Player.Gote);
        _squares[6, 0] = new Piece(PieceType.Silver, Player.Gote);
        _squares[1, 0] = new Piece(PieceType.Knight, Player.Gote);
        _squares[7, 0] = new Piece(PieceType.Knight, Player.Gote);
        _squares[0, 0] = new Piece(PieceType.Lance, Player.Gote);
        _squares[8, 0] = new Piece(PieceType.Lance, Player.Gote);
        _squares[1, 1] = new Piece(PieceType.Bishop, Player.Gote);
        _squares[7, 1] = new Piece(PieceType.Rook, Player.Gote);
        for (int col = 0; col < 9; col++)
            _squares[col, 2] = new Piece(PieceType.Pawn, Player.Gote);

        // 先手の駒配置 (下側)
        _squares[4, 8] = new Piece(PieceType.King, Player.Sente);
        _squares[3, 8] = new Piece(PieceType.Gold, Player.Sente);
        _squares[5, 8] = new Piece(PieceType.Gold, Player.Sente);
        _squares[2, 8] = new Piece(PieceType.Silver, Player.Sente);
        _squares[6, 8] = new Piece(PieceType.Silver, Player.Sente);
        _squares[1, 8] = new Piece(PieceType.Knight, Player.Sente);
        _squares[7, 8] = new Piece(PieceType.Knight, Player.Sente);
        _squares[0, 8] = new Piece(PieceType.Lance, Player.Sente);
        _squares[8, 8] = new Piece(PieceType.Lance, Player.Sente);
        _squares[7, 7] = new Piece(PieceType.Bishop, Player.Sente);
        _squares[1, 7] = new Piece(PieceType.Rook, Player.Sente);
        for (int col = 0; col < 9; col++)
            _squares[col, 6] = new Piece(PieceType.Pawn, Player.Sente);
    }

    public Board Clone()
    {
        var clone = new Board();
        for (int col = 0; col < 9; col++)
            for (int row = 0; row < 9; row++)
                clone._squares[col, row] = _squares[col, row]?.Clone();
        return clone;
    }

    public Position? FindKing(Player player)
    {
        for (int col = 0; col < 9; col++)
        {
            for (int row = 0; row < 9; row++)
            {
                var piece = _squares[col, row];
                if (piece?.Type == PieceType.King && piece.Owner == player)
                    return new Position(col, row);
            }
        }
        return null;
    }

    public IEnumerable<(Position pos, Piece piece)> GetAllPieces(Player? player = null)
    {
        for (int col = 0; col < 9; col++)
        {
            for (int row = 0; row < 9; row++)
            {
                var piece = _squares[col, row];
                if (piece != null && (player == null || piece.Owner == player))
                    yield return (new Position(col, row), piece);
            }
        }
    }

    public bool HasPawnInColumn(int col, Player player)
    {
        for (int row = 0; row < 9; row++)
        {
            var piece = _squares[col, row];
            if (piece?.Type == PieceType.Pawn && piece.Owner == player)
                return true;
        }
        return false;
    }
}
