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
/// 持ち駒を管理するクラス
/// </summary>
public class CapturedPieces : IReadOnlyCapturedPieces
{
    private readonly Dictionary<PieceType, int> _pieces;

    public CapturedPieces() => this._pieces = [];

    public CapturedPieces(IEnumerable<KeyValuePair<PieceType, int>> pieces) =>
        this._pieces = new Dictionary<PieceType, int>(pieces);

    public static CapturedPieces Empty => new();

    public int GetCount(PieceType type) => this._pieces.GetValueOrDefault(type);

    /// <summary>駒を追加（成駒は元の駒として追加）</summary>
    public void Add(PieceType type)
    {
        var baseType = type.GetUnpromotedType();
        this._pieces[baseType] = this._pieces.GetValueOrDefault(baseType) + 1;
    }

    /// <summary>駒を削除（失敗時はfalse）</summary>
    public bool TryRemove(PieceType type)
    {
        if (this._pieces.TryGetValue(type, out var count) && count > 0) {
            this._pieces[type] = count - 1;
            return true;
        }
        return false;
    }

    public IEnumerable<(PieceType type, int count)> GetAll() =>
        this._pieces.Where(x => x.Value > 0).Select(x => (x.Key, x.Value));

    /// <summary>クローンを作成</summary>
    public CapturedPieces Clone() => new(this._pieces);

    /// <summary>内容をリセット</summary>
    public void Clear() => this._pieces.Clear();

    /// <summary>別のCapturedPiecesからコピー</summary>
    public void CopyFrom(CapturedPieces other)
    {
        this._pieces.Clear();
        foreach (var kvp in other._pieces) {
            this._pieces[kvp.Key] = kvp.Value;
        }
    }
}

/// <summary>
/// 対局の状態を表すクラス（mutable）
/// IReadOnlyGameStateを実装し、UI層には読み取り専用として公開
/// </summary>
public class GameState : IReadOnlyGameState
{
    public Board Board { get; set; }
    public Turn CurrentTurn { get; set; }
    public GameStatus Status { get; set; }
    public CapturedPieces FirstCaptured { get; set; }
    public CapturedPieces SecondCaptured { get; set; }
    public List<Move> MoveHistory { get; set; }
    public Turn LocalTurn { get; set; }
    public int? ViewingMoveIndex { get; set; }
    public List<Move>? ViewingBranchHistory { get; set; }
    public List<TimeSpan>? MoveTimes { get; set; }
    public GameTimeState? TimeState { get; set; }

    // IReadOnlyGameState 明示的実装（読み取り専用インターフェース用）
    IReadOnlyCapturedPieces IReadOnlyGameState.FirstCaptured => this.FirstCaptured;
    IReadOnlyCapturedPieces IReadOnlyGameState.SecondCaptured => this.SecondCaptured;
    IReadOnlyList<Move> IReadOnlyGameState.MoveHistory => this.MoveHistory;
    IReadOnlyList<Move>? IReadOnlyGameState.ViewingBranchHistory => this.ViewingBranchHistory;
    IReadOnlyList<TimeSpan>? IReadOnlyGameState.MoveTimes => this.MoveTimes;
    IReadOnlyCapturedPieces IReadOnlyGameState.GetCapturedPieces(Turn turn) => this.GetCapturedPieces(turn);

    public GameState()
    {
        this.Board = new Board();
        this.CurrentTurn = Turn.First;
        this.Status = GameStatus.WaitingForConnection;
        this.FirstCaptured = CapturedPieces.Empty;
        this.SecondCaptured = CapturedPieces.Empty;
        this.MoveHistory = [];
        this.LocalTurn = Turn.None;
    }

    public GameState(
        Board board,
        Turn currentTurn,
        GameStatus status,
        CapturedPieces firstCaptured,
        CapturedPieces secondCaptured,
        IReadOnlyList<Move> moveHistory,
        Turn localTurn,
        int? viewingMoveIndex = null,
        IReadOnlyList<Move>? viewingBranchHistory = null,
        IReadOnlyList<TimeSpan>? moveTimes = null,
        GameTimeState? timeState = null)
    {
        this.Board = board;
        this.CurrentTurn = currentTurn;
        this.Status = status;
        this.FirstCaptured = firstCaptured;
        this.SecondCaptured = secondCaptured;
        this.MoveHistory = [.. moveHistory];
        this.LocalTurn = localTurn;
        this.ViewingMoveIndex = viewingMoveIndex;
        this.ViewingBranchHistory = viewingBranchHistory is not null ? [.. viewingBranchHistory] : null;
        this.MoveTimes = moveTimes is not null ? [.. moveTimes] : null;
        this.TimeState = timeState;
    }

    /// <summary>各手の消費時間リスト</summary>
    public IReadOnlyList<TimeSpan> Times => this.MoveTimes ?? [];

    /// <summary>持ち時間管理（null = 時間制限なし）</summary>
    public GameTimeState TimeControl => this.TimeState ?? GameTimeState.None;

    /// <summary>時間制限が有効か</summary>
    public bool HasTimeControl => this.TimeState is not null &&
                                   this.TimeState.Settings.Type != TimeControlType.None;

    public static GameState Initial => new();

    /// <summary>デフォルト状態（対局が開始されていない状態）</summary>
    public static GameState Default => Initial;

    public CapturedPieces GetCapturedPieces(Turn turn) =>
        turn == Turn.First ? this.FirstCaptured : this.SecondCaptured;

    public void SetCapturedPieces(Turn turn, CapturedPieces captured)
    {
        if (turn == Turn.First) {
            this.FirstCaptured = captured;
        } else {
            this.SecondCaptured = captured;
        }
    }

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

    /// <summary>手番を交代</summary>
    public void SwitchTurn() => this.CurrentTurn = this.CurrentTurn.GetOpponent();

    /// <summary>先手の累計消費時間</summary>
    public TimeSpan FirstTotalTime => this.Times
        .Where((_, i) => i % 2 == 0)  // 0, 2, 4, ... は先手
        .Aggregate(TimeSpan.Zero, (sum, t) => sum + t);

    /// <summary>後手の累計消費時間</summary>
    public TimeSpan SecondTotalTime => this.Times
        .Where((_, i) => i % 2 == 1)  // 1, 3, 5, ... は後手
        .Aggregate(TimeSpan.Zero, (sum, t) => sum + t);

    /// <summary>初期状態にリセット</summary>
    public void Reset()
    {
        this.Board = new Board();
        this.CurrentTurn = Turn.First;
        this.Status = GameStatus.WaitingForConnection;
        this.FirstCaptured = CapturedPieces.Empty;
        this.SecondCaptured = CapturedPieces.Empty;
        this.MoveHistory.Clear();
        this.LocalTurn = Turn.None;
        this.ViewingMoveIndex = null;
        this.ViewingBranchHistory = null;
        this.MoveTimes = null;
        this.TimeState = null;
    }
}
