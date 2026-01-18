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
    private readonly GameStore _store;
    private readonly IBoardCache _boardCache;

    public GameNavigationService(
        IShogiRules rules,
        GameStore store,
        IBoardCache boardCache)
    {
        this._rules = rules;
        this._store = store;
        this._boardCache = boardCache;
    }

    private GameState State => this._store.MutableState;
    private MoveTree MoveTree => this._store.MoveTree;

    public Task GoBackAsync()
    {
        // MoveTreeも同期（検討モード中、または閲覧中の評価値キャッシュ参照用）
        if ((this.State.Status == GameStatus.Reviewing || this.State.IsReviewing)
            && this.MoveTree.CurrentNode is not null) {
            this.MoveTree.GoBack();
        }

        this._store.Update(state => {
            if (state.DisplayMoveIndex > 0) {
                state.ViewingMoveIndex = state.DisplayMoveIndex - 1;
            }
        });
        return Task.CompletedTask;
    }

    public Task GoForwardAsync()
    {
        // MoveTreeも同期（検討モード中、または閲覧中の評価値キャッシュ参照用）
        if ((this.State.Status == GameStatus.Reviewing || this.State.IsReviewing)
            && this.State.DisplayMoveIndex < this.State.MoveHistory.Count) {
            this.MoveTree.GoForward();
        }

        this._store.Update(state => {
            var currentIndex = state.DisplayMoveIndex;
            if (currentIndex < state.MoveHistory.Count) {
                var newIndex = currentIndex + 1;
                state.ViewingMoveIndex = newIndex == state.MoveHistory.Count ? null : newIndex;
            }
        });
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

                this._store.Update(state => {
                    state.Board = board;
                    state.FirstCaptured = firstCaptured;
                    state.SecondCaptured = secondCaptured;
                    state.CurrentTurn = currentTurn;
                    state.MoveHistory = [.. branchMoves];
                    state.ViewingMoveIndex = null;
                });
            }
        }
        return Task.CompletedTask;
    }

    public Task GoToLatestAsync()
    {
        if (this.State.IsReviewing) {
            this.GoToMoveHistoryEnd();
            this._store.Update(state => {
                state.ViewingMoveIndex = null;
                state.ViewingBranchHistory = null;
            });
        }
        return Task.CompletedTask;
    }

    public Task SetViewingMoveIndexAsync(int moveIndex)
    {
        // MoveTreeも同期（検討モード中、または閲覧中の評価値キャッシュ参照用）
        if (this.State.Status == GameStatus.Reviewing || this.State.IsReviewing) {
            this.SyncMoveTreeToPosition(moveIndex);
        }

        this._store.Update(state => {
            var newIndex = moveIndex >= state.MoveHistory.Count ? null : (int?)moveIndex;
            if (state.ViewingMoveIndex != newIndex) {
                state.ViewingMoveIndex = newIndex;
            }
        });
        return Task.CompletedTask;
    }

    public Task GoToNodeAsync(MoveNode? node)
    {
        this.MoveTree.GoTo(node);

        this._store.Update(state => {
            if (node is null) {
                state.ViewingMoveIndex = 0;
                state.ViewingBranchHistory = null;
                return;
            }

            var nodeMoves = node.GetMoves();
            var isExactSamePath = nodeMoves.Count == state.MoveHistory.Count &&
                                  nodeMoves.Select((m, i) => MoveNode.IsSameMove(m, state.MoveHistory[i])).All(x => x);

            if (isExactSamePath) {
                state.ViewingMoveIndex = null;
                state.ViewingBranchHistory = null;
                return;
            }

            var isSamePath = nodeMoves.Count <= state.MoveHistory.Count &&
                             nodeMoves.Select((m, i) => MoveNode.IsSameMove(m, state.MoveHistory[i])).All(x => x);

            if (isSamePath) {
                state.ViewingMoveIndex = node.Depth;
                state.ViewingBranchHistory = null;
                return;
            }

            state.ViewingMoveIndex = null;
            state.ViewingBranchHistory = [.. nodeMoves];
        });
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
