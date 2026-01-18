using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

using R3;

using Fu.Core.Abstractions;
using Fu.Core.Models;
using Fu.Core.Models.Dto;
using Fu.Core.Services;
using Fu.Services;

namespace Fu.Pages;

public partial class Index : IAsyncDisposable
{
    private readonly CompositeDisposable _disposables = [];
    [Inject] private ShogiGameService GameService { get; set; } = null!;
    [Inject] private WebRtcService WebRtcService { get; set; } = null!;
    [Inject] private ShogiEngineService EngineService { get; set; } = null!;
    [Inject] private GameSessionService SessionService { get; set; } = null!;
    [Inject] private UserSettingsService UserSettings { get; set; } = null!;
    [Inject] private LobbyService Lobby { get; set; } = null!;
    [Inject] private IJSRuntime JS { get; set; } = null!;
    [Inject] private NavigationManager Navigation { get; set; } = null!;

    [Parameter] public string? RoomIdParam { get; set; }

    // 観戦者・検討中用の盤面反転状態
    private bool SpectatorFlipped { get; set; }

    // 対局者は自分が後手なら反転（検討中は手動切り替え）、観戦者は手動切り替え
    private bool IsFlipped => this.IsPlayer && !this.GameService.State.IsReviewing
        ? this.GameService.State.LocalTurn == Turn.Second
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
    private bool IsGameEnded => this.GameService.State.Status.IsGameOver();

    // 参加者一覧表示用（SessionServiceから取得）
    private string GetParticipantRole(PlayerId playerId) =>
        this.SessionService.Session.GetRole(playerId);

    private int GetParticipantSortOrder(PlayerId playerId) =>
        this.SessionService.Session.GetSortOrder(playerId);

    private IEnumerable<(TransportParticipant Participant, string Role, bool IsConnected, bool IsMe)> GetAllParticipantsInfo()
    {
        var connectedPlayerIds = this.Lobby.Participants.Select(p => p.PlayerId).ToHashSet();
        var myPlayerId = this.Lobby.MyPlayerId;

        // 接続中の参加者（対局者優先でソート）
        foreach (var p in this.Lobby.Participants.OrderBy(p => this.GetParticipantSortOrder(p.PlayerId))) {
            yield return (p, this.GetParticipantRole(p.PlayerId), true, p.PlayerId == myPlayerId);
        }

        // 切断された対局者を表示（ゲーム中の場合のみ）
        if (this.GameService.State.Status is not (GameStatus.Playing or GameStatus.Reviewing)) {
            yield break;
        }

        if (this.FirstPlayerId is { } firstId && !connectedPlayerIds.Contains(firstId)) {
            yield return (new TransportParticipant(firstId, this.FirstNickname, false), "先手", false, false);
        }
        if (this.SecondPlayerId is { } secondId && !connectedPlayerIds.Contains(secondId)) {
            yield return (new TransportParticipant(secondId, this.SecondNickname, false), "後手", false, false);
        }
    }

    // 評価値表示（観戦者・対局終了後・検討中は常に全表示）
    private bool CanShowAllEvaluation => this.IsSpectator || this.IsGameEnded || this.GameService.State.IsReviewing;
    private bool ShowAdvantage => this.CanShowAllEvaluation || (this.IsPlayer && this.CurrentEvaluationOptions.ShowAdvantage);
    private bool ShowEvaluationValue => this.CanShowAllEvaluation || (this.IsPlayer && this.CurrentEvaluationOptions.ShowEvaluationValue);
    private bool ShowHasMate => this.CanShowAllEvaluation || (this.IsPlayer && this.CurrentEvaluationOptions.ShowHasMate);
    private bool ShowMateCount => this.CanShowAllEvaluation || (this.IsPlayer && this.CurrentEvaluationOptions.ShowMateCount);
    private bool ShowEvaluation => this.ShowAdvantage || this.ShowEvaluationValue || this.ShowHasMate;
    private bool ShowCandidateArrows => this.CanShowAllEvaluation;

    // 評価表示用の盤面（検討モード時は表示中の盤面）
    private Board DisplayBoard => this.GameService.State.IsReviewing
        ? this.GameService.GetBoardAtMove(this.GameService.State.DisplayMoveIndex).board
        : this.GameService.State.Board;

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
        // Dispose済みなら何もしない
        if (this._disposed) {
            return;
        }

