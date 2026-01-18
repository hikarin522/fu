using Fu.Core.Models;

namespace Fu.Core.Abstractions;

/// <summary>
/// SFEN形式との相互変換インターフェース
/// </summary>
public interface ISfenConverter
{
    /// <summary>盤面をSFEN形式に変換</summary>
    string ToSfen(Board board, Turn currentTurn, CapturedPieces firstCaptured, CapturedPieces secondCaptured);

    /// <summary>駒をSFEN形式に変換</summary>
    string PieceToSfen(Piece piece);

    /// <summary>持ち駒をSFEN形式に変換</summary>
    string CapturedToSfen(CapturedPieces captured, bool isFirst);
}
