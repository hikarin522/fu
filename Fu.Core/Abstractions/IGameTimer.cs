namespace Fu.Core.Abstractions;

/// <summary>
/// ゲーム用タイマーのインターフェース
/// </summary>
public interface IGameTimer
{
    /// <summary>タイマーを開始（リスタート）</summary>
    void Start();

    /// <summary>タイマーを停止し、丸め済みの経過時間を返す</summary>
    TimeSpan StopAndGetElapsed();

    /// <summary>現在の経過時間（丸め済み）</summary>
    TimeSpan Elapsed { get; }

    /// <summary>タイマーが動作中か</summary>
    bool IsRunning { get; }
}
