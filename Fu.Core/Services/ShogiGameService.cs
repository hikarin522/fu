using System.Collections.Frozen;
using System.Collections.Immutable;

using Fu.Core.Models;

namespace Fu.Core.Services;

public class ShogiGameService
{
    private const int SentePromotionBoundary = 2;  // 先手の成れる段（0-2）
    private const int GotePromotionBoundary = 6;   // 後手の成れる段（6-8）

    public GameState State { get; private set; } = GameState.Initial;

    public event Func<ValueTask>? OnStateChangedAsync;
    public event Func<ImmutableList<Move>, ValueTask>? OnBranchResumedAsync;

    private ValueTask NotifyStateChangedAsync() =>
        OnStateChangedAsync is { } handler ? handler() : ValueTask.CompletedTask;

    private ValueTask NotifyBranchResumedAsync(ImmutableList<Move> moveHistory) =>
        OnBranchResumedAsync is { } handler ? handler(moveHistory) : ValueTask.CompletedTask;

    public async Task NewGameAsync()
    {
        var localPlayer = this.State.LocalPlayer;
        this.State = GameState.Initial with {
            Status = GameStatus.Playing,
            LocalPlayer = localPlayer,
            MoveTree = new MoveTree()
        };
        await this.NotifyStateChangedAsync();
    }

    public async Task SetLocalPlayerAsync(Player player)
    {
        this.State = this.State with { LocalPlayer = player };
        await this.NotifyStateChangedAsync();
    }

    public List<Position> GetLegalMoves(Position from)
    {
        var piece = this.State.Board[from];
        if (piece is null || piece.Owner != this.State.CurrentPlayer) {
            return [];
        }

        var moves = GetPossibleMovesOnBoard(this.State.Board, from, piece);
        return [.. moves.Where(to => !WouldBeInCheck(this.State.Board, from, to, this.State.CurrentPlayer))];
    }

    public List<Position> GetLegalDropPositions(PieceType pieceType) =>
        [.. Board.AllPositions.Where(pos => this.CanDropAt(pos, pieceType))];

    private bool CanDropAt(Position pos, PieceType pieceType)
    {
        if (this.State.Board[pos] is not null) {
            return false;
        }

        if (pieceType == PieceType.Pawn && this.State.Board.HasPawnInColumn(pos.Col, this.State.CurrentPlayer)) {
            return false;
        }

        if (!CanExistAtRow(pieceType, pos.Row, this.State.CurrentPlayer)) {
            return false;
        }

        if (pieceType == PieceType.Pawn && this.WouldBePawnDropMate(pos)) {
            return false;
        }

        var testBoard = this.State.Board.SetPiece(pos, new Piece(pieceType, this.State.CurrentPlayer));
        return !IsInCheck(testBoard, this.State.CurrentPlayer);
    }

    private static bool CanExistAtRow(PieceType type, int row, Player player)
    {
        var effectiveRow = player == Player.Sente ? row : Board.Size - 1 - row;

        return type switch {
            PieceType.Pawn or PieceType.Lance => effectiveRow > 0,
            PieceType.Knight => effectiveRow > 1,
            _ => true
        };
    }

    private bool WouldBePawnDropMate(Position dropPos)
    {
        var opponent = this.State.CurrentPlayer.GetOpponent();
        var kingPos = this.State.Board.FindKing(opponent);
        if (kingPos is null) {
            return false;
        }

        var direction = this.State.CurrentPlayer.GetForwardDirection();
        if (dropPos.Col != kingPos.Value.Col || dropPos.Row != kingPos.Value.Row + direction) {
            return false;
        }

        var tempBoard = this.State.Board.SetPiece(dropPos, new Piece(PieceType.Pawn, this.State.CurrentPlayer));

        var kingMoves = GetPossibleMovesOnBoard(tempBoard, kingPos.Value, tempBoard[kingPos.Value]!);
        foreach (var move in kingMoves) {
            var testBoard = tempBoard.MovePiece(kingPos.Value, move);
            if (!IsInCheck(testBoard, opponent)) {
                return false;
            }
        }

        foreach (var (pos, piece) in this.State.Board.GetAllPieces(opponent)) {
            if (pos == kingPos) {
                continue;
            }

            var moves = GetPossibleMovesOnBoard(this.State.Board, pos, piece);
            if (moves.Contains(dropPos)) {
                var testBoard = tempBoard.MovePiece(pos, dropPos);
                if (!IsInCheck(testBoard, opponent)) {
                    return false;
                }
            }
        }

        return true;
    }

