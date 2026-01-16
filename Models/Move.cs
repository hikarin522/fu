using System.Text.Json.Serialization;

namespace ShogiGame.Models;

public class Move
{
    public Position? From { get; set; }  // nullなら持ち駒を打つ
    public Position To { get; set; }
    public PieceType PieceType { get; set; }
    public bool IsPromotion { get; set; }
    public bool IsDrop { get; set; }  // 持ち駒を打つ場合true
    public PieceType? CapturedPiece { get; set; }

    [JsonIgnore]
    public Player Player { get; set; }

    public Move() { }

    public Move(Position from, Position to, PieceType pieceType, bool isPromotion = false)
    {
        From = from;
        To = to;
        PieceType = pieceType;
        IsPromotion = isPromotion;
        IsDrop = false;
    }

    public static Move CreateDrop(Position to, PieceType pieceType, Player player)
    {
        return new Move
        {
            From = null,
            To = to,
            PieceType = pieceType,
            IsPromotion = false,
            IsDrop = true,
            Player = player
        };
    }

    public string ToNotation()
    {
        if (IsDrop)
        {
            return $"{To.ToNotation()}{GetPieceChar()}打";
        }

        var promotion = IsPromotion ? "成" : "";
        return $"{To.ToNotation()}{GetPieceChar()}{promotion}";
    }

    private string GetPieceChar() => PieceType switch
    {
        PieceType.King => "玉",
        PieceType.Rook or PieceType.PromotedRook => "飛",
        PieceType.Bishop or PieceType.PromotedBishop => "角",
        PieceType.Gold => "金",
        PieceType.Silver or PieceType.PromotedSilver => "銀",
        PieceType.Knight or PieceType.PromotedKnight => "桂",
        PieceType.Lance or PieceType.PromotedLance => "香",
        PieceType.Pawn or PieceType.PromotedPawn => "歩",
        _ => ""
    };
}
