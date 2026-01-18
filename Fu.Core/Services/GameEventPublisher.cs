using MessagePipe;

using Fu.Core.Abstractions;
using Fu.Core.Events;
using Fu.Core.Models;

namespace Fu.Core.Services;

/// <summary>
/// ゲームイベントの発行実装（MessagePipeベース）
/// </summary>
public sealed class GameEventPublisher : IGameEventPublisher
{
    private readonly IPublisher<GameStateChangedEvent> _stateChanged;
    private readonly IPublisher<BranchResumedEvent> _branchResumed;
    private readonly IPublisher<ReviewStartedEvent> _reviewStarted;
    private readonly IPublisher<ReviewMoveEvent> _reviewMove;

    public GameEventPublisher(
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

    public void NotifyStateChanged() => this._stateChanged.Publish(new GameStateChangedEvent());
    public void NotifyBranchResumed(IReadOnlyList<Move> moveHistory) => this._branchResumed.Publish(new BranchResumedEvent(moveHistory));
    public void NotifyReviewStarted(IReadOnlyList<Move> moveHistory) => this._reviewStarted.Publish(new ReviewStartedEvent(moveHistory));
    public void NotifyReviewMove(Move move) => this._reviewMove.Publish(new ReviewMoveEvent(move));

    public void Dispose()
    {
        // MessagePipeのPublisherはDIコンテナが管理するため、ここでは何もしない
    }
}
