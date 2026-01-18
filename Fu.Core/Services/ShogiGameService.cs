using System.Collections.Immutable;

using Fu.Core.Abstractions;
using Fu.Core.Models;

namespace Fu.Core.Services;

/// <summary>
/// 将棋ゲームサービス（ファサード）
/// 各専門サービスに委譲しつつ、手の実行とタイマー管理を担当
/// </summary>
public class ShogiGameService : IGameLifecycleService, IGameNavigationService, IBranchService, IDisposable
{
    private readonly IShogiRules _rules;
    private readonly ShogiGameState _state;
    private readonly IGameEventPublisher _events;
    private readonly ITurnTimerService _timer;
    private readonly IBoardCache _boardCache;

    private readonly IGameLifecycleService _lifecycle;
    private readonly IGameNavigationService _navigation;
    private readonly IBranchService _branch;

    public ShogiGameService(
        IShogiRules rules,
        IUsiParser usiParser,
        ShogiGameState state,
        IGameEventPublisher events,
        ITurnTimerService timer,
        IBoardCache boardCache,
        IGameLifecycleService lifecycle,
        IGameNavigationService navigation,
        IBranchService branch)
    {
        this._rules = rules;
        this._state = state;
        this._events = events;
        this._timer = timer;
        this._boardCache = boardCache;
        this._lifecycle = lifecycle;
        this._navigation = navigation;
        this._branch = branch;
    }

    public GameState State
    {
        get => this._state.State;
        private set => this._state.State = value;
    }

    /// <summary>棋譜ツリー（分岐対応）</summary>
    public MoveTree MoveTree
    {
        get => this._state.MoveTree;
        private set => this._state.MoveTree = value;
    }

    /// <summary>最後に記録された手の消費時間（送信用）</summary>
    public TimeSpan LastMoveElapsedTime => this._state.LastMoveElapsedTime;

    private void NotifyStateChanged() => this._events.NotifyStateChanged();
    private void NotifyReviewMove(Move move) => this._events.NotifyReviewMove(move);

    #region IGameLifecycleService 委譲

    public Task NewGameAsync() => this._lifecycle.NewGameAsync();

    public Task NewGameAsync(TimeControlSettings timeSettings) => this._lifecycle.NewGameAsync(timeSettings);

    public Task SetLocalTurnAsync(Turn turn) => this._lifecycle.SetLocalTurnAsync(turn);

    public Task ResignAsync() => this._lifecycle.ResignAsync();

    public Task RestoreStateAsync(IReadOnlyList<Move> moveHistory, GameStatus status, IReadOnlyList<TimeSpan>? moveTimes = null) =>
        this._lifecycle.RestoreStateAsync(moveHistory, status, moveTimes);

    #endregion

    #region IGameNavigationService 委譲

    public Task GoBackAsync() => this._navigation.GoBackAsync();

    public Task GoForwardAsync() => this._navigation.GoForwardAsync();

    public Task GoForwardBranchAsync(int branchIndex) => this._navigation.GoForwardBranchAsync(branchIndex);

    public Task GoToLatestAsync() => this._navigation.GoToLatestAsync();

    public Task SetViewingMoveIndexAsync(int moveIndex) => this._navigation.SetViewingMoveIndexAsync(moveIndex);

    public Task GoToNodeAsync(MoveNode? node) => this._navigation.GoToNodeAsync(node);

    public Task GoToPreviousBranchAsync() => this._navigation.GoToPreviousBranchAsync();

    public Task GoToNextBranchAsync() => this._navigation.GoToNextBranchAsync();

    public int GetCurrentBranchIndex() => this._navigation.GetCurrentBranchIndex();

    public int GetTotalBranchCount() => this._navigation.GetTotalBranchCount();

    public (Board board, CapturedPieces firstCaptured, CapturedPieces secondCaptured, Turn currentTurn) GetBoardAtMove(int moveIndex) =>
        this._navigation.GetBoardAtMove(moveIndex);

    #endregion

    #region IBranchService 委譲

    public Task ResumeFromBranchAsync() => this._branch.ResumeFromBranchAsync();

    public Task ApplyBranchResumeAsync(IReadOnlyList<Move> moveHistory) => this._branch.ApplyBranchResumeAsync(moveHistory);

    public Task RematchFromCurrentPositionAsync() => this._branch.RematchFromCurrentPositionAsync();

    public Task ApplyRematchAsync(IReadOnlyList<Move> moveHistory) => this._branch.ApplyRematchAsync(moveHistory);

    public Task StartReviewFromCurrentPositionAsync() => this._branch.StartReviewFromCurrentPositionAsync();

