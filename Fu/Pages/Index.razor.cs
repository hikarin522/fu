using MessagePipe;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

using R3;

using Fu.Core;
using Fu.Core.Abstractions;
using Fu.Core.Events;
using Fu.Core.Models;
using Fu.Core.Models.Dto;
using Fu.Core.Services;
using Fu.Services;

namespace Fu.Pages;

public partial class Index : IAsyncDisposable
{
    // MessagePipe購読管理
    private readonly List<IDisposable> _subscriptions = [];

    // R3購読管理（エンジンサービス等）
    private readonly CompositeDisposable _r3Disposables = [];

    [Inject] private ITransportSender TransportSender { get; set; } = null!;
    [Inject] private ITransportConnection TransportConnection { get; set; } = null!;
    [Inject] private ShogiEngineService EngineService { get; set; } = null!;
    [Inject] private GameSessionService SessionService { get; set; } = null!;
    [Inject] private UserSettingsService UserSettings { get; set; } = null!;
    [Inject] private LobbyService Lobby { get; set; } = null!;
    [Inject] private IKifExporter KifExporter { get; set; } = null!;
    [Inject] private IJSRuntime JS { get; set; } = null!;
    [Inject] private NavigationManager Navigation { get; set; } = null!;

    // MessagePipe Subscribers（ゲームイベント）
    [Inject] private ISubscriber<GameStateChangedEvent> GameStateChangedSubscriber { get; set; } = null!;
    [Inject] private ISubscriber<BranchResumedEvent> BranchResumedSubscriber { get; set; } = null!;
    [Inject] private ISubscriber<ReviewStartedEvent> ReviewStartedSubscriber { get; set; } = null!;
    [Inject] private ISubscriber<ReviewMoveEvent> ReviewMoveSubscriber { get; set; } = null!;

    // MessagePipe Subscribers（トランスポートイベント）
    [Inject] private ISubscriber<TransportReadyEvent> TransportReadySubscriber { get; set; } = null!;
    [Inject] private ISubscriber<TransportBecameHostEvent> TransportBecameHostSubscriber { get; set; } = null!;
    [Inject] private ISubscriber<TransportMoveReceivedEvent> TransportMoveReceivedSubscriber { get; set; } = null!;
    [Inject] private ISubscriber<TransportGameStartReceivedEvent> TransportGameStartReceivedSubscriber { get; set; } = null!;
    [Inject] private ISubscriber<TransportGameStartWithPlayersReceivedEvent> TransportGameStartWithPlayersReceivedSubscriber { get; set; } = null!;
    [Inject] private ISubscriber<TransportResignReceivedEvent> TransportResignReceivedSubscriber { get; set; } = null!;
    [Inject] private ISubscriber<TransportGameStateSyncReceivedEvent> TransportGameStateSyncReceivedSubscriber { get; set; } = null!;
    [Inject] private ISubscriber<TransportBranchResumeReceivedEvent> TransportBranchResumeReceivedSubscriber { get; set; } = null!;
    [Inject] private ISubscriber<TransportRematchReceivedEvent> TransportRematchReceivedSubscriber { get; set; } = null!;
    [Inject] private ISubscriber<TransportReviewStartReceivedEvent> TransportReviewStartReceivedSubscriber { get; set; } = null!;
    [Inject] private ISubscriber<TransportReviewMoveReceivedEvent> TransportReviewMoveReceivedSubscriber { get; set; } = null!;

    [Parameter] public string? RoomIdParam { get; set; }

    /// <summary>現在の対局スコープ内のゲームサービス（対局中のみ有効）</summary>
    private ShogiGameService? GameService => this.SessionService.GameService;

    /// <summary>対局スコープがアクティブかどうか</summary>
    private bool HasActiveGame => this.GameService is not null;

    /// <summary>ゲームサービスを取得（対局中でなければInvalidOperationException）</summary>
    private ShogiGameService RequireGameService =>
        this.GameService ?? throw new InvalidOperationException("Game service is not available");

    /// <summary>現在のゲーム状態（対局中でない場合はデフォルト状態を返す）</summary>
    private GameState CurrentGameState => this.GameService?.State ?? GameState.Default;

