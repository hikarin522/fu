using Fu.Core.Abstractions;
using Fu.Core.Models.Dto;

namespace Fu.Core.Models;

/// <summary>
/// 対局セッション情報
/// 対局者情報、評価オプション、役割判定を管理
/// </summary>
public record GameSession
{
    /// <summary>先手プレイヤー</summary>
    public PlayerInfo? FirstPlayer { get; init; }

    /// <summary>後手プレイヤー</summary>
    public PlayerInfo? SecondPlayer { get; init; }

    /// <summary>評価値表示オプション</summary>
    public EvaluationDisplayOptions EvaluationOptions { get; init; } = new();

    /// <summary>先手プレイヤーID</summary>
    public PlayerId? FirstPlayerId => this.FirstPlayer?.PlayerId;

    /// <summary>後手プレイヤーID</summary>
    public PlayerId? SecondPlayerId => this.SecondPlayer?.PlayerId;

    /// <summary>先手ニックネーム</summary>
    public string FirstNickname => this.FirstPlayer?.Nickname ?? "先手";

    /// <summary>後手ニックネーム</summary>
    public string SecondNickname => this.SecondPlayer?.Nickname ?? "後手";

    /// <summary>対局が設定済みか</summary>
    public bool IsConfigured => this.FirstPlayer is not null && this.SecondPlayer is not null;

    /// <summary>指定したプレイヤーが対局者かどうか</summary>
    public bool IsPlayer(PlayerId? playerId) =>
        playerId is not null && (playerId == this.FirstPlayerId || playerId == this.SecondPlayerId);

    /// <summary>指定したプレイヤーが観戦者かどうか</summary>
    public bool IsSpectator(PlayerId? playerId, GameStatus status) =>
        !this.IsPlayer(playerId) && status == GameStatus.Playing;

    /// <summary>指定したプレイヤーのTurnを取得</summary>
    public Turn GetLocalTurn(PlayerId? playerId) =>
        playerId == this.FirstPlayerId ? Turn.First
        : playerId == this.SecondPlayerId ? Turn.Second
        : Turn.None;

    /// <summary>プレイヤーIDから役割を取得</summary>
    public string GetRole(PlayerId playerId) =>
        playerId == this.FirstPlayerId ? "先手"
        : playerId == this.SecondPlayerId ? "後手"
        : "観戦";

    /// <summary>プレイヤーIDからソート順を取得（先手=0, 後手=1, 観戦=2）</summary>
    public int GetSortOrder(PlayerId playerId) =>
        playerId == this.FirstPlayerId ? 0
        : playerId == this.SecondPlayerId ? 1
        : 2;

    /// <summary>対局者を設定して新しいセッションを作成</summary>
    public GameSession WithPlayers(PlayerInfo first, PlayerInfo second, EvaluationDisplayOptions? options = null) =>
        this with
        {
            FirstPlayer = first with { Turn = Turn.First },
            SecondPlayer = second with { Turn = Turn.Second },
            EvaluationOptions = options ?? new EvaluationDisplayOptions()
        };

    /// <summary>空のセッション</summary>
    public static GameSession Empty => new();
}

/// <summary>ローカルストレージに保存されるゲームセッション情報</summary>
public sealed record GameSessionInfo(RoomId RoomId, string Nickname, PlayerId PlayerId);
