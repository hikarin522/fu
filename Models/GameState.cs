namespace ShogiGame.Models;

public enum GameStatus
{
    WaitingForConnection,
    Playing,
    CheckmateSente,  // 先手の勝ち
    CheckmateGote,   // 後手の勝ち
    Resign
}

public class CapturedPieces
{
    private readonly Dictionary<PieceType, int> _pieces = new();

    public int GetCount(PieceType type) => _pieces.TryGetValue(type, out var count) ? count : 0;

    public void Add(PieceType type)
    {
        // 成駒は元の駒として持ち駒になる
        var baseType = type switch
        {
            PieceType.PromotedRook => PieceType.Rook,
            PieceType.PromotedBishop => PieceType.Bishop,
            PieceType.PromotedSilver => PieceType.Silver,
            PieceType.PromotedKnight => PieceType.Knight,
            PieceType.PromotedLance => PieceType.Lance,
            PieceType.PromotedPawn => PieceType.Pawn,
            _ => type
        };

        if (!_pieces.ContainsKey(baseType))
            _pieces[baseType] = 0;
        _pieces[baseType]++;
    }

    public bool Remove(PieceType type)
    {
        if (_pieces.TryGetValue(type, out var count) && count > 0)
        {
            _pieces[type]--;
            return true;
        }
        return false;
    }

    public IEnumerable<(PieceType type, int count)> GetAll()
    {
        foreach (var kvp in _pieces.Where(x => x.Value > 0))
            yield return (kvp.Key, kvp.Value);
    }

    public CapturedPieces Clone()
    {
        var clone = new CapturedPieces();
        foreach (var kvp in _pieces)
            clone._pieces[kvp.Key] = kvp.Value;
        return clone;
    }
}

public class GameState
{
    public Board Board { get; set; } = new();
    public Player CurrentPlayer { get; set; } = Player.Sente;
    public GameStatus Status { get; set; } = GameStatus.WaitingForConnection;
    public CapturedPieces SenteCaptured { get; set; } = new();
    public CapturedPieces GoteCaptured { get; set; } = new();
    public List<Move> MoveHistory { get; set; } = new();
    public Player LocalPlayer { get; set; } = Player.None;  // このクライアントが操作するプレイヤー

    public CapturedPieces GetCapturedPieces(Player player) =>
        player == Player.Sente ? SenteCaptured : GoteCaptured;

    public bool IsMyTurn => LocalPlayer == CurrentPlayer;

    public void SwitchPlayer()
    {
        CurrentPlayer = CurrentPlayer == Player.Sente ? Player.Gote : Player.Sente;
    }

    public GameState Clone()
    {
        return new GameState
        {
            Board = Board.Clone(),
            CurrentPlayer = CurrentPlayer,
            Status = Status,
            SenteCaptured = SenteCaptured.Clone(),
            GoteCaptured = GoteCaptured.Clone(),
            MoveHistory = new List<Move>(MoveHistory),
            LocalPlayer = LocalPlayer
        };
    }
}
