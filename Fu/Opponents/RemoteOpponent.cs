using R3;

using MessagePipe;

using Fu.Core.Abstractions;
using Fu.Core.Events;
using Fu.Core.Models;

namespace Fu.Opponents;

/// <summary>
/// リモート対戦相手（トランスポート非依存）
/// </summary>
public class RemoteOpponent : IOpponent, IDisposable
{
    private readonly IGameTransport _transport;
    private readonly Subject<(Move Move, TimeSpan Elapsed)> _moveReceived = new();
    private readonly Subject<Unit> _resignReceived = new();
    private readonly List<IDisposable> _subscriptions = [];

    /// <summary>プレイヤー情報</summary>
    public RemotePlayerInfo Player { get; private set; }

    public string Id => this.Player.PlayerId.AsPrimitive();
    public string DisplayName => this.Player.Nickname;
    public bool IsHuman => true;
    public bool IsAvailable => this._transport.IsConnected && this.Player.IsConnected;

    /// <summary>手を受信した時</summary>
    public Observable<(Move Move, TimeSpan Elapsed)> MoveReceived => this._moveReceived;

    /// <summary>投了を受信した時</summary>
    public Observable<Unit> ResignReceived => this._resignReceived;

    public RemoteOpponent(
        IGameTransport transport,
        RemotePlayerInfo player,
        ISubscriber<TransportMoveReceivedEvent> moveReceivedSubscriber,
        ISubscriber<TransportResignReceivedEvent> resignReceivedSubscriber)
    {
        this._transport = transport;
        this.Player = player;

        // MessagePipeのイベントを購読
        this._subscriptions.Add(
            moveReceivedSubscriber.Subscribe(e =>
                this._moveReceived.OnNext((e.Move, e.Elapsed))));

        this._subscriptions.Add(
            resignReceivedSubscriber.Subscribe(_ =>
                this._resignReceived.OnNext(Unit.Default)));
    }

    /// <summary>プレイヤー情報を更新</summary>
    public void UpdatePlayer(RemotePlayerInfo player) => this.Player = player;

    public Task SendMoveAsync(Move move, TimeSpan elapsedTime) =>
        this._transport.SendMoveAsync(move, elapsedTime);

    public Task SendResignAsync() =>
        this._transport.SendResignAsync();

    public Task NotifyGameStartAsync(Turn opponentTurn, Board board, CapturedPieces firstCaptured, CapturedPieces secondCaptured)
    {
        // P2P対戦では特に何もしない（GameStartWithSidesMessageで通知済み）
        return Task.CompletedTask;
    }

    public Task NotifyGameEndAsync(GameStatus result)
    {
        // P2P対戦では特に何もしない
        return Task.CompletedTask;
    }

    public Task NotifyTurnAsync(Board board, Turn currentTurn, CapturedPieces firstCaptured, CapturedPieces secondCaptured)
    {
        // P2P対戦では特に何もしない（相手が自分で指す）
        return Task.CompletedTask;
    }

    /// <summary>イベント購読を解除</summary>
    public void Unsubscribe()
    {
        foreach (var subscription in this._subscriptions) {
            subscription.Dispose();
        }
        this._subscriptions.Clear();
    }

    public void Dispose()
    {
        this.Unsubscribe();
        this._moveReceived.Dispose();
        this._resignReceived.Dispose();
        GC.SuppressFinalize(this);
    }
}