    // 観戦者・検討中用の盤面反転状態
    private bool SpectatorFlipped { get; set; }

    // 対局者は自分が後手なら反転（検討中は手動切り替え）、観戦者は手動切り替え
    private bool IsFlipped => this.GameService is { } gs && this.IsPlayer && !gs.State.IsReviewing
        ? gs.State.LocalTurn == Turn.Second
        : this.SpectatorFlipped;

    private string? InitError { get; set; }
    private string BaseUrl => this.Navigation.BaseUri;

    // 対局者情報（SessionServiceから取得）
    private PlayerId? FirstPlayerId => this.SessionService.Session.FirstPlayerId;
    private PlayerId? SecondPlayerId => this.SessionService.Session.SecondPlayerId;
    private string FirstNickname => this.SessionService.Session.FirstNickname;
    private string SecondNickname => this.SessionService.Session.SecondNickname;

    // 新規対局ダイアログ
    private bool ShowNewGameDialog { get; set; }
    private PlayerId? SelectedFirstPlayerId { get; set; }
    private PlayerId? SelectedSecondPlayerId { get; set; }

    // Blazor select用の文字列バインディング
    private string SelectedFirstPlayerIdString
    {
        get => this.SelectedFirstPlayerId?.AsPrimitive() ?? "";
        set => this.SelectedFirstPlayerId = string.IsNullOrEmpty(value) ? null : new PlayerId(value);
    }
    private string SelectedSecondPlayerIdString
    {
        get => this.SelectedSecondPlayerId?.AsPrimitive() ?? "";
        set => this.SelectedSecondPlayerId = string.IsNullOrEmpty(value) ? null : new PlayerId(value);
    }

    // 対局者向け評価値表示オプション（ダイアログ用）
    private bool OptShowAdvantage { get; set; }
    private bool OptShowEvaluationValue { get; set; }
    private bool OptShowHasMate { get; set; }
    private bool OptShowMateCount { get; set; }

    // 現在のゲームに適用されている評価値表示オプション（SessionServiceから取得）
    private EvaluationDisplayOptions CurrentEvaluationOptions => this.SessionService.Session.EvaluationOptions;

    // 役割判定（SessionServiceから取得）
    private bool IsPlayer => this.SessionService.IsPlayer;
    private bool IsSpectator => this.SessionService.IsSpectator;
    private bool IsGameEnded => this.GameService?.State.Status.IsGameOver() ?? false;

    // 参加者一覧表示用（SessionServiceから取得）
    private string GetParticipantRole(PlayerId playerId) =>
        this.SessionService.Session.GetRole(playerId);

    private int GetParticipantSortOrder(PlayerId playerId) =>
        this.SessionService.Session.GetSortOrder(playerId);

    private IEnumerable<(TransportParticipantInfo Participant, string Role, bool IsConnected, bool IsMe)> GetAllParticipantsInfo()
    {
        var connectedPlayerIds = this.Lobby.Participants.Select(p => p.PlayerId).ToHashSet();
        var myPlayerId = this.Lobby.MyPlayerId;

        // 接続中の参加者（対局者優先でソート）
        foreach (var p in this.Lobby.Participants.OrderBy(p => this.GetParticipantSortOrder(p.PlayerId))) {
            yield return (p, this.GetParticipantRole(p.PlayerId), true, p.PlayerId == myPlayerId);
        }

        // 切断された対局者を表示（ゲーム中の場合のみ）
        var status = this.GameService?.State.Status ?? GameStatus.WaitingForConnection;
        if (status is not (GameStatus.Playing or GameStatus.Reviewing)) {
            yield break;
        }

        if (this.FirstPlayerId is { } firstId && !connectedPlayerIds.Contains(firstId)) {
            yield return (new TransportParticipantInfo(firstId, this.FirstNickname, false), "先手", false, false);
        }
        if (this.SecondPlayerId is { } secondId && !connectedPlayerIds.Contains(secondId)) {
            yield return (new TransportParticipantInfo(secondId, this.SecondNickname, false), "後手", false, false);
        }
    }

