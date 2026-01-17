using System.Collections.Immutable;

namespace Fu.Core.Models;

public enum GameStatus
{
    WaitingForConnection,
    Playing,
    CheckmateSente,  // 先手の勝ち
    CheckmateGote,   // 後手の勝ち
    Resign
}

public static class GameStatusExtensions
{
    /// <summary>ゲームが終了しているか</summary>
    public static bool IsGameOver(this GameStatus status) =>
        status is GameStatus.CheckmateSente or GameStatus.CheckmateGote or GameStatus.Resign;

    /// <summary>勝者を取得（終了していない場合はnull）</summary>
    public static Player? GetWinner(this GameStatus status) => status switch {
        GameStatus.CheckmateSente => Player.Sente,
        GameStatus.CheckmateGote => Player.Gote,
        _ => null
    };

    /// <summary>勝者の勝利メッセージを取得</summary>
    public static string? GetResultMessage(this GameStatus status) => status switch {
        GameStatus.CheckmateSente => "先手の勝ち！",
        GameStatus.CheckmateGote => "後手の勝ち！",
        _ => null
    };

    /// <summary>プレイヤーの勝利ステータスを取得</summary>
    public static GameStatus GetWinStatus(this Player player) => player switch {
        Player.Sente => GameStatus.CheckmateSente,
        Player.Gote => GameStatus.CheckmateGote,
        _ => throw new ArgumentException("Invalid player for win status", nameof(player))
    };
}

/// <summary>
/// 持ち駒を管理する不変レコード
/// </summary>
public record CapturedPieces(ImmutableDictionary<PieceType, int> Pieces)
{
    public static readonly CapturedPieces Empty = new(ImmutableDictionary<PieceType, int>.Empty);

    public int GetCount(PieceType type) => this.Pieces.GetValueOrDefault(type);

    /// <summary>駒を追加（成駒は元の駒として追加）して新しいインスタンスを返す</summary>
    public CapturedPieces Add(PieceType type)
    {
        var baseType = type.GetUnpromotedType();
        var newCount = this.Pieces.GetValueOrDefault(baseType) + 1;
        return this with { Pieces = this.Pieces.SetItem(baseType, newCount) };
    }

    /// <summary>駒を削除して新しいインスタンスを返す（失敗時はnull）</summary>
    public CapturedPieces? TryRemove(PieceType type)
    {
        if (this.Pieces.TryGetValue(type, out var count) && count > 0) {
            return this with { Pieces = this.Pieces.SetItem(type, count - 1) };
        }
        return null;
    }

    public IEnumerable<(PieceType type, int count)> GetAll() =>
        this.Pieces.Where(x => x.Value > 0).Select(x => (x.Key, x.Value));
}

/// <summary>
/// 対局の状態を表す不変レコード
/// </summary>
public record GameState(
    Board Board,
    Player CurrentPlayer,
    GameStatus Status,
    CapturedPieces SenteCaptured,
    CapturedPieces GoteCaptured,
    ImmutableList<Move> MoveHistory,
    Player LocalPlayer,
    int? ViewingMoveIndex = null,
    bool IsViewingDifferentBranch = false)
{
    /// <summary>棋譜ツリー（分岐対応）</summary>
    public MoveTree MoveTree { get; init; } = new();

    public static GameState Initial => new(
        new Board(),
        Player.Sente,
        GameStatus.WaitingForConnection,
        CapturedPieces.Empty,
        CapturedPieces.Empty,
        [],
        Player.None
    );

    public CapturedPieces GetCapturedPieces(Player player) =>
        player == Player.Sente ? this.SenteCaptured : this.GoteCaptured;

    /// <summary>自分の手番かどうか</summary>
    public bool IsMyTurn => this.LocalPlayer == this.CurrentPlayer;

    /// <summary>棋譜閲覧モード中かどうか（過去の局面を見ている、または別のブランチを見ている）</summary>
    public bool IsReviewing => this.IsViewingDifferentBranch ||
                               (this.ViewingMoveIndex.HasValue && this.ViewingMoveIndex.Value < this.MoveHistory.Count);

    /// <summary>現在表示中の手数（0=初期配置、1=1手目後...）</summary>
    public int DisplayMoveIndex => this.ViewingMoveIndex ?? this.MoveHistory.Count;

    /// <summary>現在位置に分岐があるか</summary>
    public bool HasBranches => this.MoveTree.HasBranchesAtCurrent;

    /// <summary>次の手の選択肢（分岐）</summary>
    public IReadOnlyList<MoveNode> NextBranches => this.MoveTree.NextMoves;

    /// <summary>手番を交代した新しい状態を返す</summary>
    public GameState SwitchPlayer() =>
        this with { CurrentPlayer = this.CurrentPlayer.GetOpponent() };

    /// <summary>持ち駒を更新した新しい状態を返す</summary>
    public GameState WithCapturedPieces(Player player, CapturedPieces captured) =>
        player == Player.Sente
            ? this with { SenteCaptured = captured }
            : this with { GoteCaptured = captured };
}
