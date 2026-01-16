using Microsoft.AspNetCore.Components;

using ShogiGame.Models;

namespace ShogiGame.Components;

public partial class CapturedPiecesPanel
{
    [Parameter] public CapturedPieces Pieces { get; set; } = CapturedPieces.Empty;
    [Parameter] public Player Player { get; set; }
    [Parameter] public bool IsOpponent { get; set; }
    [Parameter] public EventCallback<PieceType> OnPieceSelected { get; set; }
    [Parameter] public PieceType? SelectedPiece { get; set; }

    private static readonly PieceType[] PieceOrder =
    [
        PieceType.Rook, PieceType.Bishop, PieceType.Gold, PieceType.Silver,
        PieceType.Knight, PieceType.Lance, PieceType.Pawn
    ];

    private async Task OnClick(PieceType pieceType)
    {
        if (!this.IsOpponent) {
            await this.OnPieceSelected.InvokeAsync(pieceType);
        }
    }
}