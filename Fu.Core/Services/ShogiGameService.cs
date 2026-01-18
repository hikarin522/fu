using System.Collections.Immutable;

using R3;

using Fu.Core.Abstractions;
using Fu.Core.Models;

namespace Fu.Core.Services;

public class ShogiGameService : IDisposable
{
    private IGameTimer _turnTimer = GameTimerFactory.CreateDefault();
    private readonly Subject<Unit> _stateChanged = new();
    private readonly Subject<IReadOnlyList<Move>> _branchResumed = new();
    private readonly Subject<IReadOnlyList<Move>> _reviewStarted = new();
    private readonly Subject<Move> _reviewMove = new();
    private TimeSpan? _pendingRemoteElapsedTime;

    // GetBoardAtMove キャッシュ
    private (IReadOnlyList<Move> branchHistory, int moveIndex, Board board, CapturedPieces firstCaptured, CapturedPieces secondCaptured, Turn currentTurn)? _boardCache;

    public GameState State { get; private set; } = GameState.Initial;

    /// <summary>棋譜ツリー（分岐対応）</summary>
    public MoveTree MoveTree { get; private set; } = new();

    /// <summary>最後に記録された手の消費時間（送信用）</summary>
    public TimeSpan LastMoveElapsedTime { get; private set; }

    #region Observable

    /// <summary>状態が変更された時</summary>
    public Observable<Unit> StateChanged => this._stateChanged;

    /// <summary>分岐から再開した時（棋譜の手順を返す）</summary>
    public Observable<IReadOnlyList<Move>> BranchResumed => this._branchResumed;

    /// <summary>検討モードを開始した時（現在の手順を返す）</summary>
    public Observable<IReadOnlyList<Move>> ReviewStarted => this._reviewStarted;

    /// <summary>検討モードで手が進んだ時</summary>
    public Observable<Move> ReviewMove => this._reviewMove;

    #endregion

    private void NotifyStateChanged() => this._stateChanged.OnNext(Unit.Default);
    private void NotifyBranchResumed(IReadOnlyList<Move> moveHistory) => this._branchResumed.OnNext(moveHistory);
    private void NotifyReviewStarted(IReadOnlyList<Move> moveHistory) => this._reviewStarted.OnNext(moveHistory);
    private void NotifyReviewMove(Move move) => this._reviewMove.OnNext(move);

    #region ゲーム開始・状態管理

    public async Task NewGameAsync() =>
        await this.NewGameAsync(TimeControlSettings.None);

    public async Task NewGameAsync(TimeControlSettings timeSettings)
    {
        var localTurn = this.State.LocalTurn;
        var timeState = timeSettings.Type == TimeControlType.None
            ? null
            : GameTimeState.Initial(timeSettings);

        // タイマーを設定に基づいて作成
        this._turnTimer = GameTimerFactory.Create(timeSettings);

        this.State = GameState.Initial with {
            Status = GameStatus.Playing,
            LocalTurn = localTurn,
            MoveTimes = [],
            TimeState = timeState
        };
        this.MoveTree = new MoveTree();
        this.StartTurnTimer();
        this.NotifyStateChanged();
    }

    public async Task SetLocalTurnAsync(Turn turn)
    {
        this.State = this.State with { LocalTurn = turn };
        this.NotifyStateChanged();
    }

    public async Task ResignAsync()
    {
        this.State = this.State with {
            Status = this.State.CurrentTurn.GetOpponent().GetWinStatus()
        };
        this.NotifyStateChanged();
    }

    /// <summary>途中参加者向けにゲーム状態を復元</summary>
    public async Task RestoreStateAsync(IReadOnlyList<Move> moveHistory, GameStatus status, IReadOnlyList<TimeSpan>? moveTimes = null)
    {
        this.RestoreFromMoveHistory(moveHistory, status, preserveMoveTree: false, moveTimes: moveTimes);

        if (status == GameStatus.Playing) {
            this.StartTurnTimer();
        }

        this.NotifyStateChanged();
    }

    #endregion

    #region タイマー

    private void StartTurnTimer() => this._turnTimer.Start();

