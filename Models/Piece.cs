namespace ShogiGame.Models;

public enum PieceType
{
    None = 0,
    King,       // 王将/玉将
    Rook,       // 飛車
    Bishop,     // 角行
    Gold,       // 金将
    Silver,     // 銀将
    Knight,     // 桂馬
    Lance,      // 香車
    Pawn,       // 歩兵

    // 成駒
    PromotedRook,   // 龍王
    PromotedBishop, // 龍馬
    PromotedSilver, // 成銀
    PromotedKnight, // 成桂
    PromotedLance,  // 成香
    PromotedPawn    // と金
}

public enum Player
{
    None = 0,
    Sente,  // 先手（下側）
    Gote    // 後手（上側）
}

// Primary constructor (C# 12+)
public class Piece(PieceType type, Player owner)
{
    public PieceType Type { get; set; } = type;
    public Player Owner { get; set; } = owner;

    public Piece Clone() => new(Type, Owner);

    public bool IsPromoted => Type >= PieceType.PromotedRook;

    // Pattern matching with is pattern (simplified)
    public bool CanPromote => Type is PieceType.Rook or PieceType.Bishop or PieceType.Silver
                                   or PieceType.Knight or PieceType.Lance or PieceType.Pawn;

    public PieceType Promote() => Type switch
    {
        PieceType.Rook => PieceType.PromotedRook,
        PieceType.Bishop => PieceType.PromotedBishop,
        PieceType.Silver => PieceType.PromotedSilver,
        PieceType.Knight => PieceType.PromotedKnight,
        PieceType.Lance => PieceType.PromotedLance,
        PieceType.Pawn => PieceType.PromotedPawn,
        _ => Type
    };

    public PieceType Unpromote() => Type switch
    {
        PieceType.PromotedRook => PieceType.Rook,
        PieceType.PromotedBishop => PieceType.Bishop,
        PieceType.PromotedSilver => PieceType.Silver,
        PieceType.PromotedKnight => PieceType.Knight,
        PieceType.PromotedLance => PieceType.Lance,
        PieceType.PromotedPawn => PieceType.Pawn,
        _ => Type
    };

    public string GetDisplayChar() => Type switch
    {
        PieceType.King => Owner == Player.Sente ? "王" : "玉",
        PieceType.Rook => "飛",
        PieceType.Bishop => "角",
        PieceType.Gold => "金",
        PieceType.Silver => "銀",
        PieceType.Knight => "桂",
        PieceType.Lance => "香",
        PieceType.Pawn => "歩",
        PieceType.PromotedRook => "龍",
        PieceType.PromotedBishop => "馬",
        PieceType.PromotedSilver => "全",
        PieceType.PromotedKnight => "圭",
        PieceType.PromotedLance => "杏",
        PieceType.PromotedPawn => "と",
        _ => ""
    };
}
