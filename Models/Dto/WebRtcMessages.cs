namespace ShogiGame.Models.Dto;

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