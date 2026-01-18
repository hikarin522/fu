using Fu.Core.Models;

namespace Fu.Core.Abstractions;

/// <summary>
/// 手番タイマーサービスのインターフェース
/// タブ単位でScopedとして登録され、新規対局時にリセットされる
/// </summary>
public interface ITurnTimerService : IDisposable
{
    /// <summary>指定した時間設定でタイマーを初期化（対局開始時に呼び出す）</summary>
    void Initialize(TimeControlSettings settings);

    /// <summary>タイマーを開始</summary>
    void Start();

    /// <summary>タイマーを停止し、経過時間を返す（次の手番のタイマーを開始）</summary>
    TimeSpan StopAndGetElapsed();

    /// <summary>タイマーを停止のみ（経過時間は破棄）</summary>
    void StopOnly();

    /// <summary>現在の手番の経過時間</summary>
    TimeSpan CurrentElapsed { get; }

    /// <summary>タイマーが動作中か</summary>
    bool IsRunning { get; }

    /// <summary>リモートからの経過時間を設定（次のStopAndGetElapsedで使用）</summary>
    void SetPendingRemoteElapsedTime(TimeSpan? time);
}
