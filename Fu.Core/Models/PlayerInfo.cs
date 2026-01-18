using Fu.Core.Abstractions;

namespace Fu.Core.Models;

/// <summary>
/// プレイヤー情報（ID、ニックネーム、対局での役割）
/// </summary>
public record PlayerInfo(
    PlayerId PlayerId,
    string Nickname,
    Turn Turn = Turn.None);

/// <summary>
/// リモートプレイヤー情報（PlayerInfo + 接続状態）
/// WebRTC等のネットワーク通信で使用
/// </summary>
public record RemotePlayerInfo(
    PlayerId PlayerId,
    string Nickname,
    Turn Turn = Turn.None,
    PlayerConnectionStatus ConnectionStatus = PlayerConnectionStatus.Connected)
    : PlayerInfo(PlayerId, Nickname, Turn)
{
    /// <summary>接続中かどうか</summary>
    public bool IsConnected => this.ConnectionStatus == PlayerConnectionStatus.Connected;

    /// <summary>切断状態に変更</summary>
    public RemotePlayerInfo AsDisconnected() =>
        this with { ConnectionStatus = PlayerConnectionStatus.Disconnected };

    /// <summary>接続状態に変更</summary>
    public RemotePlayerInfo AsConnected() =>
        this with { ConnectionStatus = PlayerConnectionStatus.Connected };

    /// <summary>PlayerInfoからRemotePlayerInfoを作成</summary>
    public static RemotePlayerInfo FromPlayerInfo(PlayerInfo player, PlayerConnectionStatus status = PlayerConnectionStatus.Connected) =>
        new(player.PlayerId, player.Nickname, player.Turn, status);
}

/// <summary>
/// プレイヤーの接続状態
/// </summary>
public enum PlayerConnectionStatus
{
    /// <summary>接続中</summary>
    Connected,

    /// <summary>切断中</summary>
    Disconnected,

    /// <summary>再接続中</summary>
    Reconnecting
}
