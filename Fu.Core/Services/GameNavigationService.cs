using Fu.Core.Abstractions;
using Fu.Core.Models;

namespace Fu.Core.Services;

/// <summary>
/// 棋譜ナビゲーションサービスの実装
/// 手の前後移動、分岐ノード移動などを担当
/// </summary>
public class GameNavigationService : IGameNavigationService
{
    private readonly IShogiRules _rules;
    private readonly ShogiGameState _state;
    private readonly IGameEventPublisher _events;
    private readonly IBoardCache _boardCache;

    public GameNavigationService(
        IShogiRules rules,
        ShogiGameState state,
        IGameEventPublisher events,
        IBoardCache boardCache)
    {
        this._rules = rules;
        this._state = state;
        this._events = events;
        this._boardCache = boardCache;
    }

    private GameState State
    {
        get => this._state.State;
        set => this._state.State = value;
    }

    private MoveTree MoveTree => this._state.MoveTree;

    public Task GoBackAsync()
    {
        var currentIndex = this.State.DisplayMoveIndex;
        if (currentIndex > 0) {
            this.State.ViewingMoveIndex = currentIndex - 1;
            this._events.NotifyStateChanged();
        }
        return Task.CompletedTask;
    }

    public Task GoForwardAsync()
    {
        var currentIndex = this.State.DisplayMoveIndex;
        if (currentIndex < this.State.MoveHistory.Count) {
            var newIndex = currentIndex + 1;
            this.State.ViewingMoveIndex = newIndex == this.State.MoveHistory.Count ? null : newIndex;
            this._events.NotifyStateChanged();
        }
        return Task.CompletedTask;
    }

    public Task GoForwardBranchAsync(int branchIndex)
    {
        this.SyncMoveTreeToViewingPosition();

        if (this.MoveTree.GoForwardBranch(branchIndex)) {
            var currentNode = this.MoveTree.CurrentNode;
            if (currentNode is not null) {
                var branchMoves = currentNode.GetMoves();
                var (board, firstCaptured, secondCaptured, currentTurn) = this._rules.ReconstructBoard(branchMoves);

                this.State.Board = board;
                this.State.FirstCaptured = firstCaptured;
                this.State.SecondCaptured = secondCaptured;
                this.State.CurrentTurn = currentTurn;
                this.State.MoveHistory = [.. branchMoves];
                this.State.ViewingMoveIndex = branchMoves.Count == this.State.MoveHistory.Count ? null : branchMoves.Count;

                this._events.NotifyStateChanged();
            }
        }
        return Task.CompletedTask;
    }

    public Task GoToLatestAsync()
    {
        if (this.State.IsReviewing) {
            this.GoToMoveHistoryEnd();
            this.State.ViewingMoveIndex = null;
            this.State.ViewingBranchHistory = null;
            this._events.NotifyStateChanged();
        }
        return Task.CompletedTask;
    }

    public Task SetViewingMoveIndexAsync(int moveIndex)
    {
        var newIndex = moveIndex >= this.State.MoveHistory.Count ? null : (int?)moveIndex;
        if (this.State.ViewingMoveIndex != newIndex) {
            this.State.ViewingMoveIndex = newIndex;
            this._events.NotifyStateChanged();
        }
        return Task.CompletedTask;
    }

    public Task GoToNodeAsync(MoveNode? node)
    {
        this.MoveTree.GoTo(node);

        if (node is null) {
            this.State.ViewingMoveIndex = 0;
            this.State.ViewingBranchHistory = null;
        } else {
            var nodeMoves = node.GetMoves();
            var isExactSamePath = nodeMoves.Count == this.State.MoveHistory.Count &&
                                  nodeMoves.Select((m, i) => MoveNode.IsSameMove(m, this.State.MoveHistory[i])).All(x => x);

            if (isExactSamePath) {
                this.State.ViewingMoveIndex = null;
                this.State.ViewingBranchHistory = null;
            } else {
                var isSamePath = nodeMoves.Count <= this.State.MoveHistory.Count &&
                                 nodeMoves.Select((m, i) => MoveNode.IsSameMove(m, this.State.MoveHistory[i])).All(x => x);

                if (isSamePath) {
                    this.State.ViewingMoveIndex = node.Depth;
                    this.State.ViewingBranchHistory = null;
                } else {
                    this.State.ViewingMoveIndex = null;
                    this.State.ViewingBranchHistory = [.. nodeMoves];
                }
            }
        }
        this._events.NotifyStateChanged();
        return Task.CompletedTask;
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

    public int GetCurrentBranchIndex() =>
        this.MoveTree.GetBranchIndexForNode(this.MoveTree.CurrentNode);

    public int GetTotalBranchCount() =>
        this.MoveTree.GetAllBranchEndNodes().Count;

    public (Board board, CapturedPieces firstCaptured, CapturedPieces secondCaptured, Turn currentTurn) GetBoardAtMove(int moveIndex)
    {
        var branchHistory = this.State.DisplayBranchHistory;

        // キャッシュが有効かチェック
        var cached = this._boardCache.TryGet(branchHistory, moveIndex);
        if (cached is { } c) {
            return c;
        }

        // 再構築してキャッシュ
        var result = this._rules.ReconstructBoard(branchHistory.Take(moveIndex));
        this._boardCache.Store(branchHistory, moveIndex, result.board, result.firstCaptured, result.secondCaptured, result.currentTurn);
        return result;
    }

    #region ヘルパー

    private async Task GoToBranchEndAsync(int branchIndex)
    {
        var allEndNodes = this.MoveTree.GetAllBranchEndNodes();
        if (branchIndex < 0 || branchIndex >= allEndNodes.Count) {
            return;
        }

        var endNode = allEndNodes[branchIndex];
        await this.GoToNodeAsync(endNode);
    }

    private void SyncMoveTreeToViewingPosition()
    {
        var viewingIndex = this.State.ViewingMoveIndex ?? this.State.MoveHistory.Count;
        this.SyncMoveTreeToPosition(viewingIndex);
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
}
