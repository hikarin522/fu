namespace Fu.Core.Models.Dto;

/// <summary>
/// WebRTC メッセージの基底型
/// </summary>
public abstract record WebRtcMessage(string Type);

/// <summary>
/// 指し手送信メッセージ
/// </summary>
/// <param name="Move">指し手</param>
/// <param name="ElapsedSeconds">消費時間（秒）</param>
public sealed record MoveMessage(MoveDto Move, int ElapsedSeconds = 0) : WebRtcMessage("move");

/// <summary>
/// ゲーム開始メッセージ
/// </summary>
public sealed record GameStartMessage() : WebRtcMessage("gameStart");

/// <summary>
/// 対局者向け評価値表示オプション
/// </summary>
public sealed record EvaluationDisplayOptions(
    bool ShowAdvantage = false,
    bool ShowEvaluationValue = false,
    bool ShowHasMate = false,
    bool ShowMateCount = false
);

/// <summary>
/// 対局者指定付きゲーム開始メッセージ
/// </summary>
public sealed record GameStartWithPlayersMessage(
    string SentePeerId,
    string GotePeerId,
    EvaluationDisplayOptions? EvaluationOptions = null
) : WebRtcMessage("gameStartWithPlayers");

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
    string Status,
    EvaluationDisplayOptions? EvaluationOptions = null,
    int[]? MoveTimes = null
) : WebRtcMessage("gameStateSync");

/// <summary>
/// ゲーム状態リクエストメッセージ（途中参加者から）
/// </summary>
public sealed record GameStateRequestMessage() : WebRtcMessage("gameStateRequest");

/// <summary>
/// 分岐再開メッセージ（棋譜の途中から再開する場合）
/// </summary>
public sealed record BranchResumeMessage(MoveDto[] MoveHistory) : WebRtcMessage("branchResume");

/// <summary>
/// 再戦メッセージ（評価値表示をリセットして新しいゲームとして再開）
/// </summary>
public sealed record RematchMessage(MoveDto[] MoveHistory) : WebRtcMessage("rematch");

/// <summary>
/// 検討モード開始メッセージ
/// </summary>
public sealed record ReviewStartMessage(MoveDto[] MoveHistory) : WebRtcMessage("reviewStart");

/// <summary>
/// 検討モードでの手メッセージ
/// </summary>
public sealed record ReviewMoveMessage(MoveDto Move) : WebRtcMessage("reviewMove");