        // 対局中のみ更新
        if (this.GameService.State.Status == GameStatus.Playing && !this.GameService.State.IsReviewing) {
            this._currentTurnElapsed = this.GameService.GetCurrentTurnElapsed();
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
        await this.GameService.NewGameAsync();
        await this.InvokeAsync(this.StateHasChanged);
    }

    private async Task OnGameStartWithPlayersAsync(GameStartInfo info)
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
        var elapsedTime = this.GameService.LastMoveElapsedTime;
        await this.WebRtcService.SendMoveAsync(move, elapsedTime);
        this.StateHasChanged();
    }

    private async Task OnRemoteMoveReceivedAsync(Move move, TimeSpan elapsedTime)
    {
        await this.GameService.ApplyRemoteMoveAsync(move, elapsedTime);

        // 自分の手番になったら通知音を鳴らす
        if (this.IsPlayer && this.GameService.State.IsMyTurn) {
            await this.PlayTurnNotificationAsync();
        }

        await this.InvokeAsync(this.StateHasChanged);
    }

    private Task PlayTurnNotificationAsync() =>
        this.UserSettings.PlayTurnNotificationAsync();

    private Task ToggleSoundAsync() =>
        this.UserSettings.ToggleSoundAsync();

    private async Task<GameSessionData?> LoadGameSessionAsync()
    {
        try {
            return await this.JS.InvokeAsync<GameSessionData?>("GameSession.load");
        }
        catch {
            // 読み込み失敗は無視
            return null;
        }
    }

    private Task ClearGameSessionAsync() =>
        this.SessionService.ClearSessionAsync();

    private sealed record GameSessionData(string RoomId, string Nickname, string? PeerId, long Timestamp);

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
            var me = participants.FirstOrDefault(p => p.PlayerId == this.Lobby.MyPlayerId);
            var opponent = participants.FirstOrDefault(p => p.PlayerId != this.Lobby.MyPlayerId);
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

        if (firstParticipant is null || secondParticipant is null) {
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

    private async Task OnGameStateSyncReceivedAsync(GameStateSyncInfo info)
    {
        await this.InvokeAsync(async () => {
            await this.SessionService.ApplyGameStateSyncAsync(info);
            this.ShowNewGameDialog = false;
            this.StateHasChanged();
        });
    }

    private async Task OnBranchResumeReceivedAsync(IReadOnlyList<Move> moveHistory)
    {
        await this.GameService.ApplyBranchResumeAsync(moveHistory);

        // 自分の手番になったら通知音を鳴らす
        if (this.IsPlayer && this.GameService.State.IsMyTurn) {
            await this.PlayTurnNotificationAsync();
        }

        await this.InvokeAsync(this.StateHasChanged);
    }

    private async ValueTask OnBranchResumedAsync(IReadOnlyList<Move> moveHistory)
    {
        // 分岐再開を相手に通知
        await this.WebRtcService.SendBranchResumeAsync(moveHistory);
    }

    private async Task OnRematchReceivedAsync(IReadOnlyList<Move> moveHistory)
    {
        // ゲーム状態がPlayingに戻るので評価値表示は自動的にリセットされる
        await this.GameService.ApplyRematchAsync(moveHistory);

        // 自分の手番になったら通知音を鳴らす
        if (this.IsPlayer && this.GameService.State.IsMyTurn) {
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
            !string.IsNullOrEmpty(this.EngineService.PrincipalVariation)) {
            await this.GameService.AddMateSequenceBranchAsync(
                this.EngineService.PrincipalVariation,
                this.GameService.State.DisplayMoveIndex);
        }

        await this.InvokeAsync(this.StateHasChanged);
    }

    private async Task RequestEvaluationAsync()
    {
        if (!this.ShowEvaluation || !this.EngineService.IsAvailable) {
            return;
        }

        var state = this.GameService.State;
        var (board, firstCaptured, secondCaptured, currentTurn) = state.IsReviewing
            ? this.GameService.GetBoardAtMove(state.DisplayMoveIndex)
            : (state.Board, state.FirstCaptured, state.SecondCaptured, state.CurrentTurn);

        // 観戦者または対局終了後は候補手を3つ表示
        var multiPv = this.ShowCandidateArrows ? 3 : 1;

        // depth: 0 = 無限探索（局面が変わるまで継続）
        await this.EngineService.AnalyzePositionAsync(
            board,
            currentTurn,
            firstCaptured,
            secondCaptured,
            this.GameService.MoveTree,
            multiPv: multiPv);
    }

    private void ToggleBoardFlip()
    {
        this.SpectatorFlipped = !this.SpectatorFlipped;
    }

    private async Task DownloadKifAsync()
    {
        // 選択中のブランチの棋譜をダウンロード
        var moves = this.GameService.State.DisplayBranchHistory;
        var kif = KifExporter.Export(
            moves,
            this.GameService.State.Status,
            this.FirstNickname,
            this.SecondNickname,
            this.GameService.State.Times);
        var fileName = $"shogi_{DateTime.Now:yyyyMMdd_HHmmss}.kif";
        await this.JS.InvokeVoidAsync("downloadTextFile", fileName, kif);
    }

    private Task GoBackAsync() => this.GameService.GoBackAsync();

    private Task GoForwardAsync() => this.GameService.GoForwardAsync();

    private Task GoToPreviousBranchAsync() => this.GameService.GoToPreviousBranchAsync();

    private Task GoToNextBranchAsync() => this.GameService.GoToNextBranchAsync();

    private Task GoForwardBranchAsync(int branchIndex) => this.GameService.GoForwardBranchAsync(branchIndex);

    private Task GoToLatestAsync() => this.GameService.GoToLatestAsync();

    private Task GoToMoveAsync(int moveIndex) => this.GameService.SetViewingMoveIndexAsync(moveIndex);

    private Task ResumeFromBranchAsync() => this.GameService.ResumeFromBranchAsync();

    private async Task RematchFromCurrentAsync()
    {
        // 現在の位置から再戦（ゲーム状態がPlayingに戻るので評価値表示は自動的にリセットされる）
        await this.GameService.RematchFromCurrentPositionAsync();

        // 相手に再戦を通知
        await this.WebRtcService.SendRematchAsync(this.GameService.State.MoveHistory);
    }

    private Task OnTreeNodeSelected(MoveNode? node) => this.GameService.GoToNodeAsync(node);

    // 棋譜ツリーからの検討・再戦
    private async Task OnReviewFromNodeAsync(MoveNode? node)
    {
        // まずそのノードに移動してから検討開始
        await this.GameService.GoToNodeAsync(node);
        await this.GameService.StartReviewFromCurrentPositionAsync();
    }

    private async Task OnRematchFromNodeAsync(MoveNode? node)
    {
        // まずそのノードに移動してから再戦
        await this.GameService.GoToNodeAsync(node);
        await this.GameService.RematchFromCurrentPositionAsync();
        await this.WebRtcService.SendRematchAsync(this.GameService.State.MoveHistory);
    }

    // 検討モード関連
    private async Task StartReviewFromCurrentAsync()
    {
        await this.GameService.StartReviewFromCurrentPositionAsync();
        // 相手に検討モード開始を通知（イベントハンドラで行う）
    }

    private async ValueTask OnReviewStartedAsync(IReadOnlyList<Move> moveHistory)
    {
        // 検討モード開始を相手に通知
        await this.WebRtcService.SendReviewStartAsync(moveHistory);
    }

    private async ValueTask OnReviewMoveAsync(Move move)
    {
        // 検討モードでの手を相手に通知
        await this.WebRtcService.SendReviewMoveAsync(move);
    }

    private async Task OnReviewStartReceivedAsync(IReadOnlyList<Move> moveHistory)
    {
        await this.GameService.ApplyReviewStartAsync(moveHistory);
        await this.InvokeAsync(this.StateHasChanged);
    }

    private async Task OnReviewMoveReceivedAsync(Move move)
    {
        await this.GameService.ApplyReviewMoveAsync(move);
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
        var totalTime = player == Turn.First
            ? this.GameService.State.FirstTotalTime
            : this.GameService.State.SecondTotalTime;

        // 対局中で、このプレイヤーが現在の手番なら経過時間を加算
        if (this.GameService.State.Status == GameStatus.Playing &&
            !this.GameService.State.IsReviewing &&
            this.GameService.State.CurrentTurn == player) {
            totalTime += this._currentTurnElapsed;
        }

        return totalTime;
    }

    private static string FormatTime(TimeSpan time)
    {
        if (time.TotalHours >= 1) {
            return $"{(int)time.TotalHours}:{time.Minutes:D2}:{time.Seconds:D2}";
        }
        return $"{time.Minutes}:{time.Seconds:D2}";
    }

    public async ValueTask DisposeAsync()
    {
        this._disposed = true;

        if (this._uiTimer is not null) {
            await this._uiTimer.DisposeAsync();
        }

        this.UnsubscribeFromEvents();
        await this.EngineService.DisposeAsync();
        await this.WebRtcService.DisposeAsync();
        GC.SuppressFinalize(this);
    }

    private void SubscribeToEvents()
    {
        // ゲーム関連イベント（WebRtcService）- R3
        this.WebRtcService.MoveReceived
            .SubscribeAwait(async (data, _) => await this.OnRemoteMoveReceivedAsync(data.Move, data.Elapsed))
            .AddTo(this._disposables);

        this.WebRtcService.GameStartReceived
            .SubscribeAwait(async (_, _) => await this.OnRemoteGameStartAsync())
            .AddTo(this._disposables);

        this.WebRtcService.GameStartWithPlayersReceived
            .SubscribeAwait(async (info, _) => await this.OnGameStartWithPlayersAsync(info))
            .AddTo(this._disposables);

        this.WebRtcService.ResignReceived
            .SubscribeAwait(async (_, _) => await this.OnRemoteResignReceivedAsync())
            .AddTo(this._disposables);

        this.WebRtcService.GameStateSyncReceived
            .SubscribeAwait(async (info, _) => await this.OnGameStateSyncReceivedAsync(info))
            .AddTo(this._disposables);

        this.WebRtcService.BranchResumeReceived
            .SubscribeAwait(async (moves, _) => await this.OnBranchResumeReceivedAsync(moves))
            .AddTo(this._disposables);

        this.WebRtcService.RematchReceived
            .SubscribeAwait(async (moves, _) => await this.OnRematchReceivedAsync(moves))
            .AddTo(this._disposables);

        this.WebRtcService.ReviewStartReceived
            .SubscribeAwait(async (moves, _) => await this.OnReviewStartReceivedAsync(moves))
            .AddTo(this._disposables);

        this.WebRtcService.ReviewMoveReceived
            .SubscribeAwait(async (move, _) => await this.OnReviewMoveReceivedAsync(move))
            .AddTo(this._disposables);

        // ロビー関連イベント（LobbyService）- R3
        this.Lobby.Ready
            .SubscribeAwait(async (_, _) => await this.OnDataChannelReadyAsync())
            .AddTo(this._disposables);

        this.Lobby.BecameHost
            .Subscribe(_ => Console.WriteLine("Became host"))
            .AddTo(this._disposables);

        // ゲームサービスイベント - R3
        this.GameService.StateChanged
            .SubscribeAwait(async (_, _) => await this.OnGameStateChangedAsync())
            .AddTo(this._disposables);

        this.GameService.BranchResumed
            .SubscribeAwait(async (moves, _) => await this.OnBranchResumedAsync(moves))
            .AddTo(this._disposables);

        this.GameService.ReviewStarted
            .SubscribeAwait(async (moves, _) => await this.OnReviewStartedAsync(moves))
            .AddTo(this._disposables);

        this.GameService.ReviewMove
            .SubscribeAwait(async (move, _) => await this.OnReviewMoveAsync(move))
            .AddTo(this._disposables);

        // エンジンサービス - R3
        this.EngineService.EvaluationUpdated
            .SubscribeAwait(async (_, _) => await this.OnEvaluationUpdatedAsync())
            .AddTo(this._disposables);
    }

    private void UnsubscribeFromEvents()
    {
        // R3 購読は _disposables.Dispose() で解除
        this._disposables.Dispose();
    }
}
