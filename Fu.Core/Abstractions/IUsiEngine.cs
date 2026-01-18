using R3;

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
/// USI (Universal Shogi Interface) プロトコル準拠エンジンのインターフェース
/// </summary>
public interface IUsiEngine
{
    /// <summary>エンジンが利用可能か</summary>
    bool IsAvailable { get; }

    /// <summary>分析中か</summary>
    bool IsAnalyzing { get; }

    /// <summary>infoメッセージ受信時</summary>
    Observable<UsiInfo> InfoReceived { get; }

    /// <summary>bestmove受信時</summary>
    Observable<UsiBestMove> BestMoveReceived { get; }

    /// <summary>エンジンを初期化（usi → usiok → isready → readyok）</summary>
    Task<bool> InitializeAsync();

    /// <summary>エンジンを再起動</summary>
    Task<bool> RestartAsync();

    /// <summary>
    /// 分析を停止し、bestmoveを返す
    /// </summary>
    /// <returns>最善手（分析中でない場合はnull）</returns>
    Task<UsiBestMove?> StopAsync();

    /// <summary>USIコマンドを送信</summary>
    Task<bool> SendCommandAsync(string command);

    /// <summary>
    /// 局面の評価をリクエスト（go infinite または go depth N）
    /// </summary>
    Task<bool> GoAsync(string sfen, int depth = 0);
}
