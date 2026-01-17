namespace Fu.Core.Models.Dto;

/// <summary>
/// 指し手のデータ転送オブジェクト
/// </summary>
public sealed record MoveDto(
    PositionDto? From,
    PositionDto To,
    PieceType PieceType,
    bool IsPromotion,
    bool IsDrop,
    PieceType? CapturedPiece
);
