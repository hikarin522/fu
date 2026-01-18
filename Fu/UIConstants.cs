namespace Fu;

/// <summary>
/// UI関連の定数
/// </summary>
public static class UIConstants
{
    /// <summary>
    /// 矢印表示に関する定数
    /// </summary>
    public static class Arrow
    {
        /// <summary>候補手の線の太さ（ランク順）</summary>
        public static readonly int[] CandidateStrokeWidths = [6, 4, 2];

        /// <summary>矢印の頭の長さ（線の太さに対する倍率）</summary>
        public const double HeadLengthRatio = 2.5;

        /// <summary>矢印の頭の幅（線の太さに対する倍率）</summary>
        public const double HeadWidthRatio = 1.5;

        /// <summary>始点の短縮距離（ピクセル）</summary>
        public const double StartShortenPixels = 15;

        /// <summary>表示する候補手の最大数</summary>
        public const int MaxCandidatesToShow = 3;
    }

    /// <summary>
    /// 盤面表示に関する定数
    /// </summary>
    public static class Board
    {
        /// <summary>盤面の反転時のオフセット</summary>
        public const int FlipOffset = 8;
    }
}
