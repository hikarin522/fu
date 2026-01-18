using Fu.Core.Models;

namespace Fu.Core.Abstractions;

/// <summary>
/// 盤面キャッシュのインターフェース
/// タブ単位でScopedとして登録され、新規対局時にクリアされる
/// </summary>
public interface IBoardCache
{
    /// <summary>キャッシュから盤面を取得</summary>
    (Board board, CapturedPieces firstCaptured, CapturedPieces secondCaptured, Turn currentTurn)?
        TryGet(IReadOnlyList<Move> branchHistory, int moveIndex);

    /// <summary>盤面をキャッシュに保存</summary>
    void Store(
        IReadOnlyList<Move> branchHistory,
        int moveIndex,
        Board board,
        CapturedPieces firstCaptured,
        CapturedPieces secondCaptured,
        Turn currentTurn);

    /// <summary>キャッシュをクリア</summary>
    void Clear();
}
