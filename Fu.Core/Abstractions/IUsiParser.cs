using Fu.Core.Models;

namespace Fu.Core.Abstractions;

/// <summary>
/// USI形式パーサーのインターフェース
/// </summary>
public interface IUsiParser
{
    /// <summary>USI形式の手をMoveにパース</summary>
    Move? ParseMove(string usiMove, Board board, Turn turn);

    /// <summary>SFEN形式の指し手をパースして座標を返す</summary>
    ((int col, int row)? from, (int col, int row) destination, char? dropPiece)? ParseMoveCoordinates(string sfenMove);

    /// <summary>USI文字を駒種類に変換</summary>
    PieceType? CharToPieceType(char c);
}