    public async Task<bool> TryMakeMoveAsync(Move move)
    {
        if (this.State.Status != GameStatus.Playing) {
            return false;
        }

        // 検討モード中は手を指せない（「ここから再開」を押す必要がある）
        if (this.State.IsReviewing) {
            return false;
        }

        // MoveTreeのCurrentNodeをMoveHistoryと同期する
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

        if (piece is null || piece.Owner != this.State.CurrentPlayer) {
            return false;
        }

        var legalMoves = this.GetLegalMoves(from);
        if (!legalMoves.Contains(to)) {
            return false;
        }

        var captured = this.State.Board[to];
        var newCaptured = this.State.GetCapturedPieces(this.State.CurrentPlayer);
        var moveToRecord = move;

        if (captured is not null) {
            newCaptured = newCaptured.Add(captured.Type);
            moveToRecord = move.WithCapturedPiece(captured.Type);

            if (captured.Type == PieceType.King) {
                this.State.MoveTree.AddMove(moveToRecord);
                this.State = this.State with {
                    Board = this.State.Board.MovePiece(from, to),
                    MoveHistory = this.State.MoveHistory.Add(moveToRecord),
                    Status = this.State.CurrentPlayer.GetWinStatus()
                };
                this.State = this.State.WithCapturedPieces(this.State.CurrentPlayer, newCaptured);
                await this.NotifyStateChangedAsync();
                return true;
            }
        }

        var newPiece = move.IsPromotion && piece.Type.CanPromote()
            ? piece with { Type = piece.Type.GetPromotedType() }
            : piece;

        this.State.MoveTree.AddMove(moveToRecord);

        var newState = this.State with {
            Board = this.State.Board.MovePiece(from, to, newPiece),
            MoveHistory = this.State.MoveHistory.Add(moveToRecord),
            ViewingMoveIndex = null
        };
        newState = newState.WithCapturedPieces(this.State.CurrentPlayer, newCaptured);
        newState = newState.SwitchPlayer();

        this.State = newState;

        await this.CheckForCheckmateAsync();

        await this.NotifyStateChangedAsync();
        return true;
    }

    private async Task BranchFromCurrentPositionAsync()
    {
        var viewingIndex = this.State.ViewingMoveIndex ?? this.State.MoveHistory.Count;

        // MoveHistoryの手順に沿ってMoveTreeを辿る（分岐を正しく追跡）
        this.State.MoveTree.GoToStart();
        for (var i = 0; i < viewingIndex; i++) {
            // AddMoveは既存の同じ手があればそれを返すので、正しいパスを辿れる
            this.State.MoveTree.AddMove(this.State.MoveHistory[i]);
        }

        var (board, senteCaptured, goteCaptured, currentPlayer) = this.GetBoardAtMove(viewingIndex);

        var newHistory = this.State.MoveHistory.Take(viewingIndex).ToImmutableList();

        this.State = this.State with {
            Board = board,
            SenteCaptured = senteCaptured,
            GoteCaptured = goteCaptured,
            CurrentPlayer = currentPlayer,
            MoveHistory = newHistory,
            ViewingMoveIndex = null
        };

        // 分岐再開を通知（相手側に同期するため）
        await this.NotifyBranchResumedAsync(newHistory);
    }

    private async Task<bool> TryDropPieceAsync(Move move)
    {
        var captured = this.State.GetCapturedPieces(this.State.CurrentPlayer);
        if (captured.GetCount(move.PieceType) <= 0) {
            return false;
        }

        var legalPositions = this.GetLegalDropPositions(move.PieceType);
        if (!legalPositions.Contains(move.To)) {
            return false;
        }

        var newCaptured = captured.TryRemove(move.PieceType);
        if (newCaptured is null) {
            return false;
        }

        this.State.MoveTree.AddMove(move);

        var newState = this.State with {
            Board = this.State.Board.SetPiece(move.To, new Piece(move.PieceType, this.State.CurrentPlayer)),
            MoveHistory = this.State.MoveHistory.Add(move),
            ViewingMoveIndex = null
        };
        newState = newState.WithCapturedPieces(this.State.CurrentPlayer, newCaptured);
        newState = newState.SwitchPlayer();

        this.State = newState;

        await this.CheckForCheckmateAsync();

        await this.NotifyStateChangedAsync();
        return true;
    }

