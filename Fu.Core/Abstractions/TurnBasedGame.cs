namespace Fu.Core.Abstractions;

/// <summary>
/// 二人零和有限確定完全情報ゲームの手番を表す
/// </summary>
public enum Turn
{
    None = 0,
    First,   // 先手（将棋: 先手、チェス: 白）
    Second   // 後手（将棋: 後手、チェス: 黒）
}

public static class TurnExtensions
{
    /// <summary>相手の手番を取得</summary>
    public static Turn GetOpponent(this Turn turn) => turn switch {
        Turn.First => Turn.Second,
        Turn.Second => Turn.First,
        _ => Turn.None
    };

    /// <summary>先手かどうか</summary>
    public static bool IsFirst(this Turn turn) => turn == Turn.First;

    /// <summary>後手かどうか</summary>
    public static bool IsSecond(this Turn turn) => turn == Turn.Second;

    /// <summary>前進方向を取得（先手: -1 = 上方向, 後手: 1 = 下方向）</summary>
    public static int GetForwardDirection(this Turn turn) =>
        turn == Turn.First ? -1 : 1;
}

/// <summary>
/// ゲームの結果
/// </summary>
public enum GameResult
{
    None,           // 進行中
    FirstWins,      // 先手勝ち
    SecondWins,     // 後手勝ち
    Draw            // 引き分け
}

public static class GameResultExtensions
{
    /// <summary>勝者を取得（なければnull）</summary>
    public static Turn? GetWinner(this GameResult result) => result switch {
        GameResult.FirstWins => Turn.First,
        GameResult.SecondWins => Turn.Second,
        _ => null
    };

    /// <summary>手番の勝利結果を取得</summary>
    public static GameResult GetWinResult(this Turn turn) => turn switch {
        Turn.First => GameResult.FirstWins,
        Turn.Second => GameResult.SecondWins,
        _ => GameResult.None
    };

    /// <summary>ゲームが終了しているか</summary>
    public static bool IsGameOver(this GameResult result) =>
        result is GameResult.FirstWins or GameResult.SecondWins or GameResult.Draw;
}

/// <summary>
/// 盤面座標の抽象インターフェース
/// </summary>
public interface IPosition
{
    /// <summary>有効な座標かどうか</summary>
    bool IsValid { get; }
}

/// <summary>
/// 駒の抽象インターフェース
/// </summary>
public interface IPiece<TPieceType> where TPieceType : Enum
{
    /// <summary>駒の種類</summary>
    TPieceType Type { get; }

    /// <summary>所有者（どちらの手番の駒か）</summary>
    Turn Owner { get; }
}

/// <summary>
/// 指し手の抽象インターフェース
/// </summary>
public interface IMove<TPosition> where TPosition : IPosition
{
    /// <summary>移動先</summary>
    TPosition Destination { get; }

    /// <summary>移動元（打ち駒の場合はnull）</summary>
    TPosition? Origin { get; }

    /// <summary>打ち駒かどうか</summary>
    bool IsDrop { get; }
}

/// <summary>
/// 盤面の抽象インターフェース
/// </summary>
public interface IBoard<TPosition, TPiece>
    where TPosition : IPosition
    where TPiece : class
{
    /// <summary>指定位置の駒を取得（なければnull）</summary>
    TPiece? this[TPosition position] { get; }

    /// <summary>盤面サイズ（幅）</summary>
    int Width { get; }

    /// <summary>盤面サイズ（高さ）</summary>
    int Height { get; }
}

/// <summary>
/// ゲームルールの抽象インターフェース
/// </summary>
public interface IGameRules<TBoard, TPosition, TPiece, TMove, TCaptured>
    where TBoard : IBoard<TPosition, TPiece>
    where TPosition : IPosition
    where TPiece : class
    where TMove : IMove<TPosition>
{
    /// <summary>盤面サイズ</summary>
    (int Width, int Height) BoardSize { get; }

    /// <summary>初期盤面を作成</summary>
    TBoard CreateInitialBoard();

    /// <summary>合法手を取得</summary>
    IEnumerable<TMove> GetLegalMoves(TBoard board, TPosition from, Turn turn, TCaptured captured);

    /// <summary>持ち駒の打てる位置を取得</summary>
    IEnumerable<TPosition> GetLegalDropPositions<TPieceType>(
        TBoard board, Turn turn, TCaptured captured, TPieceType pieceType) where TPieceType : Enum;

    /// <summary>指し手を適用して新しい盤面を返す</summary>
    (TBoard NewBoard, TCaptured NewCaptured) ApplyMove(
        TBoard board, TMove move, Turn turn, TCaptured captured, TCaptured opponentCaptured);

    /// <summary>王手かどうか</summary>
    bool IsInCheck(TBoard board, Turn turn);

    /// <summary>詰みかどうか</summary>
    bool IsCheckmate(TBoard board, Turn turn, TCaptured captured);

    /// <summary>ステイルメイト（引き分け条件）かどうか</summary>
    bool IsStalemate(TBoard board, Turn turn, TCaptured captured);

    /// <summary>ゲーム結果を判定</summary>
    GameResult GetGameResult(TBoard board, Turn currentTurn, TCaptured currentCaptured);
}

/// <summary>
/// ゲーム状態の抽象インターフェース
/// </summary>
public interface IGameState<TMove>
{
    /// <summary>現在の手番</summary>
    Turn CurrentTurn { get; }

    /// <summary>ゲーム結果</summary>
    GameResult Result { get; }

    /// <summary>棋譜</summary>
    IReadOnlyList<TMove> MoveHistory { get; }

    /// <summary>ゲームが進行中かどうか</summary>
    bool IsPlaying => this.Result == GameResult.None;
}
