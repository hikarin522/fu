using ShogiGame.Models.Dto;

namespace ShogiGame.Models;

/// <summary>
/// 指し手を表すドメインモデル
/// </summary>
public class Move
{
    /// <summary>移動元（nullなら持ち駒を打つ）</summary>
    public Position? From { get; set; }
    public Position To { get; set; }
    public PieceType PieceType { get; set; }
    public bool IsPromotion { get; set; }
    /// <summary>持ち駒を打つ場合true</summary>
    public bool IsDrop { get; set; }
    /// <summary>取った駒（あれば）</summary>
    public PieceType? CapturedPiece { get; set; }
    /// <summary>この手を指したプレイヤー</summary>
    public Player Player { get; set; }

    public Move() { }

    public Move(Position from, Position to, PieceType pieceType, bool isPromotion = false)
    {
        this.From = from;
        this.To = to;
        this.PieceType = pieceType;
        this.IsPromotion = isPromotion;
        this.IsDrop = false;
    }

    /// <summary>持ち駒を打つ手を作成</summary>
    public static Move CreateDrop(Position to, PieceType pieceType, Player player) => new() {
        From = null,
        To = to,
        PieceType = pieceType,
        IsPromotion = false,
        IsDrop = true,
        Player = player
    };

    /// <summary>棋譜表記 (例: 7六歩、7六歩成、7六歩打)</summary>
    public string ToNotation() => this.IsDrop
        ? $"{this.To.ToNotation()}{this.PieceType.GetCapturedChar()}打"
        : $"{this.To.ToNotation()}{this.PieceType.GetCapturedChar()}{(this.IsPromotion ? "成" : "")}";

    // DTO変換
    public MoveDto ToDto() => new(
        this.From?.ToDto(),
        this.To.ToDto(),
        this.PieceType,
        this.IsPromotion,
        this.IsDrop,
        this.CapturedPiece
    );

    public static Move FromDto(MoveDto dto) => new() {
        From = dto.From is { } from ? Position.FromDto(from) : null,
        To = Position.FromDto(dto.To),
        PieceType = dto.PieceType,
        IsPromotion = dto.IsPromotion,
        IsDrop = dto.IsDrop,
        CapturedPiece = dto.CapturedPiece
    };
}