    public Task ApplyRemoteMoveAsync(Move move) =>
        this.TryMakeMoveAsync(move.WithPlayer(this.State.CurrentPlayer));

    /// <summary>リモートからの分岐再開を適用</summary>
    public async Task ApplyBranchResumeAsync(IReadOnlyList<Move> moveHistory)
    {
        var localPlayer = this.State.LocalPlayer;
        var moveTree = new MoveTree();

        // 棋譜を再生して盤面を復元
        var (board, senteCaptured, goteCaptured, currentPlayer) = ReconstructBoard(moveHistory);

        // MoveTree に棋譜を追加
        foreach (var move in moveHistory) {
            moveTree.AddMove(move);
        }

        this.State = new GameState(
            board,
            currentPlayer,
            GameStatus.Playing,
            senteCaptured,
            goteCaptured,
            [.. moveHistory],
            localPlayer,
            null
        ) { MoveTree = moveTree };

        await this.NotifyStateChangedAsync();
    }

    public bool CanPromote(Position from, Position to)
    {
        var piece = this.State.Board[from];
        if (piece is null || !piece.Type.CanPromote() || piece.Type.IsPromoted()) {
            return false;
        }

        return piece.Owner == Player.Sente
            ? from.Row <= SentePromotionBoundary || to.Row <= SentePromotionBoundary
            : from.Row >= GotePromotionBoundary || to.Row >= GotePromotionBoundary;
    }

    public bool MustPromote(Position from, Position to)
    {
        var piece = this.State.Board[from];
        if (piece is null) {
            return false;
        }

        return !CanExistAtRow(piece.Type, to.Row, piece.Owner);
    }

    private static List<Position> GetPossibleMovesOnBoard(Board board, Position from, Piece piece)
    {
        List<Position> moves = [];
        var directions = GetMoveDirections(piece.Type, piece.Owner);

        foreach (var (dc, dr, slide) in directions) {
            var col = from.Col + dc;
            var row = from.Row + dr;

            while (col is >= 0 and < Board.Size && row is >= 0 and < Board.Size) {
                var target = board[col, row];
                if (target is null) {
                    moves.Add(new Position(col, row));
                }
                else if (target.Owner != piece.Owner) {
                    moves.Add(new Position(col, row));
                    break;
                }
                else {
                    break;
                }

                if (!slide) {
                    break;
                }

                col += dc;
                row += dr;
            }
        }

        return moves;
    }

    private static readonly FrozenDictionary<(PieceType, int), (int dc, int dr, bool slide)[]> DirectionCache =
        BuildDirectionCache().ToFrozenDictionary();

