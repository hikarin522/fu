using R3;

using Fu.Core.Abstractions;
using Fu.Core.Models;
using Fu.Services;

namespace Fu.Observers;

/// <summary>
/// 評価エンジンをオブザーバーとして扱うアダプター
/// 局面が変わるたびに評価値を計算する
/// </summary>
public class EvaluationObserver : IGameObserver, IDisposable
{
    private readonly ShogiEngineService _engineService;
    private readonly Func<MoveTree> _getMoveTree;
    private readonly Func<int> _getMultiPv;
    private readonly Subject<Unit> _evaluationUpdated = new();
    private readonly IDisposable _subscription;

    public string Id => "evaluation-engine";
    public string DisplayName => "YaneuraOu";
    public bool IsAvailable => this._engineService.IsAvailable;

    /// <summary>評価値が更新された時</summary>
    public Observable<Unit> EvaluationUpdated => this._evaluationUpdated;

    public EvaluationObserver(
        ShogiEngineService engineService,
        Func<MoveTree> getMoveTree,
        Func<int> getMultiPv)
    {
        this._engineService = engineService;
        this._getMoveTree = getMoveTree;
        this._getMultiPv = getMultiPv;

        this._subscription = this._engineService.EvaluationUpdated
            .Subscribe(_ => this._evaluationUpdated.OnNext(Unit.Default));
    }

    public Task OnGameStartedAsync(Board board, Turn currentTurn, CapturedPieces firstCaptured, CapturedPieces secondCaptured) =>
        this.AnalyzePositionAsync(board, currentTurn, firstCaptured, secondCaptured);

    public Task OnMoveMadeAsync(Move move, Board board, Turn currentTurn, CapturedPieces firstCaptured, CapturedPieces secondCaptured) =>
        this.AnalyzePositionAsync(board, currentTurn, firstCaptured, secondCaptured);

    public Task OnGameEndedAsync(GameStatus result) =>
        this._engineService.StopAnalysisAsync();

    public Task OnPositionChangedAsync(Board board, Turn currentTurn, CapturedPieces firstCaptured, CapturedPieces secondCaptured) =>
        this.AnalyzePositionAsync(board, currentTurn, firstCaptured, secondCaptured);

    private Task AnalyzePositionAsync(Board board, Turn currentTurn, CapturedPieces firstCaptured, CapturedPieces secondCaptured)
    {
        if (!this.IsAvailable) {
            return Task.CompletedTask;
        }

        return this._engineService.AnalyzePositionAsync(
            board,
            currentTurn,
            firstCaptured,
            secondCaptured,
            this._getMoveTree(),
            multiPv: this._getMultiPv());
    }

    /// <summary>評価値情報へのアクセス</summary>
    public int? Evaluation => this._engineService.Evaluation;
    public int? MateIn => this._engineService.MateIn;
    public Turn MateTurn => this._engineService.MateTurn;
    public string? BestMove => this._engineService.BestMove;
    public string? PrincipalVariation => this._engineService.PrincipalVariation;
    public int Depth => this._engineService.Depth;
    public IReadOnlyList<CandidateMove> Candidates => this._engineService.Candidates;
    public bool IsAnalyzing => this._engineService.IsAnalyzing;

    public void Unsubscribe()
    {
        this._subscription.Dispose();
    }

    public void Dispose()
    {
        this._subscription.Dispose();
        this._evaluationUpdated.Dispose();
        GC.SuppressFinalize(this);
    }
}
