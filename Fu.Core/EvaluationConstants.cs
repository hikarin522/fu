namespace Fu.Core;

/// <summary>
/// 評価値に関する定数
/// </summary>
public static class EvaluationConstants
{
    /// <summary>詰みスコアの基準値（USIプロトコル）</summary>
    public const int MateScoreBase = 30000;

    /// <summary>大きく有利/不利とみなす閾値（センチポーン）</summary>
    public const int LargeAdvantageThreshold = 1000;

    /// <summary>やや有利/不利とみなす閾値（センチポーン）</summary>
    public const int SlightAdvantageThreshold = 100;

    /// <summary>センチポーンから評価値（歩換算）に変換</summary>
    public static double CentipawnToPawnValue(int centipawn) => centipawn / 100.0;

    /// <summary>詰みスコアかどうか判定</summary>
    public static bool IsMateScore(int score) => Math.Abs(score) >= MateScoreBase - 1000;

    /// <summary>詰みまでの手数を計算（詰みスコアの場合）</summary>
    public static int GetMateInMoves(int score) =>
        score > 0 ? MateScoreBase - score : -MateScoreBase - score;
}
