using Fu.Core.Models;

namespace Fu.Core.Abstractions;

/// <summary>
/// 分岐・再戦・検討モードサービス
/// 分岐からの再開、再戦、検討モードの操作を担当
/// </summary>
public interface IBranchService
{
    #region 分岐再開

    /// <summary>現在の局面から分岐再開（ローカル操作）</summary>
    Task ResumeFromBranchAsync();

    /// <summary>分岐再開を適用（リモートから受信時）</summary>
    Task ApplyBranchResumeAsync(IReadOnlyList<Move> moveHistory);

    #endregion

    #region 再戦

    /// <summary>現在の局面から再戦（ローカル操作）</summary>
    Task RematchFromCurrentPositionAsync();

    /// <summary>再戦を適用（リモートから受信時）</summary>
    Task ApplyRematchAsync(IReadOnlyList<Move> moveHistory);

    #endregion

    #region 検討モード

    /// <summary>現在の局面から検討モード開始（ローカル操作）</summary>
    Task StartReviewFromCurrentPositionAsync();

    /// <summary>検討モード開始を適用（リモートから受信時）</summary>
    Task ApplyReviewStartAsync(IReadOnlyList<Move> moveHistory);

    /// <summary>検討モードの手を適用（リモートから受信時）</summary>
    Task ApplyReviewMoveAsync(Move move);

    #endregion

    #region 詰み手順

    /// <summary>詰み手順をブランチとして追加</summary>
    Task<bool> AddMateSequenceBranchAsync(string usiMoves, int branchStartIndex);

    #endregion
}
