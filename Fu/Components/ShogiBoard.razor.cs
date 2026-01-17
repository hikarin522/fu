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

    /// <summary>候補手の手番（駒打ち矢印の始点判定用）</summary>
    [Parameter] public Player CandidateMovePlayer { get; set; } = Player.Sente;

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
        if (!piece.Type.CanPromote() || piece.Type.IsPromoted()) {
            return false;
        }
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

    /// <summary>候補手の矢印データを取得（通常移動のみ）</summary>
    private IEnumerable<ArrowData> GetMoveArrows()
    {
        if (this.CandidateMoves is not { Count: > 0 }) {
            yield break;
        }

        var strokeWidths = new[] { 6, 4, 2 };

        foreach (var candidate in this.CandidateMoves.Take(3)) {
            if (ShogiEngineService.ParseSfenMove(candidate.Move) is not { } move) {
                continue;
            }
            var (from, to, dropPiece) = move;

            if (from is { } f) {
                var strokeWidth = strokeWidths[Math.Min(candidate.Rank - 1, strokeWidths.Length - 1)];
                var toDisplay = this.IsFlipped ? (8 - to.col, 8 - to.row) : (to.col, to.row);
                var fromDisplay = this.IsFlipped ? (8 - f.col, 8 - f.row) : (f.col, f.row);
                yield return new ArrowData(fromDisplay.Item1, fromDisplay.Item2, toDisplay.Item1, toDisplay.Item2, strokeWidth, null);
            }
        }
    }

    /// <summary>駒打ちの矢印データを取得</summary>
    private IEnumerable<ArrowData> GetDropArrows()
    {
        if (this.CandidateMoves is not { Count: > 0 }) {
            yield break;
        }

        var strokeWidths = new[] { 6, 4, 2 };

        foreach (var candidate in this.CandidateMoves.Take(3)) {
            if (ShogiEngineService.ParseSfenMove(candidate.Move) is not { } move) {
                continue;
            }
            var (from, to, dropPiece) = move;

            if (dropPiece is { } piece) {
                var strokeWidth = strokeWidths[Math.Min(candidate.Rank - 1, strokeWidths.Length - 1)];
                var toDisplay = this.IsFlipped ? (8 - to.col, 8 - to.row) : (to.col, to.row);
                yield return new ArrowData(0, 0, toDisplay.Item1, toDisplay.Item2, strokeWidth, piece);
            }
        }
    }

    /// <summary>持ち駒パネル内の駒の表示インデックスを取得</summary>
    private static int GetCapturedPieceIndex(CapturedPieces pieces, char sfenPiece)
    {
        var pieceOrder = new[] { PieceType.Rook, PieceType.Bishop, PieceType.Gold, PieceType.Silver, PieceType.Knight, PieceType.Lance, PieceType.Pawn };
        var targetType = sfenPiece switch {
            'R' => PieceType.Rook,
            'B' => PieceType.Bishop,
            'G' => PieceType.Gold,
            'S' => PieceType.Silver,
            'N' => PieceType.Knight,
            'L' => PieceType.Lance,
            'P' => PieceType.Pawn,
            _ => (PieceType?)null
        };

        if (targetType is null) {
            return -1;
        }

        var index = 0;
        foreach (var pieceType in pieceOrder) {
            if (pieces.GetCount(pieceType) > 0) {
                if (pieceType == targetType) {
                    return index;
                }
                index++;
            }
        }
        return -1;
    }

    /// <summary>駒打ちの駒種類（SFEN形式の大文字: P, L, N, S, G, B, R）、駒打ちでない場合はnull</summary>
    private sealed record ArrowData(int FromCol, int FromRow, int ToCol, int ToRow, int StrokeWidth, char? DropPiece)
    {
        public bool IsDrop => this.DropPiece is not null;
    }

    /// <summary>矢印描画用の計算結果</summary>
    private readonly record struct ArrowGeometry(
        double X1, double Y1,
        double BaseX, double BaseY,
        double TipX, double TipY,
        double LeftX, double LeftY,
        double RightX, double RightY,
        int StrokeWidth);

    /// <summary>矢印のジオメトリを計算</summary>
    private static ArrowGeometry? CalculateArrowGeometry(double x1, double y1, double x2, double y2, int strokeWidth, bool shortenStart = false)
    {
        var dx = x2 - x1;
        var dy = y2 - y1;
        var length = Math.Sqrt(dx * dx + dy * dy);
        if (length <= 0) {
            return null;
        }

        var arrowHeadLength = strokeWidth * 2.5;
        var arrowHeadWidth = strokeWidth * 1.5;
        var unitX = dx / length;
        var unitY = dy / length;
        var perpX = -unitY;
        var perpY = unitX;

        // 始点を少し短くして駒と重ならないようにする（通常移動の場合）
        var adjX1 = x1;
        var adjY1 = y1;
        if (shortenStart) {
            var shortenRatio = 15 / length;
            adjX1 = x1 + dx * shortenRatio;
            adjY1 = y1 + dy * shortenRatio;
        }

        // 三角形の頂点はマス目の中心(x2, y2)、付け根は頂点から後ろ
        var baseX = x2 - unitX * arrowHeadLength;
        var baseY = y2 - unitY * arrowHeadLength;

        // 鏃の左右の点
        var leftX = baseX + perpX * arrowHeadWidth;
        var leftY = baseY + perpY * arrowHeadWidth;
        var rightX = baseX - perpX * arrowHeadWidth;
        var rightY = baseY - perpY * arrowHeadWidth;

        return new ArrowGeometry(adjX1, adjY1, baseX, baseY, x2, y2, leftX, leftY, rightX, rightY, strokeWidth);
    }
}
