using System.Text;

using Fu.Core.Abstractions;
using Fu.Core.Models;

namespace Fu.Core;

/// <summary>
/// SFEN形式との相互変換
/// </summary>
public class SfenConverter : ISfenConverter
{
    /// <summary>初期局面のSFEN</summary>
    public const string StartPosition = "lnsgkgsnl/1r5b1/ppppppppp/9/9/9/PPPPPPPPP/1B5R1/LNSGKGSNL b - 1";

    /// <summary>盤面をSFEN形式に変換</summary>
    public string ToSfen(Board board, Turn currentTurn, CapturedPieces firstCaptured, CapturedPieces secondCaptured)
    {
        var sb = new StringBuilder();

        // 盤面
        for (var row = 0; row < 9; row++) {
            var emptyCount = 0;
            for (var col = 0; col < 9; col++) {
                var piece = board[col, row];
                if (piece is null) {
                    emptyCount++;
                }
                else {
                    if (emptyCount > 0) {
                        sb.Append(emptyCount);
                        emptyCount = 0;
                    }
                    sb.Append(this.PieceToSfen(piece));
                }
            }
            if (emptyCount > 0) {
                sb.Append(emptyCount);
            }
            if (row < 8) {
                sb.Append('/');
            }
        }

        // 手番
        sb.Append(currentTurn == Turn.First ? " b " : " w ");

        // 持ち駒
        var captured = this.CapturedToSfen(firstCaptured, true) + this.CapturedToSfen(secondCaptured, false);
        sb.Append(string.IsNullOrEmpty(captured) ? "-" : captured);

        // 手数（常に1）
        sb.Append(" 1");

        return sb.ToString();
    }

    /// <summary>駒をSFEN形式に変換</summary>
    public string PieceToSfen(Piece piece)
    {
        var basePiece = piece.Type.IsPromoted() ? piece.Type.GetUnpromotedType() : piece.Type;
        var c = basePiece switch {
            PieceType.King => "K",
            PieceType.Rook => "R",
            PieceType.Bishop => "B",
            PieceType.Gold => "G",
            PieceType.Silver => "S",
            PieceType.Knight => "N",
            PieceType.Lance => "L",
            PieceType.Pawn => "P",
            _ => ""
        };
        if (piece.Type.IsPromoted()) {
            c = "+" + c;
        }
        return piece.Owner == Turn.First ? c : c.ToLowerInvariant();
    }

    /// <summary>持ち駒をSFEN形式に変換</summary>
    public string CapturedToSfen(CapturedPieces captured, bool isFirst)
    {
        var sb = new StringBuilder();

        void Append(int count, char c)
        {
            if (count > 0) {
                if (count > 1) {
                    sb.Append(count);
                }
                sb.Append(isFirst ? c : char.ToLowerInvariant(c));
            }
        }

        Append(captured.GetCount(PieceType.Rook), 'R');
        Append(captured.GetCount(PieceType.Bishop), 'B');
        Append(captured.GetCount(PieceType.Gold), 'G');
        Append(captured.GetCount(PieceType.Silver), 'S');
        Append(captured.GetCount(PieceType.Knight), 'N');
        Append(captured.GetCount(PieceType.Lance), 'L');
        Append(captured.GetCount(PieceType.Pawn), 'P');

        return sb.ToString();
    }
}
