using Microsoft.JSInterop;

using R3;

using Fu.Core.Abstractions;
using Fu.Core.Models;
using Fu.Core.Models.Dto;
using Fu.Core.Services;
using Fu.Observers;

namespace Fu.Services;

/// <summary>
/// 対局セッションの管理サービス
/// セッションの作成・復元・永続化、状態同期、フェーズ管理を担当
/// </summary>
public class GameSessionService : IDisposable
{
    private readonly IGameTransport _transport;
    private readonly ShogiGameService _gameService;
    private readonly LobbyService _lobbyService;
    private readonly IJSRuntime _js;
    private readonly CompositeDisposable _disposables = [];

    // フェーズ変更通知用Subject
    private readonly Subject<GamePhase> _phaseChanged = new();
    private readonly Subject<Unit> _sessionChanged = new();

    private GamePhase _phase = GamePhase.Disconnected;

    /// <summary>現在のセッション</summary>
    public GameSession Session { get; private set; } = GameSession.Empty;

    /// <summary>現在のフェーズ</summary>
    public GamePhase Phase
    {
        get => this._phase;
        private set
        {
            if (this._phase != value) {
                this._phase = value;
                this._phaseChanged.OnNext(value);
            }
        }
    }

    /// <summary>状態の権威者（ホスト）かどうか</summary>
    public bool IsAuthority => this._lobbyService.IsHost;

    /// <summary>フェーズが変更された時</summary>
    public Observable<GamePhase> PhaseChanged => this._phaseChanged;

    /// <summary>セッションが変更された時</summary>
    public Observable<Unit> SessionChanged => this._sessionChanged;

    public GameSessionService(
        IGameTransport transport,
        ShogiGameService gameService,
        LobbyService lobbyService,
        IJSRuntime js)
    {
        this._transport = transport;
        this._gameService = gameService;
        this._lobbyService = lobbyService;
        this._js = js;

        // 接続状態の変更を監視してフェーズを更新
        this._lobbyService.ConnectionStateChanged
            .Subscribe(this.OnConnectionStateChanged)
            .AddTo(this._disposables);

        // 参加者が来た時（ホストなら状態同期を送信）
        this._lobbyService.ParticipantJoined
            .Subscribe(this.OnParticipantJoined)
            .AddTo(this._disposables);

        // ゲーム状態リクエストの処理を登録
        this._transport.GameStateRequested
            .SubscribeAwait(async (_, _) => await this.HandleGameStateRequestedAsync())
            .AddTo(this._disposables);
    }

    /// <summary>自分のプレイヤーID</summary>
    public PlayerId? MyPlayerId => this._transport.MyPlayerId;

    /// <summary>自分が対局者かどうか</summary>
    public bool IsPlayer => this.Session.IsPlayer(this.MyPlayerId);

    /// <summary>自分が観戦者かどうか</summary>
    public bool IsSpectator => this.Session.IsSpectator(this.MyPlayerId, this._gameService.State.Status);

    /// <summary>自分のTurn</summary>
    public Turn LocalTurn => this.Session.GetLocalTurn(this.MyPlayerId);

    #region フェーズ遷移

    /// <summary>
    /// フェーズを遷移（検証付き）
    /// </summary>
    public bool TransitionTo(GamePhase newPhase)
    {
        if (!this.CanTransitionTo(newPhase)) {
            return false;
        }

        this.Phase = newPhase;
        return true;
    }

    /// <summary>
    /// 指定のフェーズに遷移可能か
    /// </summary>
    public bool CanTransitionTo(GamePhase newPhase)
    {
        return (this.Phase, newPhase) switch {
            // 切断状態から
            (GamePhase.Disconnected, GamePhase.Connecting) => true,

            // 接続中から
            (GamePhase.Connecting, GamePhase.WaitingInLobby) => true,
            (GamePhase.Connecting, GamePhase.Disconnected) => true,

            // ロビー待機中から
            (GamePhase.WaitingInLobby, GamePhase.Playing) => true,
            (GamePhase.WaitingInLobby, GamePhase.Disconnected) => true,

            // 対局中から
            (GamePhase.Playing, GamePhase.GameOver) => true,
            (GamePhase.Playing, GamePhase.Disconnected) => true,

            // 対局終了から
            (GamePhase.GameOver, GamePhase.WaitingInLobby) => true, // 再戦
            (GamePhase.GameOver, GamePhase.Playing) => true, // 分岐再開
            (GamePhase.GameOver, GamePhase.Reviewing) => true,
            (GamePhase.GameOver, GamePhase.Disconnected) => true,

            // 検討中から
            (GamePhase.Reviewing, GamePhase.WaitingInLobby) => true, // 再戦
            (GamePhase.Reviewing, GamePhase.Playing) => true, // 分岐再開
            (GamePhase.Reviewing, GamePhase.Disconnected) => true,

            _ => false
        };
    }

