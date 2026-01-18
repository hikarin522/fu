using Fu.Core.Abstractions;

namespace Fu.Core.Models;

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

/// <summary>
/// 将棋の駒を表すレコード（データのみ）
/// </summary>
public record Piece(PieceType Type, Turn Owner);
