using Microsoft.JSInterop;

using R3;

using Fu.Core;
using Fu.Core.Abstractions;
using Fu.Core.Models;

namespace Fu.Services;

/// <summary>
/// YaneuraOu WASM エンジンとのインターフェース
/// </summary>
public class ShogiEngineService : IAsyncDisposable
{
    private readonly IJSRuntime _jsRuntime;
    private readonly IUsiParser _usiParser;
    private readonly ISfenConverter _sfenConverter;
    private readonly Subject<Unit> _evaluationUpdated = new();
    private readonly Dictionary<int, CandidateMove> _candidates = [];
    private readonly Dictionary<int, CandidateMove> _previousCandidates = [];

    private DotNetObjectReference<ShogiEngineService>? _dotNetRef;
    private TaskCompletionSource? _readyTcs;
    private bool _initialized;
    private bool _isReady;
    private bool _isAnalyzing;
    private int _newDepthCandidateCount;
    private int _lastMultiPv;

    // 現在分析中のノード情報
    private MoveTree? _currentMoveTree;
    private MoveNode? _currentNode;
    private Turn _currentTurn = Turn.First;

    // 最後に分析を開始したノード（遅延結果のキャッシュ更新用）
    private MoveTree? _lastAnalyzedMoveTree;
    private MoveNode? _lastAnalyzedNode;
    private Turn _lastAnalyzedTurn;

    #region Public Properties

    /// <summary>現在の評価値（先手から見た値、センチポーン）</summary>
    public int? Evaluation { get; private set; }

    /// <summary>詰み手数（正:手番側勝ち、負:手番側負け）</summary>
    public int? MateIn { get; private set; }

    /// <summary>詰みを見つけた時の手番</summary>
    public Turn MateTurn { get; private set; }

    /// <summary>最善手</summary>
    public string? BestMove { get; private set; }

    /// <summary>読み筋</summary>
    public string? PrincipalVariation { get; private set; }

    /// <summary>探索深さ</summary>
    public int Depth { get; private set; }

    /// <summary>候補手リスト（MultiPV、前の深さで補完）</summary>
    public IReadOnlyList<CandidateMove> Candidates => this.GetMergedCandidates();

    /// <summary>エンジンが利用可能か</summary>
    public bool IsAvailable { get; private set; }

    /// <summary>分析中か</summary>
    public bool IsAnalyzing => this._isAnalyzing;

    /// <summary>評価値が更新された時</summary>
    public Observable<Unit> EvaluationUpdated => this._evaluationUpdated;

    #endregion

    public ShogiEngineService(IJSRuntime jsRuntime, IUsiParser usiParser, ISfenConverter sfenConverter)
    {
        this._jsRuntime = jsRuntime;
        this._usiParser = usiParser;
        this._sfenConverter = sfenConverter;
    }

    #region Initialization

    /// <summary>エンジンを初期化</summary>
    public async Task<bool> InitializeAsync()
    {
        if (this._initialized) {
            return this.IsAvailable;
        }

        this._initialized = true;

        try {
            // Cross-Origin Isolation確認（SharedArrayBufferに必要）
            if (!await this._jsRuntime.InvokeAsync<bool>("ShogiEngine.isCrossOriginIsolated")) {
                return this.IsAvailable = false;
            }

            // エンジン初期化（JSでUSIハンドシェイク完了まで待機）
            if (!await this._jsRuntime.InvokeAsync<bool>("ShogiEngine.init")) {
                return this.IsAvailable = false;
            }

            // コールバック設定
            this._dotNetRef = DotNetObjectReference.Create(this);
            await this._jsRuntime.InvokeVoidAsync("ShogiEngine.setCallback", this._dotNetRef);

            this.IsAvailable = true;

            // readyok待機
            this._readyTcs = new TaskCompletionSource();
            await this._jsRuntime.InvokeAsync<bool>("ShogiEngine.sendCommand", "isready");

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try {
                await this._readyTcs.Task.WaitAsync(cts.Token);
                this._isReady = true;
            }
            catch (OperationCanceledException) {
                return this.IsAvailable = false;
            }

            return true;
        }
        catch {
            return this.IsAvailable = false;
        }
    }

    #endregion

    #region Analysis

