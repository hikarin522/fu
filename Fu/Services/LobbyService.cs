using Fu.Core.Abstractions;
using Fu.Core.Events;
using Fu.Core.Models;

namespace Fu.Services;

/// <summary>
/// ロビー/ルーム管理サービス
/// ルームの作成・参加・参加者管理を担当
/// </summary>
public class LobbyService(ITransportConnection transport)
{
    #region プロパティ

    /// <summary>現在のルーム</summary>
    public Room CurrentRoom => new(
        (transport as IGameTransport)?.RoomId ?? new RoomId(""),
        transport.Participants,
        transport.Participants.Where(p => p.IsHost).Select(p => p.PlayerId).FirstOrDefault());

    /// <summary>ルームID</summary>
    public RoomId? RoomId => (transport as IGameTransport)?.RoomId;

    /// <summary>自分のプレイヤーID</summary>
    public PlayerId? MyPlayerId => transport.MyPlayerId;

    /// <summary>自分のニックネーム</summary>
    public string? MyNickname => transport.MyNickname;

    /// <summary>接続中か</summary>
    public bool IsConnected => transport.IsConnected;

    /// <summary>ホストか</summary>
    public bool IsHost => transport.IsHost;

    /// <summary>参加者一覧</summary>
    public IReadOnlyCollection<TransportParticipantInfo> Participants => transport.Participants;

    /// <summary>接続状態</summary>
    public TransportConnectionState ConnectionState => transport.ConnectionState;

    #endregion

    #region ルーム操作

    /// <summary>
    /// トランスポートを初期化
    /// </summary>
    public Task InitializeAsync() => transport.InitializeAsync();

    /// <summary>
    /// ルームを作成
    /// </summary>
    public Task<RoomId> CreateRoomAsync(string nickname, PlayerId? savedPlayerId = null) =>
        transport.CreateRoomAsync(nickname, savedPlayerId);

    /// <summary>
    /// ルームに参加
    /// </summary>
    public Task JoinRoomAsync(RoomId roomId, string nickname, PlayerId? savedPlayerId = null) =>
        transport.JoinRoomAsync(roomId, nickname, savedPlayerId);

    /// <summary>
    /// 切断
    /// </summary>
    public Task DisconnectAsync() => transport.DisconnectAsync();

    #endregion
}
