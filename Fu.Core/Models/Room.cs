using Fu.Core.Events;

namespace Fu.Core.Models;

/// <summary>
/// ルーム情報
/// </summary>
public record Room(
    RoomId Id,
    IReadOnlyCollection<TransportParticipantInfo> Participants,
    PlayerId? HostPlayerId)
{
    /// <summary>指定したプレイヤーがホストかどうか</summary>
    public bool IsHost(PlayerId? playerId) =>
        playerId is not null && playerId == this.HostPlayerId;

    /// <summary>参加者を取得</summary>
    public TransportParticipantInfo? GetParticipant(PlayerId playerId) =>
        this.Participants.FirstOrDefault(p => p.PlayerId == playerId);

    /// <summary>参加者数</summary>
    public int ParticipantCount => this.Participants.Count;

    /// <summary>空のルーム</summary>
    public static Room Empty => new(new RoomId(""), [], null);
}
