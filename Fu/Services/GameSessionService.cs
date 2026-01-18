using MessagePipe;
using R3;

using Fu.Core.Abstractions;
using Fu.Core.Events;
using Fu.Core.Models;
using Fu.Core.Models.Dto;
using Fu.Core.Services;
using Fu.Observers;

namespace Fu.Services;

/// <summary>
/// 対局セッションの管理サービス
/// 対局スコープの作成・破棄、セッションの作成・復元・永続化、状態同期、フェーズ管理を担当
/// </summary>
public class GameSessionService : IAsyncDisposable
{
    private const string GameSessionKey = "game_session";
    private static readonly TimeSpan SessionMaxAge = TimeSpan.FromHours(24);

    private readonly IGameTransport _transport;
    private readonly IGameScopeFactory _gameScopeFactory;
    private readonly LobbyService _lobbyService;
    private readonly IStorageService _storage;
    private readonly UserSettingsService _userSettings;

    // MessagePipe 購読管理
    private readonly List<IDisposable> _subscriptions = [];

    // フェーズ変更通知用Subject (UI用なのでR3のまま)
    private readonly Subject<GamePhase> _phaseChanged = new();
    private readonly Subject<Unit> _sessionChanged = new();

    private GamePhase _phase = GamePhase.Disconnected;

    /// <summary>現在の対局スコープ（対局中のみ有効）</summary>
    private IGameScope? _currentGameScope;

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

    /// <summary>対局スコープが有効かどうか</summary>
    public bool HasActiveGameScope => this._currentGameScope?.IsActive ?? false;

    /// <summary>現在の対局スコープ内のゲームサービス（対局中のみ有効）</summary>
    public ShogiGameService? GameService => this._currentGameScope?.GetServiceOrDefault<ShogiGameService>();

    /// <summary>状態の権威者（ホスト）かどうか</summary>
    public bool IsAuthority => this._lobbyService.IsHost;

    /// <summary>フェーズが変更された時</summary>
    public Observable<GamePhase> PhaseChanged => this._phaseChanged;

    /// <summary>セッションが変更された時</summary>
    public Observable<Unit> SessionChanged => this._sessionChanged;

    public GameSessionService(
        IGameTransport transport,
        IGameScopeFactory gameScopeFactory,
        LobbyService lobbyService,
        IStorageService storage,
        UserSettingsService userSettings,
        ISubscriber<TransportConnectionStateChangedEvent> connectionStateChanged,
        ISubscriber<TransportParticipantJoinedEvent> participantJoined,
        ISubscriber<TransportGameStateRequestedEvent> gameStateRequested)
    {
        this._transport = transport;
        this._gameScopeFactory = gameScopeFactory;
        this._lobbyService = lobbyService;
        this._storage = storage;
        this._userSettings = userSettings;

        // 接続状態の変更を監視してフェーズを更新
        this._subscriptions.Add(
            connectionStateChanged.Subscribe(e => this.OnConnectionStateChanged(e.State)));

        // 参加者が来た時（ホストなら状態同期を送信）
        this._subscriptions.Add(
            participantJoined.Subscribe(e => this.OnParticipantJoined(e.Participant)));

        // ゲーム状態リクエストの処理を登録
        this._subscriptions.Add(
            gameStateRequested.Subscribe(async _ => await this.HandleGameStateRequestedAsync()));
    }

    /// <summary>自分のプレイヤーID</summary>
    public PlayerId? MyPlayerId => this._transport.MyPlayerId;

    /// <summary>自分が対局者かどうか</summary>
    public bool IsPlayer => this.Session.IsPlayer(this.MyPlayerId);

    /// <summary>自分が観戦者かどうか</summary>
    public bool IsSpectator => this.GameService is { } gs
        ? this.Session.IsSpectator(this.MyPlayerId, gs.State.Status)
        : true;

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
                var gamePhase = this.GameService?.State.Status.ToGamePhase() ?? GamePhase.Disconnected;
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

    private void OnParticipantJoined(TransportParticipantInfo participant)
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

    #region 対局スコープ管理

    /// <summary>
    /// 新しい対局スコープを作成（既存のスコープがあればDisposeする）
    /// </summary>
    private async Task<IGameScope> CreateNewGameScopeAsync()
    {
        // 既存のスコープをDispose
        if (this._currentGameScope is not null) {
            await this._currentGameScope.DisposeAsync();
        }

        // 新しいスコープを作成
        this._currentGameScope = this._gameScopeFactory.CreateScope();
        return this._currentGameScope;
    }

