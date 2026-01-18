using Fu.Core.Models;

namespace Fu.Core.Abstractions;

/// <summary>
/// ゲームイベントの発行インターフェース
/// 対局スコープでScopedとして登録される
/// </summary>
public interface IGameEventPublisher : IDisposable
{
    void NotifyStateChanged();
    void NotifyBranchResumed(IReadOnlyList<Move> moveHistory);
    void NotifyReviewStarted(IReadOnlyList<Move> moveHistory);
    void NotifyReviewMove(Move move);
}
