using R3;

using Fu.Core.Models;

namespace Fu.Core.Abstractions;

/// <summary>
/// 対戦相手の抽象インターフェース
/// P2P対戦の相手、将棋エンジン、ローカル対戦の相手など
/// </summary>
public interface IOpponent
{
    /// <summary>対戦相手の識別子</summary>
    string Id { get; }

    /// <summary>対戦相手の表示名</summary>
    string DisplayName { get; }

    /// <summary>対戦相手が人間かどうか</summary>
    bool IsHuman { get; }

    /// <summary>対戦相手が利用可能か（接続中、エンジン起動中など）</summary>
    bool IsAvailable { get; }

    /// <summary>相手の手が届いた時</summary>
    Observable<(Move Move, TimeSpan Elapsed)> MoveReceived { get; }

    /// <summary>相手が投了した時</summary>
    Observable<Unit> ResignReceived { get; }

    /// <summary>自分の手を送信（相手に通知）</summary>
    Task SendMoveAsync(Move move, TimeSpan elapsedTime);

    /// <summary>投了を送信</summary>
    Task SendResignAsync();

    /// <summary>対局開始を通知（エンジンの場合は思考開始の準備）</summary>
    Task NotifyGameStartAsync(Turn opponentTurn, Board board, CapturedPieces firstCaptured, CapturedPieces secondCaptured);

    /// <summary>対局終了を通知</summary>
    Task NotifyGameEndAsync(GameStatus result);

    /// <summary>相手の手番になったことを通知（エンジンの場合は思考開始）</summary>
    Task NotifyTurnAsync(Board board, Turn currentTurn, CapturedPieces firstCaptured, CapturedPieces secondCaptured);
}
