using Fu.Core.Abstractions;
using Fu.Core.Models;

namespace Fu.Core;

/// <summary>
/// USI形式のパース
/// </summary>
public static class UsiParser
{
    /// <summary>USI形式の手をMoveにパース</summary>
    public static Move? ParseMove(string usiMove, Board board, Turn turn)
    {
        if (string.IsNullOrEmpty(usiMove) || usiMove.Length < 4) {
            return null;
        }

        // 駒打ち（例: P*3d）
        if (usiMove.Length >= 4 && usiMove[1] == '*') {
            var pieceType = CharToPieceType(usiMove[0]);
            if (pieceType is null) {
                return null;
            }

            var toCol = 8 - (usiMove[2] - '1');
            var toRow = usiMove[3] - 'a';
            if (toCol is < 0 or > 8 || toRow is < 0 or > 8) {
                return null;
            }

            return Move.CreateDrop(new Position(toCol, toRow), pieceType.Value, turn);
        }

        // 通常の移動（例: 7g7f, 7g7f+）
        var fromCol = 8 - (usiMove[0] - '1');
        var fromRow = usiMove[1] - 'a';
        var toCol2 = 8 - (usiMove[2] - '1');
        var toRow2 = usiMove[3] - 'a';

        if (fromCol is < 0 or > 8 || fromRow is < 0 or > 8 ||
            toCol2 is < 0 or > 8 || toRow2 is < 0 or > 8) {
            return null;
        }

        var isPromotion = usiMove.Length > 4 && usiMove[4] == '+';

        var piece = board[fromCol, fromRow];
        if (piece is null) {
            return null;
        }

        return Move.CreateMove(
            new Position(fromCol, fromRow),
            new Position(toCol2, toRow2),
            piece.Type,
            isPromotion);
    }

    /// <summary>SFEN形式の指し手をパースして座標を返す</summary>
    public static ((int col, int row)? from, (int col, int row) to, char? dropPiece)? ParseMoveCoordinates(string sfenMove)
    {
        if (string.IsNullOrEmpty(sfenMove) || sfenMove.Length < 4) {
            return null;
        }

        // 駒打ちの場合（例: G*5b）
        if (sfenMove[1] == '*') {
            var toCol = sfenMove[2] - '1';
            var toRow = sfenMove[3] - 'a';
            if (toCol is >= 0 and < 9 && toRow is >= 0 and < 9) {
                // SFEN列は1-9、内部は0-8。SFEN 1 = 内部 8, SFEN 9 = 内部 0
                return (null, (8 - toCol, toRow), char.ToUpperInvariant(sfenMove[0]));
            }
            return null;
        }

        // 通常の移動（例: 7g7f, 7g7f+）
        var fromCol = sfenMove[0] - '1';
        var fromRow = sfenMove[1] - 'a';
        var toCol2 = sfenMove[2] - '1';
        var toRow2 = sfenMove[3] - 'a';

        if (fromCol is >= 0 and < 9 && fromRow is >= 0 and < 9 &&
            toCol2 is >= 0 and < 9 && toRow2 is >= 0 and < 9) {
            return ((8 - fromCol, fromRow), (8 - toCol2, toRow2), null);
        }

        return null;
    }

    /// <summary>USI文字を駒種類に変換</summary>
    public static PieceType? CharToPieceType(char c) => c switch {
        'P' => PieceType.Pawn,
        'L' => PieceType.Lance,
        'N' => PieceType.Knight,
        'S' => PieceType.Silver,
        'G' => PieceType.Gold,
        'B' => PieceType.Bishop,
        'R' => PieceType.Rook,
        'K' => PieceType.King,
        _ => null
    };
}
