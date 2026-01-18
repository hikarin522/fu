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
    /// <summary>詰みスコアの基準値（USIプロトコル）</summary>
    private const int MateScoreBase = 30000;

    private readonly IJSRuntime _jsRuntime;
    private readonly Subject<Unit> _evaluationUpdated = new();
    private DotNetObjectReference<ShogiEngineService>? _dotNetRef;
    private bool _initialized;
    private bool _isAnalyzing;
    private readonly Dictionary<int, CandidateMove> _candidates = [];
    private readonly Dictionary<int, CandidateMove> _previousCandidates = [];
    private int _newDepthCandidateCount;
    private int _lastMultiPv;

    // 現在分析中のノード情報
    private MoveTree? _currentMoveTree;
    private MoveNode? _currentNode; // nullは開始局面
    private Turn _currentTurn = Turn.First;

    // 最後に分析を開始したノード（結果が遅延して届いた時にキャッシュ更新に使用）
    private MoveTree? _lastAnalyzedMoveTree;
    private MoveNode? _lastAnalyzedNode;
    private Turn _lastAnalyzedTurn;

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

    /// <summary>候補手リスト（MultiPV）</summary>
    /// <remarks>
    /// 深さが増えた直後で候補手が揃っていない場合、前の深さの候補手で補完する。
    /// 例: 深さ32で1位のみの場合、前の深さの1位→2位、2位→3位として表示。
    /// </remarks>
    public IReadOnlyList<CandidateMove> Candidates => this.GetMergedCandidates();

    /// <summary>エンジンが利用可能か</summary>
    public bool IsAvailable { get; private set; }

    /// <summary>分析中か</summary>
    public bool IsAnalyzing => this._isAnalyzing;

    /// <summary>評価値が更新された時</summary>
    public Observable<Unit> EvaluationUpdated => this._evaluationUpdated;

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

    /// <summary>局面を分析（MoveTreeベース）</summary>
    /// <param name="depth">探索深さ（0 = 無限探索）</param>
    public async Task AnalyzePositionAsync(
        Board board,
        Turn currentTurn,
        CapturedPieces firstCaptured,
        CapturedPieces secondCaptured,
        MoveTree moveTree,
        int depth = 0,
        int multiPv = 1)
    {
        if (!this.IsAvailable) {
            return;
        }

        var currentNode = moveTree.CurrentNode;

        // 現在のノードと最後に分析したノードを記録
        this._currentMoveTree = moveTree;
        this._currentNode = currentNode;
        this._currentTurn = currentTurn;

        this._lastAnalyzedMoveTree = moveTree;
        this._lastAnalyzedNode = currentNode;
        this._lastAnalyzedTurn = currentTurn;

        // ノードのキャッシュをチェック
        var cached = GetCachedEvaluationFromNode(moveTree, currentNode);
        if (cached is not null) {
            // キャッシュから復元（即座に表示）
            this.RestoreFromCache(cached);
            this._evaluationUpdated.OnNext(Unit.Default);
            // 無限探索の場合は継続、深さ指定の場合はキャッシュ深さ以上ならスキップ
            if (depth > 0 && cached.Depth >= depth) {
                return;
            }
            // 継続して分析する場合、キャッシュの深さから継続（新しい結果は上書きされる）
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

        // MultiPVを設定（値が変わった時のみ送信）
        if (multiPv != this._lastMultiPv) {
            await this._jsRuntime.InvokeAsync<bool>("ShogiEngine.sendCommand", $"setoption name MultiPV value {multiPv}");
            this._lastMultiPv = multiPv;
        }

        // SFEN生成してエンジンに送信
        var sfen = SfenConverter.ToSfen(board, currentTurn, firstCaptured, secondCaptured);
        await this._jsRuntime.InvokeAsync<bool>("ShogiEngine.requestEvaluation", sfen, depth);
    }

    /// <summary>ノードからキャッシュを取得</summary>
    private static CachedEvaluation? GetCachedEvaluationFromNode(MoveTree moveTree, MoveNode? node) =>
        node is not null ? node.CachedEvaluation : moveTree.RootEvaluation;

    /// <summary>ノードにキャッシュを保存</summary>
    private static void SetCachedEvaluationToNode(MoveTree moveTree, MoveNode? node, CachedEvaluation cached)
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

        SetCachedEvaluationToNode(this._currentMoveTree, this._currentNode, cached);
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
    public Task OnEngineMessage(string message)
    {
        // USIプロトコルのメッセージをパース
        if (message.StartsWith("info ", StringComparison.Ordinal)) {
            this.ParseInfoMessage(message);
            this._evaluationUpdated.OnNext(Unit.Default);
        }
        else if (message.StartsWith("bestmove ", StringComparison.Ordinal)) {
            var parts = message.Split(' ');
            if (parts.Length >= 2) {
                this.BestMove = parts[1];
            }
            this._isAnalyzing = false;

            // 探索完了時にキャッシュに保存
            this.SaveToCurrentNode();

            this._evaluationUpdated.OnNext(Unit.Default);
        }

        return Task.CompletedTask;
    }

    private void ParseInfoMessage(string message)
    {
        var parts = message.Split(' ');
        var info = ParseUsiInfo(parts);

        // 現在の局面の結果かどうか判定（同じノードかどうか）
        var isCurrentPosition = this._currentMoveTree is not null
            && ReferenceEquals(this._lastAnalyzedMoveTree, this._currentMoveTree)
            && ReferenceEquals(this._lastAnalyzedNode, this._currentNode);

        if (!isCurrentPosition) {
            // 現在の局面ではない場合、最後に分析したノードのキャッシュを更新（UI更新はしない）
            this.UpdateCacheOnly(info);
            return;
        }

        // 深さがない、または現在より深い場合のみメイン評価値を更新
        var isNewDepth = info.Depth.HasValue && info.Depth.Value > this.Depth;
        // ただし、詰みが検出された場合は深さに関係なく更新（キャッシュから復元した場合も詰みを優先）
        var hasMate = info.MateIn.HasValue && this.MateIn is null;

        // 既に詰みが検出されている場合、cp スコアでは上書きしない
        // （詰み検出後の別分析結果で詰みが消えることを防ぐ）
        if (this.MateIn.HasValue && !info.MateIn.HasValue) {
            return;
        }

        // 評価値を先手視点に変換
        // エンジンは分析対象局面の手番側目線でスコアを返す
        // - 先手番の局面を分析 → 先手目線のスコア → 変換不要
        // - 後手番の局面を分析 → 後手目線のスコア → 符号反転で先手視点に変換
        // ※プレイヤーが誰か（先手側/後手側）は関係なく、分析対象の局面の手番で決まる
        var normalizedScore = this._currentTurn == Turn.Second ? -info.Score : info.Score;

        // デバッグ用：変換前後の値をログ出力
        if (info.Score.HasValue) {
            Console.WriteLine($"[Eval] Turn={this._currentTurn}, Raw={info.Score}, Normalized={normalizedScore}, Depth={info.Depth}");
        }

        // メインの評価値を更新（multipv=1または指定なしの場合、かつ新しい深さの場合または詰みが見つかった場合）
        if ((info.MultiPv is null or 1) && (isNewDepth || hasMate)) {
            if (isNewDepth) {
                this.Depth = info.Depth!.Value;
            }
            if (normalizedScore.HasValue) {
                this.Evaluation = normalizedScore.Value;
            }

            // 詰みは手番視点でそのまま保存
            if (info.MateIn.HasValue) {
                this.MateIn = info.MateIn.Value;
                this.MateTurn = this._currentTurn;
            }
            else if (isNewDepth) {
                // 新しい深さで詰みがない場合のみリセット
                this.MateIn = null;
            }

            if (info.Pv is not null) {
                this.PrincipalVariation = info.Pv;
            }

            // 新しい深さになったら前の候補手を保存してからクリア
            if (isNewDepth) {
                this._previousCandidates.Clear();
                foreach (var kvp in this._candidates) {
                    this._previousCandidates[kvp.Key] = kvp.Value;
                }
                this._candidates.Clear();
                this._newDepthCandidateCount = 0;
            }
        }

        // 候補手リストを更新（現在の深さ以上の結果のみ）
        // 候補手の詰みも手番視点で正規化（正=手番側勝ち → 先手視点に変換）
        var normalizedMate = this._currentTurn == Turn.Second ? -info.MateIn : info.MateIn;
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
    }

    /// <summary>過去の局面の解析結果をキャッシュに反映（UI更新なし）</summary>
    private void UpdateCacheOnly((int? MultiPv, int? Depth, int? Score, int? MateIn, string? Pv, string? Move) info)
    {
        // 深さ情報がない、multipv=1以外の場合、または最後に分析した局面がない場合は無視
        if (!info.Depth.HasValue || (info.MultiPv.HasValue && info.MultiPv.Value != 1) || this._lastAnalyzedMoveTree is null) {
            return;
        }

        var moveTree = this._lastAnalyzedMoveTree;
        var node = this._lastAnalyzedNode;
        var turn = this._lastAnalyzedTurn;

        // キャッシュが存在し、より深い結果であれば更新
        var cached = GetCachedEvaluationFromNode(moveTree, node);
        if (cached is not null && info.Depth.Value > cached.Depth) {
            var normalizedScore = turn == Turn.Second ? -info.Score : info.Score;
            var normalizedMate = turn == Turn.Second ? -info.MateIn : info.MateIn;

            var newCached = new CachedEvaluation(
                normalizedScore ?? cached.Evaluation,
                normalizedMate ?? cached.MateIn,
                cached.MateTurn,
                info.Move ?? cached.BestMove,
                info.Pv ?? cached.PrincipalVariation,
                info.Depth.Value,
                cached.Candidates // 候補手は維持
            );

            SetCachedEvaluationToNode(moveTree, node, newCached);

            Console.WriteLine($"[Cache] Updated cache for old position: depth={info.Depth}, node={node?.Depth ?? 0}");
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
                        score = mate > 0 ? MateScoreBase - mate : -MateScoreBase - mate;
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

    /// <summary>候補手リストを取得（前の深さの結果で補完）</summary>
    private List<CandidateMove> GetMergedCandidates()
    {
        var result = new List<CandidateMove>();

        // 現在の深さの候補手を追加
        foreach (var candidate in this._candidates.Values.OrderBy(c => c.Rank)) {
            result.Add(candidate);
        }

        // 前の深さの候補手で補完（新しい深さで取得済みの数だけシフト）
        if (this._previousCandidates.Count > 0 && this._newDepthCandidateCount > 0) {
            var shift = this._newDepthCandidateCount;
            foreach (var prev in this._previousCandidates.Values.OrderBy(c => c.Rank)) {
                var newRank = prev.Rank + shift;
                // 既に現在の深さで同じランクがある場合はスキップ
                if (!this._candidates.ContainsKey(newRank)) {
                    result.Add(prev with { Rank = newRank });
                }
            }
        }

        return [.. result.OrderBy(c => c.Rank)];
    }

    /// <summary>SFEN形式の指し手をパースして移動元・移動先の座標を返す</summary>
    public static ((int col, int row)? from, (int col, int row) to, char? dropPiece)? ParseSfenMove(string sfenMove) =>
        UsiParser.ParseMoveCoordinates(sfenMove);

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
