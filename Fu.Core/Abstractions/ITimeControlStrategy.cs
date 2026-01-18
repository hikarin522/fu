using Fu.Core.Models;

namespace Fu.Core.Abstractions;

/// <summary>
/// 時間制御戦略のインターフェース
/// </summary>
public interface ITimeControlStrategy
{
    /// <summary>時間を消費して新しい状態を返す</summary>
    PlayerTimeState ConsumeTime(PlayerTimeState current, TimeSpan elapsed, TimeControlSettings settings);

    /// <summary>初期状態を作成</summary>
    PlayerTimeState CreateInitialState(TimeControlSettings settings);
}

/// <summary>
/// 時間制限なし
/// </summary>
public sealed class NoTimeControlStrategy : ITimeControlStrategy
{
    public static readonly NoTimeControlStrategy Instance = new();

    public PlayerTimeState ConsumeTime(PlayerTimeState current, TimeSpan elapsed, TimeControlSettings settings) =>
        current;

    public PlayerTimeState CreateInitialState(TimeControlSettings settings) =>
        new(TimeSpan.MaxValue);
}

/// <summary>
/// 切れ負け
/// </summary>
public sealed class SuddenDeathStrategy : ITimeControlStrategy
{
    public static readonly SuddenDeathStrategy Instance = new();

    public PlayerTimeState ConsumeTime(PlayerTimeState current, TimeSpan elapsed, TimeControlSettings settings) =>
        current with { RemainingTime = current.RemainingTime - elapsed };

    public PlayerTimeState CreateInitialState(TimeControlSettings settings) =>
        new(settings.MainTime);
}

/// <summary>
/// 秒読み
/// </summary>
public sealed class ByoyomiStrategy : ITimeControlStrategy
{
    public static readonly ByoyomiStrategy Instance = new();

    public PlayerTimeState ConsumeTime(PlayerTimeState current, TimeSpan elapsed, TimeControlSettings settings)
    {
        if (current.IsInByoyomi) {
            return current with { RemainingTime = settings.Byoyomi };
        }

        var newRemaining = current.RemainingTime - elapsed;
        if (newRemaining <= TimeSpan.Zero && settings.Byoyomi > TimeSpan.Zero) {
            return current with {
                RemainingTime = settings.Byoyomi,
                IsInByoyomi = true
            };
        }
        return current with { RemainingTime = newRemaining };
    }

    public PlayerTimeState CreateInitialState(TimeControlSettings settings) =>
        settings.MainTime == TimeSpan.Zero
            ? new(settings.Byoyomi, IsInByoyomi: true)
            : new(settings.MainTime);
}

/// <summary>
/// フィッシャー
/// </summary>
public sealed class FischerStrategy : ITimeControlStrategy
{
    public static readonly FischerStrategy Instance = new();

    public PlayerTimeState ConsumeTime(PlayerTimeState current, TimeSpan elapsed, TimeControlSettings settings) =>
        current with { RemainingTime = current.RemainingTime - elapsed + settings.Increment };

    public PlayerTimeState CreateInitialState(TimeControlSettings settings) =>
        new(settings.MainTime);
}

/// <summary>
/// 持ち時間＋秒読み（NHK杯方式）
/// </summary>
public sealed class MainPlusByoyomiStrategy : ITimeControlStrategy
{
    public static readonly MainPlusByoyomiStrategy Instance = new();

    public PlayerTimeState ConsumeTime(PlayerTimeState current, TimeSpan elapsed, TimeControlSettings settings)
    {
        if (current.IsInByoyomi) {
            return current with { RemainingTime = settings.Byoyomi };
        }

        var newRemaining = current.RemainingTime - elapsed;
        if (newRemaining <= TimeSpan.Zero && settings.Byoyomi > TimeSpan.Zero) {
            return current with {
                RemainingTime = settings.Byoyomi,
                IsInByoyomi = true
            };
        }
        return current with { RemainingTime = newRemaining };
    }

    public PlayerTimeState CreateInitialState(TimeControlSettings settings) =>
        new(settings.MainTime);
}

/// <summary>
/// 時間制御戦略のファクトリ
/// </summary>
public static class TimeControlStrategyFactory
{
    public static ITimeControlStrategy Create(TimeControlType type) => type switch {
        TimeControlType.None => NoTimeControlStrategy.Instance,
        TimeControlType.Sudden => SuddenDeathStrategy.Instance,
        TimeControlType.Byoyomi => ByoyomiStrategy.Instance,
        TimeControlType.Fischer => FischerStrategy.Instance,
        TimeControlType.MainPlusByoyomi => MainPlusByoyomiStrategy.Instance,
        _ => NoTimeControlStrategy.Instance
    };
}