    private void OnConnectionStateChanged(TransportConnectionState state)
    {
        switch (state) {
            case TransportConnectionState.Connecting:
                this.TransitionTo(GamePhase.Connecting);
                break;

            case TransportConnectionState.Connected:
                // 接続完了時、ゲーム状態に応じてフェーズを決定
                var gamePhase = this._gameService.State.Status.ToGamePhase();
                if (gamePhase == GamePhase.Disconnected) {
                    gamePhase = GamePhase.WaitingInLobby;
                }
                this.TransitionTo(gamePhase);
                break;

            case TransportConnectionState.Disconnected:
                this.Phase = GamePhase.Disconnected; // 強制遷移
                break;
        }
    }

    // OnBecameHost: ホストになった時の処理
    // 特別な処理は不要（IsAuthorityプロパティが自動的にtrueになる）

    private void OnParticipantJoined(TransportParticipant participant)
    {
        // ホストの場合、新しい参加者に状態を同期
        if (this.IsAuthority && this.Session.IsConfigured) {
            _ = this.BroadcastStateAsync();
        }
    }

    #endregion

    #region 状態同期

    /// <summary>
    /// 権威者から状態同期をリクエスト（非権威者用）
    /// </summary>
    public async Task RequestStateSyncAsync()
    {
        if (!this.IsAuthority) {
            await this._transport.SendGameStateRequestAsync();
        }
    }

    /// <summary>
    /// 状態を全参加者にブロードキャスト（権威者用）
    /// </summary>
    public async Task BroadcastStateAsync()
    {
        if (this.IsAuthority && this.Session.IsConfigured) {
            await this.SendGameStateSyncAsync();
        }
    }

    private async Task HandleGameStateRequestedAsync()
    {
        // ホストのみが応答
        if (this.IsAuthority) {
            await this.BroadcastStateAsync();
        }
    }

    #endregion

    #region ゲーム開始・終了

    /// <summary>
    /// 新規対局を開始
    /// </summary>
    public async Task StartNewGameAsync(
        PlayerInfo sentePlayer,
        PlayerInfo gotePlayer,
        EvaluationDisplayOptions? options = null)
    {
        this.Session = this.Session.WithPlayers(sentePlayer, gotePlayer, options);

        var localTurn = this.Session.GetLocalTurn(this.MyPlayerId);
        await this._gameService.SetLocalTurnAsync(localTurn);
        await this._gameService.NewGameAsync();

        // 対局者の場合、セッション情報を永続化
        if (localTurn != Turn.None && this._transport.MyPlayerId is not null) {
            await this.SaveSessionAsync();
        }

        // フェーズを対局中に遷移
        this.TransitionTo(GamePhase.Playing);

        this.NotifySessionChanged();

        // 相手に通知
        await this._transport.SendGameStartAsync(
            sentePlayer.PlayerId,
            gotePlayer.PlayerId,
            options);
    }

    /// <summary>
    /// リモートからのゲーム開始を適用
    /// </summary>
    public async Task ApplyRemoteGameStartAsync(GameStartInfo info)
    {
        var firstPlayer = new PlayerInfo(info.SentePlayerId, info.SenteNickname, Turn.First);
        var secondPlayer = new PlayerInfo(info.GotePlayerId, info.GoteNickname, Turn.Second);

        this.Session = this.Session.WithPlayers(firstPlayer, secondPlayer, info.EvaluationOptions);

        var localTurn = this.Session.GetLocalTurn(this.MyPlayerId);
        await this._gameService.SetLocalTurnAsync(localTurn);
        await this._gameService.NewGameAsync();

        // 対局者の場合、セッション情報を永続化
        if (localTurn != Turn.None) {
            await this.SaveSessionAsync();
        }

        // フェーズを対局中に遷移
        this.TransitionTo(GamePhase.Playing);

        this.NotifySessionChanged();
    }

