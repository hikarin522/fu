using Fu.Core.Abstractions;
using Fu.Core.Models;
using Fu.Core.Models.Dto;

namespace Fu.Core.Events;

// =====================================================
// 接続関連イベント
// =====================================================

/// <summary>接続状態変更イベント</summary>
public readonly record struct TransportConnectionStateChangedEvent(TransportConnectionState State);

/// <summary>データチャネル準備完了イベント</summary>
public readonly record struct TransportReadyEvent;

/// <summary>参加者追加イベント</summary>
public readonly record struct TransportParticipantJoinedEvent(TransportParticipantInfo Participant);

/// <summary>参加者離脱イベント</summary>
public readonly record struct TransportParticipantLeftEvent(PlayerId PlayerId);

/// <summary>ホスト昇格イベント</summary>
public readonly record struct TransportBecameHostEvent;

// =====================================================
// ゲームメッセージ受信イベント
// =====================================================

/// <summary>手を受信イベント</summary>
public readonly record struct TransportMoveReceivedEvent(Move Move, TimeSpan Elapsed);

/// <summary>ゲーム開始受信イベント</summary>
public readonly record struct TransportGameStartReceivedEvent;

/// <summary>対局者指定付きゲーム開始受信イベント</summary>
public readonly record struct TransportGameStartWithPlayersReceivedEvent(TransportGameStartInfo Info);

/// <summary>投了受信イベント</summary>
public readonly record struct TransportResignReceivedEvent;

/// <summary>ゲーム状態リクエスト受信イベント</summary>
public readonly record struct TransportGameStateRequestedEvent;

/// <summary>ゲーム状態同期受信イベント</summary>
public readonly record struct TransportGameStateSyncReceivedEvent(TransportGameStateSyncInfo Info);

/// <summary>分岐再開受信イベント</summary>
public readonly record struct TransportBranchResumeReceivedEvent(IReadOnlyList<Move> MoveHistory);

/// <summary>再戦受信イベント</summary>
public readonly record struct TransportRematchReceivedEvent(IReadOnlyList<Move> MoveHistory);

/// <summary>検討開始受信イベント</summary>
public readonly record struct TransportReviewStartReceivedEvent(IReadOnlyList<Move> MoveHistory);

/// <summary>検討の手受信イベント</summary>
public readonly record struct TransportReviewMoveReceivedEvent(Move Move);

// =====================================================
// データ型（IGameTransportから移動）
// =====================================================

/// <summary>接続状態</summary>
public enum TransportConnectionState
{
    Disconnected,
    Connecting,
    Connected
}

/// <summary>
/// 参加者情報（イベント用、イミュータブル）
/// </summary>
public readonly record struct TransportParticipantInfo(PlayerId PlayerId, string Nickname, bool IsHost)
{
    /// <summary>RemotePlayerInfoに変換</summary>
    public RemotePlayerInfo ToRemotePlayerInfo(Turn turn = Turn.None, PlayerConnectionStatus status = PlayerConnectionStatus.Connected) =>
        new(this.PlayerId, this.Nickname, turn, status);
}

/// <summary>
/// 対局開始情報
/// </summary>
public readonly record struct TransportGameStartInfo(
    PlayerId SentePlayerId,
    PlayerId GotePlayerId,
    string SenteNickname,
    string GoteNickname,
    EvaluationDisplayOptions? EvaluationOptions
);

/// <summary>
/// ゲーム状態同期情報
/// </summary>
public readonly record struct TransportGameStateSyncInfo(
    IReadOnlyList<Move> MoveHistory,
    PlayerId SentePlayerId,
    PlayerId GotePlayerId,
    string SenteNickname,
    string GoteNickname,
    GameStatus Status,
    EvaluationDisplayOptions? EvaluationOptions,
    IReadOnlyList<TimeSpan>? MoveTimes
);
