using Fu.Core.Abstractions;
using Fu.Core.Models;

namespace Fu.Core.Services;

/// <summary>
/// 分岐・再戦・検討モードサービスの実装
/// 分岐からの再開、再戦、検討モードの操作を担当
/// </summary>
public class BranchService : IBranchService
{
    private readonly IShogiRules _rules;
    private readonly IUsiParser _usiParser;
    private readonly GameStore _store;
    private readonly ITurnTimerService _timer;
    private readonly IBoardCache _boardCache;

    public BranchService(
        IShogiRules rules,
        IUsiParser usiParser,
        GameStore store,
        ITurnTimerService timer,
        IBoardCache boardCache)
    {
        this._rules = rules;
        this._usiParser = usiParser;
        this._store = store;
        this._timer = timer;
        this._boardCache = boardCache;
    }

    private GameState State => this._store.MutableState;
    private MoveTree MoveTree => this._store.MoveTree;

    #region 分岐再開

    public Task ResumeFromBranchAsync()
    {
        if (this.State.IsReviewing && this.State.Status == GameStatus.Playing) {
            this.BranchFromCurrentPosition(notifyBranchResumed: true);
            this._timer.Start();
        }
        return Task.CompletedTask;
    }

    public Task ApplyBranchResumeAsync(IReadOnlyList<Move> moveHistory)
    {
        this.RestoreFromMoveHistory(moveHistory, GameStatus.Playing, preserveMoveTree: true);
        this._timer.Start();
        return Task.CompletedTask;
    }

    #endregion

    #region 再戦

    public Task RematchFromCurrentPositionAsync()
    {
        var newHistory = this.GetCurrentDisplayHistory();
        this.RestoreFromMoveHistory(newHistory, GameStatus.Playing, preserveMoveTree: false, resetTimes: true);
        this._timer.Start();
        return Task.CompletedTask;
    }

    public Task ApplyRematchAsync(IReadOnlyList<Move> moveHistory)
    {
        this.RestoreFromMoveHistory(moveHistory, GameStatus.Playing, preserveMoveTree: false, resetTimes: true);
        this._timer.Start();
        return Task.CompletedTask;
    }

    #endregion

    #region 検討モード

    public Task StartReviewFromCurrentPositionAsync()
    {
        var newHistory = this.GetCurrentDisplayHistory();
        this.RestoreFromMoveHistory(newHistory, GameStatus.Reviewing, preserveMoveTree: true);
        this._store.NotifyReviewStarted(newHistory);
        return Task.CompletedTask;
    }

    public Task ApplyReviewStartAsync(IReadOnlyList<Move> moveHistory)
    {
        this.RestoreFromMoveHistory(moveHistory, GameStatus.Reviewing, preserveMoveTree: true);
        return Task.CompletedTask;
    }

    public Task ApplyReviewMoveAsync(Move move)
    {
        if (this.State.Status != GameStatus.Reviewing) {
            return Task.CompletedTask;
        }

        this.SyncMoveTreeToCurrentPosition();

        if (move.IsDrop) {
            var recordedMove = this.ApplyDropCore(move);
            if (recordedMove is null) {
                return Task.CompletedTask;
            }

            this.MoveTree.AddMove(recordedMove);
        } else if (move.From is not null) {
            var piece = this.State.Board[move.From.Value];
            if (piece is null) {
                return Task.CompletedTask;
            }

            var recordedMove = this.ApplyMoveCore(move);
            this.MoveTree.AddMove(recordedMove);
        }

        return Task.CompletedTask;
    }

    #endregion

    #region 詰み手順