    public Task ApplyReviewStartAsync(IReadOnlyList<Move> moveHistory) => this._branch.ApplyReviewStartAsync(moveHistory);

    public Task ApplyReviewMoveAsync(Move move) => this._branch.ApplyReviewMoveAsync(move);

    public Task<bool> AddMateSequenceBranchAsync(string usiMoves, int branchStartIndex) =>
        this._branch.AddMateSequenceBranchAsync(usiMoves, branchStartIndex);

    #endregion

    #region タイマー

    private void StartTurnTimer() => this._timer.Start();

    private TimeSpan StopTurnTimer()
    {
        var elapsed = this._timer.StopAndGetElapsed();
        this._state.LastMoveElapsedTime = elapsed;

        // 時間制御が有効な場合、時間状態を更新
        if (this.State.TimeState is { } timeState) {
            var newTimeState = timeState.ConsumeTime(this.State.CurrentTurn, elapsed);
            this.State.TimeState = newTimeState;
        }

        return elapsed;
    }

    /// <summary>時間切れをチェックし、該当する場合は終了処理</summary>
    public async Task<bool> CheckTimeoutAsync()
    {
        if (this.State.TimeState is not { } timeState) {
            return false;
        }

        var expiredPlayer = timeState.GetExpiredPlayer();
        if (expiredPlayer is null) {
            return false;
        }

        this._timer.StopOnly();
        this.State.Status = expiredPlayer.Value.GetTimeoutStatus();
        this.NotifyStateChanged();
        return true;
    }

    public TimeSpan GetCurrentTurnElapsed() => this._timer.CurrentElapsed;

    /// <summary>指定プレイヤーの残り時間を取得（現在の手番なら経過時間を考慮）</summary>
    public TimeSpan GetRemainingTime(Turn turn)
    {
        if (this.State.TimeState is not { } timeState) {
            return TimeSpan.MaxValue;
        }

        var playerTime = timeState.GetPlayerTime(turn);
        var remaining = playerTime.RemainingTime;

        // 現在の手番プレイヤーなら経過時間を引く
        if (this.State.Status == GameStatus.Playing && this.State.CurrentTurn == turn) {
            remaining -= this.GetCurrentTurnElapsed();
        }

        return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
    }

    /// <summary>指定プレイヤーが秒読み中かどうか</summary>
    public bool IsInByoyomi(Turn turn) =>
        this.State.TimeState?.GetPlayerTime(turn).IsInByoyomi ?? false;

    #endregion

    #region 合法手取得（ShogiRulesへ委譲）

    public List<Position> GetLegalMoves(Position from) =>
        this._rules.GetLegalMoves(this.State.Board, from, this.State.CurrentTurn);

    public List<Position> GetLegalDropPositions(PieceType pieceType) =>
        this._rules.GetLegalDropPositions(this.State.Board, this.State.CurrentTurn, this.State.GetCapturedPieces(this.State.CurrentTurn), pieceType);

    public List<Position> GetLegalMovesForTurn(Position from, Turn turn) =>
        this._rules.GetLegalMoves(this.State.Board, from, turn);

    public List<Position> GetLegalDropPositionsForTurn(PieceType pieceType, Turn turn) =>
        this._rules.GetLegalDropPositions(this.State.Board, turn, this.State.GetCapturedPieces(turn), pieceType);

    public bool CanPromote(Position from, Position to) =>
        this._rules.CanPromote(this.State.Board, from, to);

    public bool MustPromote(Position from, Position to) =>
        this._rules.MustPromote(this.State.Board, from, to);

    public bool IsInCheck() =>
        this._rules.IsInCheck(this.State.Board, this.State.CurrentTurn);

    #endregion

    #region 手を指す

