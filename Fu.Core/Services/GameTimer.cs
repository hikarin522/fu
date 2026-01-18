using System.Diagnostics;

using Fu.Core.Abstractions;
using Fu.Core.Models;

namespace Fu.Core.Services;

/// <summary>
/// 時間の丸めモード
/// </summary>
public enum TimeRoundingMode
{
    /// <summary>秒単位で切り捨て（将棋ウォーズ等）</summary>
    FloorSeconds,

    /// <summary>ミリ秒単位で切り捨て</summary>
    FloorMilliseconds,

    /// <summary>丸めなし（そのまま）</summary>
    Exact
}

/// <summary>
/// Stopwatch ベースのゲームタイマー実装
/// </summary>
public sealed class StopwatchGameTimer : IGameTimer
{
    private readonly Stopwatch _stopwatch = new();
    private readonly TimeRoundingMode _roundingMode;

    public StopwatchGameTimer(TimeRoundingMode roundingMode = TimeRoundingMode.FloorSeconds) =>
        this._roundingMode = roundingMode;

    public void Start() => this._stopwatch.Restart();

    public TimeSpan StopAndGetElapsed()
    {
        this._stopwatch.Stop();
        return this.Elapsed;
    }

    public TimeSpan Elapsed => this.Round(this._stopwatch.Elapsed);

    public bool IsRunning => this._stopwatch.IsRunning;

    private TimeSpan Round(TimeSpan time) => this._roundingMode switch {
        TimeRoundingMode.FloorSeconds => TimeSpan.FromSeconds(Math.Floor(time.TotalSeconds)),
        TimeRoundingMode.FloorMilliseconds => TimeSpan.FromMilliseconds(Math.Floor(time.TotalMilliseconds)),
        _ => time
    };
}

/// <summary>
/// タイマーファクトリの実装
/// </summary>
public class GameTimerFactory : IGameTimerFactory
{
    /// <summary>TimeControlSettings から適切なタイマーを生成</summary>
    public IGameTimer Create(TimeControlSettings settings) =>
        new StopwatchGameTimer(settings.RoundingMode);

    /// <summary>デフォルトのタイマーを生成（秒切り捨て）</summary>
    public IGameTimer CreateDefault() =>
        new StopwatchGameTimer(TimeRoundingMode.FloorSeconds);
}
