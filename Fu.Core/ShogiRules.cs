using System.Collections.Frozen;

using Fu.Core.Abstractions;
using Fu.Core.Models;

namespace Fu.Core;

/// <summary>
/// 将棋のルール判定（純粋関数）
/// </summary>
public static class ShogiRules
{
    private const int FirstPromotionBoundary = 2;  // 先手の成れる段（0-2）
    private const int SecondPromotionBoundary = 6; // 後手の成れる段（6-8）

    /// <summary>指定位置から移動可能な位置を取得</summary>
    public static List<Position> GetLegalMoves(Board board, Position from, Turn turn)
    {
        var piece = board[from];
        if (piece is null || piece.Owner != turn) {
            return [];
        }

        var moves = GetPossibleMoves(board, from, piece);
        return [.. moves.Where(to => !WouldBeInCheck(board, from, to, turn))];
    }

    /// <summary>指定プレイヤーが駒を打てる位置を取得</summary>
    public static List<Position> GetLegalDropPositions(Board board, Turn turn, CapturedPieces captured, PieceType pieceType) =>
        [.. Board.AllPositions.Where(pos => CanDropAt(board, turn, captured, pos, pieceType))];

    /// <summary>成れるかどうか</summary>
    public static bool CanPromote(Board board, Position from, Position to)
    {
        var piece = board[from];
        if (piece is null || !piece.Type.CanPromote() || piece.Type.IsPromoted()) {
            return false;
        }

        return piece.Owner == Turn.First
            ? from.Row <= FirstPromotionBoundary || to.Row <= FirstPromotionBoundary
            : from.Row >= SecondPromotionBoundary || to.Row >= SecondPromotionBoundary;
    }

    /// <summary>成らなければならないか</summary>
    public static bool MustPromote(Board board, Position from, Position to)
    {
        var piece = board[from];
        if (piece is null) {
            return false;
        }

        return !CanExistAtRow(piece.Type, to.Row, piece.Owner);
    }

    /// <summary>王手かどうか</summary>
    public static bool IsInCheck(Board board, Turn turn)
    {
        var kingPos = board.FindKing(turn);
        if (kingPos is null) {
            return false;
        }

        return board.GetAllPieces(turn.GetOpponent())
            .Any(x => GetPossibleMoves(board, x.pos, x.piece).Contains(kingPos.Value));
    }

    /// <summary>詰みかどうか（合法手が存在しない）</summary>
    public static bool IsCheckmate(Board board, Turn turn, CapturedPieces captured)
    {
        // 盤上の駒で合法手があるか
        foreach (var (pos, _) in board.GetAllPieces(turn)) {
            if (GetLegalMoves(board, pos, turn).Count > 0) {
                return false;
            }
        }

        // 持ち駒を打てる場所があるか
        foreach (var (type, count) in captured.GetAll()) {
            if (count > 0 && GetLegalDropPositions(board, turn, captured, type).Count > 0) {
                return false;
            }
        }

        return true;
    }

    /// <summary>盤面に手を適用</summary>
    public static (Board board, CapturedPieces firstCaptured, CapturedPieces secondCaptured) ApplyMove(
        Board board, Move move, Turn turn, CapturedPieces firstCaptured, CapturedPieces secondCaptured)
    {
        var captured = turn == Turn.First ? firstCaptured : secondCaptured;

        if (move.IsDrop) {
            var newCaptured = captured.TryRemove(move.PieceType) ?? captured;
            board = board.SetPiece(move.To, new Piece(move.PieceType, turn));

            return turn == Turn.First
                ? (board, newCaptured, secondCaptured)
                : (board, firstCaptured, newCaptured);
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

        return turn == Turn.First
            ? (board, captured, secondCaptured)
            : (board, firstCaptured, captured);
    }

    /// <summary>棋譜から盤面を再構築</summary>
    public static (Board board, CapturedPieces firstCaptured, CapturedPieces secondCaptured, Turn currentTurn) ReconstructBoard(IEnumerable<Move> moves)
    {
        var board = new Board();
        var firstCaptured = CapturedPieces.Empty;
        var secondCaptured = CapturedPieces.Empty;
        var currentTurn = Turn.First;

        foreach (var move in moves) {
            (board, firstCaptured, secondCaptured) = ApplyMove(board, move, currentTurn, firstCaptured, secondCaptured);
            currentTurn = currentTurn.GetOpponent();
        }

        return (board, firstCaptured, secondCaptured, currentTurn);
    }

    /// <summary>駒の移動可能な方向を取得（王手無視）</summary>
    public static List<Position> GetPossibleMoves(Board board, Position from, Piece piece)
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

    private static bool CanDropAt(Board board, Turn turn, CapturedPieces captured, Position pos, PieceType pieceType)
    {
        if (board[pos] is not null) {
            return false;
        }

        if (pieceType == PieceType.Pawn && board.HasPawnInColumn(pos.Col, turn)) {
            return false;
        }

        if (!CanExistAtRow(pieceType, pos.Row, turn)) {
            return false;
        }

        if (pieceType == PieceType.Pawn && WouldBePawnDropMate(board, turn, pos)) {
            return false;
        }

        var testBoard = board.SetPiece(pos, new Piece(pieceType, turn));
        return !IsInCheck(testBoard, turn);
    }

    private static bool CanExistAtRow(PieceType type, int row, Turn turn)
    {
        var effectiveRow = turn == Turn.First ? row : Board.Size - 1 - row;

        return type switch {
            PieceType.Pawn or PieceType.Lance => effectiveRow > 0,
            PieceType.Knight => effectiveRow > 1,
            _ => true
        };
    }

    private static bool WouldBePawnDropMate(Board board, Turn turn, Position dropPos)
    {
        var opponent = turn.GetOpponent();
        var kingPos = board.FindKing(opponent);
        if (kingPos is null) {
            return false;
        }

        var direction = turn.GetForwardDirection();
        if (dropPos.Col != kingPos.Value.Col || dropPos.Row != kingPos.Value.Row + direction) {
            return false;
        }

        var tempBoard = board.SetPiece(dropPos, new Piece(PieceType.Pawn, turn));

        var kingMoves = GetPossibleMoves(tempBoard, kingPos.Value, tempBoard[kingPos.Value]!);
        foreach (var move in kingMoves) {
            var testBoard = tempBoard.MovePiece(kingPos.Value, move);
            if (!IsInCheck(testBoard, opponent)) {
                return false;
            }
        }

        foreach (var (pos, piece) in board.GetAllPieces(opponent)) {
            if (pos == kingPos) {
                continue;
            }

            var moves = GetPossibleMoves(board, pos, piece);
            if (moves.Contains(dropPos)) {
                var testBoard = tempBoard.MovePiece(pos, dropPos);
                if (!IsInCheck(testBoard, opponent)) {
                    return false;
                }
            }
        }

        return true;
    }

    private static bool WouldBeInCheck(Board board, Position from, Position to, Turn turn)
    {
        var testBoard = board.MovePiece(from, to);
        return IsInCheck(testBoard, turn);
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

    private static (int dc, int dr, bool slide)[] GetMoveDirections(PieceType type, Turn owner)
    {
        var forward = owner.GetForwardDirection();
        return DirectionCache.GetValueOrDefault((type, forward), []);
    }
}