    public Task<bool> AddMateSequenceBranchAsync(string usiMoves, int branchStartIndex)
    {
        if (string.IsNullOrWhiteSpace(usiMoves)) {
            return Task.FromResult(false);
        }

        var usiMoveList = usiMoves.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (usiMoveList.Length == 0) {
            return Task.FromResult(false);
        }

        var displayHistory = this.State.DisplayBranchHistory;
        var viewingIndex = this.State.ViewingMoveIndex ?? displayHistory.Count;
        var (board, firstCaptured, secondCaptured, currentTurn) = this.GetBoardAtMove(viewingIndex);

        // 全ての手をパース
        var parsedMoves = new List<Move>();
        var tempBoard = board;
        var tempFirstCaptured = firstCaptured;
        var tempSecondCaptured = secondCaptured;
        var tempTurn = currentTurn;

        foreach (var usiMove in usiMoveList) {
            var move = this._usiParser.ParseMove(usiMove, tempBoard, tempTurn);
            if (move is null) {
                return Task.FromResult(false);
            }

            if (!move.IsDrop && move.From is { } from) {
                var targetPiece = tempBoard[move.To];
                if (targetPiece is not null) {
                    move = move.WithCapturedPiece(targetPiece.Type);
                }
            }
            move = move.WithTurn(tempTurn);

            parsedMoves.Add(move);

            (tempBoard, tempFirstCaptured, tempSecondCaptured) = this._rules.ApplyMove(
                tempBoard, move, tempTurn, tempFirstCaptured, tempSecondCaptured);
            tempTurn = tempTurn.GetOpponent();
        }

        // 現在表示中の位置に対応するノードを取得
        var nodeToAddFrom = this.GetNodeAtViewingPosition(viewingIndex);

        // 全く同じ手順が既に存在するかチェック
        if (this.HasExactSequenceFromNode(nodeToAddFrom, parsedMoves)) {
            return Task.FromResult(false);
        }

        // 分岐を追加
        foreach (var move in parsedMoves) {
            nodeToAddFrom = nodeToAddFrom is null
                ? this.MoveTree.AddMoveWithoutAdvance(move)
                : nodeToAddFrom.AddChild(move);
        }

        return Task.FromResult(true);
    }

    #endregion

    #region ヘルパー

    private IReadOnlyList<Move> GetCurrentDisplayHistory()
    {
        var displayHistory = this.State.DisplayBranchHistory;
        var viewingIndex = this.State.ViewingMoveIndex ?? displayHistory.Count;
        return [.. displayHistory.Take(viewingIndex)];
    }

    private void BranchFromCurrentPosition(bool notifyBranchResumed)
    {
        var newHistory = this.GetCurrentDisplayHistory();
        var (board, firstCaptured, secondCaptured, currentTurn) = this._rules.ReconstructBoard(newHistory);

        this.MoveTree.GoToStart();
        foreach (var move in newHistory) {
            this.MoveTree.AddMove(move);
        }

        this._store.Update(state => {
            state.Board = board;
            state.FirstCaptured = firstCaptured;
            state.SecondCaptured = secondCaptured;
            state.CurrentTurn = currentTurn;
            state.MoveHistory = [.. newHistory];
            state.ViewingMoveIndex = null;
            state.ViewingBranchHistory = null;
        });

        if (notifyBranchResumed) {
            this._store.NotifyBranchResumed(newHistory);
        }
    }

    /// <summary>
    /// 指定した手順履歴から状態を復元する
    /// 注意: これは同一スコープ内での状態遷移（再戦・検討モード等）のためのメソッド
    /// 新規対局には使用しないこと（新しいGameScopeを作成すべき）
    /// </summary>
    private void RestoreFromMoveHistory(IReadOnlyList<Move> moveHistory, GameStatus status, bool preserveMoveTree, bool resetTimes = false, IReadOnlyList<TimeSpan>? moveTimes = null)
    {
        var localTurn = this.State.LocalTurn;
        var (board, firstCaptured, secondCaptured, currentTurn) = this._rules.ReconstructBoard(moveHistory);

        // 再戦時は分岐履歴をリセット（preserveMoveTree=false）
        // 検討モード時は分岐履歴を保持（preserveMoveTree=true）
        if (!preserveMoveTree) {
            this._store.ReplaceMoveTree(new MoveTree());
        }

        // 盤面が変わるのでキャッシュは無効化
        this._boardCache.Clear();

        this.MoveTree.GoToStart();
        foreach (var move in moveHistory) {
            this.MoveTree.AddMove(move);
        }

        var times = moveTimes is not null
            ? moveTimes.ToList()
            : resetTimes
                ? null
                : this.State.MoveTimes;

        this._store.Update(state => {
            state.Board = board;
            state.CurrentTurn = currentTurn;
            state.Status = status;
            state.FirstCaptured = firstCaptured;
            state.SecondCaptured = secondCaptured;
            state.MoveHistory = [.. moveHistory];
            state.LocalTurn = localTurn;
            state.ViewingMoveIndex = null;
            state.ViewingBranchHistory = null;
            state.MoveTimes = times;
            state.TimeState = null;
        });
    }

