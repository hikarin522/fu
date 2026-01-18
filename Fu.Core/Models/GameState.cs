using System.Collections.Immutable;

using Fu.Core.Abstractions;

namespace Fu.Core.Models;

public enum GameStatus
{
    WaitingForConnection,
    Playing,
    CheckmateFirst,  // 先手の勝ち
    CheckmateSecond, // 後手の勝ち
    Resign,
    TimeoutFirst,    // 先手の時間切れ（後手の勝ち）
    TimeoutSecond,   // 後手の時間切れ（先手の勝ち）
    Reviewing        // 検討モード（手番関係なく自由に駒を動かせる）
}

public static class GameStatusExtensions
{
    /// <summary>ゲームが終了しているか</summary>
    public static bool IsGameOver(this GameStatus status) =>
        status is GameStatus.CheckmateFirst or GameStatus.CheckmateSecond or GameStatus.Resign
               or GameStatus.TimeoutFirst or GameStatus.TimeoutSecond;

    /// <summary>勝者を取得（終了していない場合はnull）</summary>
    public static Turn? GetWinner(this GameStatus status) => status switch {
        GameStatus.CheckmateFirst or GameStatus.TimeoutSecond => Turn.First,
        GameStatus.CheckmateSecond or GameStatus.TimeoutFirst => Turn.Second,
        _ => null
    };

    /// <summary>勝者の勝利メッセージを取得</summary>
    public static string? GetResultMessage(this GameStatus status) => status switch {
        GameStatus.CheckmateFirst => "先手の勝ち！",
        GameStatus.CheckmateSecond => "後手の勝ち！",
        GameStatus.TimeoutFirst => "先手時間切れ - 後手の勝ち！",
        GameStatus.TimeoutSecond => "後手時間切れ - 先手の勝ち！",
        _ => null
    };

    /// <summary>プレイヤーの勝利ステータスを取得</summary>
    public static GameStatus GetWinStatus(this Turn turn) => turn switch {
        Turn.First => GameStatus.CheckmateFirst,
        Turn.Second => GameStatus.CheckmateSecond,
        _ => throw new ArgumentException("Invalid turn for win status", nameof(turn))
    };

    /// <summary>プレイヤーの時間切れステータスを取得</summary>
    public static GameStatus GetTimeoutStatus(this Turn turn) => turn switch {
        Turn.First => GameStatus.TimeoutFirst,
        Turn.Second => GameStatus.TimeoutSecond,
        _ => throw new ArgumentException("Invalid turn for timeout status", nameof(turn))
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
    Turn CurrentTurn,
    GameStatus Status,
    CapturedPieces FirstCaptured,
    CapturedPieces SecondCaptured,
    ImmutableList<Move> MoveHistory,
    Turn LocalTurn,
    int? ViewingMoveIndex = null,
    ImmutableList<Move>? ViewingBranchHistory = null,
    ImmutableList<TimeSpan>? MoveTimes = null,
    GameTimeState? TimeState = null)
{
    /// <summary>各手の消費時間リスト</summary>
    public IReadOnlyList<TimeSpan> Times => this.MoveTimes ?? [];

    /// <summary>持ち時間管理（null = 時間制限なし）</summary>
    public GameTimeState TimeControl => this.TimeState ?? GameTimeState.None;

    /// <summary>時間制限が有効か</summary>
    public bool HasTimeControl => this.TimeState is not null &&
                                   this.TimeState.Settings.Type != TimeControlType.None;

    public static GameState Initial => new(
        new Board(),
        Turn.First,
        GameStatus.WaitingForConnection,
        CapturedPieces.Empty,
        CapturedPieces.Empty,
        [],
        Turn.None
    );

    public CapturedPieces GetCapturedPieces(Turn turn) =>
        turn == Turn.First ? this.FirstCaptured : this.SecondCaptured;

    /// <summary>自分の手番かどうか</summary>
    public bool IsMyTurn => this.LocalTurn == this.CurrentTurn;

    /// <summary>別のブランチを見ているかどうか</summary>
    public bool IsViewingDifferentBranch => this.ViewingBranchHistory is not null;

    /// <summary>現在表示中のブランチの棋譜</summary>
    public IReadOnlyList<Move> DisplayBranchHistory => this.ViewingBranchHistory ?? this.MoveHistory;

    /// <summary>過去の局面を見ているかどうか</summary>
    public bool IsViewingPastPosition => this.ViewingMoveIndex.HasValue && this.ViewingMoveIndex.Value < this.MoveHistory.Count;

    /// <summary>棋譜閲覧モード中かどうか（過去の局面を見ている、または別のブランチを見ている）</summary>
    public bool IsReviewing => this.IsViewingDifferentBranch || this.IsViewingPastPosition;

    /// <summary>対局中のブランチの最新局面を見ているかどうか</summary>
    public bool IsAtActiveBranchLatest => !this.IsReviewing;

    /// <summary>現在表示中の手数（0=初期配置、1=1手目後...）</summary>
    public int DisplayMoveIndex => this.ViewingMoveIndex ?? this.DisplayBranchHistory.Count;

    /// <summary>手番を交代した新しい状態を返す</summary>
    public GameState SwitchTurn() =>
        this with { CurrentTurn = this.CurrentTurn.GetOpponent() };

    /// <summary>持ち駒を更新した新しい状態を返す</summary>
    public GameState WithCapturedPieces(Turn turn, CapturedPieces captured) =>
        turn == Turn.First
            ? this with { FirstCaptured = captured }
            : this with { SecondCaptured = captured };

    /// <summary>先手の累計消費時間</summary>
    public TimeSpan FirstTotalTime => this.Times
        .Where((_, i) => i % 2 == 0)  // 0, 2, 4, ... は先手
        .Aggregate(TimeSpan.Zero, (sum, t) => sum + t);

    /// <summary>後手の累計消費時間</summary>
    public TimeSpan SecondTotalTime => this.Times
        .Where((_, i) => i % 2 == 1)  // 1, 3, 5, ... は後手
        .Aggregate(TimeSpan.Zero, (sum, t) => sum + t);
}
