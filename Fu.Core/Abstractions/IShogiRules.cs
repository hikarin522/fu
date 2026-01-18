using Fu.Core.Models;

namespace Fu.Core.Abstractions;

/// <summary>
/// 将棋のルール判定インターフェース
/// </summary>
public interface IShogiRules
{
    /// <summary>指定位置から移動可能な位置を取得</summary>
    List<Position> GetLegalMoves(Board board, Position from, Turn turn);

    /// <summary>指定プレイヤーが駒を打てる位置を取得</summary>
    List<Position> GetLegalDropPositions(Board board, Turn turn, CapturedPieces captured, PieceType pieceType);

    /// <summary>成れるかどうか</summary>
    bool CanPromote(Board board, Position from, Position destination);

    /// <summary>成らなければならないか</summary>
    bool MustPromote(Board board, Position from, Position destination);

    /// <summary>王手かどうか</summary>
    bool IsInCheck(Board board, Turn turn);

    /// <summary>詰みかどうか（合法手が存在しない）</summary>
    bool IsCheckmate(Board board, Turn turn, CapturedPieces captured);

    /// <summary>盤面に手を適用</summary>
    (Board board, CapturedPieces firstCaptured, CapturedPieces secondCaptured) ApplyMove(
        Board board, Move move, Turn turn, CapturedPieces firstCaptured, CapturedPieces secondCaptured);

    /// <summary>棋譜から盤面を再構築</summary>
    (Board board, CapturedPieces firstCaptured, CapturedPieces secondCaptured, Turn currentTurn) ReconstructBoard(IEnumerable<Move> moves);

    /// <summary>駒の移動可能な方向を取得（王手無視）</summary>
    List<Position> GetPossibleMoves(Board board, Position from, Piece piece);
}