    private void SyncMoveTreeToCurrentPosition()
    {
        var targetIndex = this.State.MoveHistory.Count;
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

    private (Board board, CapturedPieces firstCaptured, CapturedPieces secondCaptured, Turn currentTurn) GetBoardAtMove(int moveIndex)
    {
        var branchHistory = this.State.DisplayBranchHistory;

        var cached = this._boardCache.TryGet(branchHistory, moveIndex);
        if (cached is { } c) {
            return c;
        }

        var result = this._rules.ReconstructBoard(branchHistory.Take(moveIndex));
        this._boardCache.Store(branchHistory, moveIndex, result.board, result.firstCaptured, result.secondCaptured, result.currentTurn);
        return result;
    }

    /// <summary>表示位置に対応するノードを取得</summary>
    private MoveNode? GetNodeAtViewingPosition(int viewingIndex)
    {
        if (viewingIndex == 0) {
            return null;
        }

        var branchHistory = this.State.DisplayBranchHistory;
        var movesToFollow = branchHistory.Take(viewingIndex).ToList();

        // ルートから順にたどる
        MoveNode? node = null;
        var children = this.MoveTree.RootChildren;

        foreach (var move in movesToFollow) {
            var next = children.FirstOrDefault(c => MoveNode.IsSameMove(c.Move, move));
            if (next is null) {
                // ツリーに存在しない手順の場合はnull
                return null;
            }
            node = next;
            children = node.Children;
        }

        return node;
    }

    /// <summary>指定ノードから完全一致する手順が存在するかチェック</summary>
#pragma warning disable CA1859
    private bool HasExactSequenceFromNode(MoveNode? startNode, IReadOnlyList<Move> moves)
#pragma warning restore CA1859
    {
        if (moves.Count == 0) {
            return true;
        }

        var children = startNode?.Children ?? (IReadOnlyList<MoveNode>)this.MoveTree.RootChildren;
        var node = children.FirstOrDefault(c => MoveNode.IsSameMove(c.Move, moves[0]));

        for (var i = 0; i < moves.Count; i++) {
            if (node is null) {
                return false;
            }

            if (!MoveNode.IsSameMove(node.Move, moves[i])) {
                return false;
            }

            if (i < moves.Count - 1) {
                node = node.Children.FirstOrDefault(c => MoveNode.IsSameMove(c.Move, moves[i + 1]));
            }
        }

        return true;
    }

    /// <summary>移動を適用（検証済み前提）</summary>
    private Move ApplyMoveCore(Move move, TimeSpan? moveTime = null)
    {
        var from = move.From!.Value;
        var to = move.To;
        var turn = move.Turn;
        var piece = this.State.Board[from]!;
        var captured = this.State.Board[to];
        var myCaptured = this.State.GetCapturedPieces(turn);
        var recordedMove = move;

        if (captured is not null) {
            myCaptured.Add(captured.Type);
            recordedMove = recordedMove.WithCapturedPiece(captured.Type);
        }

        var newPiece = move.IsPromotion && piece.Type.CanPromote()
            ? piece with { Type = piece.Type.GetPromotedType() }
            : piece;

        this._store.Update(state => {
            state.Board = state.Board.MovePiece(from, to, newPiece);
            state.MoveHistory.Add(recordedMove);
            if (moveTime.HasValue) {
                state.MoveTimes ??= [];
                state.MoveTimes.Add(moveTime.Value);
            }
            state.CurrentTurn = turn.GetOpponent();
            state.ViewingMoveIndex = null;
        });

        return recordedMove;
    }

    /// <summary>打ち駒を適用（検証済み前提）</summary>
    private Move? ApplyDropCore(Move move, TimeSpan? moveTime = null)
    {
        var turn = move.Turn;
        var myCaptured = this.State.GetCapturedPieces(turn);
        if (!myCaptured.TryRemove(move.PieceType)) {
            return null;
        }

        var recordedMove = move.WithTurn(turn);

        this._store.Update(state => {
            state.Board = state.Board.SetPiece(move.To, new Piece(move.PieceType, turn));
            state.MoveHistory.Add(recordedMove);
            if (moveTime.HasValue) {
                state.MoveTimes ??= [];
                state.MoveTimes.Add(moveTime.Value);
            }
            state.CurrentTurn = turn.GetOpponent();
            state.ViewingMoveIndex = null;
        });

        return recordedMove;
    }

    #endregion
}
