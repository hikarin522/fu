using Fu.Core.Events;
using Fu.Core.Models;
using Fu.Core.Models.Dto;

namespace Fu.Core.Abstractions;

/// <summary>
/// トランスポート接続の状態を管理するインターフェース
/// 接続状態、参加者情報などの読み取り専用アクセスを提供
/// </summary>
public interface ITransportConnection
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
    IReadOnlyCollection<TransportParticipantInfo> Participants { get; }

    /// <summary>参加者を取得</summary>
    TransportParticipantInfo? GetParticipant(PlayerId playerId);

    /// <summary>初期化</summary>
    Task InitializeAsync();

    /// <summary>ルームを作成（ホスト用）</summary>
    Task<RoomId> CreateRoomAsync(string nickname, PlayerId? savedPlayerId = null);

    /// <summary>ルームに参加</summary>
    Task JoinRoomAsync(RoomId roomId, string nickname, PlayerId? savedPlayerId = null);

    /// <summary>切断</summary>
    Task DisconnectAsync();
}

/// <summary>
/// トランスポートでメッセージを送信するインターフェース
/// </summary>
public interface ITransportSender
{
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
}

/// <summary>
/// ゲーム通信のトランスポート層
/// 接続管理と送信機能を統合したインターフェース
/// </summary>
public interface IGameTransport : ITransportConnection, ITransportSender, IAsyncDisposable
{
    /// <summary>ルームID（実装固有、取得可能な場合のみ）</summary>
    RoomId? RoomId { get; }
}