    public async Task<bool> TryMakeMoveAsync(Move move)
    {
        if (this.State.Status == GameStatus.Reviewing) {
            return await this.TryMakeReviewMoveAsync(move);
        }

        if (this.State.Status != GameStatus.Playing) {
            return false;
        }

        if (this.State.IsReviewing) {
            return false;
        }

        this.SyncMoveTreeToCurrentPosition();

        if (move.IsDrop) {
            return await this.TryDropPieceAsync(move);
        }

        if (move.From is null) {
            return false;
        }

        var from = move.From.Value;
        var to = move.To;
        var piece = this.State.Board[from];

        if (piece is null || piece.Owner != this.State.CurrentTurn) {
            return false;
        }

        var legalMoves = this.GetLegalMoves(from);
        if (!legalMoves.Contains(to)) {
            return false;
        }

        var captured = this.State.Board[to];
        var newCaptured = this.State.GetCapturedPieces(this.State.CurrentTurn);
        var moveToRecord = move;

        if (captured is not null) {
            newCaptured.Add(captured.Type);
            moveToRecord = move.WithCapturedPiece(captured.Type);

            if (captured.Type == PieceType.King) {
                var kingCaptureTime = this.StopTurnTimer();
                this.MoveTree.AddMove(moveToRecord);
                this.State.Board = this.State.Board.MovePiece(from, to);
                this.State.MoveHistory.Add(moveToRecord);
                (this.State.MoveTimes ??= []).Add(kingCaptureTime);
                this.State.Status = this.State.CurrentTurn.GetWinStatus();
                this.State.SetCapturedPieces(this.State.CurrentTurn, newCaptured);
                this.NotifyStateChanged();
                return true;
            }
        }

        var moveTime = this.StopTurnTimer();
        var newPiece = move.IsPromotion && piece.Type.CanPromote()
            ? piece with { Type = piece.Type.GetPromotedType() }
            : piece;

        this.MoveTree.AddMove(moveToRecord);

        this.State.Board = this.State.Board.MovePiece(from, to, newPiece);
        this.State.MoveHistory.Add(moveToRecord);
        (this.State.MoveTimes ??= []).Add(moveTime);
        this.State.ViewingMoveIndex = null;
        this.State.SetCapturedPieces(this.State.CurrentTurn, newCaptured);
        this.State.SwitchTurn();

        await this.CheckForCheckmateAsync();
        this.NotifyStateChanged();
        return true;
    }

    private async Task<bool> TryDropPieceAsync(Move move)
    {
        var captured = this.State.GetCapturedPieces(this.State.CurrentTurn);
        if (captured.GetCount(move.PieceType) <= 0) {
            return false;
        }

        var legalPositions = this.GetLegalDropPositions(move.PieceType);
        if (!legalPositions.Contains(move.To)) {
            return false;
        }

        var moveTime = this.StopTurnTimer();
        var moveWithTurn = move.WithTurn(this.State.CurrentTurn);
        var recordedMove = this.ApplyDropCore(moveWithTurn, moveTime);
        if (recordedMove is null) {
            return false;
        }

        this.MoveTree.AddMove(recordedMove);

        await this.CheckForCheckmateAsync();
        this.NotifyStateChanged();
        return true;
    }

    private async Task CheckForCheckmateAsync()
    {
        var currentTurn = this.State.CurrentTurn;
        var captured = this.State.GetCapturedPieces(currentTurn);

        if (this._rules.IsCheckmate(this.State.Board, currentTurn, captured)) {
            this.State.Status = currentTurn.GetOpponent().GetWinStatus();
            this.NotifyStateChanged();
        }
    }

    /// <summary>リモートから受信した手を適用（消費時間も適用）</summary>
    public Task ApplyRemoteMoveAsync(Move move, TimeSpan elapsedTime) =>
        this.TryMakeMoveWithTimeAsync(move.WithTurn(this.State.CurrentTurn), elapsedTime);

    private async Task<bool> TryMakeMoveWithTimeAsync(Move move, TimeSpan elapsedTime)
    {
        this._timer.StopOnly();
        this._timer.SetPendingRemoteElapsedTime(elapsedTime);
        try {
            return await this.TryMakeMoveAsync(move);
        }
        finally {
            this._timer.SetPendingRemoteElapsedTime(null);
        }
    }

    #endregion

    #region 検討モード（手の実行部分）

    private async Task<bool> TryMakeReviewMoveAsync(Move move)
    {
        if (this.State.IsReviewing) {
            await this.BranchFromCurrentPositionAsync(notifyBranchResumed: false);
        }

        this.SyncMoveTreeToCurrentPosition();

        if (move.IsDrop) {
            return await this.TryDropPieceForReviewAsync(move);
        }

        if (move.From is null) {
            return false;
        }

        var from = move.From.Value;
        var to = move.To;
        var piece = this.State.Board[from];

        if (piece is null) {
            return false;
        }

        var turn = piece.Owner;
        var legalMoves = this.GetLegalMovesForTurn(from, turn);
        if (!legalMoves.Contains(to)) {
            return false;
        }

        var moveWithTurn = move.WithTurn(turn);
        var recordedMove = this.ApplyMoveCore(moveWithTurn);
        this.MoveTree.AddMove(recordedMove);

        this.NotifyReviewMove(recordedMove);
        this.NotifyStateChanged();
        return true;
    }

