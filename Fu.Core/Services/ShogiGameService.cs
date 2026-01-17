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
    public event Func<ImmutableList<Move>, ValueTask>? OnReviewStartedAsync;
    public event Func<Move, ValueTask>? OnReviewMoveAsync;

    private ValueTask NotifyStateChangedAsync() =>
        OnStateChangedAsync is { } handler ? handler() : ValueTask.CompletedTask;

    private ValueTask NotifyBranchResumedAsync(ImmutableList<Move> moveHistory) =>
        OnBranchResumedAsync is { } handler ? handler(moveHistory) : ValueTask.CompletedTask;

    private ValueTask NotifyReviewStartedAsync(ImmutableList<Move> moveHistory) =>
        OnReviewStartedAsync is { } handler ? handler(moveHistory) : ValueTask.CompletedTask;

    private ValueTask NotifyReviewMoveAsync(Move move) =>
        OnReviewMoveAsync is { } handler ? handler(move) : ValueTask.CompletedTask;

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
        // 検討モードの場合は専用メソッドにルーティング
        if (this.State.Status == GameStatus.Reviewing) {
            return await this.TryMakeReviewMoveAsync(move);
        }

        if (this.State.Status != GameStatus.Playing) {
            return false;
        }

        // 閲覧モード中は手を指せない（「ここから再開」を押す必要がある）
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
        // 別ブランチを見ている場合はそのブランチの棋譜を使う
        var displayHistory = this.State.DisplayBranchHistory;
        // ViewingMoveIndexがnullの場合は表示中ブランチの最後（別ブランチの最新局面）
        var viewingIndex = this.State.ViewingMoveIndex ?? displayHistory.Count;
        this.State.MoveTree.GoToStart();
        for (var i = 0; i < viewingIndex; i++) {
            // AddMoveは既存の同じ手があればそれを返すので、正しいパスを辿れる
            this.State.MoveTree.AddMove(displayHistory[i]);
        }

        var (board, senteCaptured, goteCaptured, currentPlayer) = this.GetBoardAtMove(viewingIndex);

        var newHistory = displayHistory.Take(viewingIndex).ToImmutableList();

        this.State = this.State with {
            Board = board,
            SenteCaptured = senteCaptured,
            GoteCaptured = goteCaptured,
            CurrentPlayer = currentPlayer,
            MoveHistory = newHistory,
            ViewingMoveIndex = null,
            ViewingBranchHistory = null
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
        var moveTree = this.State.MoveTree; // 既存のMoveTreeを保持

        // 棋譜を再生して盤面を復元
        var (board, senteCaptured, goteCaptured, currentPlayer) = ReconstructBoard(moveHistory);

        // MoveTree の現在位置を棋譜に同期
        moveTree.GoToStart();
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
            null,
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
            // 対局中のブランチ（MoveHistory）の最新局面に移動
            // MoveTreeの現在位置も対局中のブランチの最後に戻す
            this.GoToMoveHistoryEnd();

            this.State = this.State with {
                ViewingMoveIndex = null,
                ViewingBranchHistory = null
            };
            await this.NotifyStateChangedAsync();
        }
    }

    /// <summary>MoveTreeの現在位置をMoveHistoryの最後に移動</summary>
    private void GoToMoveHistoryEnd()
    {
        var moveTree = this.State.MoveTree;
        moveTree.GoToStart();
        foreach (var move in this.State.MoveHistory) {
            // MoveTree上で対応するノードを探して進む
            var nextMoves = moveTree.NextMoves;
            var matchingNode = nextMoves.FirstOrDefault(n => MoveNode.IsSameMove(n.Move, move));
            if (matchingNode is not null) {
                moveTree.GoTo(matchingNode);
            } else {
                break;
            }
        }
    }

    public async Task ResumeFromBranchAsync()
    {
        if (this.State.IsReviewing && this.State.Status == GameStatus.Playing) {
            await this.BranchFromCurrentPositionAsync();
            await this.NotifyStateChangedAsync();
        }
    }

    /// <summary>現在の位置から再戦（新しいゲームとして開始、MoveTreeをリセット）</summary>
    public async Task RematchFromCurrentPositionAsync()
    {
        var displayHistory = this.State.DisplayBranchHistory;
        var viewingIndex = this.State.ViewingMoveIndex ?? displayHistory.Count;
        var (board, senteCaptured, goteCaptured, currentPlayer) = this.GetBoardAtMove(viewingIndex);
        var newHistory = displayHistory.Take(viewingIndex).ToImmutableList();

        // MoveTreeを新規作成して棋譜を追加
        var newMoveTree = new MoveTree();
        foreach (var move in newHistory) {
            newMoveTree.AddMove(move);
        }

        this.State = this.State with {
            Board = board,
            SenteCaptured = senteCaptured,
            GoteCaptured = goteCaptured,
            CurrentPlayer = currentPlayer,
            MoveHistory = newHistory,
            Status = GameStatus.Playing,
            ViewingMoveIndex = null,
            ViewingBranchHistory = null,
            MoveTree = newMoveTree
        };

        await this.NotifyStateChangedAsync();
    }

    /// <summary>リモートからの再戦を適用</summary>
    public async Task ApplyRematchAsync(IReadOnlyList<Move> moveHistory)
    {
        var localPlayer = this.State.LocalPlayer;
        var (board, senteCaptured, goteCaptured, currentPlayer) = ReconstructBoard(moveHistory);

        // MoveTreeを新規作成して棋譜を追加
        var newMoveTree = new MoveTree();
        foreach (var move in moveHistory) {
            newMoveTree.AddMove(move);
        }

        this.State = new GameState(
            board,
            currentPlayer,
            GameStatus.Playing,
            senteCaptured,
            goteCaptured,
            [.. moveHistory],
            localPlayer,
            null,
            null
        ) { MoveTree = newMoveTree };

        await this.NotifyStateChangedAsync();
    }

    /// <summary>現在の位置から検討モードを開始</summary>
    public async Task StartReviewFromCurrentPositionAsync()
    {
        var displayHistory = this.State.DisplayBranchHistory;
        var viewingIndex = this.State.ViewingMoveIndex ?? displayHistory.Count;
        var (board, senteCaptured, goteCaptured, currentPlayer) = this.GetBoardAtMove(viewingIndex);
        var newHistory = displayHistory.Take(viewingIndex).ToImmutableList();

        // 既存のMoveTreeを保持して現在位置に同期
        var moveTree = this.State.MoveTree;
        moveTree.GoToStart();
        foreach (var move in newHistory) {
            moveTree.AddMove(move);
        }

        this.State = this.State with {
            Board = board,
            SenteCaptured = senteCaptured,
            GoteCaptured = goteCaptured,
            CurrentPlayer = currentPlayer,
            MoveHistory = newHistory,
            Status = GameStatus.Reviewing,
            ViewingMoveIndex = null,
            ViewingBranchHistory = null
        };

        await this.NotifyReviewStartedAsync(newHistory);
        await this.NotifyStateChangedAsync();
    }

    /// <summary>リモートからの検討モード開始を適用</summary>
    public async Task ApplyReviewStartAsync(IReadOnlyList<Move> moveHistory)
    {
        var localPlayer = this.State.LocalPlayer;
        var moveTree = this.State.MoveTree;

        var (board, senteCaptured, goteCaptured, currentPlayer) = ReconstructBoard(moveHistory);

        moveTree.GoToStart();
        foreach (var move in moveHistory) {
            moveTree.AddMove(move);
        }

        this.State = new GameState(
            board,
            currentPlayer,
            GameStatus.Reviewing,
            senteCaptured,
            goteCaptured,
            [.. moveHistory],
            localPlayer,
            null,
            null
        ) { MoveTree = moveTree };

        await this.NotifyStateChangedAsync();
    }

    /// <summary>検討モードで手を指す（手番関係なく自由に動かせる）</summary>
    private async Task<bool> TryMakeReviewMoveAsync(Move move)
    {
        // 過去の局面を見ている場合は、その位置から分岐を作る
        if (this.State.IsReviewing) {
            await this.BranchFromCurrentPositionForReviewAsync();
        }

        // MoveTreeのCurrentNodeをMoveHistoryと同期する
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

        // 検討モードでは手番関係なく動かせる
        var player = piece.Owner;
        var legalMoves = this.GetLegalMovesForPlayer(from, player);
        if (!legalMoves.Contains(to)) {
            return false;
        }

        var captured = this.State.Board[to];
        var newCaptured = this.State.GetCapturedPieces(player);
        var moveToRecord = move.WithPlayer(player);

        if (captured is not null) {
            newCaptured = newCaptured.Add(captured.Type);
            moveToRecord = moveToRecord.WithCapturedPiece(captured.Type);
        }

        var newPiece = move.IsPromotion && piece.Type.CanPromote()
            ? piece with { Type = piece.Type.GetPromotedType() }
            : piece;

        this.State.MoveTree.AddMove(moveToRecord);

        var newState = this.State with {
            Board = this.State.Board.MovePiece(from, to, newPiece),
            MoveHistory = this.State.MoveHistory.Add(moveToRecord),
            CurrentPlayer = player.GetOpponent(),
            ViewingMoveIndex = null
        };
        newState = newState.WithCapturedPieces(player, newCaptured);

        this.State = newState;

        await this.NotifyReviewMoveAsync(moveToRecord);
        await this.NotifyStateChangedAsync();
        return true;
    }

    /// <summary>検討モード用の駒打ち</summary>
    private async Task<bool> TryDropPieceForReviewAsync(Move move)
    {
        // 検討モードでは現在の手番のプレイヤーの持ち駒を使う
        var player = this.State.CurrentPlayer;
        var captured = this.State.GetCapturedPieces(player);
        if (captured.GetCount(move.PieceType) <= 0) {
            return false;
        }

        var legalPositions = this.GetLegalDropPositionsForPlayer(move.PieceType, player);
        if (!legalPositions.Contains(move.To)) {
            return false;
        }

        var newCaptured = captured.TryRemove(move.PieceType);
        if (newCaptured is null) {
            return false;
        }

        var moveToRecord = move.WithPlayer(player);
        this.State.MoveTree.AddMove(moveToRecord);

        var newState = this.State with {
            Board = this.State.Board.SetPiece(move.To, new Piece(move.PieceType, player)),
            MoveHistory = this.State.MoveHistory.Add(moveToRecord),
            CurrentPlayer = player.GetOpponent(),
            ViewingMoveIndex = null
        };
        newState = newState.WithCapturedPieces(player, newCaptured);

        this.State = newState;

        await this.NotifyReviewMoveAsync(moveToRecord);
        await this.NotifyStateChangedAsync();
        return true;
    }

    /// <summary>検討モード用：現在位置から分岐を作成</summary>
    private async Task BranchFromCurrentPositionForReviewAsync()
    {
        var displayHistory = this.State.DisplayBranchHistory;
        var viewingIndex = this.State.ViewingMoveIndex ?? displayHistory.Count;
        this.State.MoveTree.GoToStart();
        for (var i = 0; i < viewingIndex; i++) {
            this.State.MoveTree.AddMove(displayHistory[i]);
        }

        var (board, senteCaptured, goteCaptured, currentPlayer) = this.GetBoardAtMove(viewingIndex);
        var newHistory = displayHistory.Take(viewingIndex).ToImmutableList();

        this.State = this.State with {
            Board = board,
            SenteCaptured = senteCaptured,
            GoteCaptured = goteCaptured,
            CurrentPlayer = currentPlayer,
            MoveHistory = newHistory,
            ViewingMoveIndex = null,
            ViewingBranchHistory = null
        };

        await Task.CompletedTask;
    }

    /// <summary>リモートからの検討モードの手を適用</summary>
    public async Task ApplyReviewMoveAsync(Move move)
    {
        // 検討モードでなければ無視
        if (this.State.Status != GameStatus.Reviewing) {
            return;
        }

        // MoveTreeのCurrentNodeを同期
        this.SyncMoveTreeToCurrentPosition();

        var player = move.Player;
        var captured = this.State.Board[move.To];
        var newCaptured = this.State.GetCapturedPieces(player);

        if (move.IsDrop) {
            var droppedCaptured = newCaptured.TryRemove(move.PieceType);
            if (droppedCaptured is null) {
                return;
            }

            this.State.MoveTree.AddMove(move);

            var newState = this.State with {
                Board = this.State.Board.SetPiece(move.To, new Piece(move.PieceType, player)),
                MoveHistory = this.State.MoveHistory.Add(move),
                CurrentPlayer = player.GetOpponent(),
                ViewingMoveIndex = null
            };
            newState = newState.WithCapturedPieces(player, droppedCaptured);

            this.State = newState;
        } else if (move.From is { } from) {
            var piece = this.State.Board[from];
            if (piece is null) {
                return;
            }

            if (captured is not null) {
                newCaptured = newCaptured.Add(captured.Type);
            }

            var newPiece = move.IsPromotion && piece.Type.CanPromote()
                ? piece with { Type = piece.Type.GetPromotedType() }
                : piece;

            this.State.MoveTree.AddMove(move);

            var newState = this.State with {
                Board = this.State.Board.MovePiece(from, move.To, newPiece),
                MoveHistory = this.State.MoveHistory.Add(move),
                CurrentPlayer = player.GetOpponent(),
                ViewingMoveIndex = null
            };
            newState = newState.WithCapturedPieces(player, newCaptured);

            this.State = newState;
        }

        await this.NotifyStateChangedAsync();
    }

    /// <summary>指定したプレイヤーの合法手を取得</summary>
    public List<Position> GetLegalMovesForPlayer(Position from, Player player)
    {
        var piece = this.State.Board[from];
        if (piece is null || piece.Owner != player) {
            return [];
        }

        var moves = GetPossibleMovesOnBoard(this.State.Board, from, piece);
        return [.. moves.Where(to => !WouldBeInCheck(this.State.Board, from, to, player))];
    }

    /// <summary>指定したプレイヤーの合法打ち位置を取得</summary>
    public List<Position> GetLegalDropPositionsForPlayer(PieceType pieceType, Player player)
    {
        return [.. Board.AllPositions.Where(pos => this.CanDropAtForPlayer(pos, pieceType, player))];
    }

    private bool CanDropAtForPlayer(Position pos, PieceType pieceType, Player player)
    {
        if (this.State.Board[pos] is not null) {
            return false;
        }

        if (pieceType == PieceType.Pawn && this.State.Board.HasPawnInColumn(pos.Col, player)) {
            return false;
        }

        if (!CanExistAtRow(pieceType, pos.Row, player)) {
            return false;
        }

        // 打ち歩詰めチェック（簡略化）
        var testBoard = this.State.Board.SetPiece(pos, new Piece(pieceType, player));
        return !IsInCheck(testBoard, player);
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
            this.State = this.State with {
                ViewingMoveIndex = 0,
                ViewingBranchHistory = null
            };
        } else {
            // ノードのパスを取得
            var nodeMoves = node.GetMoves();

            // 現在のMoveHistory（対局中のブランチ）と完全に同じか（同じパスかつ同じ長さ）
            var isExactSamePath = nodeMoves.Count == this.State.MoveHistory.Count &&
                                  nodeMoves.Select((m, i) => MoveNode.IsSameMove(m, this.State.MoveHistory[i])).All(x => x);

            if (isExactSamePath) {
                // 完全に同じパスなら閲覧モードを解除
                this.State = this.State with {
                    ViewingMoveIndex = null,
                    ViewingBranchHistory = null
                };
            } else {
                // 現在のMoveHistory（対局中のブランチ）の一部かチェック
                var isSamePath = nodeMoves.Count <= this.State.MoveHistory.Count &&
                                 nodeMoves.Select((m, i) => MoveNode.IsSameMove(m, this.State.MoveHistory[i])).All(x => x);

                if (isSamePath) {
                    // 同じパス上なら閲覧モードで移動（別ブランチではない）
                    this.State = this.State with {
                        ViewingMoveIndex = node.Depth,
                        ViewingBranchHistory = null
                    };
                } else {
                    // 別の分岐なら、そのパスを閲覧用に設定（終端を表示）
                    this.State = this.State with {
                        ViewingMoveIndex = null,
                        ViewingBranchHistory = nodeMoves
                    };
                }
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
        ReconstructBoard(this.State.DisplayBranchHistory.Take(moveIndex));

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