    private static Dictionary<(PieceType, int), (int dc, int dr, bool slide)[]> BuildDirectionCache()
    {
        var cache = new Dictionary<(PieceType, int), (int dc, int dr, bool slide)[]>();

        foreach (var forward in new[] { -1, 1 }) {
            cache[(PieceType.King, forward)] = [
                (-1, -1, false), (0, -1, false), (1, -1, false),
                (-1, 0, false), (1, 0, false),
                (-1, 1, false), (0, 1, false), (1, 1, false)
            ];

            cache[(PieceType.Rook, forward)] = [
                (0, -1, true), (0, 1, true), (-1, 0, true), (1, 0, true)
            ];

            cache[(PieceType.PromotedRook, forward)] = [
                (0, -1, true), (0, 1, true), (-1, 0, true), (1, 0, true),
                (-1, -1, false), (1, -1, false), (-1, 1, false), (1, 1, false)
            ];

            cache[(PieceType.Bishop, forward)] = [
                (-1, -1, true), (1, -1, true), (-1, 1, true), (1, 1, true)
            ];

            cache[(PieceType.PromotedBishop, forward)] = [
                (-1, -1, true), (1, -1, true), (-1, 1, true), (1, 1, true),
                (0, -1, false), (0, 1, false), (-1, 0, false), (1, 0, false)
            ];

            var goldMoves = new (int, int, bool)[] {
                (0, forward, false), (-1, forward, false), (1, forward, false),
                (-1, 0, false), (1, 0, false), (0, -forward, false)
            };
            cache[(PieceType.Gold, forward)] = goldMoves;
            cache[(PieceType.PromotedSilver, forward)] = goldMoves;
            cache[(PieceType.PromotedKnight, forward)] = goldMoves;
            cache[(PieceType.PromotedLance, forward)] = goldMoves;
            cache[(PieceType.PromotedPawn, forward)] = goldMoves;

            cache[(PieceType.Silver, forward)] = [
                (0, forward, false), (-1, forward, false), (1, forward, false),
                (-1, -forward, false), (1, -forward, false)
            ];

            cache[(PieceType.Knight, forward)] = [
                (-1, forward * 2, false), (1, forward * 2, false)
            ];

            cache[(PieceType.Lance, forward)] = [
                (0, forward, true)
            ];

            cache[(PieceType.Pawn, forward)] = [
                (0, forward, false)
            ];
        }

        return cache;
    }

    private static (int dc, int dr, bool slide)[] GetMoveDirections(PieceType type, Player owner)
    {
        var forward = owner.GetForwardDirection();
        return DirectionCache.GetValueOrDefault((type, forward), []);
    }

    private static bool WouldBeInCheck(Board board, Position from, Position to, Player player)
    {
        var testBoard = board.MovePiece(from, to);
        return IsInCheck(testBoard, player);
    }

    private static bool IsInCheck(Board board, Player player)
    {
        var kingPos = board.FindKing(player);
        if (kingPos is null) {
            return false;
        }

        return board.GetAllPieces(player.GetOpponent())
            .Any(x => GetPossibleMovesOnBoard(board, x.pos, x.piece).Contains(kingPos.Value));
    }

    public bool IsInCheck() => IsInCheck(this.State.Board, this.State.CurrentPlayer);

    private async Task CheckForCheckmateAsync()
    {
        var currentPlayer = this.State.CurrentPlayer;

        foreach (var (pos, _) in this.State.Board.GetAllPieces(currentPlayer)) {
            if (this.GetLegalMoves(pos).Count > 0) {
                return;
            }
        }

        var captured = this.State.GetCapturedPieces(currentPlayer);
        foreach (var (type, count) in captured.GetAll()) {
            if (count > 0 && this.GetLegalDropPositions(type).Count > 0) {
                return;
            }
        }

        this.State = this.State with {
            Status = currentPlayer.GetOpponent().GetWinStatus()
        };
        await this.NotifyStateChangedAsync();
    }

    public async Task ResignAsync()
    {
        this.State = this.State with {
            Status = this.State.CurrentPlayer.GetOpponent().GetWinStatus()
        };
        await this.NotifyStateChangedAsync();
    }

    /// <summary>途中参加者向けにゲーム状態を復元</summary>
    public async Task RestoreStateAsync(IReadOnlyList<Move> moveHistory, GameStatus status)
    {
        var localPlayer = this.State.LocalPlayer;
        var moveTree = new MoveTree();

        // 棋譜を再生して盤面を復元
        var (board, senteCaptured, goteCaptured, currentPlayer) = ReconstructBoard(moveHistory);

        // MoveTree に棋譜を追加
        foreach (var move in moveHistory) {
            moveTree.AddMove(move);
        }

        this.State = new GameState(
            board,
            currentPlayer,
            status,
            senteCaptured,
            goteCaptured,
            [.. moveHistory],
            localPlayer,
            null
        ) { MoveTree = moveTree };

        await this.NotifyStateChangedAsync();
    }

    public async Task GoBackAsync()
    {
        var currentIndex = this.State.DisplayMoveIndex;
        if (currentIndex > 0) {
            this.State = this.State with { ViewingMoveIndex = currentIndex - 1 };
            await this.NotifyStateChangedAsync();
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
            await this.NotifyStateChangedAsync();
        }
    }