    private TimeSpan StopTurnTimer()
    {
        TimeSpan elapsed;
        if (this._pendingRemoteElapsedTime is { } remoteTime) {
            this._turnTimer.Start();
            elapsed = remoteTime;
        } else {
            elapsed = this._turnTimer.StopAndGetElapsed();
            this._turnTimer.Start();
        }

        this.LastMoveElapsedTime = elapsed;

        // 時間制御が有効な場合、時間状態を更新
        if (this.State.TimeState is { } timeState) {
            var newTimeState = timeState.ConsumeTime(this.State.CurrentTurn, elapsed);
            this.State = this.State with { TimeState = newTimeState };
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

        this._turnTimer.StopAndGetElapsed();
        this.State = this.State with {
            Status = expiredPlayer.Value.GetTimeoutStatus()
        };
        this.NotifyStateChanged();
        return true;
    }

    public TimeSpan GetCurrentTurnElapsed() =>
        this._turnTimer.IsRunning ? this._turnTimer.Elapsed : TimeSpan.Zero;

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
        ShogiRules.GetLegalMoves(this.State.Board, from, this.State.CurrentTurn);

    public List<Position> GetLegalDropPositions(PieceType pieceType) =>
        ShogiRules.GetLegalDropPositions(this.State.Board, this.State.CurrentTurn, this.State.GetCapturedPieces(this.State.CurrentTurn), pieceType);

    public List<Position> GetLegalMovesForTurn(Position from, Turn turn) =>
        ShogiRules.GetLegalMoves(this.State.Board, from, turn);

    public List<Position> GetLegalDropPositionsForTurn(PieceType pieceType, Turn turn) =>
        ShogiRules.GetLegalDropPositions(this.State.Board, turn, this.State.GetCapturedPieces(turn), pieceType);

    public bool CanPromote(Position from, Position to) =>
        ShogiRules.CanPromote(this.State.Board, from, to);

    public bool MustPromote(Position from, Position to) =>
        ShogiRules.MustPromote(this.State.Board, from, to);

    public bool IsInCheck() =>
        ShogiRules.IsInCheck(this.State.Board, this.State.CurrentTurn);

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
            newCaptured = newCaptured.Add(captured.Type);
            moveToRecord = move.WithCapturedPiece(captured.Type);

            if (captured.Type == PieceType.King) {
                var kingCaptureTime = this.StopTurnTimer();
                this.MoveTree.AddMove(moveToRecord);
                this.State = this.State with {
                    Board = this.State.Board.MovePiece(from, to),
                    MoveHistory = this.State.MoveHistory.Add(moveToRecord),
                    MoveTimes = (this.State.MoveTimes ?? []).Add(kingCaptureTime),
                    Status = this.State.CurrentTurn.GetWinStatus()
                };
                this.State = this.State.WithCapturedPieces(this.State.CurrentTurn, newCaptured);
                this.NotifyStateChanged();
                return true;
            }
        }

        var moveTime = this.StopTurnTimer();
        var newPiece = move.IsPromotion && piece.Type.CanPromote()
            ? piece with { Type = piece.Type.GetPromotedType() }
            : piece;

        this.MoveTree.AddMove(moveToRecord);

        var newState = this.State with {
            Board = this.State.Board.MovePiece(from, to, newPiece),
            MoveHistory = this.State.MoveHistory.Add(moveToRecord),
            MoveTimes = (this.State.MoveTimes ?? []).Add(moveTime),
            ViewingMoveIndex = null
        };
        newState = newState.WithCapturedPieces(this.State.CurrentTurn, newCaptured);
        newState = newState.SwitchTurn();

        this.State = newState;

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
        var result = this.ApplyDropCore(moveWithTurn, moveTime);
        if (result is null) {
            return false;
        }

        var (newState, recordedMove) = result.Value;
        this.MoveTree.AddMove(recordedMove);
        this.State = newState;

