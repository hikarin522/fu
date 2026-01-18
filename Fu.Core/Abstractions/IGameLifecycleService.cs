using Fu.Core.Models;

namespace Fu.Core.Abstractions;

/// <summary>
/// ゲームのライフサイクル管理サービス
/// 新規対局、リセット、投了、状態復元などを担当
/// </summary>
public interface IGameLifecycleService
{
    /// <summary>新規対局を開始（時間制御なし）</summary>
    Task NewGameAsync();

    /// <summary>新規対局を開始（時間制御あり）</summary>
    Task NewGameAsync(TimeControlSettings timeSettings);

    /// <summary>自分の手番を設定</summary>
    Task SetLocalTurnAsync(Turn turn);

    /// <summary>投了</summary>
    Task ResignAsync();

    /// <summary>途中参加者向けにゲーム状態を復元</summary>
    Task RestoreStateAsync(IReadOnlyList<Move> moveHistory, GameStatus status, IReadOnlyList<TimeSpan>? moveTimes = null);
}
