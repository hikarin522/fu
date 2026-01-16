using ShogiGame.Models;

namespace ShogiGame.Services;

public class ShogiGameService
{
    public GameState State { get; private set; } = new();

    public event Action? OnStateChanged;

    public void NewGame()
    {
        var localPlayer = State.LocalPlayer; // LocalPlayerを保持
        State = new GameState();
        State.Board.Initialize();
        State.Status = GameStatus.Playing;
        State.LocalPlayer = localPlayer; // LocalPlayerを復元
        OnStateChanged?.Invoke();
    }

    public void SetLocalPlayer(Player player)
    {
        State.LocalPlayer = player;
        OnStateChanged?.Invoke();
    }

    public List<Position> GetLegalMoves(Position from)
    {
        var piece = State.Board[from];
        if (piece == null || piece.Owner != State.CurrentPlayer)
            return new List<Position>();

        var moves = GetPossibleMoves(from, piece);

        // 自玉が王手になる手を除外
        return moves.Where(to => !WouldBeInCheck(from, to, State.CurrentPlayer)).ToList();
    }

    public List<Position> GetLegalDropPositions(PieceType pieceType)
    {
        var positions = new List<Position>();

        for (int col = 0; col < 9; col++)
        {
            for (int row = 0; row < 9; row++)
            {
                var pos = new Position(col, row);
                if (State.Board[pos] != null) continue;

                // 二歩チェック
                if (pieceType == PieceType.Pawn && State.Board.HasPawnInColumn(col, State.CurrentPlayer))
                    continue;

                // 行きどころのない駒チェック
                if (!CanExistAtRow(pieceType, row, State.CurrentPlayer))
                    continue;

                // 打ち歩詰めチェック
                if (pieceType == PieceType.Pawn && WouldBePawnDropMate(pos))
                    continue;

                positions.Add(pos);
            }
        }

        return positions;
    }

    private bool CanExistAtRow(PieceType type, int row, Player player)
    {
        int effectiveRow = player == Player.Sente ? row : 8 - row;

        return type switch
        {
            PieceType.Pawn or PieceType.Lance => effectiveRow > 0,
            PieceType.Knight => effectiveRow > 1,
            _ => true
        };
    }

    private bool WouldBePawnDropMate(Position dropPos)
    {
        var opponent = State.CurrentPlayer == Player.Sente ? Player.Gote : Player.Sente;
        var kingPos = State.Board.FindKing(opponent);
        if (kingPos == null) return false;

        // 歩が王の真上/真下にあるかチェック
        int direction = State.CurrentPlayer == Player.Sente ? -1 : 1;
        if (dropPos.Col != kingPos.Value.Col || dropPos.Row != kingPos.Value.Row + direction)
            return false;

        // この打ち歩で王手になり、かつ相手が逃げられない場合は打ち歩詰め
        var tempBoard = State.Board.Clone();
        tempBoard[dropPos] = new Piece(PieceType.Pawn, State.CurrentPlayer);

        // 王が逃げられるかチェック
        var kingMoves = GetPossibleMoves(kingPos.Value, tempBoard[kingPos.Value]!);
        foreach (var move in kingMoves)
        {
            var testBoard = tempBoard.Clone();
            testBoard[move] = testBoard[kingPos.Value];
            testBoard[kingPos.Value] = null;
            if (!IsInCheck(testBoard, opponent))
                return false;
        }

        // 歩を取れるかチェック
        foreach (var (pos, piece) in State.Board.GetAllPieces(opponent))
        {
            if (pos == kingPos) continue;
            var moves = GetPossibleMoves(pos, piece);
            if (moves.Contains(dropPos))
            {
                var testBoard = tempBoard.Clone();
                testBoard[dropPos] = testBoard[pos];
                testBoard[pos] = null;
                if (!IsInCheck(testBoard, opponent))
                    return false;
            }
        }

        return true;
    }