    public async Task GoForwardBranchAsync(int branchIndex)
    {
        this.SyncMoveTreeToViewingPosition();

        if (this.State.MoveTree.GoForwardBranch(branchIndex)) {
            var currentNode = this.State.MoveTree.CurrentNode;
            if (currentNode is not null) {
                var branchMoves = currentNode.GetMoves();
                this.State = this.State with {
                    MoveHistory = branchMoves,
                    ViewingMoveIndex = branchMoves.Count == this.State.MoveHistory.Count ? null : branchMoves.Count
                };

                var (board, senteCaptured, goteCaptured, currentPlayer) = ReconstructBoard(branchMoves);
                this.State = this.State with {
                    Board = board,
                    SenteCaptured = senteCaptured,
                    GoteCaptured = goteCaptured,
                    CurrentPlayer = currentPlayer
                };

                await this.NotifyStateChangedAsync();
            }
        }
    }

    private void SyncMoveTreeToViewingPosition()
    {
        var viewingIndex = this.State.ViewingMoveIndex ?? this.State.MoveHistory.Count;
        this.SyncMoveTreeToPosition(viewingIndex);
    }

    /// <summary>MoveTreeのCurrentNodeをMoveHistoryの現在位置に同期する</summary>
    private void SyncMoveTreeToCurrentPosition()
    {
        this.SyncMoveTreeToPosition(this.State.MoveHistory.Count);
    }

    private void SyncMoveTreeToPosition(int targetIndex)
    {
        // MoveTreeの現在位置を確認
        var currentDepth = this.State.MoveTree.CurrentDepth;

        // 既に正しい位置にあればスキップ
        if (currentDepth == targetIndex) {
            // 念のため、手が一致するか確認
            var currentMoves = this.State.MoveTree.CurrentLine;
            var historySlice = this.State.MoveHistory.Take(targetIndex).ToList();
            if (currentMoves.Count == historySlice.Count &&
                currentMoves.Select((m, i) => m == historySlice[i]).All(x => x)) {
                return;
            }
        }

        // 位置がずれているので再同期
        this.State.MoveTree.GoToStart();
        for (var i = 0; i < targetIndex && i < this.State.MoveHistory.Count; i++) {
            // 既存の手を辿る（AddMoveは同じ手があればそれを返す）
            this.State.MoveTree.AddMove(this.State.MoveHistory[i]);
        }
    }

    public async Task GoToLatestAsync()
    {
        if (this.State.IsReviewing) {
            this.State = this.State with { ViewingMoveIndex = null };
            await this.NotifyStateChangedAsync();
        }
    }

    public async Task ResumeFromBranchAsync()
    {
        if (this.State.IsReviewing && this.State.Status == GameStatus.Playing) {
            await this.BranchFromCurrentPositionAsync();
            await this.NotifyStateChangedAsync();
        }
    }

    public async Task SetViewingMoveIndexAsync(int moveIndex)
    {
        var newIndex = moveIndex >= this.State.MoveHistory.Count ? null : (int?)moveIndex;
        if (this.State.ViewingMoveIndex != newIndex) {
            this.State = this.State with { ViewingMoveIndex = newIndex };
            await this.NotifyStateChangedAsync();
        }
    }

    public async Task GoToNodeAsync(MoveNode? node)
    {
        // MoveTreeの位置を更新
        this.State.MoveTree.GoTo(node);

        if (node is null) {
            // 開始位置に移動
            this.State = this.State with { ViewingMoveIndex = 0 };
        } else {
            // ノードのパスを取得
            var nodeMoves = node.GetMoves();

            // 現在のMoveHistoryと同じパスかチェック
            var isSamePath = nodeMoves.Count <= this.State.MoveHistory.Count &&
                             nodeMoves.Select((m, i) => (m, i)).All(x => x.m == this.State.MoveHistory[x.i]);

            if (isSamePath) {
                // 同じパス上なら閲覧モードで移動
                this.State = this.State with { ViewingMoveIndex = node.Depth };
            } else {
                // 別の分岐なら、そのパスに切り替え（閲覧モードで）
                this.State = this.State with {
                    MoveHistory = nodeMoves,
                    ViewingMoveIndex = node.Depth
                };
            }
        }
        await this.NotifyStateChangedAsync();
    }

