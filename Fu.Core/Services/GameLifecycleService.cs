using System.Collections.Immutable;

using Fu.Core.Abstractions;
using Fu.Core.Models;

namespace Fu.Core.Services;

/// <summary>
/// ゲームライフサイクルサービスの実装
/// 新規対局、リセット、投了、状態復元を担当
/// </summary>
public class GameLifecycleService : IGameLifecycleService
{
    private readonly IShogiRules _rules;
    private readonly ShogiGameState _state;
    private readonly IGameEventPublisher _events;
    private readonly ITurnTimerService _timer;
    private readonly IBoardCache _boardCache;

    public GameLifecycleService(
        IShogiRules rules,
        ShogiGameState state,
        IGameEventPublisher events,
        ITurnTimerService timer,
        IBoardCache boardCache)
    {
        this._rules = rules;
        this._state = state;
        this._events = events;
        this._timer = timer;
        this._boardCache = boardCache;
    }

    public Task NewGameAsync() =>
        this.NewGameAsync(TimeControlSettings.None);

    public Task NewGameAsync(TimeControlSettings timeSettings)
    {
        var localTurn = this._state.State.LocalTurn;

        // タイマーを初期化
        this._timer.Initialize(timeSettings);

        var timeState = timeSettings.Type == TimeControlType.None
            ? null
            : GameTimeState.Initial(timeSettings);

        // 新しいスコープで呼ばれる前提なので、State/MoveTree/BoardCacheは初期状態
        // LocalTurnとゲーム開始に必要な設定のみ更新
        this._state.State.LocalTurn = localTurn;
        this._state.State.Status = GameStatus.Playing;
        this._state.State.MoveTimes = [];
        this._state.State.TimeState = timeState;

        this._timer.Start();
        this._events.NotifyStateChanged();

        return Task.CompletedTask;
    }

    public Task SetLocalTurnAsync(Turn turn)
    {
        this._state.State.LocalTurn = turn;
        this._events.NotifyStateChanged();
        return Task.CompletedTask;
    }

    public Task ResignAsync()
    {
        this._state.State.Status = this._state.State.CurrentTurn.GetOpponent().GetWinStatus();
        this._events.NotifyStateChanged();
        return Task.CompletedTask;
    }

    public Task RestoreStateAsync(IReadOnlyList<Move> moveHistory, GameStatus status, IReadOnlyList<TimeSpan>? moveTimes = null)
    {
        var localTurn = this._state.State.LocalTurn;
        var (board, firstCaptured, secondCaptured, currentTurn) = this._rules.ReconstructBoard(moveHistory);

        // MoveTreeに履歴を追加（新しいスコープで呼ばれる前提なので初期状態）
        foreach (var move in moveHistory) {
            this._state.MoveTree.AddMove(move);
        }

        var times = moveTimes?.ToImmutableList();

        this._state.State = new GameState(
            board,
            currentTurn,
            status,
            firstCaptured,
            secondCaptured,
            [.. moveHistory],
            localTurn,
            null,
            null,
            times
        );

        if (status == GameStatus.Playing) {
            this._timer.Start();
        }

        this._events.NotifyStateChanged();
        return Task.CompletedTask;
    }
}
