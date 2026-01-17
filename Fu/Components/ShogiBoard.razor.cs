using Microsoft.AspNetCore.Components;

using Fu.Core.Models;
using Fu.Core.Services;
using Fu.Services;

namespace Fu.Components;

public partial class ShogiBoard
{
    [Parameter] public GameState State { get; set; } = GameState.Initial;
    [Parameter] public ShogiGameService GameService { get; set; } = null!;
    [Parameter] public EventCallback<Move> OnMoveMade { get; set; }
    [Parameter] public bool IsFlipped { get; set; }

    /// <summary>候補手リスト（観戦者向け矢印表示用）</summary>
    [Parameter] public IReadOnlyList<CandidateMove>? CandidateMoves { get; set; }

    private Position? SelectedPosition { get; set; }
    private List<Position> LegalMoves { get; set; } = [];
    private bool ShowPromotionDialog { get; set; }
    private Position? PendingMoveFrom { get; set; }
    private Position? PendingMoveTo { get; set; }

    private bool IsSelectingDropTarget { get; set; }
    private PieceType? SelectedDropPiece { get; set; }
    private List<Position> DropLegalMoves { get; set; } = [];

    // 閲覧モード用の表示状態（キャッシュ）
    private (Board board, CapturedPieces senteCaptured, CapturedPieces goteCaptured, Player currentPlayer) DisplayState {
        get {
            if (this.State.IsReviewing) {
                return this.GameService.GetBoardAtMove(this.State.DisplayMoveIndex);
            }
            return (this.State.Board, this.State.SenteCaptured, this.State.GoteCaptured, this.State.CurrentPlayer);
        }
    }

    private Board DisplayBoard => this.DisplayState.board;
    private CapturedPieces DisplaySenteCaptured => this.DisplayState.senteCaptured;
    private CapturedPieces DisplayGoteCaptured => this.DisplayState.goteCaptured;
    private Player DisplayCurrentPlayer => this.DisplayState.currentPlayer;

    // 閲覧モードでは表示中の盤面の手番で判定（分岐から再開できる）
    private bool CanInteract =>
        this.State.Status == GameStatus.Playing && this.DisplayCurrentPlayer == this.State.LocalPlayer;

    private async Task OnCellClickAsync(Position pos)
    {
        if (!this.CanInteract) {
            return;
        }

        if (this.IsSelectingDropTarget && this.SelectedDropPiece is { } dropPiece) {
            await this.HandleDropAsync(pos, dropPiece);
        }
        else if (this.SelectedPosition is { } from) {
            // 閲覧モードでは合法手リストが空なので、移動先候補かどうかを直接チェック
            if (this.LegalMoves.Contains(pos) || (this.State.IsReviewing && this.IsPotentialMoveTarget(from, pos))) {
                await this.HandleMoveAsync(from, pos);
            }
            else {
                this.HandlePieceSelection(pos);
            }
        }
        else {
            this.HandlePieceSelection(pos);
        }
    }

    private bool IsPotentialMoveTarget(Position from, Position to)
    {
        if (from == to) {
            return false;
        }
        var targetPiece = this.DisplayBoard[to];
        // 自分の駒がある場所には動けない
        return targetPiece is null || targetPiece.Owner != this.DisplayCurrentPlayer;
    }

    private async Task HandleDropAsync(Position pos, PieceType dropPiece)
    {
        // 閲覧モードでは合法手リストが空なので、配置可能かどうかを直接チェック
        if (this.DropLegalMoves.Contains(pos) || (this.State.IsReviewing && this.DisplayBoard[pos] is null)) {
            var move = Move.CreateDrop(pos, dropPiece, this.DisplayCurrentPlayer);
            await this.ExecuteMoveAsync(move);
        }
        this.ClearSelection();
    }

    private async Task HandleMoveAsync(Position from, Position to)
    {
        var piece = this.DisplayBoard[from];
        if (piece is null) {
            this.ClearSelection();
            return;
        }

        // 閲覧モードでは成り判定も表示中の盤面で行う
        var canPromote = CanPromoteOnDisplayBoard(from, to, piece);
        var mustPromote = MustPromoteOnDisplayBoard(to, piece);

        if (canPromote) {
            if (mustPromote) {
                await this.ExecuteMoveWithPromotionAsync(from, to, piece.Type, promote: true);
            }
            else {
                this.ShowPromotionDialogFor(from, to);
            }
        }
        else {
            await this.ExecuteMoveWithPromotionAsync(from, to, piece.Type, promote: false);
        }
    }

    private static bool CanPromoteOnDisplayBoard(Position from, Position to, Piece piece)
    {
        if (!piece.Type.CanPromote() || piece.Type.IsPromoted()) return false;
        const int sentePromotionBoundary = 2, gotePromotionBoundary = 6;
        return piece.Owner == Player.Sente
            ? from.Row <= sentePromotionBoundary || to.Row <= sentePromotionBoundary
            : from.Row >= gotePromotionBoundary || to.Row >= gotePromotionBoundary;
    }