    // 評価値表示（観戦者・対局終了後・検討中は常に全表示）
    private bool CanShowAllEvaluation => this.IsSpectator || this.IsGameEnded || (this.GameService?.State.IsReviewing ?? false);
    private bool ShowAdvantage => this.CanShowAllEvaluation || (this.IsPlayer && this.CurrentEvaluationOptions.ShowAdvantage);
    private bool ShowEvaluationValue => this.CanShowAllEvaluation || (this.IsPlayer && this.CurrentEvaluationOptions.ShowEvaluationValue);
    private bool ShowHasMate => this.CanShowAllEvaluation || (this.IsPlayer && this.CurrentEvaluationOptions.ShowHasMate);
    private bool ShowMateCount => this.CanShowAllEvaluation || (this.IsPlayer && this.CurrentEvaluationOptions.ShowMateCount);
    private bool ShowEvaluation => this.ShowAdvantage || this.ShowEvaluationValue || this.ShowHasMate;
    private bool ShowCandidateArrows => this.CanShowAllEvaluation;

    // 評価表示用の盤面（検討モード時は表示中の盤面）
    private Board? DisplayBoard => this.GameService is { } gs && gs.State.IsReviewing
        ? gs.GetBoardAtMove(gs.State.DisplayMoveIndex).board
        : this.GameService?.State.Board;

    // Cross-Origin Isolationのリロードが必要かどうか
    private bool NeedsReload { get; set; }

    // 通知音設定（UserSettingsから取得）
    private bool SoundEnabled => this.UserSettings.SoundEnabled;

    // タイマー更新用
    private Timer? _uiTimer;
    private TimeSpan _currentTurnElapsed;
    private bool _disposed;

    protected override async Task OnInitializedAsync()
    {
        try {
            // Cross-Origin Isolationのチェック（Service Workerが有効か）
            await this.CheckAndReloadForCrossOriginIsolationAsync();
            if (this.NeedsReload) {
                return; // リロード中なので以降の初期化をスキップ
            }

            await this.Lobby.InitializeAsync();

            // 通知音設定を読み込み
            await this.UserSettings.LoadAsync();

            this.SubscribeToEvents();

            // エンジン初期化（バックグラウンドで実行）
            _ = this.InitializeEngineAsync();

            // UIタイマー開始（1秒ごとに更新）
            this._uiTimer = new Timer(this.OnTimerTick, null, TimeSpan.Zero, TimeSpan.FromSeconds(1));
        }
        catch (Exception ex) {
            this.InitError = ex.Message;
        }
    }

    private void OnTimerTick(object? state)
    {
        // Dispose済みまたはゲームサービス未初期化なら何もしない
        if (this._disposed || this.GameService is not { } gs) {
            return;
        }

        // 対局中のみ更新
        if (gs.State.Status == GameStatus.Playing && !gs.State.IsReviewing) {
            this._currentTurnElapsed = gs.GetCurrentTurnElapsed();
            _ = this.InvokeAsync(this.StateHasChanged);
        }
    }

    private async Task OnDataChannelReadyAsync()
    {
        await this.InvokeAsync(async () => {
            if (!this.SessionService.IsAuthority) {
                // 非権威者（ゲスト）の場合は現在のゲーム状態をリクエスト
                await this.SessionService.RequestStateSyncAsync();
            }
            // 権威者（ホスト）の場合は空の盤面を表示（「新規対局」ボタンから対局設定を開く）
            this.StateHasChanged();
        });
    }

    private async Task OnRemoteGameStartAsync()
    {
        // 古いプロトコル用（gameStartWithPlayersを使う新しいプロトコルでは呼ばれない）
        if (this.GameService is not { } gs) {
            return;
        }
        await gs.NewGameAsync();
        await this.InvokeAsync(this.StateHasChanged);
    }

    private async Task OnGameStartWithPlayersAsync(TransportGameStartInfo info)
    {
        await this.InvokeAsync(async () => {
            await this.SessionService.ApplyRemoteGameStartAsync(info);

            // 自分が先手（最初の手番）なら通知音を鳴らす
            if (this.SessionService.LocalTurn == Turn.First) {
                await this.PlayTurnNotificationAsync();
            }

            this.ShowNewGameDialog = false;
            this.StateHasChanged();
        });
    }

