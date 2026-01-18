namespace Fu.Core.Abstractions;

using Fu.Core.Models;

/// <summary>
/// ゲーム状態の読み取り専用インターフェース
/// UI層に公開する際に使用し、状態の変更を防ぐ
/// </summary>
public interface IReadOnlyGameState
{
    Board Board { get; }
    Turn CurrentTurn { get; }
    GameStatus Status { get; }
    IReadOnlyCapturedPieces FirstCaptured { get; }
    IReadOnlyCapturedPieces SecondCaptured { get; }
    IReadOnlyList<Move> MoveHistory { get; }
    Turn LocalTurn { get; }
    int? ViewingMoveIndex { get; }
    IReadOnlyList<Move>? ViewingBranchHistory { get; }
    IReadOnlyList<TimeSpan>? MoveTimes { get; }
    GameTimeState? TimeState { get; }

    /// <summary>各手の消費時間リスト</summary>
    IReadOnlyList<TimeSpan> Times { get; }

    /// <summary>持ち時間管理（null = 時間制限なし）</summary>
    GameTimeState TimeControl { get; }

    /// <summary>時間制限が有効か</summary>
    bool HasTimeControl { get; }

    /// <summary>指定手番の持ち駒を取得</summary>
    IReadOnlyCapturedPieces GetCapturedPieces(Turn turn);

    /// <summary>自分の手番かどうか</summary>
    bool IsMyTurn { get; }

    /// <summary>別のブランチを見ているかどうか</summary>
    bool IsViewingDifferentBranch { get; }

    /// <summary>現在表示中のブランチの棋譜</summary>
    IReadOnlyList<Move> DisplayBranchHistory { get; }

    /// <summary>過去の局面を見ているかどうか</summary>
    bool IsViewingPastPosition { get; }

    /// <summary>棋譜閲覧モード中かどうか（過去の局面を見ている、または別のブランチを見ている）</summary>
    bool IsReviewing { get; }

    /// <summary>対局中のブランチの最新局面を見ているかどうか</summary>
    bool IsAtActiveBranchLatest { get; }

    /// <summary>現在表示中の手数（0=初期配置、1=1手目後...）</summary>
    int DisplayMoveIndex { get; }

    /// <summary>先手の累計消費時間</summary>
    TimeSpan FirstTotalTime { get; }

    /// <summary>後手の累計消費時間</summary>
    TimeSpan SecondTotalTime { get; }
}

/// <summary>
/// 持ち駒の読み取り専用インターフェース
/// </summary>
public interface IReadOnlyCapturedPieces
{
    /// <summary>指定した駒種の数を取得</summary>
    int GetCount(PieceType type);

    /// <summary>全ての持ち駒を列挙</summary>
    IEnumerable<(PieceType type, int count)> GetAll();
}
