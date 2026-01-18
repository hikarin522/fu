using Fu.Core.Models;

namespace Fu.Core.Abstractions;

/// <summary>
/// 棋譜ナビゲーションサービス
/// 手の前後移動、分岐ノード移動などを担当
/// </summary>
public interface IGameNavigationService
{
    /// <summary>1手戻る</summary>
    Task GoBackAsync();

    /// <summary>1手進む</summary>
    Task GoForwardAsync();

    /// <summary>分岐を選択して進む</summary>
    Task GoForwardBranchAsync(int branchIndex);

    /// <summary>最新の局面に移動</summary>
    Task GoToLatestAsync();

    /// <summary>指定した手数に移動</summary>
    Task SetViewingMoveIndexAsync(int moveIndex);

    /// <summary>指定したノードに移動</summary>
    Task GoToNodeAsync(MoveNode? node);

    /// <summary>前の分岐に移動</summary>
    Task GoToPreviousBranchAsync();

    /// <summary>次の分岐に移動</summary>
    Task GoToNextBranchAsync();

    /// <summary>現在の分岐インデックスを取得</summary>
    int GetCurrentBranchIndex();

    /// <summary>分岐総数を取得</summary>
    int GetTotalBranchCount();

    /// <summary>指定手数時点の盤面を取得</summary>
    (Board board, CapturedPieces firstCaptured, CapturedPieces secondCaptured, Turn currentTurn) GetBoardAtMove(int moveIndex);
}
