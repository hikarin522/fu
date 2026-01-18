using Fu.Core.Models;

namespace Fu.Core.Events;

/// <summary>
/// ゲーム状態が変更された時のイベント
/// </summary>
public readonly record struct GameStateChangedEvent;

/// <summary>
/// ブランチから再開された時のイベント
/// </summary>
public readonly record struct BranchResumedEvent(IReadOnlyList<Move> MoveHistory);

/// <summary>
/// 検討モードが開始された時のイベント
/// </summary>
public readonly record struct ReviewStartedEvent(IReadOnlyList<Move> MoveHistory);

/// <summary>
/// 検討モードで指し手が行われた時のイベント
/// </summary>
public readonly record struct ReviewMoveEvent(Move Move);
