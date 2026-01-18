using Fu.Core.Abstractions;
using Fu.Core.Models.Dto;

namespace Fu.Core.Models;

/// <summary>
/// 指し手を表す不変レコード
/// </summary>
public sealed record Move(
    Position To,
    PieceType PieceType,
    bool IsPromotion = false,
    bool IsDrop = false,
    Position? From = null,
    PieceType? CapturedPiece = null,
    Turn Turn = Turn.None)
{
    /// <summary>盤上の駒を動かす手を作成</summary>
    public static Move CreateMove(Position from, Position to, PieceType pieceType, bool isPromotion = false) =>
        new(to, pieceType, isPromotion, IsDrop: false, From: from);

    /// <summary>持ち駒を打つ手を作成</summary>
    public static Move CreateDrop(Position to, PieceType pieceType, Turn turn) =>
        new(to, pieceType, IsPromotion: false, IsDrop: true, Turn: turn);

    /// <summary>棋譜表記 (例: 7六歩、7六歩成、7六歩打)</summary>
    public string ToNotation() => this.IsDrop
        ? $"{this.To.ToNotation()}{this.PieceType.GetCapturedChar()}打"
        : $"{this.To.ToNotation()}{this.PieceType.GetCapturedChar()}{(this.IsPromotion ? "成" : "")}";

    /// <summary>移動元付き棋譜表記 (例: 7六歩(77)、7六歩成(67)、7六歩打)</summary>
    public string ToNotationWithFrom() => this.IsDrop
        ? $"{this.To.ToNotation()}{this.PieceType.GetCapturedChar()}打"
        : $"{this.To.ToNotation()}{this.PieceType.GetCapturedChar()}{(this.IsPromotion ? "成" : "")}{(this.From is { } f ? $"({9 - f.Col}{f.Row + 1})" : "")}";

    /// <summary>取った駒を設定した新しいインスタンスを返す</summary>
    public Move WithCapturedPiece(PieceType capturedPiece) =>
        this with { CapturedPiece = capturedPiece };

    /// <summary>プレイヤーを設定した新しいインスタンスを返す</summary>
    public Move WithTurn(Turn turn) =>
        this with { Turn = turn };

    // DTO変換
    public MoveDto ToDto() => new(
        this.From?.ToDto(),
        this.To.ToDto(),
        this.PieceType,
        this.IsPromotion,
        this.IsDrop,
        this.CapturedPiece
    );

    public static Move FromDto(MoveDto dto) => new(
        Position.FromDto(dto.To),
        dto.PieceType,
        dto.IsPromotion,
        dto.IsDrop,
        dto.From is { } from ? Position.FromDto(from) : null,
        dto.CapturedPiece
    );
}
