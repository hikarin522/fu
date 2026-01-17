using Microsoft.JSInterop;

using Fu.Core.Models;

namespace Fu.Services;

/// <summary>
/// 候補手の情報
/// </summary>
public record CandidateMove(
    int Rank,
    string Move,
    int? Evaluation,
    int? MateIn,
    string? PrincipalVariation
);

/// <summary>
/// YaneuraOu WASM エンジンとのインターフェース
/// </summary>
public class ShogiEngineService : IAsyncDisposable
{
    private readonly IJSRuntime _jsRuntime;
    private DotNetObjectReference<ShogiEngineService>? _dotNetRef;
    private bool _initialized;
    private bool _isAnalyzing;
    private readonly Dictionary<int, CandidateMove> _candidates = [];

    /// <summary>現在の評価値（先手から見た値、センチポーン）</summary>
    public int? Evaluation { get; private set; }

    /// <summary>詰み手数（正:先手勝ち、負:後手勝ち、null:詰みなし）</summary>
    public int? MateIn { get; private set; }

    /// <summary>最善手</summary>
    public string? BestMove { get; private set; }

    /// <summary>読み筋</summary>
    public string? PrincipalVariation { get; private set; }

    /// <summary>探索深さ</summary>
    public int Depth { get; private set; }

    /// <summary>候補手リスト（MultiPV）</summary>
    public IReadOnlyList<CandidateMove> Candidates => this._candidates.Values.OrderBy(c => c.Rank).ToList();

    /// <summary>エンジンが利用可能か</summary>
    public bool IsAvailable { get; private set; }

    /// <summary>分析中か</summary>
    public bool IsAnalyzing => this._isAnalyzing;

    /// <summary>評価値が更新された時のイベント</summary>
    public event Func<Task>? OnEvaluationUpdated;

    public ShogiEngineService(IJSRuntime jsRuntime) => this._jsRuntime = jsRuntime;

    /// <summary>エンジンを初期化</summary>
    public async Task<bool> InitializeAsync()
    {
        if (this._initialized) {
            return this.IsAvailable;
        }

        try {
            // SharedArrayBufferが利用可能か確認
            var isCrossOriginIsolated = await this._jsRuntime.InvokeAsync<bool>("ShogiEngine.isCrossOriginIsolated");
            if (!isCrossOriginIsolated) {
                Console.WriteLine("Cross-origin isolation is not enabled. Engine will not be available.");
                this._initialized = true;
                this.IsAvailable = false;
                return false;
            }

            this._dotNetRef = DotNetObjectReference.Create(this);
            await this._jsRuntime.InvokeVoidAsync("ShogiEngine.setCallback", this._dotNetRef);

            var success = await this._jsRuntime.InvokeAsync<bool>("ShogiEngine.init");
            this._initialized = true;
            this.IsAvailable = success;

            if (success) {
                // エンジンの設定
                await this._jsRuntime.InvokeAsync<bool>("ShogiEngine.sendCommand", "isready");
            }

            return success;
        }
        catch (Exception ex) {
            Console.WriteLine($"Failed to initialize engine: {ex.Message}");
            this._initialized = true;
            this.IsAvailable = false;
            return false;
        }
    }

    /// <summary>局面を分析</summary>
    public async Task AnalyzePositionAsync(Board board, Player currentPlayer, CapturedPieces senteCaptured, CapturedPieces goteCaptured, int depth = 10, int multiPv = 1)
    {
        if (!this.IsAvailable) {
            return;
        }

        var sfen = ToSfen(board, currentPlayer, senteCaptured, goteCaptured);
        this._isAnalyzing = true;
        this._candidates.Clear();

        // MultiPVを設定
        if (multiPv > 1) {
            await this._jsRuntime.InvokeAsync<bool>("ShogiEngine.sendCommand", $"setoption name MultiPV value {multiPv}");
        }

        await this._jsRuntime.InvokeAsync<bool>("ShogiEngine.requestEvaluation", sfen, depth);
    }

