using Fu.Core.Abstractions;
using Fu.Core.Models;

namespace Fu.Core.Services;

/// <summary>
/// 盤面キャッシュの実装
/// </summary>
public sealed class BoardCache : IBoardCache
{
    private (IReadOnlyList<Move> branchHistory, int moveIndex, Board board, CapturedPieces firstCaptured, CapturedPieces secondCaptured, Turn currentTurn)? _cache;

    public (Board board, CapturedPieces firstCaptured, CapturedPieces secondCaptured, Turn currentTurn)?
        TryGet(IReadOnlyList<Move> branchHistory, int moveIndex)
    {
        if (this._cache is { } cache
            && ReferenceEquals(cache.branchHistory, branchHistory)
            && cache.moveIndex == moveIndex) {
            return (cache.board, cache.firstCaptured, cache.secondCaptured, cache.currentTurn);
        }
        return null;
    }

    public void Store(
        IReadOnlyList<Move> branchHistory,
        int moveIndex,
        Board board,
        CapturedPieces firstCaptured,
        CapturedPieces secondCaptured,
        Turn currentTurn)
    {
        this._cache = (branchHistory, moveIndex, board, firstCaptured, secondCaptured, currentTurn);
    }

    public void Clear() => this._cache = null;
}
