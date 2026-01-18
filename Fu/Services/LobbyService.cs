using R3;

using Fu.Core.Abstractions;
using Fu.Core.Models;

namespace Fu.Services;

/// <summary>
/// ロビー/ルーム管理サービス
/// ルームの作成・参加・参加者管理を担当
/// </summary>
public class LobbyService(IGameTransport transport)
{
    #region プロパティ

    /// <summary>現在のルーム</summary>
    public Room CurrentRoom => new(
        this.RoomId ?? new RoomId(""),
        transport.Participants,
        transport.Participants.FirstOrDefault(p => p.IsHost)?.PlayerId);

    /// <summary>ルームID</summary>
    public RoomId? RoomId { get; private set; }

    /// <summary>自分のプレイヤーID</summary>
    public PlayerId? MyPlayerId => transport.MyPlayerId;

    /// <summary>自分のニックネーム</summary>
    public string? MyNickname => transport.MyNickname;

    /// <summary>接続中か</summary>
    public bool IsConnected => transport.IsConnected;

    /// <summary>ホストか</summary>
    public bool IsHost => transport.IsHost;

    /// <summary>参加者一覧</summary>
    public IReadOnlyCollection<TransportParticipant> Participants => transport.Participants;

    /// <summary>接続状態</summary>
    public TransportConnectionState ConnectionState => transport.ConnectionState;

    #endregion

    #region Observable

    /// <summary>参加者が加わった時</summary>
    public Observable<TransportParticipant> ParticipantJoined => transport.ParticipantJoined;

    /// <summary>参加者が離脱した時</summary>
    public Observable<PlayerId> ParticipantLeft => transport.ParticipantLeft;

    /// <summary>準備完了時</summary>
    public Observable<Unit> Ready => transport.Ready;

    /// <summary>接続状態が変化した時</summary>
    public Observable<TransportConnectionState> ConnectionStateChanged => transport.ConnectionStateChanged;

    /// <summary>ホストになった時</summary>
    public Observable<Unit> BecameHost => transport.BecameHost;

    #endregion

    #region ルーム操作

    /// <summary>
    /// トランスポートを初期化
    /// </summary>
    public Task InitializeAsync() => transport.InitializeAsync();

    /// <summary>
    /// ルームを作成
    /// </summary>
    public async Task<RoomId> CreateRoomAsync(string nickname, string? savedPlayerId = null)
    {
        this.RoomId = await transport.CreateRoomAsync(nickname, savedPlayerId);
        return this.RoomId.Value;
    }

    /// <summary>
    /// ルームに参加
    /// </summary>
    public async Task JoinRoomAsync(RoomId roomId, string nickname, string? savedPlayerId = null)
    {
        await transport.JoinRoomAsync(roomId, nickname, savedPlayerId);
        this.RoomId = roomId;
    }

    /// <summary>
    /// 切断
    /// </summary>
    public async Task DisconnectAsync()
    {
        await transport.DisconnectAsync();
        this.RoomId = null;
    }

    #endregion
}
