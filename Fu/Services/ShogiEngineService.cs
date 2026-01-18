using R3;

using Fu.Core;
using Fu.Core.Abstractions;
using Fu.Core.Models;

namespace Fu.Services;

/// <summary>
/// エンジン分析サービス（評価値の解釈、キャッシュ管理）
/// </summary>
public class ShogiEngineService : IDisposable
{
    private readonly IUsiEngine _engine;
    private readonly IUsiParser _usiParser;
    private readonly ISfenConverter _sfenConverter;
    private readonly Subject<Unit> _evaluationUpdated = new();
    private readonly Dictionary<int, CandidateMove> _candidates = [];
    private readonly Dictionary<int, CandidateMove> _previousCandidates = [];

    private CancellationTokenSource? _analysisCts;
    private int _newDepthCandidateCount;
    private int _lastMultiPv;

    // 現在分析中のノード情報
    private MoveTree? _currentMoveTree;
    private MoveNode? _currentNode;
    private Turn _currentTurn = Turn.First;

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
    public bool IsAvailable => this._engine.IsAvailable;

    /// <summary>分析中か</summary>
    public bool IsAnalyzing => this._engine.IsAnalyzing;

    /// <summary>評価値が更新された時</summary>
    public Observable<Unit> EvaluationUpdated => this._evaluationUpdated;

    #endregion

    public ShogiEngineService(IUsiEngine engine, IUsiParser usiParser, ISfenConverter sfenConverter)
    {
        this._engine = engine;
        this._usiParser = usiParser;
        this._sfenConverter = sfenConverter;
    }

    #region Initialization

    /// <summary>エンジンを初期化</summary>
    public async Task<bool> InitializeAsync()
    {
        var engineId = await this._engine.InitializeAsync();
        return engineId is not null;
    }

    /// <summary>エンジンを再起動</summary>
    public async Task<bool> RestartAsync()
    {
        await this.StopAnalysisAsync();
        this.ResetEvaluation();
        this._lastMultiPv = 0;
        return await this._engine.RestartAsync();
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
        if (!this._engine.IsAvailable) {
            return;
        }

        // 前回の分析を停止
        await this.StopAnalysisAsync();

        var currentNode = moveTree.CurrentNode;

        // ノード情報を記録
        this._currentMoveTree = moveTree;
        this._currentNode = currentNode;
        this._currentTurn = currentTurn;

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

        // MultiPV設定（変更時のみ）
        if (multiPv != this._lastMultiPv) {
            await this._engine.SetOptionAsync("MultiPV", multiPv);
            this._lastMultiPv = multiPv;
        }

        // 分析開始
        var sfen = this._sfenConverter.ToSfen(board, currentTurn, firstCaptured, secondCaptured);
        this._analysisCts = new CancellationTokenSource();

        // バックグラウンドでストリームを処理
        _ = this.ProcessAnalysisStreamAsync(sfen, depth, this._analysisCts.Token);
    }

    private async Task ProcessAnalysisStreamAsync(string sfen, int depth, CancellationToken cancellationToken)
    {
        try {
            await foreach (var result in this._engine.GoAsync(sfen, depth).WithCancellation(cancellationToken)) {
                switch (result) {
                    case UsiGoResult.Info info:
                        this.HandleInfo(info.Value);
                        break;

                    case UsiGoResult.BestMove bestMove:
                        this.HandleBestMove(bestMove.Value);
                        break;
                }
            }
        }
        catch (OperationCanceledException) {
            // キャンセルは正常終了
        }
    }

    /// <summary>分析を停止</summary>
    public async Task StopAnalysisAsync()
    {
        if (this._analysisCts is not null) {
            await this._analysisCts.CancelAsync();
            this._analysisCts.Dispose();
            this._analysisCts = null;
        }
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

    #region Event Handlers

    private void HandleInfo(UsiInfo info)
    {
        var isNewDepth = info.Depth.HasValue && info.Depth.Value > this.Depth;
        var hasMate = info.MateIn.HasValue && this.MateIn is null;

        // 詰み検出後はcpスコアで上書きしない
        if (this.MateIn.HasValue && !info.MateIn.HasValue) {
            return;
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

        if (isNewDepth || hasMate) {
            this._evaluationUpdated.OnNext(Unit.Default);
        }
    }

    private void HandleBestMove(UsiBestMove bestMove)
    {
        this.BestMove = bestMove.Move;
        this.SaveToCurrentNode();
        this._evaluationUpdated.OnNext(Unit.Default);
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

    #endregion

    #region Candidates

    private List<CandidateMove> GetMergedCandidates()
    {
        var result = this._candidates.Values.OrderBy(c => c.Rank).ToList();

        // 前の深さの候補手で補完（現在の候補手と同じ手は除外）
        if (this._previousCandidates.Count > 0 && this._newDepthCandidateCount > 0) {
            // 現在の候補手の指し手を収集
            var currentMoves = this._candidates.Values
                .Select(c => c.Move)
                .ToHashSet();

            var nextRank = this._newDepthCandidateCount + 1;
            foreach (var prev in this._previousCandidates.Values.OrderBy(c => c.Rank)) {
                // 現在の候補手と同じ手はスキップ
                if (currentMoves.Contains(prev.Move)) {
                    continue;
                }

                result.Add(prev with { Rank = nextRank });
                nextRank++;
            }
        }

        return [.. result.OrderBy(c => c.Rank)];
    }

    #endregion

    #region USI Parsing

    /// <summary>SFEN形式の指し手をパース</summary>
    public ((int col, int row)? from, (int col, int row) destination, char? dropPiece)? ParseSfenMove(string sfenMove) =>
        this._usiParser.ParseMoveCoordinates(sfenMove);

    #endregion

    public void Dispose()
    {
        this._analysisCts?.Cancel();
        this._analysisCts?.Dispose();
        this._evaluationUpdated.Dispose();
        GC.SuppressFinalize(this);
    }
}
