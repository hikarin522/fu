namespace Fu.Core.Abstractions;

/// <summary>
/// USI infoメッセージのパース結果
/// </summary>
public record UsiInfo(
    int? MultiPv,
    int? Depth,
    int? Score,
    int? MateIn,
    string? Pv,
    string? Move);

/// <summary>
/// USI bestmoveレスポンス
/// </summary>
public record UsiBestMove(string Move, string? Ponder);

/// <summary>
/// Go分析のストリーム結果（infoまたはbestmove）
/// </summary>
public abstract record UsiGoResult
{
    /// <summary>info メッセージ</summary>
    public sealed record Info(UsiInfo Value) : UsiGoResult;

    /// <summary>bestmove メッセージ（ストリーム終了）</summary>
    public sealed record BestMove(UsiBestMove Value) : UsiGoResult;
}

/// <summary>
/// USI エンジン情報（id name, id author）
/// </summary>
public record UsiEngineId(string Name, string Author);

/// <summary>
/// go コマンドのオプション
/// </summary>
public record UsiGoOptions
{
    /// <summary>無限探索（stopまで継続）</summary>
    public bool Infinite { get; init; }

    /// <summary>探索深さ</summary>
    public int? Depth { get; init; }

    /// <summary>探索ノード数</summary>
    public long? Nodes { get; init; }

    /// <summary>秒読み（ミリ秒）</summary>
    public int? Byoyomi { get; init; }

    /// <summary>先手持ち時間（ミリ秒）</summary>
    public int? BTime { get; init; }

    /// <summary>後手持ち時間（ミリ秒）</summary>
    public int? WTime { get; init; }

    /// <summary>先手加算時間（ミリ秒）</summary>
    public int? BInc { get; init; }

    /// <summary>後手加算時間（ミリ秒）</summary>
    public int? WInc { get; init; }

    /// <summary>ponder モード</summary>
    public bool Ponder { get; init; }

    /// <summary>詰み探索専用</summary>
    public bool Mate { get; init; }

    /// <summary>詰み探索の制限時間（ミリ秒、Mate=true時のみ）</summary>
    public int? MateTime { get; init; }

    /// <summary>デフォルト：無限探索</summary>
    public static UsiGoOptions Default => new() { Infinite = true };

    /// <summary>深さ指定</summary>
    public static UsiGoOptions WithDepth(int depth) => new() { Depth = depth };
}

/// <summary>
/// USI (Universal Shogi Interface) プロトコル準拠エンジンのインターフェース
/// </summary>
public interface IUsiEngine
{
    #region Properties

    /// <summary>エンジンが利用可能か</summary>
    bool IsAvailable { get; }

    /// <summary>分析中（go実行中）か</summary>
    bool IsAnalyzing { get; }

    /// <summary>エンジン情報</summary>
    UsiEngineId? EngineId { get; }

    #endregion

    #region Lifecycle

    /// <summary>
    /// エンジンを初期化（usi → usiok → isready → readyok）
    /// </summary>
    /// <returns>エンジン情報（失敗時はnull）</returns>
    Task<UsiEngineId?> InitializeAsync();

    /// <summary>エンジンを再起動</summary>
    Task<bool> RestartAsync();

    /// <summary>エンジンを終了（quit）</summary>
    Task QuitAsync();

    #endregion

    #region Commands

    /// <summary>
    /// isready → readyok を待機
    /// </summary>
    Task<bool> IsReadyAsync();

    /// <summary>
    /// 新規対局を開始（usinewgame）
    /// </summary>
    Task NewGameAsync();

    /// <summary>
    /// オプションを設定（setoption name [name] value [value]）
    /// </summary>
    Task SetOptionAsync(string name, string value);

    /// <summary>
    /// オプションを設定（setoption name [name] value [value]）
    /// </summary>
    Task SetOptionAsync(string name, int value);

    /// <summary>
    /// オプションを設定（setoption name [name] value [value]）
    /// </summary>
    Task SetOptionAsync(string name, bool value);

    /// <summary>
    /// 局面を設定（position sfen [sfen] moves [moves...]）
    /// </summary>
    Task PositionAsync(string sfen, IEnumerable<string>? moves = null);

    /// <summary>
    /// 探索開始（go [options]）
    /// ストリームをDisposeするとstopが送信され、bestmove受信後に完了する
    /// </summary>
    /// <returns>info/bestmoveのストリーム（最後は必ずBestMove）</returns>
    IAsyncEnumerable<UsiGoResult> GoAsync(UsiGoOptions options);

    /// <summary>
    /// 探索開始（go infinite または go depth N）- 簡易版
    /// ストリームをDisposeするとstopが送信され、bestmove受信後に完了する
    /// </summary>
    /// <returns>info/bestmoveのストリーム（最後は必ずBestMove）</returns>
    IAsyncEnumerable<UsiGoResult> GoAsync(string sfen, int depth = 0);

    /// <summary>
    /// ponder中に予想手が一致（ponderhit）
    /// </summary>
    Task PonderHitAsync();

    /// <summary>
    /// 対局終了を通知（gameover [result]）
    /// </summary>
    Task GameOverAsync(UsiGameResult result);

    #endregion
}

/// <summary>
/// 対局結果
/// </summary>
public enum UsiGameResult
{
    Win,
    Lose,
    Draw
}
