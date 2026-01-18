using R3;

using Fu.Core.Abstractions;
using Fu.Core.Models;

namespace Fu.Observers;

/// <summary>
/// リモート観戦者への同期を行うオブザーバー
/// トランスポート経由で接続している観戦者に局面を同期する
/// </summary>
public class RemoteSpectatorObserver : IGameObserver, IDisposable
{
    private readonly IGameTransport _transport;
    private readonly Func<GameStateForSync> _getGameState;
    private readonly IDisposable _subscription;

    public string Id => "remote-spectators";
    public string DisplayName => "Remote Spectators";
    public bool IsAvailable => this._transport.IsConnected;

    public RemoteSpectatorObserver(
        IGameTransport transport,
        Func<GameStateForSync> getGameState)
    {
        this._transport = transport;
        this._getGameState = getGameState;

        // 新しい参加者が来たらゲーム状態をリクエストされる
        this._subscription = this._transport.GameStateRequested
            .SubscribeAwait(async (_, _) => await this.HandleGameStateRequestedAsync());
    }

    public Task OnGameStartedAsync(Board board, Turn currentTurn, CapturedPieces firstCaptured, CapturedPieces secondCaptured)
    {
        // 対局開始時の同期はGameStartWithSidesMessageで行われるため不要
        return Task.CompletedTask;
    }

    public Task OnMoveMadeAsync(Move move, Board board, Turn currentTurn, CapturedPieces firstCaptured, CapturedPieces secondCaptured)
    {
        // 手は既にSendMoveAsyncで送信されているため不要
        return Task.CompletedTask;
    }

    public Task OnGameEndedAsync(GameStatus result)
    {
        // 投了は既にSendResignAsyncで送信されているため不要
        return Task.CompletedTask;
    }

    public Task OnPositionChangedAsync(Board board, Turn currentTurn, CapturedPieces firstCaptured, CapturedPieces secondCaptured)
    {
        // 検討モードでの移動は別途SendReviewMoveAsyncで送信
        return Task.CompletedTask;
    }

    private async Task HandleGameStateRequestedAsync()
    {
        if (!this._transport.IsHost) {
            return;
        }

        var state = this._getGameState();
        await this._transport.SendGameStateSyncAsync(
            state.MoveHistory,
            state.SentePlayerId,
            state.GotePlayerId,
            state.SenteNickname,
            state.GoteNickname,
            state.Status,
            state.EvaluationOptions,
            state.MoveTimes);
    }

    public void Unsubscribe()
    {
        this._subscription.Dispose();
    }

    public void Dispose()
    {
        this._subscription.Dispose();
        GC.SuppressFinalize(this);
    }
}

/// <summary>
/// ゲーム状態同期用のデータ
/// </summary>
public record GameStateForSync(
    IEnumerable<Move> MoveHistory,
    PlayerId SentePlayerId,
    PlayerId GotePlayerId,
    string SenteNickname,
    string GoteNickname,
    GameStatus Status,
    Core.Models.Dto.EvaluationDisplayOptions? EvaluationOptions,
    IEnumerable<TimeSpan>? MoveTimes
);