    /// <summary>分析を停止</summary>
    public async Task StopAnalysisAsync()
    {
        if (!this.IsAvailable) {
            return;
        }

        this._isAnalyzing = false;
        await this._jsRuntime.InvokeAsync<bool>("ShogiEngine.stop");
    }

    /// <summary>エンジンからのメッセージを処理</summary>
    [JSInvokable]
    public async Task OnEngineMessage(string message)
    {
        // USIプロトコルのメッセージをパース
        if (message.StartsWith("info ", StringComparison.Ordinal)) {
            this.ParseInfoMessage(message);
            if (OnEvaluationUpdated is { } handler) {
                await handler();
            }
        }
        else if (message.StartsWith("bestmove ", StringComparison.Ordinal)) {
            var parts = message.Split(' ');
            if (parts.Length >= 2) {
                this.BestMove = parts[1];
            }
            this._isAnalyzing = false;
            if (OnEvaluationUpdated is { } handler) {
                await handler();
            }
        }
    }

    private void ParseInfoMessage(string message)
    {
        var parts = message.Split(' ');
        var info = ParseUsiInfo(parts);

        // メインの評価値を更新（multipv=1または指定なしの場合）
        if (info.MultiPv is null or 1) {
            if (info.Depth.HasValue) {
                this.Depth = info.Depth.Value;
            }
            if (info.Score.HasValue) {
                this.Evaluation = info.Score.Value;
                this.MateIn = info.MateIn;
            }
            if (info.Pv is not null) {
                this.PrincipalVariation = info.Pv;
            }
        }

        // 候補手リストを更新
        if (info.MultiPv.HasValue && info.Move is not null) {
            this._candidates[info.MultiPv.Value] = new CandidateMove(
                info.MultiPv.Value,
                info.Move,
                info.Score,
                info.MateIn,
                info.Pv
            );
        }
    }

    private static (int? MultiPv, int? Depth, int? Score, int? MateIn, string? Pv, string? Move) ParseUsiInfo(string[] parts)
    {
        int? multipv = null, depth = null, score = null, mateIn = null;
        string? pv = null, move = null;

        for (var i = 0; i < parts.Length; i++) {
            switch (parts[i]) {
                case "multipv" when i + 1 < parts.Length && int.TryParse(parts[i + 1], out var mpv):
                    multipv = mpv;
                    break;
                case "depth" when i + 1 < parts.Length && int.TryParse(parts[i + 1], out var d):
                    depth = d;
                    break;
                case "score" when i + 2 < parts.Length:
                    if (parts[i + 1] == "cp" && int.TryParse(parts[i + 2], out var cp)) {
                        score = cp;
                    }
                    else if (parts[i + 1] == "mate" && int.TryParse(parts[i + 2], out var mate)) {
                        score = mate > 0 ? 30000 - mate : -30000 - mate;
                        mateIn = mate;
                    }
                    break;
                case "pv" when i + 1 < parts.Length:
                    var pvParts = parts.Skip(i + 1).ToArray();
                    pv = string.Join(" ", pvParts);
                    if (pvParts.Length > 0) {
                        move = pvParts[0];
                    }
                    break;
            }
        }
        return (multipv, depth, score, mateIn, pv, move);
    }

    /// <summary>盤面をSFEN形式に変換</summary>
    private static string ToSfen(Board board, Player currentPlayer, CapturedPieces senteCaptured, CapturedPieces goteCaptured)
    {
        var sb = new System.Text.StringBuilder();

        // 盤面
        for (var row = 0; row < 9; row++) {
            var emptyCount = 0;
            for (var col = 0; col < 9; col++) {
                var piece = board[col, row];
                if (piece is null) {
                    emptyCount++;
                }
                else {
                    if (emptyCount > 0) {
                        sb.Append(emptyCount);
                        emptyCount = 0;
                    }
                    sb.Append(PieceToSfen(piece));
                }
            }
            if (emptyCount > 0) {
                sb.Append(emptyCount);
            }
            if (row < 8) {
                sb.Append('/');
            }
        }

        // 手番
        sb.Append(currentPlayer == Player.Sente ? " b " : " w ");

        // 持ち駒
        var captured = CapturedToSfen(senteCaptured, true) + CapturedToSfen(goteCaptured, false);
        sb.Append(string.IsNullOrEmpty(captured) ? "-" : captured);

        // 手数（常に1）
        sb.Append(" 1");

        return sb.ToString();
    }