    private static bool MustPromoteOnDisplayBoard(Position to, Piece piece)
    {
        var effectiveRow = piece.Owner == Player.Sente ? to.Row : Board.Size - 1 - to.Row;
        return piece.Type switch {
            PieceType.Pawn or PieceType.Lance => effectiveRow == 0,
            PieceType.Knight => effectiveRow <= 1,
            _ => false
        };
    }

    private async Task ExecuteMoveWithPromotionAsync(Position from, Position to, PieceType pieceType, bool promote)
    {
        var move = Move.CreateMove(from, to, pieceType, promote).WithPlayer(this.DisplayCurrentPlayer);
        await this.ExecuteMoveAsync(move);
        this.ClearSelection();
    }

    private void ShowPromotionDialogFor(Position from, Position to)
    {
        this.PendingMoveFrom = from;
        this.PendingMoveTo = to;
        this.ShowPromotionDialog = true;
    }

    private void HandlePieceSelection(Position pos)
    {
        var clickedPiece = this.DisplayBoard[pos];
        if (clickedPiece is { Owner: var owner } && owner == this.DisplayCurrentPlayer) {
            this.SelectedPosition = pos;
            // 閲覧モードでは合法手ハイライト無し（動かした時にBranchFromCurrentPositionAsyncで復元してからチェック）
            this.LegalMoves = this.State.IsReviewing ? [] : this.GameService.GetLegalMoves(pos);
            this.IsSelectingDropTarget = false;
            this.SelectedDropPiece = null;
        }
        else {
            this.ClearSelection();
        }
    }

    private void OnCapturedPieceClick(PieceType pieceType)
    {
        if (!this.CanInteract) {
            return;
        }

        // 閲覧モードでは表示中の持ち駒を使用
        var captured = this.DisplayCurrentPlayer == Player.Sente
            ? this.DisplaySenteCaptured
            : this.DisplayGoteCaptured;

        if (captured.GetCount(pieceType) <= 0) {
            return;
        }

        this.IsSelectingDropTarget = true;
        this.SelectedDropPiece = pieceType;
        // 閲覧モードでは合法手ハイライト無し
        this.DropLegalMoves = this.State.IsReviewing ? [] : this.GameService.GetLegalDropPositions(pieceType);
        this.LegalMoves = this.DropLegalMoves;
        this.SelectedPosition = null;
    }

    private async Task CompleteMoveAsync(bool promote)
    {
        this.ShowPromotionDialog = false;

        if (this.PendingMoveFrom is { } moveFrom && this.PendingMoveTo is { } moveTo) {
            var piece = this.DisplayBoard[moveFrom];
            var move = Move.CreateMove(moveFrom, moveTo, piece!.Type, promote)
                .WithPlayer(this.DisplayCurrentPlayer);
            await this.ExecuteMoveAsync(move);
        }

        this.ClearSelection();
    }

    private async Task ExecuteMoveAsync(Move move)
    {
        if (await this.GameService.TryMakeMoveAsync(move)) {
            await this.OnMoveMade.InvokeAsync(move);
        }
    }

    private void ClearSelection()
    {
        this.SelectedPosition = null;
        this.LegalMoves.Clear();
        this.PendingMoveFrom = null;
        this.PendingMoveTo = null;
        this.IsSelectingDropTarget = false;
        this.SelectedDropPiece = null;
        this.DropLegalMoves.Clear();
    }

    private void OnBoardClick()
    {
        // クリックが盤面の外側だった場合は選択解除
        this.ClearSelection();
    }

    private bool IsLastMovePosition(Position pos)
    {
        var displayIndex = this.State.DisplayMoveIndex;
        if (displayIndex == 0 || displayIndex > this.State.MoveHistory.Count) {
            return false;
        }
        var lastMove = this.State.MoveHistory[displayIndex - 1];
        return lastMove.To == pos || (lastMove.From is { } from && from == pos);
    }

    /// <summary>候補手の矢印データを取得</summary>
    private IEnumerable<ArrowData> GetCandidateArrows()
    {
        if (this.CandidateMoves is not { Count: > 0 }) yield break;

        // 太さで順位を表現: 1位=10, 2位=6, 3位=3
        var strokeWidths = new[] { 10, 6, 3 };

        foreach (var candidate in this.CandidateMoves.Take(3)) {
            if (ShogiEngineService.ParseSfenMove(candidate.Move) is not { } move) continue;
            var (from, to) = move;

            var strokeWidth = strokeWidths[Math.Min(candidate.Rank - 1, strokeWidths.Length - 1)];
            var toDisplay = this.IsFlipped ? (8 - to.col, 8 - to.row) : (to.col, to.row);

            if (from is { } f) {
                var fromDisplay = this.IsFlipped ? (8 - f.col, 8 - f.row) : (f.col, f.row);
                yield return new ArrowData(fromDisplay.Item1, fromDisplay.Item2, toDisplay.Item1, toDisplay.Item2, strokeWidth, false);
            }
            else {
                yield return new ArrowData(toDisplay.Item1, toDisplay.Item2, toDisplay.Item1, toDisplay.Item2, strokeWidth, true);
            }
        }
    }

    private sealed record ArrowData(int FromCol, int FromRow, int ToCol, int ToRow, int StrokeWidth, bool IsDrop);
}