    // JS interop用にPlayerIdを文字列に変換
    private static string? PlayerIdToString(PlayerId? playerId) => playerId?.AsPrimitive();

    private async Task OnMoveMade(Move move)
    {
        // 自分の消費時間を取得して送信
        var elapsedTime = this.RequireGameService.LastMoveElapsedTime;
        await this.TransportSender.SendMoveAsync(move, elapsedTime);
        this.StateHasChanged();
    }

    private async Task OnRemoteMoveReceivedAsync(Move move, TimeSpan elapsedTime)
    {
        var gs = this.RequireGameService;
        await gs.ApplyRemoteMoveAsync(move, elapsedTime);

        // 自分の手番になったら通知音を鳴らす
        if (this.IsPlayer && gs.State.IsMyTurn) {
            await this.PlayTurnNotificationAsync();
        }

        await this.InvokeAsync(this.StateHasChanged);
    }

    private Task PlayTurnNotificationAsync() =>
        this.UserSettings.PlayTurnNotificationAsync();

    private Task ToggleSoundAsync() =>
        this.UserSettings.ToggleSoundAsync();

    private ValueTask<GameSessionInfo?> LoadGameSessionAsync() =>
        this.SessionService.LoadGameSessionAsync();

    private Task ClearGameSessionAsync() =>
        this.SessionService.ClearSessionAsync();

    private async ValueTask OnGameStateChangedAsync()
    {
        await this.InvokeAsync(this.StateHasChanged);

        // 評価値表示が有効な場合はリクエスト
        if (this.ShowEvaluation && this.EngineService.IsAvailable) {
            await this.RequestEvaluationAsync();
        }
    }

    private void OpenNewGameDialog()
    {
        // 参加者が2人の場合、デフォルトで先手・後手を割り当て
        var participants = this.Lobby.Participants.ToList();
        if (participants.Count >= 2) {
            // 自分を先手、相手を後手にデフォルト設定
            var me = participants.Where(p => p.PlayerId == this.Lobby.MyPlayerId).Select(p => (TransportParticipantInfo?)p).FirstOrDefault();
            var opponent = participants.Where(p => p.PlayerId != this.Lobby.MyPlayerId).Select(p => (TransportParticipantInfo?)p).FirstOrDefault();
            this.SelectedFirstPlayerId = me?.PlayerId ?? participants[0].PlayerId;
            this.SelectedSecondPlayerId = opponent?.PlayerId ?? participants[1].PlayerId;
        } else {
            this.SelectedFirstPlayerId = null;
            this.SelectedSecondPlayerId = null;
        }
        this.OptShowAdvantage = false;
        this.OptShowEvaluationValue = false;
        this.OptShowHasMate = false;
        this.OptShowMateCount = false;
        this.ShowNewGameDialog = true;
    }

    private async Task StartNewGameAsync()
    {
        if (this.SelectedFirstPlayerId is not { } firstId || this.SelectedSecondPlayerId is not { } secondId) {
            return;
        }

        var firstParticipant = this.Lobby.Participants.FirstOrDefault(p => p.PlayerId == firstId);
        var secondParticipant = this.Lobby.Participants.FirstOrDefault(p => p.PlayerId == secondId);

        if (firstParticipant.PlayerId == default || secondParticipant.PlayerId == default) {
            return;
        }

        var options = new EvaluationDisplayOptions(
            this.OptShowAdvantage,
            this.OptShowEvaluationValue,
            this.OptShowHasMate,
            this.OptShowMateCount);

        var firstPlayer = new PlayerInfo(firstId, firstParticipant.Nickname, Turn.First);
        var secondPlayer = new PlayerInfo(secondId, secondParticipant.Nickname, Turn.Second);

        await this.SessionService.StartNewGameAsync(firstPlayer, secondPlayer, options);

        // 自分が先手（最初の手番）なら通知音を鳴らす
        if (this.SessionService.LocalTurn == Turn.First) {
            await this.PlayTurnNotificationAsync();
        }

        this.ShowNewGameDialog = false;
        this.StateHasChanged();
    }

    private async Task ResignAsync()
    {
        await this.SessionService.ResignAsync();
    }