    private static string PieceToSfen(Piece piece)
    {
        var basePiece = piece.Type.IsPromoted() ? piece.Type.GetUnpromotedType() : piece.Type;
        var c = basePiece switch {
            PieceType.King => "K", PieceType.Rook => "R", PieceType.Bishop => "B",
            PieceType.Gold => "G", PieceType.Silver => "S", PieceType.Knight => "N",
            PieceType.Lance => "L", PieceType.Pawn => "P", _ => ""
        };
        if (piece.Type.IsPromoted()) {
            c = "+" + c;
        }
        return piece.Owner == Player.Sente ? c : c.ToLowerInvariant();
    }

    private static string CapturedToSfen(CapturedPieces captured, bool isSente)
    {
        var sb = new System.Text.StringBuilder();
        void Append(int count, char c)
        {
            if (count > 0) {
                if (count > 1) {
                    sb.Append(count);
                }
                sb.Append(isSente ? c : char.ToLowerInvariant(c));
            }
        }

        Append(captured.GetCount(PieceType.Rook), 'R');
        Append(captured.GetCount(PieceType.Bishop), 'B');
        Append(captured.GetCount(PieceType.Gold), 'G');
        Append(captured.GetCount(PieceType.Silver), 'S');
        Append(captured.GetCount(PieceType.Knight), 'N');
        Append(captured.GetCount(PieceType.Lance), 'L');
        Append(captured.GetCount(PieceType.Pawn), 'P');

        return sb.ToString();
    }

    /// <summary>SFEN形式の指し手をパースして移動元・移動先の座標を返す</summary>
    /// <param name="sfenMove">SFEN形式の指し手（例: 7g7f, G*5b）</param>
    /// <returns>移動元（駒打ちの場合はnull）、移動先のタプル。パース失敗時はnull</returns>
    public static ((int col, int row)? from, (int col, int row) to)? ParseSfenMove(string sfenMove)
    {
        if (string.IsNullOrEmpty(sfenMove)) {
            return null;
        }

        // 駒打ちの場合（例: G*5b）
        if (sfenMove.Length >= 4 && sfenMove[1] == '*') {
            var toCol = sfenMove[2] - '1';
            var toRow = sfenMove[3] - 'a';
            if (toCol is >= 0 and < 9 && toRow is >= 0 and < 9) {
                // SFEN列は1-9、内部は0-8。SFEN 1 = 内部 8, SFEN 9 = 内部 0
                return (null, (8 - toCol, toRow));
            }
            return null;
        }

        // 通常の移動（例: 7g7f, 7g7f+）
        if (sfenMove.Length >= 4) {
            var fromCol = sfenMove[0] - '1';
            var fromRow = sfenMove[1] - 'a';
            var toCol = sfenMove[2] - '1';
            var toRow = sfenMove[3] - 'a';

            if (fromCol is >= 0 and < 9 && fromRow is >= 0 and < 9 &&
                toCol is >= 0 and < 9 && toRow is >= 0 and < 9) {
                return ((8 - fromCol, fromRow), (8 - toCol, toRow));
            }
        }

        return null;
    }

    public async ValueTask DisposeAsync()
    {
        if (this.IsAvailable) {
            await this.StopAnalysisAsync();
        }
        this._dotNetRef?.Dispose();
        GC.SuppressFinalize(this);
    }
}