    /// <summary>
    /// 現在の対局スコープを破棄
    /// </summary>
    private async Task DisposeCurrentGameScopeAsync()
    {
        if (this._currentGameScope is not null) {
            await this._currentGameScope.DisposeAsync();
            this._currentGameScope = null;
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
        // 新しい対局スコープを作成
        var scope = await this.CreateNewGameScopeAsync();
        var gameService = scope.GetService<ShogiGameService>();

        // 明示的に新しいセッションを作成（前の対局情報をクリア）
        this.Session = GameSession.Empty.WithPlayers(sentePlayer, gotePlayer, options);

        var localTurn = this.Session.GetLocalTurn(this.MyPlayerId);
        await gameService.SetLocalTurnAsync(localTurn);
        await gameService.NewGameAsync();

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
    public async Task ApplyRemoteGameStartAsync(TransportGameStartInfo info)
    {
        // 新しい対局スコープを作成
        var scope = await this.CreateNewGameScopeAsync();
        var gameService = scope.GetService<ShogiGameService>();

        var firstPlayer = new PlayerInfo(info.SentePlayerId, info.SenteNickname, Turn.First);
        var secondPlayer = new PlayerInfo(info.GotePlayerId, info.GoteNickname, Turn.Second);

        // 明示的に新しいセッションを作成（前の対局情報をクリア）
        this.Session = GameSession.Empty.WithPlayers(firstPlayer, secondPlayer, info.EvaluationOptions);

        var localTurn = this.Session.GetLocalTurn(this.MyPlayerId);
        await gameService.SetLocalTurnAsync(localTurn);
        await gameService.NewGameAsync();

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
    public async Task ApplyGameStateSyncAsync(TransportGameStateSyncInfo info)
    {
        // 新しい対局スコープを作成
        var scope = await this.CreateNewGameScopeAsync();
        var gameService = scope.GetService<ShogiGameService>();

        var firstPlayer = new PlayerInfo(info.SentePlayerId, info.SenteNickname, Turn.First);
        var secondPlayer = new PlayerInfo(info.GotePlayerId, info.GoteNickname, Turn.Second);

        // 明示的に新しいセッションを作成（前の対局情報をクリア）
        this.Session = GameSession.Empty.WithPlayers(firstPlayer, secondPlayer, info.EvaluationOptions);

        var localTurn = this.Session.GetLocalTurn(this.MyPlayerId);
        await gameService.SetLocalTurnAsync(localTurn);
        await gameService.RestoreStateAsync(info.MoveHistory, info.Status, info.MoveTimes);

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
    public GameStateForSync? GetGameStateForSync()
    {
        if (this.GameService is not { } gs) {
            return null;
        }
        return new(
            gs.State.MoveHistory,
            this.Session.FirstPlayerId ?? new PlayerId(""),
            this.Session.SecondPlayerId ?? new PlayerId(""),
            this.Session.FirstNickname,
            this.Session.SecondNickname,
            gs.State.Status,
            this.Session.EvaluationOptions,
            gs.State.Times
        );
    }

    /// <summary>
    /// ゲーム状態同期を送信
    /// </summary>
    public async Task SendGameStateSyncAsync()
    {
        if (!this.Session.IsConfigured || this.GameService is not { } gs) {
            return;
        }

        await this._transport.SendGameStateSyncAsync(
            gs.State.MoveHistory,
            this.Session.FirstPlayerId!.Value,
            this.Session.SecondPlayerId!.Value,
            this.Session.FirstNickname,
            this.Session.SecondNickname,
            gs.State.Status,
            this.Session.EvaluationOptions,
            gs.State.Times
        );
    }

    /// <summary>
    /// 投了処理
    /// </summary>
    public async Task ResignAsync()
    {
        if (this.GameService is not { } gs) {
            return;
        }
        await gs.ResignAsync();
        await this._transport.SendResignAsync();
        await this.ClearSessionAsync();

        this.TransitionTo(GamePhase.GameOver);
    }

    /// <summary>
    /// リモートからの投了を適用
    /// </summary>
    public async Task ApplyRemoteResignAsync()
    {
        if (this.GameService is { } gs) {
            await gs.ResignAsync();
        }
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
    public async Task ReturnToLobbyAsync()
    {
        // 対局スコープを破棄
        await this.DisposeCurrentGameScopeAsync();

        this.Session = GameSession.Empty;
        this.TransitionTo(GamePhase.WaitingInLobby);
        this.NotifySessionChanged();
    }

    /// <summary>
    /// セッションをリセット
    /// </summary>
    public async Task ResetAsync()
    {
        // 対局スコープを破棄
        await this.DisposeCurrentGameScopeAsync();

        this.Session = GameSession.Empty;
        this.Phase = GamePhase.Disconnected;
    }

    #endregion

    #region 永続化

    private async Task SaveSessionAsync()
    {
        var roomId = this._transport.RoomId;
        if (roomId is null || this.MyPlayerId is null) {
            return;
        }
        await this._storage.SetAsync(GameSessionKey, new StoredGameSession(
            roomId.Value.AsPrimitive(),
            this._userSettings.Nickname,
            this.MyPlayerId.Value.AsPrimitive(),
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        ), StorageScope.Session);
    }

    /// <summary>
    /// ゲームセッションを読み込み（期限切れの場合はnull）
    /// </summary>
    public async ValueTask<GameSessionInfo?> LoadGameSessionAsync()
    {
        var session = await this._storage.GetAsync<StoredGameSession>(GameSessionKey, StorageScope.Session);
        if (session is null) {
            return null;
        }

        // 期限切れチェック
        var timestamp = DateTimeOffset.FromUnixTimeMilliseconds(session.Timestamp);
        if (DateTimeOffset.UtcNow - timestamp > SessionMaxAge) {
            await this.ClearSessionAsync();
            return null;
        }

        return new GameSessionInfo(
            new RoomId(session.RoomId),
            session.Nickname,
            new PlayerId(session.PeerId)
        );
    }

    /// <summary>
    /// セッションをクリア
    /// </summary>
    public async Task ClearSessionAsync() =>
        await this._storage.RemoveAsync(GameSessionKey, StorageScope.Session);

    /// <summary>保存用の内部型</summary>
    private sealed record StoredGameSession(string RoomId, string Nickname, string PeerId, long Timestamp);

    #endregion

    private void NotifySessionChanged()
    {
        this._sessionChanged.OnNext(Unit.Default);
    }

    public async ValueTask DisposeAsync()
    {
        // 対局スコープを破棄
        await this.DisposeCurrentGameScopeAsync();

        // MessagePipe購読を解除
        foreach (var subscription in this._subscriptions) {
            subscription.Dispose();
        }
        this._subscriptions.Clear();

        this._phaseChanged.Dispose();
        this._sessionChanged.Dispose();

        GC.SuppressFinalize(this);
    }
}
