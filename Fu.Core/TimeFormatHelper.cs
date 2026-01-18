namespace Fu.Core;

/// <summary>
/// 時間フォーマットのユーティリティ
/// </summary>
public static class TimeFormatHelper
{
    /// <summary>
    /// 時間を表示用にフォーマット（UI用: 1:23:45 または 1:23）
    /// </summary>
    public static string FormatDisplay(TimeSpan time)
    {
        if (time.TotalHours >= 1) {
            return $"{(int)time.TotalHours}:{time.Minutes:D2}:{time.Seconds:D2}";
        }
        return $"{time.Minutes}:{time.Seconds:D2}";
    }

    /// <summary>
    /// 時間を棋譜用にフォーマット（KIF用: 01:23:45 または 01:23）
    /// </summary>
    public static string FormatKif(TimeSpan time)
    {
        if (time.TotalHours >= 1) {
            return $"{(int)time.TotalHours:D2}:{time.Minutes:D2}:{time.Seconds:D2}";
        }
        return $"{time.Minutes:D2}:{time.Seconds:D2}";
    }
}
