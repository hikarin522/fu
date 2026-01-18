using Fu.Core.Abstractions;
using Fu.Core.Services;

namespace Fu.Core.Models;

/// <summary>
/// 持ち時間ルールの種類
/// </summary>
public enum TimeControlType
{
    /// <summary>時間制限なし（現在のデフォルト）</summary>
    None,

    /// <summary>切れ負け（時間切れで即負け）</summary>
    Sudden,

    /// <summary>秒読み（残り時間0後、毎手N秒以内）</summary>
    Byoyomi,

    /// <summary>フィッシャー（1手ごとに時間加算）</summary>
    Fischer,

    /// <summary>持ち時間+秒読み（NHK杯方式）</summary>
    MainPlusByoyomi
}

/// <summary>
/// 持ち時間設定
/// </summary>
public record TimeControlSettings(
    TimeControlType Type,
    TimeSpan MainTime = default,
    TimeSpan Byoyomi = default,
    TimeSpan Increment = default,
    TimeRoundingMode RoundingMode = TimeRoundingMode.FloorSeconds)
{
    /// <summary>時間制限なし</summary>
    public static readonly TimeControlSettings None = new(TimeControlType.None);

    /// <summary>切れ負け</summary>
    public static TimeControlSettings Sudden(TimeSpan mainTime, TimeRoundingMode rounding = TimeRoundingMode.FloorSeconds) =>
        new(TimeControlType.Sudden, mainTime, RoundingMode: rounding);

    /// <summary>秒読みのみ（持ち時間なし）</summary>
    public static TimeControlSettings ByoyomiOnly(TimeSpan byoyomi, TimeRoundingMode rounding = TimeRoundingMode.FloorSeconds) =>
        new(TimeControlType.Byoyomi, Byoyomi: byoyomi, RoundingMode: rounding);

    /// <summary>フィッシャー（デフォルトはミリ秒精度）</summary>
    public static TimeControlSettings Fischer(TimeSpan mainTime, TimeSpan increment, TimeRoundingMode rounding = TimeRoundingMode.FloorMilliseconds) =>
        new(TimeControlType.Fischer, mainTime, Increment: increment, RoundingMode: rounding);

    /// <summary>持ち時間+秒読み</summary>
    public static TimeControlSettings MainPlusByoyomi(TimeSpan mainTime, TimeSpan byoyomi, TimeRoundingMode rounding = TimeRoundingMode.FloorSeconds) =>
        new(TimeControlType.MainPlusByoyomi, mainTime, byoyomi, RoundingMode: rounding);
}

/// <summary>
/// プレイヤーごとの時間状態
/// </summary>
public record PlayerTimeState(
    TimeSpan RemainingTime,
    bool IsInByoyomi = false,
    int ByoyomiPeriodsLeft = 0)
{
    /// <summary>時間切れかどうか</summary>
    public bool IsExpired => this.RemainingTime <= TimeSpan.Zero && !this.IsInByoyomi;

    /// <summary>時間を消費して新しい状態を返す</summary>
    public PlayerTimeState ConsumeTime(TimeSpan elapsed, TimeControlSettings settings)
    {
        return settings.Type switch {
            TimeControlType.None => this,

            TimeControlType.Sudden => this with {
                RemainingTime = this.RemainingTime - elapsed
            },

            TimeControlType.Byoyomi when this.IsInByoyomi => this with {
                RemainingTime = settings.Byoyomi // 秒読みはリセット
            },

            TimeControlType.Byoyomi => this.ConsumeWithByoyomiTransition(elapsed, settings),

            TimeControlType.Fischer => this with {
                RemainingTime = this.RemainingTime - elapsed + settings.Increment
            },

            TimeControlType.MainPlusByoyomi when this.IsInByoyomi => this with {
                RemainingTime = settings.Byoyomi // 秒読みはリセット
            },

            TimeControlType.MainPlusByoyomi => this.ConsumeWithByoyomiTransition(elapsed, settings),

            _ => this
        };
    }

    private PlayerTimeState ConsumeWithByoyomiTransition(TimeSpan elapsed, TimeControlSettings settings)
    {
        var newRemaining = this.RemainingTime - elapsed;
        if (newRemaining <= TimeSpan.Zero && settings.Byoyomi > TimeSpan.Zero) {
            // 秒読みに移行
            return this with {
                RemainingTime = settings.Byoyomi,
                IsInByoyomi = true
            };
        }
        return this with { RemainingTime = newRemaining };
    }

    /// <summary>初期状態を作成</summary>
    public static PlayerTimeState Initial(TimeControlSettings settings) =>
        settings.Type switch {
            TimeControlType.None => new(TimeSpan.MaxValue),
            TimeControlType.Byoyomi when settings.MainTime == TimeSpan.Zero =>
                new(settings.Byoyomi, IsInByoyomi: true),
            _ => new(settings.MainTime)
        };
}

/// <summary>
/// ゲーム全体の時間管理
/// </summary>
public record GameTimeState(
    TimeControlSettings Settings,
    PlayerTimeState FirstTime,
    PlayerTimeState SecondTime)
{
    /// <summary>初期状態を作成</summary>
    public static GameTimeState Initial(TimeControlSettings settings) =>
        new(settings, PlayerTimeState.Initial(settings), PlayerTimeState.Initial(settings));

    /// <summary>時間制限なしの初期状態</summary>
    public static GameTimeState None => Initial(TimeControlSettings.None);

    /// <summary>指定プレイヤーの時間状態を取得</summary>
    public PlayerTimeState GetPlayerTime(Turn turn) =>
        turn == Turn.First ? this.FirstTime : this.SecondTime;

    /// <summary>時間を消費して新しい状態を返す</summary>
    public GameTimeState ConsumeTime(Turn turn, TimeSpan elapsed)
    {
        var playerTime = this.GetPlayerTime(turn);
        var newPlayerTime = playerTime.ConsumeTime(elapsed, this.Settings);

        return turn == Turn.First
            ? this with { FirstTime = newPlayerTime }
            : this with { SecondTime = newPlayerTime };
    }

    /// <summary>時間切れのプレイヤーがいるか</summary>
    public Turn? GetExpiredPlayer()
    {
        if (this.FirstTime.IsExpired) {
            return Turn.First;
        }
        if (this.SecondTime.IsExpired) {
            return Turn.Second;
        }
        return null;
    }
}