    private async Task OnRemoteResignReceivedAsync()
    {
        await this.SessionService.ApplyRemoteResignAsync();
        await this.InvokeAsync(this.StateHasChanged);
    }

    private async Task OnGameStateRequestedAsync()
    {
        // 権威者（ホスト）がゲーム状態リクエストを受信したら、現在の状態を送信
        await this.SessionService.BroadcastStateAsync();
    }

    private async Task OnGameStateSyncReceivedAsync(TransportGameStateSyncInfo info)
    {
        await this.InvokeAsync(async () => {
            await this.SessionService.ApplyGameStateSyncAsync(info);
            this.ShowNewGameDialog = false;
            this.StateHasChanged();
        });
    }

    private async Task OnBranchResumeReceivedAsync(IReadOnlyList<Move> moveHistory)
    {
        var gs = this.RequireGameService;
        await gs.ApplyBranchResumeAsync(moveHistory);

        // 自分の手番になったら通知音を鳴らす
        if (this.IsPlayer && gs.State.IsMyTurn) {
            await this.PlayTurnNotificationAsync();
        }

        await this.InvokeAsync(this.StateHasChanged);
    }

    private async ValueTask OnBranchResumedAsync(IReadOnlyList<Move> moveHistory)
    {
        // 分岐再開を相手に通知
        await this.TransportSender.SendBranchResumeAsync(moveHistory);
    }

    private async Task OnRematchReceivedAsync(IReadOnlyList<Move> moveHistory)
    {
        // ゲーム状態がPlayingに戻るので評価値表示は自動的にリセットされる
        var gs = this.RequireGameService;
        await gs.ApplyRematchAsync(moveHistory);

        // 自分の手番になったら通知音を鳴らす
        if (this.IsPlayer && gs.State.IsMyTurn) {
            await this.PlayTurnNotificationAsync();
        }

        await this.InvokeAsync(this.StateHasChanged);
    }

    private async Task InitializeEngineAsync()
    {
        try {
            await this.EngineService.InitializeAsync();
            // R3 購読は SubscribeToEvents で行う
        }
        catch {
            // エンジン初期化失敗は無視
        }
    }

    private async Task OnEvaluationUpdatedAsync()
    {
        // 詰みが見つかった場合、詰み手順をブランチとして追加（対局中の対戦者以外）
        if (this.CanShowAllEvaluation &&
            this.EngineService.MateIn is not null &&
            !string.IsNullOrEmpty(this.EngineService.PrincipalVariation) &&
            this.GameService is { } gs) {
            await gs.AddMateSequenceBranchAsync(
                this.EngineService.PrincipalVariation,
                gs.State.DisplayMoveIndex);
        }

        await this.InvokeAsync(this.StateHasChanged);
    }

    private async Task RequestEvaluationAsync()
    {
        if (!this.ShowEvaluation || !this.EngineService.IsAvailable || this.GameService is not { } gs) {
            return;
        }

        var state = gs.State;
        var (board, firstCaptured, secondCaptured, currentTurn) = state.IsReviewing
            ? gs.GetBoardAtMove(state.DisplayMoveIndex)
            : (state.Board, state.FirstCaptured, state.SecondCaptured, state.CurrentTurn);

        // 観戦者または対局終了後は候補手を3つ表示
        var multiPv = this.ShowCandidateArrows ? 3 : 1;

        // depth: 0 = 無限探索（局面が変わるまで継続）
        await this.EngineService.AnalyzePositionAsync(
            board,
            currentTurn,
            firstCaptured,
            secondCaptured,
            gs.MoveTree,
            multiPv: multiPv);
    }

    private void ToggleBoardFlip()
    {
        this.SpectatorFlipped = !this.SpectatorFlipped;
    }

    private async Task DownloadKifAsync()
    {
        // 選択中のブランチの棋譜をダウンロード
        var gs = this.RequireGameService;
        var moves = gs.State.DisplayBranchHistory;
        var kif = this.KifExporter.Export(
            moves,
            gs.State.Status,
            this.FirstNickname,
            this.SecondNickname,
            gs.State.Times);
        var fileName = $"shogi_{DateTime.Now:yyyyMMdd_HHmmss}.kif";
        await this.JS.InvokeVoidAsync("downloadTextFile", fileName, kif);
    }

