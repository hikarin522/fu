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