    /// <summary>
    /// ゲーム状態同期を適用（途中参加時）
    /// </summary>
    public async Task ApplyGameStateSyncAsync(GameStateSyncInfo info)
    {
        var firstPlayer = new PlayerInfo(info.SentePlayerId, info.SenteNickname, Turn.First);
        var secondPlayer = new PlayerInfo(info.GotePlayerId, info.GoteNickname, Turn.Second);

        this.Session = this.Session.WithPlayers(firstPlayer, secondPlayer, info.EvaluationOptions);

        var localTurn = this.Session.GetLocalTurn(this.MyPlayerId);
        await this._gameService.SetLocalTurnAsync(localTurn);
        await this._gameService.RestoreStateAsync(info.MoveHistory, info.Status, info.MoveTimes);

        // 対局者かつ対局中ならセッション保存、終了していればクリア
        if (localTurn != Turn.None) {
            if (info.Status == GameStatus.Playing) {
                await this.SaveSessionAsync();
            } else if (info.Status.IsGameOver()) {
                await this.ClearSessionAsync();
            }
        }

        // フェーズを同期された状態に更新
        var newPhase = info.Status.ToGamePhase();
        if (newPhase != GamePhase.Disconnected) {
            this.Phase = newPhase; // 強制更新（同期なので検証不要）
        }

        this.NotifySessionChanged();
    }

    /// <summary>
    /// ゲーム状態同期データを取得
    /// </summary>
    public GameStateForSync GetGameStateForSync() =>
        new(
            this._gameService.State.MoveHistory,
            this.Session.FirstPlayerId ?? new PlayerId(""),
            this.Session.SecondPlayerId ?? new PlayerId(""),
            this.Session.FirstNickname,
            this.Session.SecondNickname,
            this._gameService.State.Status,
            this.Session.EvaluationOptions,
            this._gameService.State.Times
        );

    /// <summary>
    /// ゲーム状態同期を送信
    /// </summary>
    public async Task SendGameStateSyncAsync()
    {
        if (!this.Session.IsConfigured) {
            return;
        }

        await this._transport.SendGameStateSyncAsync(
            this._gameService.State.MoveHistory,
            this.Session.FirstPlayerId!.Value,
            this.Session.SecondPlayerId!.Value,
            this.Session.FirstNickname,
            this.Session.SecondNickname,
            this._gameService.State.Status,
            this.Session.EvaluationOptions,
            this._gameService.State.Times
        );
    }

    /// <summary>
    /// 投了処理
    /// </summary>
    public async Task ResignAsync()
    {
        await this._gameService.ResignAsync();
        await this._transport.SendResignAsync();
        await this.ClearSessionAsync();

        this.TransitionTo(GamePhase.GameOver);
    }

    /// <summary>
    /// リモートからの投了を適用
    /// </summary>
    public async Task ApplyRemoteResignAsync()
    {
        await this._gameService.ResignAsync();
        await this.ClearSessionAsync();

        this.TransitionTo(GamePhase.GameOver);
    }

    /// <summary>
    /// ゲーム終了を通知（詰み等）
    /// </summary>
    public void NotifyGameOver()
    {
        this.TransitionTo(GamePhase.GameOver);
    }

    /// <summary>
    /// 検討モードを開始
    /// </summary>
    public void StartReview()
    {
        this.TransitionTo(GamePhase.Reviewing);
    }

    /// <summary>
    /// ロビーに戻る（再戦準備）
    /// </summary>
    public void ReturnToLobby()
    {
        this.Session = GameSession.Empty;
        this.TransitionTo(GamePhase.WaitingInLobby);
        this.NotifySessionChanged();
    }

    /// <summary>
    /// セッションをリセット
    /// </summary>
    public void Reset()
    {
        this.Session = GameSession.Empty;
        this.Phase = GamePhase.Disconnected;
    }

    #endregion

    #region 永続化

    private async Task SaveSessionAsync()
    {
        try {
            var nickname = await this._js.InvokeAsync<string>("NicknameStorage.load");
            var roomId = this.GetRoomIdFromTransport();
            await this._js.InvokeVoidAsync("GameSession.save", roomId?.AsPrimitive(), nickname, this.MyPlayerId?.AsPrimitive());
        }
        catch {
            // 保存失敗は無視
        }
    }

    /// <summary>
    /// セッションをクリア
    /// </summary>
    public async Task ClearSessionAsync()
    {
        try {
            await this._js.InvokeVoidAsync("GameSession.clear");
        }
        catch {
            // クリア失敗は無視
        }
    }

    private RoomId? GetRoomIdFromTransport()
    {
        // WebRtcServiceにRoomIdがある場合はそれを使用
        if (this._transport is WebRtcService webRtc) {
            return webRtc.RoomId;
        }
        return null;
    }

    #endregion

    private void NotifySessionChanged()
    {
        this._sessionChanged.OnNext(Unit.Default);
    }

    public void Dispose()
    {
        this._phaseChanged.Dispose();
        this._sessionChanged.Dispose();
        this._disposables.Dispose();

        GC.SuppressFinalize(this);
    }
}
