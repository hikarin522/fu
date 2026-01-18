using Fu.Core.Models;

namespace Fu.Core.Abstractions;

/// <summary>
/// ゲームの状態を監視するオブザーバー
/// 評価エンジン、リモート観戦者、棋譜記録サービスなど
/// </summary>
public interface IGameObserver
{
    /// <summary>オブザーバーの識別子</summary>
    string Id { get; }

    /// <summary>オブザーバーの表示名</summary>
    string DisplayName { get; }

    /// <summary>オブザーバーが利用可能か</summary>
    bool IsAvailable { get; }

    /// <summary>対局開始を通知</summary>
    Task OnGameStartedAsync(Board board, Turn currentTurn, CapturedPieces firstCaptured, CapturedPieces secondCaptured);

    /// <summary>手が指されたことを通知</summary>
    Task OnMoveMadeAsync(Move move, Board board, Turn currentTurn, CapturedPieces firstCaptured, CapturedPieces secondCaptured);

    /// <summary>対局終了を通知</summary>
    Task OnGameEndedAsync(GameStatus result);

    /// <summary>局面が変わったことを通知（検討モードでの移動など）</summary>
    Task OnPositionChangedAsync(Board board, Turn currentTurn, CapturedPieces firstCaptured, CapturedPieces secondCaptured);
}
