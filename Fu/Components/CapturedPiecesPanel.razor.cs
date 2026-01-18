using Microsoft.AspNetCore.Components;

using Fu.Core.Abstractions;
using Fu.Core.Models;

namespace Fu.Components;

public partial class CapturedPiecesPanel
{
    [Parameter] public CapturedPieces Pieces { get; set; } = CapturedPieces.Empty;
    [Parameter] public Turn Player { get; set; }
    [Parameter] public bool IsOpponent { get; set; }
    [Parameter] public EventCallback<PieceType> OnPieceSelected { get; set; }
    [Parameter] public PieceType? SelectedPiece { get; set; }

    private static PieceType[] PieceOrder => PieceTypeExtensions.CapturedPieceOrder;

    private async Task OnClick(PieceType pieceType)
    {
        if (!this.IsOpponent) {
            await this.OnPieceSelected.InvokeAsync(pieceType);
        }
    }
}
