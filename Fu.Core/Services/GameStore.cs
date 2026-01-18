using MessagePipe;

using Fu.Core.Abstractions;
using Fu.Core.Events;
using Fu.Core.Models;

namespace Fu.Core.Services;

/// <summary>
/// ゲーム状態の中央管理ストア
/// 状態変更時に自動通知を提供
/// </summary>
public sealed class GameStore : IDisposable
{
    private readonly IPublisher<GameStateChangedEvent> _stateChanged;
    private readonly IPublisher<BranchResumedEvent> _branchResumed;
    private readonly IPublisher<ReviewStartedEvent> _reviewStarted;
    private readonly IPublisher<ReviewMoveEvent> _reviewMove;
    private readonly object _lock = new();

    private GameState _state = GameState.Initial;
    private MoveTree _moveTree = new();
    private TimeSpan _lastMoveElapsedTime;

    // バッチ処理用
    private int _batchDepth;
    private bool _hasChanges;

    public GameStore(
        IPublisher<GameStateChangedEvent> stateChanged,
        IPublisher<BranchResumedEvent> branchResumed,
        IPublisher<ReviewStartedEvent> reviewStarted,
        IPublisher<ReviewMoveEvent> reviewMove)
    {
        this._stateChanged = stateChanged;
        this._branchResumed = branchResumed;
        this._reviewStarted = reviewStarted;
        this._reviewMove = reviewMove;
    }

    /// <summary>現在の状態（内部用・mutable）</summary>
    internal GameState MutableState => this._state;

    /// <summary>現在の状態（UI用・読み取り専用）</summary>
    public IReadOnlyGameState State => this._state;

    /// <summary>棋譜ツリー（MoveTreeはmutable）</summary>
    public MoveTree MoveTree => this._moveTree;

    /// <summary>最後の手の消費時間</summary>
    public TimeSpan LastMoveElapsedTime
    {
        get => this._lastMoveElapsedTime;
        set => this._lastMoveElapsedTime = value;
    }

    /// <summary>状態を更新して通知</summary>
    public void Update(Action<GameState> updater)
    {
        lock (this._lock) {
            updater(this._state);

            // バッチ中は通知を遅延
            if (this._batchDepth == 0) {
                this._stateChanged.Publish(new GameStateChangedEvent());
            } else {
                this._hasChanges = true;
            }
        }
    }

    /// <summary>状態を置き換えて通知</summary>
    public void SetState(GameState newState)
    {
        lock (this._lock) {
            this._state = newState;

            if (this._batchDepth == 0) {
                this._stateChanged.Publish(new GameStateChangedEvent());
            } else {
                this._hasChanges = true;
            }
        }
    }

    /// <summary>複数の更新をバッチ処理（1回の通知にまとめる）</summary>
    public void Batch(Action action)
    {
        lock (this._lock) {
            this._batchDepth++;
            if (this._batchDepth == 1) {
                this._hasChanges = false;
            }
        }

        try {
            action();
        } finally {
            lock (this._lock) {
                this._batchDepth--;
                if (this._batchDepth == 0 && this._hasChanges) {
                    this._stateChanged.Publish(new GameStateChangedEvent());
                    this._hasChanges = false;
                }
            }
        }
    }

    /// <summary>通知なしで状態を更新（無限ループ回避用）</summary>
    public void UpdateSilently(Action<GameState> updater)
    {
        lock (this._lock) {
            updater(this._state);
        }
    }

    /// <summary>通知のみ（状態変更なし）</summary>
    public void NotifyStateChanged()
    {
        lock (this._lock) {
            if (this._batchDepth == 0) {
                this._stateChanged.Publish(new GameStateChangedEvent());
            } else {
                this._hasChanges = true;
            }
        }
    }

    /// <summary>MoveTreeを置き換え</summary>
    public void ReplaceMoveTree(MoveTree newTree)
    {
        lock (this._lock) {
            this._moveTree = newTree;
        }
    }

    /// <summary>状態をリセット</summary>
    public void Reset()
    {
        lock (this._lock) {
            this._state = GameState.Initial;
            this._moveTree = new MoveTree();
            this._lastMoveElapsedTime = TimeSpan.Zero;
        }
    }

    /// <summary>分岐再開イベントを発行</summary>
    public void NotifyBranchResumed(IReadOnlyList<Move> moveHistory) =>
        this._branchResumed.Publish(new BranchResumedEvent(moveHistory));

    /// <summary>検討モード開始イベントを発行</summary>
    public void NotifyReviewStarted(IReadOnlyList<Move> moveHistory) =>
        this._reviewStarted.Publish(new ReviewStartedEvent(moveHistory));

    /// <summary>検討モードの手イベントを発行</summary>
    public void NotifyReviewMove(Move move) =>
        this._reviewMove.Publish(new ReviewMoveEvent(move));

    public void Dispose()
    {
        // MessagePipeのPublisherはDIコンテナが管理するため、ここでは何もしない
    }
}
