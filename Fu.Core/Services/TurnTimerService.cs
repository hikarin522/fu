using Fu.Core.Abstractions;
using Fu.Core.Models;

namespace Fu.Core.Services;

/// <summary>
/// 手番タイマーサービスの実装
/// </summary>
public sealed class TurnTimerService : ITurnTimerService
{
    private readonly IGameTimerFactory _timerFactory;
    private IGameTimer? _timer;
    private TimeSpan? _pendingRemoteElapsedTime;

    public TurnTimerService(IGameTimerFactory timerFactory)
    {
        this._timerFactory = timerFactory;
    }

    public void Initialize(TimeControlSettings settings)
    {
        if (this._timer is IDisposable disposable) {
            disposable.Dispose();
        }
        this._timer = this._timerFactory.Create(settings);
        this._pendingRemoteElapsedTime = null;
    }

    public void Start()
    {
        if (this._timer is null) {
            this._timer = this._timerFactory.CreateDefault();
        }
        this._timer.Start();
    }

    public TimeSpan StopAndGetElapsed()
    {
        if (this._timer is null) {
            return TimeSpan.Zero;
        }

        TimeSpan elapsed;
        if (this._pendingRemoteElapsedTime is { } remoteTime) {
            this._timer.Start();
            elapsed = remoteTime;
            this._pendingRemoteElapsedTime = null;
        } else {
            elapsed = this._timer.StopAndGetElapsed();
            this._timer.Start();
        }

        return elapsed;
    }

    public void StopOnly()
    {
        this._timer?.StopAndGetElapsed();
    }

    public TimeSpan CurrentElapsed =>
        this._timer?.IsRunning == true ? this._timer.Elapsed : TimeSpan.Zero;

    public bool IsRunning => this._timer?.IsRunning ?? false;

    public void SetPendingRemoteElapsedTime(TimeSpan? time) =>
        this._pendingRemoteElapsedTime = time;

    public void Dispose()
    {
        if (this._timer is IDisposable disposable) {
            disposable.Dispose();
        }
    }
}
