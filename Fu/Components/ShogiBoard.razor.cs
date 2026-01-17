using Microsoft.AspNetCore.Components;

using Fu.Core.Models;
using Fu.Core.Services;

namespace Fu.Components;

public partial class ShogiBoard
{
    [Parameter] public GameState State { get; set; } = GameState.Initial;
    [Parameter] public ShogiGameService GameService { get; set; } = null!;
    [Parameter] public EventCallback<Move> OnMoveMade { get; set; }
    [Parameter] public bool IsFlipped { get; set; }

    private Position? SelectedPosition { get; set; }
    private List<Position> LegalMoves { get; set; } = [];
    private bool ShowPromotionDialog { get; set; }
    private Position? PendingMoveFrom { get; set; }
    private Position? PendingMoveTo { get; set; }

    private bool IsSelectingDropTarget { get; set; }
    private PieceType? SelectedDropPiece { get; set; }
    private List<Position> DropLegalMoves { get; set; } = [];

    // 閲覧モード用の表示状態（キャッシュ）
    private (Board board, CapturedPieces senteCaptured, CapturedPieces goteCaptured) DisplayState {
        get {
            if (this.State.IsReviewing) {
                var (board, senteCaptured, goteCaptured, _) = this.GameService.GetBoardAtMove(this.State.DisplayMoveIndex);
                return (board, senteCaptured, goteCaptured);
            }
            return (this.State.Board, this.State.SenteCaptured, this.State.GoteCaptured);
        }
    }

    private Board DisplayBoard => this.DisplayState.board;
    private CapturedPieces DisplaySenteCaptured => this.DisplayState.senteCaptured;
    private CapturedPieces DisplayGoteCaptured => this.DisplayState.goteCaptured;

    private bool CanInteract =>
        !this.State.IsReviewing && this.State.Status == GameStatus.Playing && this.State.IsMyTurn;

    private async Task OnCellClickAsync(Position pos)
    {
        if (!this.CanInteract) {
            return;
        }

        if (this.IsSelectingDropTarget && this.SelectedDropPiece is { } dropPiece) {
            await this.HandleDropAsync(pos, dropPiece);
        }
        else if (this.SelectedPosition is { } from && this.LegalMoves.Contains(pos)) {
            await this.HandleMoveAsync(from, pos);
        }
        else {
            this.HandlePieceSelection(pos);
        }
    }

    private async Task HandleDropAsync(Position pos, PieceType dropPiece)
    {
        if (this.DropLegalMoves.Contains(pos)) {
            var move = Move.CreateDrop(pos, dropPiece, this.State.CurrentPlayer);
            await this.ExecuteMoveAsync(move);
        }
        this.ClearSelection();
    }

    private async Task HandleMoveAsync(Position from, Position to)
    {
        var piece = this.State.Board[from];
        if (piece is null) {
            this.ClearSelection();
            return;
        }

        if (this.GameService.CanPromote(from, to)) {
            if (this.GameService.MustPromote(from, to)) {
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

    private async Task ExecuteMoveWithPromotionAsync(Position from, Position to, PieceType pieceType, bool promote)
    {
        var move = Move.CreateMove(from, to, pieceType, promote).WithPlayer(this.State.CurrentPlayer);
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
        var clickedPiece = this.State.Board[pos];
        if (clickedPiece is { Owner: var owner } && owner == this.State.CurrentPlayer) {
            this.SelectedPosition = pos;
            this.LegalMoves = this.GameService.GetLegalMoves(pos);
            this.IsSelectingDropTarget = false;
            this.SelectedDropPiece = null;
        }
        else {
            this.ClearSelection();
        }
    }

    private void OnCapturedPieceClick(PieceType pieceType)
    {
        // 閲覧モード中は操作無効
        if (this.State.IsReviewing || this.State.Status != GameStatus.Playing || !this.State.IsMyTurn) {
            return;
        }

        var captured = this.State.GetCapturedPieces(this.State.CurrentPlayer);
        if (captured.GetCount(pieceType) <= 0) {
            return;
        }

        this.IsSelectingDropTarget = true;
        this.SelectedDropPiece = pieceType;
        this.DropLegalMoves = this.GameService.GetLegalDropPositions(pieceType);
        this.LegalMoves = this.DropLegalMoves;
        this.SelectedPosition = null;
    }

    private async Task CompleteMoveAsync(bool promote)
    {
        this.ShowPromotionDialog = false;

        if (this.PendingMoveFrom is { } moveFrom && this.PendingMoveTo is { } moveTo) {
            var piece = this.State.Board[moveFrom];
            var move = Move.CreateMove(moveFrom, moveTo, piece!.Type, promote)
                .WithPlayer(this.State.CurrentPlayer);
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
}