    /// <summary>前のブランチに移動（そのブランチの終端を表示）</summary>
    public async Task GoToPreviousBranchAsync()
    {
        var allEndNodes = this.State.MoveTree.GetAllBranchEndNodes();
        if (allEndNodes.Count <= 1) {
            return;
        }

        var currentIndex = this.GetCurrentBranchIndex();
        var newIndex = currentIndex > 0 ? currentIndex - 1 : allEndNodes.Count - 1;
        await this.GoToBranchEndAsync(newIndex);
    }

    /// <summary>次のブランチに移動（そのブランチの終端を表示）</summary>
    public async Task GoToNextBranchAsync()
    {
        var allEndNodes = this.State.MoveTree.GetAllBranchEndNodes();
        if (allEndNodes.Count <= 1) {
            return;
        }

        var currentIndex = this.GetCurrentBranchIndex();
        var newIndex = (currentIndex + 1) % allEndNodes.Count;
        await this.GoToBranchEndAsync(newIndex);
    }

    /// <summary>指定したブランチの終端に移動</summary>
    private async Task GoToBranchEndAsync(int branchIndex)
    {
        var allEndNodes = this.State.MoveTree.GetAllBranchEndNodes();
        if (branchIndex < 0 || branchIndex >= allEndNodes.Count) {
            return;
        }

        var endNode = allEndNodes[branchIndex];
        await this.GoToNodeAsync(endNode);
    }

    /// <summary>現在表示中のブランチインデックスを取得</summary>
    public int GetCurrentBranchIndex()
    {
        // 閲覧位置またはMoveHistoryの最後を基準にする
        this.SyncMoveTreeToViewingPosition();
        return this.State.MoveTree.GetBranchIndexForNode(this.State.MoveTree.CurrentNode);
    }

    /// <summary>総ブランチ数を取得（終端ノードの数）</summary>
    public int GetTotalBranchCount() => this.State.MoveTree.GetAllBranchEndNodes().Count;

    public (Board board, CapturedPieces senteCaptured, CapturedPieces goteCaptured, Player currentPlayer) GetBoardAtMove(int moveIndex) =>
        ReconstructBoard(this.State.MoveHistory.Take(moveIndex));

    private static (Board board, CapturedPieces senteCaptured, CapturedPieces goteCaptured, Player currentPlayer) ReconstructBoard(IEnumerable<Move> moves)
    {
        var board = new Board();
        var senteCaptured = CapturedPieces.Empty;
        var goteCaptured = CapturedPieces.Empty;
        var currentPlayer = Player.Sente;

        foreach (var move in moves) {
            (board, senteCaptured, goteCaptured) = ApplyMove(board, move, currentPlayer, senteCaptured, goteCaptured);
            currentPlayer = currentPlayer.GetOpponent();
        }

        return (board, senteCaptured, goteCaptured, currentPlayer);
    }

    private static (Board board, CapturedPieces senteCaptured, CapturedPieces goteCaptured) ApplyMove(
        Board board, Move move, Player player, CapturedPieces senteCaptured, CapturedPieces goteCaptured)
    {
        var captured = player == Player.Sente ? senteCaptured : goteCaptured;

        if (move.IsDrop) {
            var newCaptured = captured.TryRemove(move.PieceType) ?? captured;
            board = board.SetPiece(move.To, new Piece(move.PieceType, player));

            return player == Player.Sente
                ? (board, newCaptured, goteCaptured)
                : (board, senteCaptured, newCaptured);
        }

        if (move.From is { } from) {
            var piece = board[from];
            if (piece is not null) {
                if (move.CapturedPiece is { } capturedType) {
                    captured = captured.Add(capturedType);
                }

                var newPiece = move.IsPromotion && piece.Type.CanPromote()
                    ? piece with { Type = piece.Type.GetPromotedType() }
                    : piece;

                board = board.MovePiece(from, move.To, newPiece);
            }
        }

        return player == Player.Sente
            ? (board, captured, goteCaptured)
            : (board, senteCaptured, captured);
    }
}