    public bool TryMakeMove(Move move)
    {
        if (State.Status != GameStatus.Playing)
            return false;

        if (move.IsDrop)
        {
            return TryDropPiece(move);
        }

        if (move.From == null) return false;

        var from = move.From.Value;
        var to = move.To;
        var piece = State.Board[from];

        if (piece == null || piece.Owner != State.CurrentPlayer)
            return false;

        var legalMoves = GetLegalMoves(from);
        if (!legalMoves.Contains(to))
            return false;

        // 駒を取る
        var captured = State.Board[to];
        if (captured != null)
        {
            State.GetCapturedPieces(State.CurrentPlayer).Add(captured.Type);
            move.CapturedPiece = captured.Type;
        }

        // 駒を移動
        State.Board[to] = piece;
        State.Board[from] = null;

        // 成り
        if (move.IsPromotion && piece.CanPromote)
        {
            State.Board[to]!.Type = piece.Promote();
        }

        State.MoveHistory.Add(move);
        State.SwitchPlayer();

        // 詰みチェック
        CheckForCheckmate();

        OnStateChanged?.Invoke();
        return true;
    }

    private bool TryDropPiece(Move move)
    {
        var captured = State.GetCapturedPieces(State.CurrentPlayer);
        if (captured.GetCount(move.PieceType) <= 0)
            return false;

        var legalPositions = GetLegalDropPositions(move.PieceType);
        if (!legalPositions.Contains(move.To))
            return false;

        captured.Remove(move.PieceType);
        State.Board[move.To] = new Piece(move.PieceType, State.CurrentPlayer);

        State.MoveHistory.Add(move);
        State.SwitchPlayer();

        CheckForCheckmate();

        OnStateChanged?.Invoke();
        return true;
    }

    public void ApplyRemoteMove(Move move)
    {
        move.Player = State.CurrentPlayer;
        TryMakeMove(move);
    }

    public bool CanPromote(Position from, Position to)
    {
        var piece = State.Board[from];
        if (piece == null || !piece.CanPromote || piece.IsPromoted)
            return false;

        // 敵陣（相手から見て1-3段目）に入る or 出る場合に成れる
        if (piece.Owner == Player.Sente)
        {
            return from.Row <= 2 || to.Row <= 2;
        }
        else
        {
            return from.Row >= 6 || to.Row >= 6;
        }
    }

    public bool MustPromote(Position from, Position to)
    {
        var piece = State.Board[from];
        if (piece == null) return false;

        return !CanExistAtRow(piece.Type, to.Row, piece.Owner);
    }

    private List<Position> GetPossibleMoves(Position from, Piece piece)
    {
        var moves = new List<Position>();
        var directions = GetMoveDirections(piece.Type, piece.Owner);

        foreach (var (dc, dr, slide) in directions)
        {
            int col = from.Col + dc;
            int row = from.Row + dr;

            while (col >= 0 && col < 9 && row >= 0 && row < 9)
            {
                var target = State.Board[col, row];
                if (target == null)
                {
                    moves.Add(new Position(col, row));
                }
                else if (target.Owner != piece.Owner)
                {
                    moves.Add(new Position(col, row));
                    break;
                }
                else
                {
                    break;
                }

                if (!slide) break;

                col += dc;
                row += dr;
            }
        }

        return moves;
    }