    private async Task<bool> TryDropPieceForReviewAsync(Move move)
    {
        var turn = this.State.CurrentTurn;
        var captured = this.State.GetCapturedPieces(turn);
        if (captured.GetCount(move.PieceType) <= 0) {
            return false;
        }

        var legalPositions = this.GetLegalDropPositionsForTurn(move.PieceType, turn);
        if (!legalPositions.Contains(move.To)) {
            return false;
        }

        var moveWithTurn = move.WithTurn(turn);
        var recordedMove = this.ApplyDropCore(moveWithTurn);
        if (recordedMove is null) {
            return false;
        }

        this.MoveTree.AddMove(recordedMove);

        this.NotifyReviewMove(recordedMove);
        this.NotifyStateChanged();
        return true;
    }

    private async Task BranchFromCurrentPositionAsync(bool notifyBranchResumed)
    {
        var newHistory = this.GetCurrentDisplayHistory();
        var (board, firstCaptured, secondCaptured, currentTurn) = this._rules.ReconstructBoard(newHistory);

        this.MoveTree.GoToStart();
        foreach (var move in newHistory) {
            this.MoveTree.AddMove(move);
        }

        this.State.Board = board;
        this.State.FirstCaptured = firstCaptured;
        this.State.SecondCaptured = secondCaptured;
        this.State.CurrentTurn = currentTurn;
        this.State.MoveHistory = [.. newHistory];
        this.State.ViewingMoveIndex = null;
        this.State.ViewingBranchHistory = null;

        if (notifyBranchResumed) {
            this._events.NotifyBranchResumed(newHistory);
        }
    }

    #endregion

    #region 共通Move/Drop処理

    /// <summary>移動を適用（検証済み前提）</summary>
    private Move ApplyMoveCore(Move move, TimeSpan? moveTime = null)
    {
        var from = move.From!.Value;
        var to = move.To;
        var turn = move.Turn;
        var piece = this.State.Board[from]!;
        var captured = this.State.Board[to];
        var newCaptured = this.State.GetCapturedPieces(turn);
        var recordedMove = move;

        if (captured is not null) {
            newCaptured.Add(captured.Type);
            recordedMove = recordedMove.WithCapturedPiece(captured.Type);
        }

        var newPiece = move.IsPromotion && piece.Type.CanPromote()
            ? piece with { Type = piece.Type.GetPromotedType() }
            : piece;

        this.State.Board = this.State.Board.MovePiece(from, to, newPiece);
        this.State.MoveHistory.Add(recordedMove);
        if (moveTime.HasValue) {
            (this.State.MoveTimes ??= []).Add(moveTime.Value);
        }
        this.State.CurrentTurn = turn.GetOpponent();
        this.State.ViewingMoveIndex = null;
        this.State.SetCapturedPieces(turn, newCaptured);

        return recordedMove;
    }

    /// <summary>打ち駒を適用（検証済み前提）</summary>
    private Move? ApplyDropCore(Move move, TimeSpan? moveTime = null)
    {
        var turn = move.Turn;
        var captured = this.State.GetCapturedPieces(turn);
        if (!captured.TryRemove(move.PieceType)) {
            return null;
        }

        var recordedMove = move.WithTurn(turn);

        this.State.Board = this.State.Board.SetPiece(move.To, new Piece(move.PieceType, turn));
        this.State.MoveHistory.Add(recordedMove);
        if (moveTime.HasValue) {
            (this.State.MoveTimes ??= []).Add(moveTime.Value);
        }
        this.State.CurrentTurn = turn.GetOpponent();
        this.State.ViewingMoveIndex = null;
        this.State.SetCapturedPieces(turn, captured);

        return recordedMove;
    }

    #endregion

    #region ヘルパー

    private IReadOnlyList<Move> GetCurrentDisplayHistory()
    {
        var displayHistory = this.State.DisplayBranchHistory;
        var viewingIndex = this.State.ViewingMoveIndex ?? displayHistory.Count;
        return [.. displayHistory.Take(viewingIndex)];
    }

    private void SyncMoveTreeToCurrentPosition()
    {
        this.SyncMoveTreeToPosition(this.State.MoveHistory.Count);
    }

    private void SyncMoveTreeToPosition(int targetIndex)
    {
        var currentDepth = this.MoveTree.CurrentDepth;

        if (currentDepth == targetIndex) {
            var currentMoves = this.MoveTree.CurrentLine;
            var historySlice = this.State.MoveHistory.Take(targetIndex).ToList();
            if (currentMoves.Count == historySlice.Count &&
                currentMoves.Select((m, i) => m == historySlice[i]).All(x => x)) {
                return;
            }
        }

        this.MoveTree.GoToStart();
        for (var i = 0; i < targetIndex && i < this.State.MoveHistory.Count; i++) {
            this.MoveTree.AddMove(this.State.MoveHistory[i]);
        }
    }

    #endregion

    public void Dispose()
    {
        GC.SuppressFinalize(this);
    }
}
