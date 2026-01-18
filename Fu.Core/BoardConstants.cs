using Fu.Core.Abstractions;

namespace Fu.Core;

/// <summary>
/// 将棋盤に関する定数
/// </summary>
public static class BoardConstants
{
    /// <summary>盤面サイズ（9x9）</summary>
    public const int Size = 9;

    /// <summary>先手の成れる段の上限（0-2段目）</summary>
    public const int FirstPromotionBoundary = 2;

    /// <summary>後手の成れる段の下限（6-8段目）</summary>
    public const int SecondPromotionBoundary = 6;

    /// <summary>先手の王の初期位置の段</summary>
    public const int FirstKingRow = 8;

    /// <summary>後手の王の初期位置の段</summary>
    public const int SecondKingRow = 0;

    /// <summary>先手の歩の初期位置の段</summary>
    public const int FirstPawnRow = 6;

    /// <summary>後手の歩の初期位置の段</summary>
    public const int SecondPawnRow = 2;

    /// <summary>指定位置が成れる段かどうか</summary>
    public static bool IsPromotionZone(int row, Turn turn) =>
        turn == Turn.First
            ? row <= FirstPromotionBoundary
            : row >= SecondPromotionBoundary;

    /// <summary>移動元または移動先が成れる段かどうか</summary>
    public static bool CanPromote(int fromRow, int toRow, Turn turn) =>
        turn == Turn.First
            ? fromRow <= FirstPromotionBoundary || toRow <= FirstPromotionBoundary
            : fromRow >= SecondPromotionBoundary || toRow >= SecondPromotionBoundary;
}
