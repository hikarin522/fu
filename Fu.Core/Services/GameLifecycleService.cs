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
    private readonly GameStore _store;
    private readonly ITurnTimerService _timer;
    private readonly IBoardCache _boardCache;

    public GameLifecycleService(
        IShogiRules rules,
        GameStore store,
        ITurnTimerService timer,
        IBoardCache boardCache)
    {
        this._rules = rules;
        this._store = store;
        this._timer = timer;
        this._boardCache = boardCache;
    }

    public Task NewGameAsync() =>
        this.NewGameAsync(TimeControlSettings.None);

    public Task NewGameAsync(TimeControlSettings timeSettings)
    {
        var localTurn = this._store.State.LocalTurn;

        // タイマーを初期化
        this._timer.Initialize(timeSettings);

        var timeState = timeSettings.Type == TimeControlType.None
            ? null
            : GameTimeState.Initial(timeSettings);

        // 新しいスコープで呼ばれる前提なので、State/MoveTree/BoardCacheは初期状態
        // LocalTurnとゲーム開始に必要な設定のみ更新
        this._store.Update(state => {
            state.LocalTurn = localTurn;
            state.Status = GameStatus.Playing;
            state.MoveTimes = [];
            state.TimeState = timeState;
        });

        this._timer.Start();

        return Task.CompletedTask;
    }

    public Task SetLocalTurnAsync(Turn turn)
    {
        this._store.Update(state => state.LocalTurn = turn);
        return Task.CompletedTask;
    }

    public Task ResignAsync()
    {
        this._store.Update(state => {
            state.Status = state.CurrentTurn.GetOpponent().GetWinStatus();
        });
        return Task.CompletedTask;
    }

    public Task RestoreStateAsync(IReadOnlyList<Move> moveHistory, GameStatus status, IReadOnlyList<TimeSpan>? moveTimes = null)
    {
        var localTurn = this._store.State.LocalTurn;
        var (board, firstCaptured, secondCaptured, currentTurn) = this._rules.ReconstructBoard(moveHistory);

        // MoveTreeに履歴を追加（新しいスコープで呼ばれる前提なので初期状態）
        foreach (var move in moveHistory) {
            this._store.MoveTree.AddMove(move);
        }

        var times = moveTimes?.ToList();

        this._store.Update(state => {
            state.Board = board;
            state.CurrentTurn = currentTurn;
            state.Status = status;
            state.FirstCaptured = firstCaptured;
            state.SecondCaptured = secondCaptured;
            state.MoveHistory = [.. moveHistory];
            state.LocalTurn = localTurn;
            state.ViewingMoveIndex = null;
            state.ViewingBranchHistory = null;
            state.MoveTimes = times;
            state.TimeState = null;
        });

        if (status == GameStatus.Playing) {
            this._timer.Start();
        }

        return Task.CompletedTask;
    }
}
