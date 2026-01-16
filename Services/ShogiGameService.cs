using System.Collections.Frozen;
using System.Collections.Immutable;

using ShogiGame.Models;

namespace ShogiGame.Services;

public class ShogiGameService
{
    private const int SentePromotionBoundary = 2;  // 先手の成れる段（0-2）
    private const int GotePromotionBoundary = 6;   // 後手の成れる段（6-8）

    public GameState State { get; private set; } = GameState.Initial;

    public event Action? OnStateChanged;

    public void NewGame()
    {
        var localPlayer = this.State.LocalPlayer;
        this.State = GameState.Initial with {
            Status = GameStatus.Playing,
            LocalPlayer = localPlayer
        };
        OnStateChanged?.Invoke();
    }

    public void SetLocalPlayer(Player player)
    {
        this.State = this.State with { LocalPlayer = player };
        OnStateChanged?.Invoke();
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
        // 空きマスでなければ打てない
        if (this.State.Board[pos] is not null) {
            return false;
        }

        // 二歩チェック
        if (pieceType == PieceType.Pawn && this.State.Board.HasPawnInColumn(pos.Col, this.State.CurrentPlayer)) {
            return false;
        }

        // 行きどころのない駒チェック
        if (!CanExistAtRow(pieceType, pos.Row, this.State.CurrentPlayer)) {
            return false;
        }

        // 打ち歩詰めチェック
        if (pieceType == PieceType.Pawn && this.WouldBePawnDropMate(pos)) {
            return false;
        }

        // 王手回避チェック：打った後も王手状態なら打てない
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

        // 歩が王の真上/真下にあるかチェック
        var direction = this.State.CurrentPlayer.GetForwardDirection();
        if (dropPos.Col != kingPos.Value.Col || dropPos.Row != kingPos.Value.Row + direction) {
            return false;
        }

        // この打ち歩で王手になり、かつ相手が逃げられない場合は打ち歩詰め
        var tempBoard = this.State.Board.SetPiece(dropPos, new Piece(PieceType.Pawn, this.State.CurrentPlayer));

        // 王が逃げられるかチェック
        var kingMoves = GetPossibleMovesOnBoard(tempBoard, kingPos.Value, tempBoard[kingPos.Value]!);
        foreach (var move in kingMoves) {
            var testBoard = tempBoard.MovePiece(kingPos.Value, move);
            if (!IsInCheck(testBoard, opponent)) {
                return false;
            }
        }

        // 歩を取れるかチェック
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

    public bool TryMakeMove(Move move)
    {
        Console.WriteLine($"TryMakeMove: Status={this.State.Status}, IsDrop={move.IsDrop}");
        if (this.State.Status != GameStatus.Playing) {
            return false;
        }

        if (move.IsDrop) {
            return this.TryDropPiece(move);
        }

        if (move.From is null) {
            return false;
        }

        var from = move.From.Value;
        var to = move.To;
        var piece = this.State.Board[from];

        Console.WriteLine($"TryMakeMove: from=({from.Col},{from.Row}) piece={piece?.Type} owner={piece?.Owner} currentPlayer={this.State.CurrentPlayer}");
        if (piece is null || piece.Owner != this.State.CurrentPlayer) {
            return false;
        }

        var legalMoves = this.GetLegalMoves(from);
        Console.WriteLine($"TryMakeMove: legalMoves.Count={legalMoves.Count}, to=({to.Col},{to.Row})");
        if (!legalMoves.Contains(to)) {
            return false;
        }

        // 駒を取る場合の処理
        var captured = this.State.Board[to];
        var newCaptured = this.State.GetCapturedPieces(this.State.CurrentPlayer);
        if (captured is not null) {
            newCaptured = newCaptured.Add(captured.Type);
            move.CapturedPiece = captured.Type;

            // 王が取られた場合はゲーム終了
            if (captured.Type == PieceType.King) {
                this.State = this.State with {
                    Board = this.State.Board.MovePiece(from, to),
                    MoveHistory = this.State.MoveHistory.Add(move),
                    Status = this.State.CurrentPlayer.GetWinStatus()
                };
                this.State = this.State.WithCapturedPieces(this.State.CurrentPlayer, newCaptured);
                OnStateChanged?.Invoke();
                return true;
            }
        }

        // 駒を移動（成りの場合は新しいPieceを作成）
        var newPiece = move.IsPromotion && piece.Type.CanPromote()
            ? piece with { Type = piece.Type.GetPromotedType() }
            : piece;

        var newState = this.State with {
            Board = this.State.Board.MovePiece(from, to, newPiece),
            MoveHistory = this.State.MoveHistory.Add(move)
        };
        newState = newState.WithCapturedPieces(this.State.CurrentPlayer, newCaptured);
        newState = newState.SwitchPlayer();

        this.State = newState;

        // 詰みチェック
        this.CheckForCheckmate();

        OnStateChanged?.Invoke();
        return true;
    }

    private bool TryDropPiece(Move move)
    {
        var captured = this.State.GetCapturedPieces(this.State.CurrentPlayer);
        if (captured.GetCount(move.PieceType) <= 0) {
            return false;
        }

        var legalPositions = this.GetLegalDropPositions(move.PieceType);
        if (!legalPositions.Contains(move.To)) {
            return false;
        }

        var newCaptured = captured.Remove(move.PieceType);
        if (newCaptured is null) {
            return false;
        }

        var newState = this.State with {
            Board = this.State.Board.SetPiece(move.To, new Piece(move.PieceType, this.State.CurrentPlayer)),
            MoveHistory = this.State.MoveHistory.Add(move)
        };
        newState = newState.WithCapturedPieces(this.State.CurrentPlayer, newCaptured);
        newState = newState.SwitchPlayer();

        this.State = newState;

        this.CheckForCheckmate();

        OnStateChanged?.Invoke();
        return true;
    }

    public void ApplyRemoteMove(Move move)
    {
        Console.WriteLine($"ApplyRemoteMove: From=({move.From?.Col},{move.From?.Row}) To=({move.To.Col},{move.To.Row}) CurrentPlayer={this.State.CurrentPlayer}");
        move.Player = this.State.CurrentPlayer;
        var result = this.TryMakeMove(move);
        Console.WriteLine($"ApplyRemoteMove: TryMakeMove returned {result}");
    }

    public bool CanPromote(Position from, Position to)
    {
        var piece = this.State.Board[from];
        if (piece is null || !piece.Type.CanPromote() || piece.Type.IsPromoted()) {
            return false;
        }

        // 敵陣（相手から見て1-3段目）に入る or 出る場合に成れる
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

    // 統合された移動先取得メソッド（盤面を引数に取る）
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

    // 移動方向の定義 - FrozenDictionary for thread-safe read-only access
    private static readonly FrozenDictionary<(PieceType, int), (int dc, int dr, bool slide)[]> DirectionCache =
        BuildDirectionCache().ToFrozenDictionary();

    private static Dictionary<(PieceType, int), (int dc, int dr, bool slide)[]> BuildDirectionCache()
    {
        var cache = new Dictionary<(PieceType, int), (int dc, int dr, bool slide)[]>();

        foreach (var forward in new[] { -1, 1 }) {
            // King - 8方向1マス
            cache[(PieceType.King, forward)] = [
                (-1, -1, false), (0, -1, false), (1, -1, false),
                (-1, 0, false), (1, 0, false),
                (-1, 1, false), (0, 1, false), (1, 1, false)
            ];

            // Rook - 縦横スライド
            cache[(PieceType.Rook, forward)] = [
                (0, -1, true), (0, 1, true), (-1, 0, true), (1, 0, true)
            ];

            // PromotedRook - 縦横スライド + 斜め1マス
            cache[(PieceType.PromotedRook, forward)] = [
                (0, -1, true), (0, 1, true), (-1, 0, true), (1, 0, true),
                (-1, -1, false), (1, -1, false), (-1, 1, false), (1, 1, false)
            ];

            // Bishop - 斜めスライド
            cache[(PieceType.Bishop, forward)] = [
                (-1, -1, true), (1, -1, true), (-1, 1, true), (1, 1, true)
            ];

            // PromotedBishop - 斜めスライド + 縦横1マス
            cache[(PieceType.PromotedBishop, forward)] = [
                (-1, -1, true), (1, -1, true), (-1, 1, true), (1, 1, true),
                (0, -1, false), (0, 1, false), (-1, 0, false), (1, 0, false)
            ];

            // Gold and promoted pieces (except Rook/Bishop)
            var goldMoves = new (int, int, bool)[] {
                (0, forward, false), (-1, forward, false), (1, forward, false),
                (-1, 0, false), (1, 0, false), (0, -forward, false)
            };
            cache[(PieceType.Gold, forward)] = goldMoves;
            cache[(PieceType.PromotedSilver, forward)] = goldMoves;
            cache[(PieceType.PromotedKnight, forward)] = goldMoves;
            cache[(PieceType.PromotedLance, forward)] = goldMoves;
            cache[(PieceType.PromotedPawn, forward)] = goldMoves;

            // Silver - 前3方向 + 斜め後ろ2方向
            cache[(PieceType.Silver, forward)] = [
                (0, forward, false), (-1, forward, false), (1, forward, false),
                (-1, -forward, false), (1, -forward, false)
            ];

            // Knight - 桂馬飛び
            cache[(PieceType.Knight, forward)] = [
                (-1, forward * 2, false), (1, forward * 2, false)
            ];

            // Lance - 前方スライド
            cache[(PieceType.Lance, forward)] = [
                (0, forward, true)
            ];

            // Pawn - 前1マス
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

    private void CheckForCheckmate()
    {
        var currentPlayer = this.State.CurrentPlayer;

        // 全ての合法手を探す
        foreach (var (pos, _) in this.State.Board.GetAllPieces(currentPlayer)) {
            if (this.GetLegalMoves(pos).Count > 0) {
                return;
            }
        }

        // 持ち駒を打てるかチェック
        var captured = this.State.GetCapturedPieces(currentPlayer);
        foreach (var (type, count) in captured.GetAll()) {
            if (count > 0 && this.GetLegalDropPositions(type).Count > 0) {
                return;
            }
        }

        // 合法手がない = 詰み（詰まされた側の負け = 相手の勝ち）
        this.State = this.State with {
            Status = currentPlayer.GetOpponent().GetWinStatus()
        };
        OnStateChanged?.Invoke();
    }

    public void Resign()
    {
        this.State = this.State with {
            Status = this.State.CurrentPlayer.GetOpponent().GetWinStatus()
        };
        OnStateChanged?.Invoke();
    }
}