    /// <summary>局面を分析</summary>
    public async Task AnalyzePositionAsync(
        Board board,
        Turn currentTurn,
        IReadOnlyCapturedPieces firstCaptured,
        IReadOnlyCapturedPieces secondCaptured,
        MoveTree moveTree,
        int depth = 0,
        int multiPv = 1)
    {
        if (!this.IsAvailable || !this._isReady) {
            return;
        }

        var currentNode = moveTree.CurrentNode;

        // ノード情報を記録
        this._currentMoveTree = moveTree;
        this._currentNode = currentNode;
        this._currentTurn = currentTurn;
        this._lastAnalyzedMoveTree = moveTree;
        this._lastAnalyzedNode = currentNode;
        this._lastAnalyzedTurn = currentTurn;

        // キャッシュをチェック
        var cached = GetCachedEvaluation(moveTree, currentNode);
        if (cached is not null) {
            this.RestoreFromCache(cached);
            this._evaluationUpdated.OnNext(Unit.Default);

            if (depth > 0 && cached.Depth >= depth) {
                return;
            }
        }
        else {
            this.ResetEvaluation();
        }

        this._isAnalyzing = true;

        // MultiPV設定（変更時のみ）
        if (multiPv != this._lastMultiPv) {
            await this._jsRuntime.InvokeAsync<bool>("ShogiEngine.sendCommand", $"setoption name MultiPV value {multiPv}");
            this._lastMultiPv = multiPv;
        }

        // 分析開始
        var sfen = this._sfenConverter.ToSfen(board, currentTurn, firstCaptured, secondCaptured);
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

    private void ResetEvaluation()
    {
        this._candidates.Clear();
        this.Depth = 0;
        this.Evaluation = null;
        this.MateIn = null;
        this.BestMove = null;
        this.PrincipalVariation = null;
    }

    #endregion

    #region Message Handling

    /// <summary>エンジンからのメッセージを処理</summary>
    [JSInvokable]
    public Task OnEngineMessage(string message)
    {
        if (message == "readyok") {
            this._readyTcs?.TrySetResult();
            return Task.CompletedTask;
        }

        if (!this._isReady) {
            return Task.CompletedTask;
        }

        if (message.StartsWith("info ", StringComparison.Ordinal)) {
            if (this.ParseInfoMessage(message)) {
                this._evaluationUpdated.OnNext(Unit.Default);
            }
        }
        else if (message.StartsWith("bestmove ", StringComparison.Ordinal)) {
            this.HandleBestMove(message);
        }

        return Task.CompletedTask;
    }

    private void HandleBestMove(string message)
    {
        var parts = message.Split(' ');
        if (parts.Length >= 2) {
            this.BestMove = parts[1];
        }

        this._isAnalyzing = false;
        this.SaveToCurrentNode();
        this._evaluationUpdated.OnNext(Unit.Default);
    }

    /// <summary>infoメッセージをパース</summary>
    /// <returns>UIを更新すべき場合true</returns>
    private bool ParseInfoMessage(string message)
    {
        var info = ParseUsiInfo(message.Split(' '));

        // 現在の局面かチェック
        var isCurrentPosition = this._currentMoveTree is not null
            && ReferenceEquals(this._lastAnalyzedMoveTree, this._currentMoveTree)
            && ReferenceEquals(this._lastAnalyzedNode, this._currentNode);

        if (!isCurrentPosition) {
            this.UpdateCacheOnly(info);
            return false;
        }

        var isNewDepth = info.Depth.HasValue && info.Depth.Value > this.Depth;
        var hasMate = info.MateIn.HasValue && this.MateIn is null;

        // 詰み検出後はcpスコアで上書きしない
        if (this.MateIn.HasValue && !info.MateIn.HasValue) {
            return false;
        }

        // 評価値を先手視点に正規化
        var normalizedScore = this._currentTurn == Turn.Second ? -info.Score : info.Score;
        var normalizedMate = this._currentTurn == Turn.Second ? -info.MateIn : info.MateIn;

        // メイン評価値を更新
        if ((info.MultiPv is null or 1) && (isNewDepth || hasMate)) {
            this.UpdateMainEvaluation(info, normalizedScore, isNewDepth, hasMate);
        }

        // 候補手を更新
        if (info.MultiPv.HasValue && info.Move is not null && info.Depth.HasValue && info.Depth.Value >= this.Depth) {
            this._candidates[info.MultiPv.Value] = new CandidateMove(
                info.MultiPv.Value,
                info.Move,
                normalizedScore,
                normalizedMate,
                info.Pv
            );
            this._newDepthCandidateCount = this._candidates.Count;
        }

        return isNewDepth || hasMate;
    }

    private void UpdateMainEvaluation(UsiInfo info, int? normalizedScore, bool isNewDepth, bool hasMate)
    {
        if (isNewDepth) {
            this.Depth = info.Depth!.Value;

            // 前の候補手を保存してクリア
            this._previousCandidates.Clear();
            foreach (var kvp in this._candidates) {
                this._previousCandidates[kvp.Key] = kvp.Value;
            }
            this._candidates.Clear();
            this._newDepthCandidateCount = 0;
        }

        if (normalizedScore.HasValue) {
            this.Evaluation = normalizedScore.Value;
        }

        if (info.MateIn.HasValue) {
            this.MateIn = info.MateIn.Value;
            this.MateTurn = this._currentTurn;
        }
        else if (isNewDepth) {
            this.MateIn = null;
        }

        if (info.Pv is not null) {
            this.PrincipalVariation = info.Pv;
        }
    }

    #endregion

    #region Cache

    private static CachedEvaluation? GetCachedEvaluation(MoveTree moveTree, MoveNode? node) =>
        node is not null ? node.CachedEvaluation : moveTree.RootEvaluation;

    private static void SetCachedEvaluation(MoveTree moveTree, MoveNode? node, CachedEvaluation cached)
    {
        if (node is not null) {
            node.CachedEvaluation = cached;
        }
        else {
            moveTree.RootEvaluation = cached;
        }
    }

    private void RestoreFromCache(CachedEvaluation cached)
    {
        this.Evaluation = cached.Evaluation;
        this.MateIn = cached.MateIn;
        this.MateTurn = cached.MateTurn;
        this.BestMove = cached.BestMove;
        this.PrincipalVariation = cached.PrincipalVariation;
        this.Depth = cached.Depth;

        this._candidates.Clear();
        foreach (var candidate in cached.Candidates) {
            this._candidates[candidate.Rank] = candidate;
        }
    }

    private void SaveToCurrentNode()
    {
        if (this._currentMoveTree is null) {
            return;
        }

        var cached = new CachedEvaluation(
            this.Evaluation,
            this.MateIn,
            this.MateTurn,
            this.BestMove,
            this.PrincipalVariation,
            this.Depth,
            this.Candidates
        );

        SetCachedEvaluation(this._currentMoveTree, this._currentNode, cached);
    }

    private void UpdateCacheOnly(UsiInfo info)
    {
        if (!info.Depth.HasValue || (info.MultiPv.HasValue && info.MultiPv.Value != 1) || this._lastAnalyzedMoveTree is null) {
            return;
        }

        var cached = GetCachedEvaluation(this._lastAnalyzedMoveTree, this._lastAnalyzedNode);
        if (cached is null || info.Depth.Value <= cached.Depth) {
            return;
        }

        var normalizedScore = this._lastAnalyzedTurn == Turn.Second ? -info.Score : info.Score;
        var normalizedMate = this._lastAnalyzedTurn == Turn.Second ? -info.MateIn : info.MateIn;

        var newCached = new CachedEvaluation(
            normalizedScore ?? cached.Evaluation,
            normalizedMate ?? cached.MateIn,
            cached.MateTurn,
            info.Move ?? cached.BestMove,
            info.Pv ?? cached.PrincipalVariation,
            info.Depth.Value,
            cached.Candidates
        );

        SetCachedEvaluation(this._lastAnalyzedMoveTree, this._lastAnalyzedNode, newCached);
    }

    #endregion

    #region Candidates

    private List<CandidateMove> GetMergedCandidates()
    {
        var result = this._candidates.Values.OrderBy(c => c.Rank).ToList();

        // 前の深さの候補手で補完
        if (this._previousCandidates.Count > 0 && this._newDepthCandidateCount > 0) {
            var shift = this._newDepthCandidateCount;
            foreach (var prev in this._previousCandidates.Values.OrderBy(c => c.Rank)) {
                var newRank = prev.Rank + shift;
                if (!this._candidates.ContainsKey(newRank)) {
                    result.Add(prev with { Rank = newRank });
                }
            }
        }

        return [.. result.OrderBy(c => c.Rank)];
    }

    #endregion

    #region USI Parsing

    private record struct UsiInfo(int? MultiPv, int? Depth, int? Score, int? MateIn, string? Pv, string? Move);

    private static UsiInfo ParseUsiInfo(string[] parts)
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
                        score = mate > 0 ? EvaluationConstants.MateScoreBase - mate : -EvaluationConstants.MateScoreBase - mate;
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

        return new UsiInfo(multipv, depth, score, mateIn, pv, move);
    }

    /// <summary>SFEN形式の指し手をパース</summary>
    public ((int col, int row)? from, (int col, int row) destination, char? dropPiece)? ParseSfenMove(string sfenMove) =>
        this._usiParser.ParseMoveCoordinates(sfenMove);

    #endregion

    public async ValueTask DisposeAsync()
    {
        if (this.IsAvailable) {
            await this.StopAnalysisAsync();
        }
        this._dotNetRef?.Dispose();
        this._evaluationUpdated.Dispose();
        GC.SuppressFinalize(this);
    }
}
