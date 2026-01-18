using System.Collections.Immutable;

using Fu.Core.Abstractions;

namespace Fu.Core.Models;

/// <summary>
/// 9x9の将棋盤を表す不変レコード
/// </summary>
public record Board
{
    public const int Size = 9;
    private readonly ImmutableArray<Piece?> _squares;

    public Piece? this[int col, int row] => this._squares[col * Size + row];
    public Piece? this[Position pos] => this._squares[pos.Col * Size + pos.Row];

    /// <summary>全マス位置（静的配列）</summary>
    public static readonly Position[] AllPositions = CreateAllPositions();

    private static Position[] CreateAllPositions()
    {
        var positions = new Position[Size * Size];
        var index = 0;
        for (var col = 0; col < Size; col++) {
            for (var row = 0; row < Size; row++) {
                positions[index++] = new Position(col, row);
            }
        }
        return positions;
    }

    private Board(ImmutableArray<Piece?> squares) => this._squares = squares;

    /// <summary>初期配置の盤面を作成</summary>
    public Board() : this(CreateInitialSquares()) { }

    private static ImmutableArray<Piece?> CreateInitialSquares()
    {
        var builder = ImmutableArray.CreateBuilder<Piece?>(Size * Size);
        builder.Count = Size * Size;

        // 後手の駒配置 (上側: row 0-2)
        builder[4 * Size + 0] = new(PieceType.King, Turn.Second);
        builder[3 * Size + 0] = new(PieceType.Gold, Turn.Second);
        builder[5 * Size + 0] = new(PieceType.Gold, Turn.Second);
        builder[2 * Size + 0] = new(PieceType.Silver, Turn.Second);
        builder[6 * Size + 0] = new(PieceType.Silver, Turn.Second);
        builder[1 * Size + 0] = new(PieceType.Knight, Turn.Second);
        builder[7 * Size + 0] = new(PieceType.Knight, Turn.Second);
        builder[0 * Size + 0] = new(PieceType.Lance, Turn.Second);
        builder[8 * Size + 0] = new(PieceType.Lance, Turn.Second);
        builder[7 * Size + 1] = new(PieceType.Bishop, Turn.Second);
        builder[1 * Size + 1] = new(PieceType.Rook, Turn.Second);
        for (var col = 0; col < Size; col++) {
            builder[col * Size + 2] = new(PieceType.Pawn, Turn.Second);
        }

        // 先手の駒配置 (下側: row 6-8)
        builder[4 * Size + 8] = new(PieceType.King, Turn.First);
        builder[3 * Size + 8] = new(PieceType.Gold, Turn.First);
        builder[5 * Size + 8] = new(PieceType.Gold, Turn.First);
        builder[2 * Size + 8] = new(PieceType.Silver, Turn.First);
        builder[6 * Size + 8] = new(PieceType.Silver, Turn.First);
        builder[1 * Size + 8] = new(PieceType.Knight, Turn.First);
        builder[7 * Size + 8] = new(PieceType.Knight, Turn.First);
        builder[0 * Size + 8] = new(PieceType.Lance, Turn.First);
        builder[8 * Size + 8] = new(PieceType.Lance, Turn.First);
        builder[1 * Size + 7] = new(PieceType.Bishop, Turn.First);
        builder[7 * Size + 7] = new(PieceType.Rook, Turn.First);
        for (var col = 0; col < Size; col++) {
            builder[col * Size + 6] = new(PieceType.Pawn, Turn.First);
        }

        return builder.MoveToImmutable();
    }

    /// <summary>指定位置に駒を置いた新しい盤面を返す</summary>
    public Board SetPiece(Position pos, Piece? piece) =>
        new(this._squares.SetItem(pos.Col * Size + pos.Row, piece));

    /// <summary>指定位置に駒を置いた新しい盤面を返す</summary>
    public Board SetPiece(int col, int row, Piece? piece) =>
        new(this._squares.SetItem(col * Size + row, piece));

    /// <summary>駒を移動した新しい盤面を返す</summary>
    public Board MovePiece(Position from, Position to, Piece? newPiece = null)
    {
        var piece = newPiece ?? this[from];
        return this.SetPiece(from, null).SetPiece(to, piece);
    }

    /// <summary>指定プレイヤーの王の位置を検索</summary>
    public Position? FindKing(Turn turn) =>
        AllPositions.FirstOrDefault(pos => this[pos] is { Type: PieceType.King } piece && piece.Owner == turn);

    /// <summary>指定プレイヤーの全駒を列挙（nullなら全プレイヤー）</summary>
    public IEnumerable<(Position pos, Piece piece)> GetAllPieces(Turn? turn = null) =>
        AllPositions
            .Select(pos => (pos, piece: this[pos]))
            .Where(x => x.piece is not null && (turn is null || x.piece.Owner == turn))
            .Select(x => (x.pos, x.piece!));

    /// <summary>指定列に歩があるか（二歩チェック用）</summary>
    public bool HasPawnInColumn(int col, Turn turn) =>
        Enumerable.Range(0, Size).Any(row => this[col, row] is { Type: PieceType.Pawn } piece && piece.Owner == turn);
}