        await this.CheckForCheckmateAsync();
        this.NotifyStateChanged();
        return true;
    }

    private async Task CheckForCheckmateAsync()
    {
        var currentTurn = this.State.CurrentTurn;
        var captured = this.State.GetCapturedPieces(currentTurn);

        if (ShogiRules.IsCheckmate(this.State.Board, currentTurn, captured)) {
            this.State = this.State with {
                Status = currentTurn.GetOpponent().GetWinStatus()
            };
            this.NotifyStateChanged();
        }
    }

    /// <summary>リモートから受信した手を適用（消費時間も適用）</summary>
    public Task ApplyRemoteMoveAsync(Move move, TimeSpan elapsedTime) =>
        this.TryMakeMoveWithTimeAsync(move.WithTurn(this.State.CurrentTurn), elapsedTime);

    private async Task<bool> TryMakeMoveWithTimeAsync(Move move, TimeSpan elapsedTime)
    {
        this._turnTimer.StopAndGetElapsed();
        this._pendingRemoteElapsedTime = elapsedTime;
        try {
            return await this.TryMakeMoveAsync(move);
        }
        finally {
            this._pendingRemoteElapsedTime = null;
        }
    }

    #endregion

    #region ナビゲーション

    public async Task GoBackAsync()
    {
        var currentIndex = this.State.DisplayMoveIndex;
        if (currentIndex > 0) {
            this.State = this.State with { ViewingMoveIndex = currentIndex - 1 };
            this.NotifyStateChanged();
        }
    }

    public async Task GoForwardAsync()
    {
        var currentIndex = this.State.DisplayMoveIndex;
        if (currentIndex < this.State.MoveHistory.Count) {
            var newIndex = currentIndex + 1;
            this.State = this.State with {
                ViewingMoveIndex = newIndex == this.State.MoveHistory.Count ? null : newIndex
            };
            this.NotifyStateChanged();
        }
    }

    public async Task GoForwardBranchAsync(int branchIndex)
    {
        this.SyncMoveTreeToViewingPosition();

        if (this.MoveTree.GoForwardBranch(branchIndex)) {
            var currentNode = this.MoveTree.CurrentNode;
            if (currentNode is not null) {
                var branchMoves = currentNode.GetMoves();
                var (board, firstCaptured, secondCaptured, currentTurn) = ShogiRules.ReconstructBoard(branchMoves);

                this.State = this.State with {
                    Board = board,
                    FirstCaptured = firstCaptured,
                    SecondCaptured = secondCaptured,
                    CurrentTurn = currentTurn,
                    MoveHistory = [.. branchMoves],
                    ViewingMoveIndex = branchMoves.Count == this.State.MoveHistory.Count ? null : branchMoves.Count
                };

                this.NotifyStateChanged();
            }
        }
    }

    public async Task GoToLatestAsync()
    {
        if (this.State.IsReviewing) {
            this.GoToMoveHistoryEnd();
            this.State = this.State with {
                ViewingMoveIndex = null,
                ViewingBranchHistory = null
            };
            this.NotifyStateChanged();
        }
    }

    public async Task SetViewingMoveIndexAsync(int moveIndex)
    {
        var newIndex = moveIndex >= this.State.MoveHistory.Count ? null : (int?)moveIndex;
        if (this.State.ViewingMoveIndex != newIndex) {
            this.State = this.State with { ViewingMoveIndex = newIndex };
            this.NotifyStateChanged();
        }
    }

    public async Task GoToNodeAsync(MoveNode? node)
    {
        this.MoveTree.GoTo(node);

        if (node is null) {
            this.State = this.State with {
                ViewingMoveIndex = 0,
                ViewingBranchHistory = null
            };
        } else {
            var nodeMoves = node.GetMoves();
            var isExactSamePath = nodeMoves.Count == this.State.MoveHistory.Count &&
                                  nodeMoves.Select((m, i) => MoveNode.IsSameMove(m, this.State.MoveHistory[i])).All(x => x);

            if (isExactSamePath) {
                this.State = this.State with {
                    ViewingMoveIndex = null,
                    ViewingBranchHistory = null
                };
            } else {
                var isSamePath = nodeMoves.Count <= this.State.MoveHistory.Count &&
                                 nodeMoves.Select((m, i) => MoveNode.IsSameMove(m, this.State.MoveHistory[i])).All(x => x);

                this.State = isSamePath
                    ? (this.State with {
                        ViewingMoveIndex = node.Depth,
                        ViewingBranchHistory = null
                    })
                    : (this.State with {
                        ViewingMoveIndex = null,
                        ViewingBranchHistory = [.. nodeMoves]
                    });
            }
        }
        this.NotifyStateChanged();
    }

    public async Task GoToPreviousBranchAsync()
    {
        var allEndNodes = this.MoveTree.GetAllBranchEndNodes();
        if (allEndNodes.Count <= 1) {
            return;
        }

        var currentIndex = this.GetCurrentBranchIndex();
        var newIndex = currentIndex > 0 ? currentIndex - 1 : allEndNodes.Count - 1;
        await this.GoToBranchEndAsync(newIndex);
    }

    public async Task GoToNextBranchAsync()
    {
        var allEndNodes = this.MoveTree.GetAllBranchEndNodes();
        if (allEndNodes.Count <= 1) {
            return;
        }

        var currentIndex = this.GetCurrentBranchIndex();
        var newIndex = (currentIndex + 1) % allEndNodes.Count;
        await this.GoToBranchEndAsync(newIndex);
    }

    private async Task GoToBranchEndAsync(int branchIndex)
    {
        var allEndNodes = this.MoveTree.GetAllBranchEndNodes();
        if (branchIndex < 0 || branchIndex >= allEndNodes.Count) {
            return;
        }

        var endNode = allEndNodes[branchIndex];
        await this.GoToNodeAsync(endNode);
    }

    public int GetCurrentBranchIndex() =>
        this.MoveTree.GetBranchIndexForNode(this.MoveTree.CurrentNode);

    public int GetTotalBranchCount() =>
        this.MoveTree.GetAllBranchEndNodes().Count;

    public (Board board, CapturedPieces firstCaptured, CapturedPieces secondCaptured, Turn currentTurn) GetBoardAtMove(int moveIndex)
    {
        var branchHistory = this.State.DisplayBranchHistory;

        // キャッシュが有効かチェック（同じブランチ履歴で同じインデックス）
        if (this._boardCache is { } cache
            && ReferenceEquals(cache.branchHistory, branchHistory)
            && cache.moveIndex == moveIndex) {
            return (cache.board, cache.firstCaptured, cache.secondCaptured, cache.currentTurn);
        }

        // 再構築してキャッシュ
        var result = ShogiRules.ReconstructBoard(branchHistory.Take(moveIndex));
        this._boardCache = (branchHistory, moveIndex, result.board, result.firstCaptured, result.secondCaptured, result.currentTurn);
        return result;
    }

    #endregion

    #region 分岐・再戦

    public async Task ResumeFromBranchAsync()
    {
        if (this.State.IsReviewing && this.State.Status == GameStatus.Playing) {
            await this.BranchFromCurrentPositionAsync();
            this.NotifyStateChanged();
        }
    }

    public async Task ApplyBranchResumeAsync(IReadOnlyList<Move> moveHistory)
    {
        this.RestoreFromMoveHistory(moveHistory, GameStatus.Playing, preserveMoveTree: true);
        this.StartTurnTimer();
        this.NotifyStateChanged();
    }

    public async Task RematchFromCurrentPositionAsync()
    {
        var newHistory = this.GetCurrentDisplayHistory();
        this.RestoreFromMoveHistory(newHistory, GameStatus.Playing, preserveMoveTree: false, resetTimes: true);
        this.StartTurnTimer();
        this.NotifyStateChanged();
    }

    public async Task ApplyRematchAsync(IReadOnlyList<Move> moveHistory)
    {
        this.RestoreFromMoveHistory(moveHistory, GameStatus.Playing, preserveMoveTree: false, resetTimes: true);
        this.StartTurnTimer();
        this.NotifyStateChanged();
    }

    private async Task BranchFromCurrentPositionAsync(bool notifyBranchResumed = true)
    {
        var newHistory = this.GetCurrentDisplayHistory();
        var (board, firstCaptured, secondCaptured, currentTurn) = ShogiRules.ReconstructBoard(newHistory);

        this.MoveTree.GoToStart();
        foreach (var move in newHistory) {
            this.MoveTree.AddMove(move);
        }

        this.State = this.State with {
            Board = board,
            FirstCaptured = firstCaptured,
            SecondCaptured = secondCaptured,
            CurrentTurn = currentTurn,
            MoveHistory = [.. newHistory],
            ViewingMoveIndex = null,
            ViewingBranchHistory = null
        };

        if (notifyBranchResumed) {
            this.NotifyBranchResumed(newHistory);
        }
    }

    #endregion

    #region 検討モード

    public async Task StartReviewFromCurrentPositionAsync()
    {
        var newHistory = this.GetCurrentDisplayHistory();
        this.RestoreFromMoveHistory(newHistory, GameStatus.Reviewing, preserveMoveTree: true);

        this.NotifyReviewStarted(newHistory);
        this.NotifyStateChanged();
    }

    public async Task ApplyReviewStartAsync(IReadOnlyList<Move> moveHistory)
    {
        this.RestoreFromMoveHistory(moveHistory, GameStatus.Reviewing, preserveMoveTree: true);
        this.NotifyStateChanged();
    }

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
        var (newState, recordedMove) = this.ApplyMoveCore(moveWithTurn);
        this.MoveTree.AddMove(recordedMove);
        this.State = newState;

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
        var result = this.ApplyDropCore(moveWithTurn);
        if (result is null) {
            return false;
        }

        var (newState, recordedMove) = result.Value;
        this.MoveTree.AddMove(recordedMove);
        this.State = newState;

        this.NotifyReviewMove(recordedMove);
        this.NotifyStateChanged();
        return true;
    }

    public async Task ApplyReviewMoveAsync(Move move)
    {
        if (this.State.Status != GameStatus.Reviewing) {
            return;
        }

        this.SyncMoveTreeToCurrentPosition();

        if (move.IsDrop) {
            var result = this.ApplyDropCore(move);
            if (result is null) {
                return;
            }

            var (newState, recordedMove) = result.Value;
            this.MoveTree.AddMove(recordedMove);
            this.State = newState;
        } else if (move.From is not null) {
            var piece = this.State.Board[move.From.Value];
            if (piece is null) {
                return;
            }

            var (newState, recordedMove) = this.ApplyMoveCore(move);
            this.MoveTree.AddMove(recordedMove);
            this.State = newState;
        }

        this.NotifyStateChanged();
    }

    #endregion

    #region 詰み手順ブランチ追加

    public async Task<bool> AddMateSequenceBranchAsync(string usiMoves, int branchStartIndex)
    {
        if (string.IsNullOrWhiteSpace(usiMoves)) {
            return false;
        }

        var moves = usiMoves.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (moves.Length == 0) {
            return false;
        }

        var displayHistory = this.State.DisplayBranchHistory;
        var viewingIndex = this.State.ViewingMoveIndex ?? displayHistory.Count;
        var (board, firstCaptured, secondCaptured, currentTurn) = this.GetBoardAtMove(viewingIndex);

        var currentNode = this.MoveTree.CurrentNode;

        var parsedMoves = new List<Move>();
        var tempBoard = board;
        var tempFirstCaptured = firstCaptured;
        var tempSecondCaptured = secondCaptured;
        var tempTurn = currentTurn;

        foreach (var usiMove in moves) {
            var move = UsiParser.ParseMove(usiMove, tempBoard, tempTurn);
            if (move is null) {
                return false;
            }

            if (!move.IsDrop && move.From is { } from) {
                var targetPiece = tempBoard[move.To];
                if (targetPiece is not null) {
                    move = move.WithCapturedPiece(targetPiece.Type);
                }
            }
            move = move.WithTurn(tempTurn);

            parsedMoves.Add(move);

            (tempBoard, tempFirstCaptured, tempSecondCaptured) = ShogiRules.ApplyMove(
                tempBoard, move, tempTurn, tempFirstCaptured, tempSecondCaptured);
            tempTurn = tempTurn.GetOpponent();
        }

        var nodeToAddFrom = currentNode;
        foreach (var move in parsedMoves) {
            nodeToAddFrom = nodeToAddFrom is null ? this.MoveTree.AddMoveWithoutAdvance(move) : nodeToAddFrom.AddChild(move);
        }

        this.NotifyStateChanged();
        return true;
    }

    #endregion

    #region 共通Move/Drop処理

    /// <summary>移動を適用（検証済み前提）</summary>
    private (GameState newState, Move recordedMove) ApplyMoveCore(Move move, TimeSpan? moveTime = null)
    {
        var from = move.From!.Value;
        var to = move.To;
        var turn = move.Turn;
        var piece = this.State.Board[from]!;
        var captured = this.State.Board[to];
        var newCaptured = this.State.GetCapturedPieces(turn);
        var recordedMove = move;

        if (captured is not null) {
            newCaptured = newCaptured.Add(captured.Type);
            recordedMove = recordedMove.WithCapturedPiece(captured.Type);
        }

        var newPiece = move.IsPromotion && piece.Type.CanPromote()
            ? piece with { Type = piece.Type.GetPromotedType() }
            : piece;

        var newMoveTimes = moveTime.HasValue
            ? (this.State.MoveTimes ?? []).Add(moveTime.Value)
            : this.State.MoveTimes;

        var newState = this.State with {
            Board = this.State.Board.MovePiece(from, to, newPiece),
            MoveHistory = this.State.MoveHistory.Add(recordedMove),
            MoveTimes = newMoveTimes,
            CurrentTurn = turn.GetOpponent(),
            ViewingMoveIndex = null
        };
        newState = newState.WithCapturedPieces(turn, newCaptured);

        return (newState, recordedMove);
    }

    /// <summary>打ち駒を適用（検証済み前提）</summary>
    private (GameState newState, Move recordedMove)? ApplyDropCore(Move move, TimeSpan? moveTime = null)
    {
        var turn = move.Turn;
        var captured = this.State.GetCapturedPieces(turn);
        var newCaptured = captured.TryRemove(move.PieceType);
        if (newCaptured is null) {
            return null;
        }

        var recordedMove = move.WithTurn(turn);
        var newMoveTimes = moveTime.HasValue
            ? (this.State.MoveTimes ?? []).Add(moveTime.Value)
            : this.State.MoveTimes;

        var newState = this.State with {
            Board = this.State.Board.SetPiece(move.To, new Piece(move.PieceType, turn)),
            MoveHistory = this.State.MoveHistory.Add(recordedMove),
            MoveTimes = newMoveTimes,
            CurrentTurn = turn.GetOpponent(),
            ViewingMoveIndex = null
        };
        newState = newState.WithCapturedPieces(turn, newCaptured);

        return (newState, recordedMove);
    }

    #endregion

    #region ヘルパー

    private IReadOnlyList<Move> GetCurrentDisplayHistory()
    {
        var displayHistory = this.State.DisplayBranchHistory;
        var viewingIndex = this.State.ViewingMoveIndex ?? displayHistory.Count;
        return [.. displayHistory.Take(viewingIndex)];
    }

    private void RestoreFromMoveHistory(IReadOnlyList<Move> moveHistory, GameStatus status, bool preserveMoveTree, bool resetTimes = false, IReadOnlyList<TimeSpan>? moveTimes = null)
    {
        var localTurn = this.State.LocalTurn;
        var (board, firstCaptured, secondCaptured, currentTurn) = ShogiRules.ReconstructBoard(moveHistory);

        if (!preserveMoveTree) {
            this.MoveTree = new MoveTree();
        }
        this.MoveTree.GoToStart();
        foreach (var move in moveHistory) {
            this.MoveTree.AddMove(move);
        }

        var times = moveTimes is not null
            ? moveTimes.ToImmutableList()
            : resetTimes
                ? null
                : this.State.MoveTimes;

        this.State = new GameState(
            board,
            currentTurn,
            status,
            firstCaptured,
            secondCaptured,
            [.. moveHistory],
            localTurn,
            null,
            null,
            times
        );
    }

    private void SyncMoveTreeToViewingPosition()
    {
        var viewingIndex = this.State.ViewingMoveIndex ?? this.State.MoveHistory.Count;
        this.SyncMoveTreeToPosition(viewingIndex);
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

    private void GoToMoveHistoryEnd()
    {
        var moveTree = this.MoveTree;
        moveTree.GoToStart();
        foreach (var move in this.State.MoveHistory) {
            var nextMoves = moveTree.NextMoves;
            var matchingNode = nextMoves.FirstOrDefault(n => MoveNode.IsSameMove(n.Move, move));
            if (matchingNode is not null) {
                moveTree.GoTo(matchingNode);
            } else {
                break;
            }
        }
    }

    #endregion

    public void Dispose()
    {
        this._stateChanged.Dispose();
        this._branchResumed.Dispose();
        this._reviewStarted.Dispose();
        this._reviewMove.Dispose();
        GC.SuppressFinalize(this);
    }
}
