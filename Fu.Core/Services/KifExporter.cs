using System.Globalization;
using System.Text;

using Fu.Core.Abstractions;
using Fu.Core.Models;

namespace Fu.Core.Services;

public class KifExporter : IKifExporter
{
    private static readonly string[] RowKanji = ["一", "二", "三", "四", "五", "六", "七", "八", "九"];
    private static readonly string[] ColKanji = ["９", "８", "７", "６", "５", "４", "３", "２", "１"];

    public string Export(
        IReadOnlyList<Move> moves,
        GameStatus status,
        string senteNickname = "",
        string goteNickname = "",
        IReadOnlyList<TimeSpan>? moveTimes = null)
    {
        var sb = new StringBuilder();

        // KIF version header
        sb.AppendLine("#KIF version=2.0 encoding=UTF-8");

        // ヘッダー
        sb.AppendLine(CultureInfo.InvariantCulture, $"開始日時：{DateTime.Now:yyyy/MM/dd HH:mm:ss}");
        sb.AppendLine("手合割：平手");
        sb.AppendLine(CultureInfo.InvariantCulture, $"先手：{senteNickname}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"後手：{goteNickname}");
        sb.AppendLine("手数----指手---------消費時間--");

        Position? lastTo = null;
        var senteTotalTime = TimeSpan.Zero;
        var goteTotalTime = TimeSpan.Zero;

        for (var i = 0; i < moves.Count; i++) {
            var move = moves[i];
            var moveNumber = i + 1;
            var notation = FormatMove(move, lastTo);

            // 時間情報
            var timeStr = "";
            if (moveTimes is not null && i < moveTimes.Count) {
                var moveTime = moveTimes[i];
                var isSente = i % 2 == 0;
                if (isSente) {
                    senteTotalTime += moveTime;
                } else {
                    goteTotalTime += moveTime;
                }
                var totalTime = isSente ? senteTotalTime : goteTotalTime;
                timeStr = $"   ({FormatKifTime(moveTime)}/{FormatKifTime(totalTime)})";
            }

            // KIF標準形式: "   1 ７六歩(77)   (00:01/00:01:23)"
            sb.AppendLine(CultureInfo.InvariantCulture, $"{moveNumber,4} {notation}{timeStr}");
            lastTo = move.To;
        }

        // 終局
        if (status is GameStatus.CheckmateFirst or GameStatus.CheckmateSecond) {
            var winner = status == GameStatus.CheckmateFirst ? "先手" : "後手";
            sb.AppendLine(CultureInfo.InvariantCulture, $"まで{moves.Count}手で{winner}の勝ち");
        }

        return sb.ToString();
    }

    private static string FormatKifTime(TimeSpan time) => TimeFormatHelper.FormatKif(time);

    private static string FormatMove(Move move, Position? lastTo)
    {
        var sb = new StringBuilder();

        // 移動先（同ならば「同」、そうでなければ座標）
        if (lastTo.HasValue && lastTo.Value == move.To) {
            sb.Append("同　");
        }
        else {
            sb.Append(FormatPosition(move.To));
        }

        // 駒種（成駒の場合は成る前の駒名 + 成）
        sb.Append(GetKifPieceName(move.PieceType));

        // 成り
        if (move.IsPromotion) {
            sb.Append('成');
        }

        // 打ち
        if (move.IsDrop) {
            sb.Append('打');
        }
        // 移動元（打ちでない場合）
        else if (move.From is { } from) {
            sb.Append(CultureInfo.InvariantCulture, $"({9 - from.Col}{from.Row + 1})");
        }

        return sb.ToString();
    }

    private static string FormatPosition(Position pos)
    {
        var col = ColKanji[pos.Col];
        var row = RowKanji[pos.Row];
        return $"{col}{row}";
    }

    private static string GetKifPieceName(PieceType type) => type switch {
        PieceType.King => "玉",
        PieceType.Rook => "飛",
        PieceType.Bishop => "角",
        PieceType.Gold => "金",
        PieceType.Silver => "銀",
        PieceType.Knight => "桂",
        PieceType.Lance => "香",
        PieceType.Pawn => "歩",
        PieceType.PromotedRook => "龍",
        PieceType.PromotedBishop => "馬",
        PieceType.PromotedSilver => "成銀",
        PieceType.PromotedKnight => "成桂",
        PieceType.PromotedLance => "成香",
        PieceType.PromotedPawn => "と",
        _ => ""
    };
}