    private Task GoBackAsync() => this.RequireGameService.GoBackAsync();

    private Task GoForwardAsync() => this.RequireGameService.GoForwardAsync();

    private Task GoToPreviousBranchAsync() => this.RequireGameService.GoToPreviousBranchAsync();

    private Task GoToNextBranchAsync() => this.RequireGameService.GoToNextBranchAsync();

    private Task GoForwardBranchAsync(int branchIndex) => this.RequireGameService.GoForwardBranchAsync(branchIndex);

    private Task GoToLatestAsync() => this.RequireGameService.GoToLatestAsync();

    private Task GoToMoveAsync(int moveIndex) => this.RequireGameService.SetViewingMoveIndexAsync(moveIndex);

    private Task ResumeFromBranchAsync() => this.RequireGameService.ResumeFromBranchAsync();

    private async Task RematchFromCurrentAsync()
    {
        // 現在の位置から再戦（ゲーム状態がPlayingに戻るので評価値表示は自動的にリセットされる）
        var gs = this.RequireGameService;
        await gs.RematchFromCurrentPositionAsync();

        // 相手に再戦を通知
        await this.TransportSender.SendRematchAsync(gs.State.MoveHistory);
    }

    private Task OnTreeNodeSelected(MoveNode? node) => this.RequireGameService.GoToNodeAsync(node);

    // 棋譜ツリーからの検討・再戦
    private async Task OnReviewFromNodeAsync(MoveNode? node)
    {
        // まずそのノードに移動してから検討開始
        var gs = this.RequireGameService;
        await gs.GoToNodeAsync(node);
        await gs.StartReviewFromCurrentPositionAsync();
    }

    private async Task OnRematchFromNodeAsync(MoveNode? node)
    {
        // まずそのノードに移動してから再戦
        var gs = this.RequireGameService;
        await gs.GoToNodeAsync(node);
        await gs.RematchFromCurrentPositionAsync();
        await this.TransportSender.SendRematchAsync(gs.State.MoveHistory);
    }

    // 検討モード関連
    private async Task StartReviewFromCurrentAsync()
    {
        await this.RequireGameService.StartReviewFromCurrentPositionAsync();
        // 相手に検討モード開始を通知（イベントハンドラで行う）
    }

    private async ValueTask OnReviewStartedAsync(IReadOnlyList<Move> moveHistory)
    {
        // 検討モード開始を相手に通知
        await this.TransportSender.SendReviewStartAsync(moveHistory);
    }

    private async ValueTask OnReviewMoveAsync(Move move)
    {
        // 検討モードでの手を相手に通知
        await this.TransportSender.SendReviewMoveAsync(move);
    }

    private async Task OnReviewStartReceivedAsync(IReadOnlyList<Move> moveHistory)
    {
        await this.RequireGameService.ApplyReviewStartAsync(moveHistory);
        await this.InvokeAsync(this.StateHasChanged);
    }

    private async Task OnReviewMoveReceivedAsync(Move move)
    {
        await this.RequireGameService.ApplyReviewMoveAsync(move);
        await this.InvokeAsync(this.StateHasChanged);
    }

    /// <summary>Cross-Origin Isolationが無効な場合、Service Workerを有効にするためにリロードする</summary>
    private async Task CheckAndReloadForCrossOriginIsolationAsync()
    {
        var shouldReload = await this.JS.InvokeAsync<bool>("eval", """
            (function() {
                if (window.crossOriginIsolated === true) {
                    sessionStorage.removeItem('coi-reload-count');
                    return false;
                }
                const key = 'coi-reload-count';
                const count = parseInt(sessionStorage.getItem(key) || '0');
                if (count < 2) {
                    sessionStorage.setItem(key, (count + 1).toString());
                    return true;
                }
                return false;
            })()
            """
        );

        if (shouldReload) {
            this.NeedsReload = true;
            await this.JS.InvokeVoidAsync("location.reload");
        }
    }

