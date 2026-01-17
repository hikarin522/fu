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
/// 局面のキャッシュされた評価情報
/// </summary>
public record CachedEvaluation(
    int? Evaluation,
    int? MateIn,
    string? BestMove,
    string? PrincipalVariation,
    int Depth,
    IReadOnlyList<CandidateMove> Candidates
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

    // 局面キャッシュ（SFEN -> 評価情報）
    private readonly Dictionary<string, CachedEvaluation> _cache = [];
    private const int MaxCacheSize = 100;
    private string? _currentSfen;

    /// <summary>現在の評価値（先手から見た値、センチポーン）</summary>
    public int? Evaluation { get; private set; }

    /// <summary>詰み手数（正:先手勝ち、負:後手勝ち、null:詰みなし）</summary>
    public int? MateIn { get; private set; }

    /// <summary>詰めろ状態（相手が受けなければ次に詰む）</summary>
    public bool IsThreatening { get; private set; }

    /// <summary>詰めろの詰み手数</summary>
    public int? ThreateningMateIn { get; private set; }

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
    /// <param name="depth">探索深さ（0 = 無限探索）</param>
    public async Task AnalyzePositionAsync(Board board, Player currentPlayer, CapturedPieces senteCaptured, CapturedPieces goteCaptured, int depth = 0, int multiPv = 1, bool checkThreatening = false)
    {
        if (!this.IsAvailable) {
            return;
        }

        var sfen = ToSfen(board, currentPlayer, senteCaptured, goteCaptured);
        this._currentSfen = sfen;

        // 詰めろチェックをリセット
        if (!this._isCheckingThreatening) {
            this.IsThreatening = false;
            this.ThreateningMateIn = null;
        }

        // キャッシュをチェック
        if (this._cache.TryGetValue(sfen, out var cached)) {
            // キャッシュから復元（即座に表示）
            this.RestoreFromCache(cached);
            if (OnEvaluationUpdated is { } h) {
                await h();
            }
            // 無限探索の場合は継続、深さ指定の場合はキャッシュ深さ以上ならスキップ
            if (depth > 0 && cached.Depth >= depth) {
                // 詰めろチェックが有効で、まだ詰みがない場合は相手番で分析
                if (checkThreatening && !this._isCheckingThreatening && this.MateIn is null) {
                    await this.CheckThreateningAsync(board, currentPlayer, senteCaptured, goteCaptured);
                }
                return;
            }
        }
        else {
            // 新規局面は初期化
            this._candidates.Clear();
            this.Depth = 0;
            this.Evaluation = null;
            this.MateIn = null;
            this.BestMove = null;
            this.PrincipalVariation = null;
        }

        this._isAnalyzing = true;
        this._checkThreateningAfterAnalysis = checkThreatening;
        this._threateningContext = checkThreatening ? (board, currentPlayer, senteCaptured, goteCaptured) : null;

        // MultiPVを設定（常に送信して状態を確実に同期）
        await this._jsRuntime.InvokeAsync<bool>("ShogiEngine.sendCommand", $"setoption name MultiPV value {multiPv}");

        // depth=0 で無限探索
        await this._jsRuntime.InvokeAsync<bool>("ShogiEngine.requestEvaluation", sfen, depth);
    }

    // 詰めろチェック用のフラグ
    private bool _isCheckingThreatening;
    private bool _checkThreateningAfterAnalysis;
    private (Board board, Player player, CapturedPieces sente, CapturedPieces gote)? _threateningContext;
    private string? _threateningSfen;

    /// <summary>詰めろをチェック（相手番として分析）</summary>
    private async Task CheckThreateningAsync(Board board, Player currentPlayer, CapturedPieces senteCaptured, CapturedPieces goteCaptured)
    {
        if (!this.IsAvailable || this._isCheckingThreatening) {
            return;
        }

        // 相手番として局面を生成
        var opponentPlayer = currentPlayer == Player.Sente ? Player.Gote : Player.Sente;
        var sfen = ToSfen(board, opponentPlayer, senteCaptured, goteCaptured);
        this._threateningSfen = sfen;
        this._isCheckingThreatening = true;

        // 浅い探索で詰みがあるかチェック（詰み探索用に深さ15程度）
        await this._jsRuntime.InvokeAsync<bool>("ShogiEngine.sendCommand", "setoption name MultiPV value 1");
        await this._jsRuntime.InvokeAsync<bool>("ShogiEngine.requestEvaluation", sfen, 15);
    }

    private void RestoreFromCache(CachedEvaluation cached)
    {
        this.Evaluation = cached.Evaluation;
        this.MateIn = cached.MateIn;
        this.BestMove = cached.BestMove;
        this.PrincipalVariation = cached.PrincipalVariation;
        this.Depth = cached.Depth;
        this._candidates.Clear();
        foreach (var candidate in cached.Candidates) {
            this._candidates[candidate.Rank] = candidate;
        }
    }

    private void SaveToCache(string sfen)
    {
        // キャッシュサイズ制限
        if (this._cache.Count >= MaxCacheSize) {
            // 最も古いエントリを削除（簡易的な実装）
            var oldestKey = this._cache.Keys.First();
            this._cache.Remove(oldestKey);
        }

        this._cache[sfen] = new CachedEvaluation(
            this.Evaluation,
            this.MateIn,
            this.BestMove,
            this.PrincipalVariation,
            this.Depth,
            this.Candidates
        );
    }

    /// <summary>指定した局面のキャッシュを取得</summary>
    public CachedEvaluation? GetCachedEvaluation(Board board, Player currentPlayer, CapturedPieces senteCaptured, CapturedPieces goteCaptured)
    {
        var sfen = ToSfen(board, currentPlayer, senteCaptured, goteCaptured);
        return this._cache.GetValueOrDefault(sfen);
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
            if (this._isCheckingThreatening) {
                // 詰めろチェック中のメッセージを処理
                this.ParseThreateningInfoMessage(message);
            }
            else {
                this.ParseInfoMessage(message);
            }
            if (OnEvaluationUpdated is { } handler) {
                await handler();
            }
        }
        else if (message.StartsWith("bestmove ", StringComparison.Ordinal)) {
            if (this._isCheckingThreatening) {
                // 詰めろチェック完了
                this._isCheckingThreatening = false;
                this._threateningSfen = null;
            }
            else {
                var parts = message.Split(' ');
                if (parts.Length >= 2) {
                    this.BestMove = parts[1];
                }
                this._isAnalyzing = false;

                // 探索完了時にキャッシュに保存
                if (this._currentSfen is not null) {
                    this.SaveToCache(this._currentSfen);
                }

                // 詰めろチェックが有効で、詰みがない場合は相手番で分析
                if (this._checkThreateningAfterAnalysis && this.MateIn is null && this._threateningContext is { } ctx) {
                    this._checkThreateningAfterAnalysis = false;
                    await this.CheckThreateningAsync(ctx.board, ctx.player, ctx.sente, ctx.gote);
                }
                this._threateningContext = null;
            }

            if (OnEvaluationUpdated is { } handler) {
                await handler();
            }
        }
    }

    private void ParseThreateningInfoMessage(string message)
    {
        var parts = message.Split(' ');
        var info = ParseUsiInfo(parts);

        // 相手番で詰みが見つかった場合、詰めろ
        if (info.MateIn.HasValue && info.MateIn.Value > 0) {
            // 相手から見て詰みがある = 自分が詰めろをかけている
            this.IsThreatening = true;
            this.ThreateningMateIn = info.MateIn.Value;
        }
    }

    private void ParseInfoMessage(string message)
    {
        var parts = message.Split(' ');
        var info = ParseUsiInfo(parts);

        // 深さがない、または現在より深い場合のみメイン評価値を更新
        var isNewDepth = info.Depth.HasValue && info.Depth.Value > this.Depth;

        // メインの評価値を更新（multipv=1または指定なしの場合、かつ新しい深さの場合）
        if ((info.MultiPv is null or 1) && isNewDepth) {
            this.Depth = info.Depth!.Value;
            if (info.Score.HasValue) {
                this.Evaluation = info.Score.Value;
                this.MateIn = info.MateIn;
            }
            if (info.Pv is not null) {
                this.PrincipalVariation = info.Pv;
            }
            // 新しい深さになったら候補手をクリア（古い深さの結果を消す）
            this._candidates.Clear();
        }

        // 候補手リストを更新（現在の深さ以上の結果のみ）
        if (info.MultiPv.HasValue && info.Move is not null && info.Depth.HasValue && info.Depth.Value >= this.Depth) {
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
    /// <returns>移動元（駒打ちの場合はnull）、移動先、駒打ちの駒種類（打ちでない場合はnull）のタプル。パース失敗時はnull</returns>
    public static ((int col, int row)? from, (int col, int row) to, char? dropPiece)? ParseSfenMove(string sfenMove)
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
                return (null, (8 - toCol, toRow), char.ToUpperInvariant(sfenMove[0]));
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
                return ((8 - fromCol, fromRow), (8 - toCol, toRow), null);
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
