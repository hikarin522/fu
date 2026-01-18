using R3;

using Fu.Core.Models;
using Fu.Core.Models.Dto;

namespace Fu.Core.Abstractions;

/// <summary>
/// ゲーム通信のトランスポート層
/// WebRTC、WebSocket、その他の通信方式を抽象化
/// </summary>
public interface IGameTransport : IAsyncDisposable
{
    /// <summary>接続状態</summary>
    TransportConnectionState ConnectionState { get; }

    /// <summary>接続中か</summary>
    bool IsConnected { get; }

    /// <summary>自分のプレイヤーID</summary>
    PlayerId? MyPlayerId { get; }

    /// <summary>自分のニックネーム</summary>
    string? MyNickname { get; }

    /// <summary>ホスト（部屋主）かどうか</summary>
    bool IsHost { get; }

    /// <summary>参加者一覧</summary>
    IReadOnlyCollection<TransportParticipant> Participants { get; }

    #region Observable

    /// <summary>接続状態が変化した時</summary>
    Observable<TransportConnectionState> ConnectionStateChanged { get; }

    /// <summary>データチャネルが準備完了した時</summary>
    Observable<Unit> Ready { get; }

    /// <summary>参加者が加わった時</summary>
    Observable<TransportParticipant> ParticipantJoined { get; }

    /// <summary>参加者が離脱した時</summary>
    Observable<PlayerId> ParticipantLeft { get; }

    /// <summary>ホストになった時</summary>
    Observable<Unit> BecameHost { get; }

    /// <summary>手を受信した時</summary>
    Observable<(Move Move, TimeSpan Elapsed)> MoveReceived { get; }

    /// <summary>ゲーム開始を受信した時</summary>
    Observable<Unit> GameStartReceived { get; }

    /// <summary>対局者指定付きゲーム開始を受信した時</summary>
    Observable<GameStartInfo> GameStartWithPlayersReceived { get; }

    /// <summary>投了を受信した時</summary>
    Observable<Unit> ResignReceived { get; }

    /// <summary>ゲーム状態リクエストを受信した時</summary>
    Observable<Unit> GameStateRequested { get; }

    /// <summary>ゲーム状態同期を受信した時</summary>
    Observable<GameStateSyncInfo> GameStateSyncReceived { get; }

    /// <summary>分岐再開を受信した時</summary>
    Observable<IReadOnlyList<Move>> BranchResumeReceived { get; }

    /// <summary>再戦を受信した時</summary>
    Observable<IReadOnlyList<Move>> RematchReceived { get; }

    /// <summary>検討開始を受信した時</summary>
    Observable<IReadOnlyList<Move>> ReviewStartReceived { get; }

    /// <summary>検討の手を受信した時</summary>
    Observable<Move> ReviewMoveReceived { get; }

    #endregion

    #region 接続管理

    /// <summary>初期化</summary>
    Task InitializeAsync();

    /// <summary>ルームを作成（ホスト用）</summary>
    Task<RoomId> CreateRoomAsync(string nickname, string? savedPlayerId = null);

    /// <summary>ルームに参加</summary>
    Task JoinRoomAsync(RoomId roomId, string nickname, string? savedPlayerId = null);

    /// <summary>切断</summary>
    Task DisconnectAsync();

    #endregion

    #region メッセージ送信

    /// <summary>手を送信</summary>
    Task SendMoveAsync(Move move, TimeSpan elapsedTime);

    /// <summary>ゲーム開始を送信</summary>
    Task SendGameStartAsync();

    /// <summary>対局者指定付きゲーム開始を送信</summary>
    Task SendGameStartAsync(PlayerId sentePlayerId, PlayerId gotePlayerId, EvaluationDisplayOptions? evaluationOptions = null);

    /// <summary>投了を送信</summary>
    Task SendResignAsync();

    /// <summary>ゲーム状態リクエストを送信</summary>
    Task SendGameStateRequestAsync();

    /// <summary>ゲーム状態同期を送信</summary>
    Task SendGameStateSyncAsync(
        IEnumerable<Move> moveHistory,
        PlayerId sentePlayerId,
        PlayerId gotePlayerId,
        string senteNickname,
        string goteNickname,
        GameStatus status,
        EvaluationDisplayOptions? evaluationOptions = null,
        IEnumerable<TimeSpan>? moveTimes = null);

    /// <summary>分岐再開を送信</summary>
    Task SendBranchResumeAsync(IEnumerable<Move> moveHistory);

    /// <summary>再戦を送信</summary>
    Task SendRematchAsync(IEnumerable<Move> moveHistory);

    /// <summary>検討開始を送信</summary>
    Task SendReviewStartAsync(IEnumerable<Move> moveHistory);

    /// <summary>検討の手を送信</summary>
    Task SendReviewMoveAsync(Move move);

    #endregion
}

/// <summary>
/// 接続状態
/// </summary>
public enum TransportConnectionState
{
    Disconnected,
    Connecting,
    Connected
}

/// <summary>
/// 参加者情報
/// </summary>
public record TransportParticipant(PlayerId PlayerId, string Nickname, bool IsHost)
{
    /// <summary>RemotePlayerInfoに変換</summary>
    public RemotePlayerInfo ToRemotePlayerInfo(Turn turn = Turn.None, PlayerConnectionStatus status = PlayerConnectionStatus.Connected) =>
        new(this.PlayerId, this.Nickname, turn, status);
}

/// <summary>
/// 対局開始情報
/// </summary>
public record GameStartInfo(
    PlayerId SentePlayerId,
    PlayerId GotePlayerId,
    string SenteNickname,
    string GoteNickname,
    EvaluationDisplayOptions? EvaluationOptions
);

/// <summary>
/// ゲーム状態同期情報
/// </summary>
public record GameStateSyncInfo(
    IReadOnlyList<Move> MoveHistory,
    PlayerId SentePlayerId,
    PlayerId GotePlayerId,
    string SenteNickname,
    string GoteNickname,
    GameStatus Status,
    EvaluationDisplayOptions? EvaluationOptions,
    IReadOnlyList<TimeSpan>? MoveTimes
);