    /// <summary>プレイヤーの累計時間を取得（現在の手番の経過時間を含む）</summary>
    private TimeSpan GetPlayerTime(Turn player)
    {
        if (this.GameService is not { } gs) {
            return TimeSpan.Zero;
        }

        var state = gs.State;
        var totalTime = player == Turn.First
            ? state.FirstTotalTime
            : state.SecondTotalTime;

        // 対局中で、このプレイヤーが現在の手番なら経過時間を加算
        if (state.Status == GameStatus.Playing &&
            !state.IsReviewing &&
            state.CurrentTurn == player) {
            totalTime += this._currentTurnElapsed;
        }

        return totalTime;
    }

    private static string FormatTime(TimeSpan time) => TimeFormatHelper.FormatDisplay(time);

    public async ValueTask DisposeAsync()
    {
        this._disposed = true;

        if (this._uiTimer is not null) {
            await this._uiTimer.DisposeAsync();
        }

        this.UnsubscribeFromEvents();
        await this.EngineService.DisposeAsync();

        // IGameTransportのDisposeは別途管理
        if (this.TransportConnection is IAsyncDisposable disposable) {
            await disposable.DisposeAsync();
        }

        GC.SuppressFinalize(this);
    }

    private void SubscribeToEvents()
    {
        // トランスポートイベント - MessagePipe
        this._subscriptions.Add(
            this.TransportReadySubscriber.Subscribe(async _ => await this.OnDataChannelReadyAsync()));

        this._subscriptions.Add(
            this.TransportBecameHostSubscriber.Subscribe(_ => Console.WriteLine("Became host")));

        this._subscriptions.Add(
            this.TransportMoveReceivedSubscriber.Subscribe(async e => await this.OnRemoteMoveReceivedAsync(e.Move, e.Elapsed)));

        this._subscriptions.Add(
            this.TransportGameStartReceivedSubscriber.Subscribe(async _ => await this.OnRemoteGameStartAsync()));

        this._subscriptions.Add(
            this.TransportGameStartWithPlayersReceivedSubscriber.Subscribe(async e => await this.OnGameStartWithPlayersAsync(e.Info)));

        this._subscriptions.Add(
            this.TransportResignReceivedSubscriber.Subscribe(async _ => await this.OnRemoteResignReceivedAsync()));

        this._subscriptions.Add(
            this.TransportGameStateSyncReceivedSubscriber.Subscribe(async e => await this.OnGameStateSyncReceivedAsync(e.Info)));

        this._subscriptions.Add(
            this.TransportBranchResumeReceivedSubscriber.Subscribe(async e => await this.OnBranchResumeReceivedAsync(e.MoveHistory)));

        this._subscriptions.Add(
            this.TransportRematchReceivedSubscriber.Subscribe(async e => await this.OnRematchReceivedAsync(e.MoveHistory)));

        this._subscriptions.Add(
            this.TransportReviewStartReceivedSubscriber.Subscribe(async e => await this.OnReviewStartReceivedAsync(e.MoveHistory)));

        this._subscriptions.Add(
            this.TransportReviewMoveReceivedSubscriber.Subscribe(async e => await this.OnReviewMoveReceivedAsync(e.Move)));

        // ゲームサービスイベント - MessagePipe
        this._subscriptions.Add(
            this.GameStateChangedSubscriber.Subscribe(async _ => await this.OnGameStateChangedAsync()));
        this._subscriptions.Add(
            this.BranchResumedSubscriber.Subscribe(async e => await this.OnBranchResumedAsync(e.MoveHistory)));
        this._subscriptions.Add(
            this.ReviewStartedSubscriber.Subscribe(async e => await this.OnReviewStartedAsync(e.MoveHistory)));
        this._subscriptions.Add(
            this.ReviewMoveSubscriber.Subscribe(async e => await this.OnReviewMoveAsync(e.Move)));

        // エンジンサービス - R3
        this.EngineService.EvaluationUpdated
            .SubscribeAwait(async (_, _) => await this.OnEvaluationUpdatedAsync())
            .AddTo(this._r3Disposables);
    }

    private void UnsubscribeFromEvents()
    {
        // MessagePipe購読を解除
        foreach (var subscription in this._subscriptions) {
            subscription.Dispose();
        }
        this._subscriptions.Clear();

        // R3購読を解除
        this._r3Disposables.Dispose();
    }
}
