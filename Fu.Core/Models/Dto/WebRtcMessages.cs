namespace Fu.Core.Models.Dto;

/// <summary>
/// WebRTC メッセージの基底型
/// </summary>
public abstract record WebRtcMessage(string Type);

/// <summary>
/// 指し手送信メッセージ
/// </summary>
public sealed record MoveMessage(MoveDto Move) : WebRtcMessage("move");

/// <summary>
/// ゲーム開始メッセージ
/// </summary>
public sealed record GameStartMessage() : WebRtcMessage("gameStart");

/// <summary>
/// 対局者指定付きゲーム開始メッセージ
/// </summary>
public sealed record GameStartWithPlayersMessage(string SentePeerId, string GotePeerId) : WebRtcMessage("gameStartWithPlayers");

/// <summary>
/// 投了メッセージ
/// </summary>
public sealed record ResignMessage() : WebRtcMessage("resign");

/// <summary>
/// ゲーム状態同期メッセージ（途中参加者向け）
/// </summary>
public sealed record GameStateSyncMessage(
    MoveDto[] MoveHistory,
    string SentePeerId,
    string GotePeerId,
    string SenteNickname,
    string GoteNickname,
    string Status
) : WebRtcMessage("gameStateSync");

/// <summary>
/// ゲーム状態リクエストメッセージ（途中参加者から）
/// </summary>
public sealed record GameStateRequestMessage() : WebRtcMessage("gameStateRequest");

/// <summary>
/// 分岐再開メッセージ（棋譜の途中から再開する場合）
/// </summary>
public sealed record BranchResumeMessage(MoveDto[] MoveHistory) : WebRtcMessage("branchResume");
