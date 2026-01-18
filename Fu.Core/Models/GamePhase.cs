namespace Fu.Core.Models;

/// <summary>
/// ゲームの状態フェーズ（接続状態とゲーム状態を統合）
/// </summary>
public enum GamePhase
{
    /// <summary>未接続（初期状態）</summary>
    Disconnected,

    /// <summary>接続中</summary>
    Connecting,

    /// <summary>ルーム待機中（対局者を待っている）</summary>
    WaitingInLobby,

    /// <summary>対局中</summary>
    Playing,

    /// <summary>対局終了</summary>
    GameOver,

    /// <summary>検討モード</summary>
    Reviewing
}

public static class GamePhaseExtensions
{
    /// <summary>対局可能な状態か</summary>
    public static bool CanPlay(this GamePhase phase) =>
        phase == GamePhase.Playing;

    /// <summary>ルームに接続済みか</summary>
    public static bool IsInRoom(this GamePhase phase) =>
        phase is GamePhase.WaitingInLobby or GamePhase.Playing or GamePhase.GameOver or GamePhase.Reviewing;

    /// <summary>GameStatusから対応するGamePhaseを取得</summary>
    public static GamePhase ToGamePhase(this GameStatus status) => status switch
    {
        GameStatus.WaitingForConnection => GamePhase.WaitingInLobby,
        GameStatus.Playing => GamePhase.Playing,
        GameStatus.CheckmateFirst or GameStatus.CheckmateSecond or GameStatus.Resign => GamePhase.GameOver,
        GameStatus.Reviewing => GamePhase.Reviewing,
        _ => GamePhase.Disconnected
    };
}