    private static List<(int dc, int dr, bool slide)> GetMoveDirections(PieceType type, Player owner)
    {
        int forward = owner == Player.Sente ? -1 : 1;

        return type switch
        {
            PieceType.King => new List<(int, int, bool)>
            {
                (-1, -1, false), (0, -1, false), (1, -1, false),
                (-1, 0, false), (1, 0, false),
                (-1, 1, false), (0, 1, false), (1, 1, false)
            },
            PieceType.Rook => new List<(int, int, bool)>
            {
                (0, -1, true), (0, 1, true), (-1, 0, true), (1, 0, true)
            },
            PieceType.PromotedRook => new List<(int, int, bool)>
            {
                (0, -1, true), (0, 1, true), (-1, 0, true), (1, 0, true),
                (-1, -1, false), (1, -1, false), (-1, 1, false), (1, 1, false)
            },
            PieceType.Bishop => new List<(int, int, bool)>
            {
                (-1, -1, true), (1, -1, true), (-1, 1, true), (1, 1, true)
            },
            PieceType.PromotedBishop => new List<(int, int, bool)>
            {
                (-1, -1, true), (1, -1, true), (-1, 1, true), (1, 1, true),
                (0, -1, false), (0, 1, false), (-1, 0, false), (1, 0, false)
            },
            PieceType.Gold or PieceType.PromotedSilver or PieceType.PromotedKnight
                or PieceType.PromotedLance or PieceType.PromotedPawn => new List<(int, int, bool)>
            {
                (0, forward, false), (-1, forward, false), (1, forward, false),
                (-1, 0, false), (1, 0, false), (0, -forward, false)
            },
            PieceType.Silver => new List<(int, int, bool)>
            {
                (0, forward, false), (-1, forward, false), (1, forward, false),
                (-1, -forward, false), (1, -forward, false)
            },
            PieceType.Knight => new List<(int, int, bool)>
            {
                (-1, forward * 2, false), (1, forward * 2, false)
            },
            PieceType.Lance => new List<(int, int, bool)>
            {
                (0, forward, true)
            },
            PieceType.Pawn => new List<(int, int, bool)>
            {
                (0, forward, false)
            },
            _ => new List<(int, int, bool)>()
        };
    }

    private bool WouldBeInCheck(Position from, Position to, Player player)
    {
        var testBoard = State.Board.Clone();
        testBoard[to] = testBoard[from];
        testBoard[from] = null;
        return IsInCheck(testBoard, player);
    }

    private bool IsInCheck(Board board, Player player)
    {
        var kingPos = board.FindKing(player);
        if (kingPos == null) return false;

        var opponent = player == Player.Sente ? Player.Gote : Player.Sente;

        foreach (var (pos, piece) in board.GetAllPieces(opponent))
        {
            var moves = GetPossibleMovesOnBoard(board, pos, piece);
            if (moves.Contains(kingPos.Value))
                return true;
        }

        return false;
    }

    private List<Position> GetPossibleMovesOnBoard(Board board, Position from, Piece piece)
    {
        var moves = new List<Position>();
        var directions = GetMoveDirections(piece.Type, piece.Owner);

        foreach (var (dc, dr, slide) in directions)
        {
            int col = from.Col + dc;
            int row = from.Row + dr;

            while (col >= 0 && col < 9 && row >= 0 && row < 9)
            {
                var target = board[col, row];
                if (target == null)
                {
                    moves.Add(new Position(col, row));
                }
                else if (target.Owner != piece.Owner)
                {
                    moves.Add(new Position(col, row));
                    break;
                }
                else
                {
                    break;
                }

                if (!slide) break;

                col += dc;
                row += dr;
            }
        }

        return moves;
    }

    public bool IsInCheck() => IsInCheck(State.Board, State.CurrentPlayer);

    private void CheckForCheckmate()
    {
        var currentPlayer = State.CurrentPlayer;

        // 全ての合法手を探す
        foreach (var (pos, piece) in State.Board.GetAllPieces(currentPlayer))
        {
            var moves = GetLegalMoves(pos);
            if (moves.Any()) return;
        }

        // 持ち駒を打てるかチェック
        var captured = State.GetCapturedPieces(currentPlayer);
        foreach (var (type, count) in captured.GetAll())
        {
            if (count > 0 && GetLegalDropPositions(type).Any())
                return;
        }

        // 合法手がない = 詰み
        State.Status = currentPlayer == Player.Sente
            ? GameStatus.CheckmateGote  // 先手が詰まされた = 後手の勝ち
            : GameStatus.CheckmateSente;

        OnStateChanged?.Invoke();
    }

    public void Resign()
    {
        State.Status = State.CurrentPlayer == Player.Sente
            ? GameStatus.CheckmateGote
            : GameStatus.CheckmateSente;
        OnStateChanged?.Invoke();
    }
}
