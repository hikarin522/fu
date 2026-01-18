using Fu.Core.Models;

namespace Fu.Core.Services;

/// <summary>
/// 将棋ゲームのセッション固有の状態（純粋なデータクラス）
/// タブ単位でScopedとして登録され、新規対局時に手動でリセットされる
/// </summary>
public sealed class ShogiGameState
{
    /// <summary>ゲーム状態</summary>
    public GameState State { get; set; } = GameState.Initial;

    /// <summary>棋譜ツリー（分岐対応）</summary>
    public MoveTree MoveTree { get; set; } = new();

    /// <summary>最後に記録された手の消費時間（送信用）</summary>
    public TimeSpan LastMoveElapsedTime { get; set; }
}
