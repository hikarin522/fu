using Fu.Core.Models;

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

/// <summary>
/// ゲームタイマーを生成するファクトリのインターフェース
/// </summary>
public interface IGameTimerFactory
{
    /// <summary>TimeControlSettings から適切なタイマーを生成</summary>
    IGameTimer Create(TimeControlSettings settings);

    /// <summary>デフォルトのタイマーを生成</summary>
    IGameTimer CreateDefault();
}
