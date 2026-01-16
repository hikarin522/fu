using Microsoft.AspNetCore.Components;

using ShogiGame.Models;
using ShogiGame.Services;

namespace ShogiGame.Components;

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

    private async Task OnCellClickAsync(Position pos)
    {
        if (this.State.Status != GameStatus.Playing || !this.State.IsMyTurn) {
            return;
        }

        // 持ち駒を選択中の場合
        if (this.IsSelectingDropTarget && this.SelectedDropPiece is { } dropPiece) {
            if (this.DropLegalMoves.Contains(pos)) {
                var move = Move.CreateDrop(pos, dropPiece, this.State.CurrentPlayer);
                await this.ExecuteMoveAsync(move);
            }
            this.ClearSelection();
            return;
        }

        // 既に駒を選択済みで、合法手の位置をクリックした場合
        if (this.SelectedPosition is { } from && this.LegalMoves.Contains(pos)) {
            var piece = this.State.Board[from];

            if (piece is not null && this.GameService.CanPromote(from, pos)) {
                if (this.GameService.MustPromote(from, pos)) {
                    // 強制成り
                    var move = Move.CreateMove(from, pos, piece.Type, isPromotion: true)
                        .WithPlayer(this.State.CurrentPlayer);
                    await this.ExecuteMoveAsync(move);
                    this.ClearSelection();
                }
                else {
                    // 成り選択ダイアログ表示
                    this.PendingMoveFrom = from;
                    this.PendingMoveTo = pos;
                    this.ShowPromotionDialog = true;
                }
            }
            else {
                var move = Move.CreateMove(from, pos, piece!.Type)
                    .WithPlayer(this.State.CurrentPlayer);
                await this.ExecuteMoveAsync(move);
                this.ClearSelection();
            }
            return;
        }

        // 自分の駒をクリックした場合
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
        if (this.State.Status != GameStatus.Playing || !this.State.IsMyTurn) {
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
        if (this.State.MoveHistory is not [.., var lastMove]) {
            return false;
        }
        return lastMove.To == pos || (lastMove.From is { } from && from == pos);
    }
}