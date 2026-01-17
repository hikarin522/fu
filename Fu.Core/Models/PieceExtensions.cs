namespace Fu.Core.Models;

/// <summary>
/// Player の拡張メソッド
/// </summary>
public static class PlayerExtensions
{
    /// <summary>相手プレイヤーを取得</summary>
    public static Player GetOpponent(this Player player) =>
        player == Player.Sente ? Player.Gote : Player.Sente;

    /// <summary>前進方向を取得（先手: -1 = 上方向, 後手: 1 = 下方向）</summary>
    public static int GetForwardDirection(this Player player) =>
        player == Player.Sente ? -1 : 1;
}

/// <summary>
/// PieceType の拡張メソッド
/// </summary>
public static class PieceTypeExtensions
{
    /// <summary>成駒かどうか</summary>
    public static bool IsPromoted(this PieceType type) =>
        type >= PieceType.PromotedRook;

    /// <summary>成れる駒かどうか（金と王は成れない）</summary>
    public static bool CanPromote(this PieceType type) =>
        type is PieceType.Rook or PieceType.Bishop or PieceType.Silver
             or PieceType.Knight or PieceType.Lance or PieceType.Pawn;

    /// <summary>成った後の駒種を取得</summary>
    public static PieceType GetPromotedType(this PieceType type) => type switch {
        PieceType.Rook => PieceType.PromotedRook,
        PieceType.Bishop => PieceType.PromotedBishop,
        PieceType.Silver => PieceType.PromotedSilver,
        PieceType.Knight => PieceType.PromotedKnight,
        PieceType.Lance => PieceType.PromotedLance,
        PieceType.Pawn => PieceType.PromotedPawn,
        _ => type
    };

    /// <summary>成りを戻した駒種を取得（持ち駒として使う場合）</summary>
    public static PieceType GetUnpromotedType(this PieceType type) => type switch {
        PieceType.PromotedRook => PieceType.Rook,
        PieceType.PromotedBishop => PieceType.Bishop,
        PieceType.PromotedSilver => PieceType.Silver,
        PieceType.PromotedKnight => PieceType.Knight,
        PieceType.PromotedLance => PieceType.Lance,
        PieceType.PromotedPawn => PieceType.Pawn,
        _ => type
    };

    /// <summary>持ち駒表示用の駒文字を取得（成駒は元の駒の文字）</summary>
    public static string GetCapturedChar(this PieceType type) => type switch {
        PieceType.Rook or PieceType.PromotedRook => "飛",
        PieceType.Bishop or PieceType.PromotedBishop => "角",
        PieceType.Gold => "金",
        PieceType.Silver or PieceType.PromotedSilver => "銀",
        PieceType.Knight or PieceType.PromotedKnight => "桂",
        PieceType.Lance or PieceType.PromotedLance => "香",
        PieceType.Pawn or PieceType.PromotedPawn => "歩",
        PieceType.King => "玉",
        _ => ""
    };

    /// <summary>盤面表示用の文字を取得</summary>
    public static string GetDisplayChar(this PieceType type) => type switch {
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
        PieceType.PromotedSilver => "全",
        PieceType.PromotedKnight => "圭",
        PieceType.PromotedLance => "杏",
        PieceType.PromotedPawn => "と",
        _ => ""
    };
}

/// <summary>
/// Piece の拡張メソッド
/// </summary>
public static class PieceExtensions
{
    /// <summary>盤面表示用の文字を取得（王は先手/後手で異なる）</summary>
    public static string GetDisplayChar(this Piece piece) =>
        piece.Type == PieceType.King
            ? (piece.Owner == Player.Sente ? "王" : "玉")
            : piece.Type.GetDisplayChar();
}
