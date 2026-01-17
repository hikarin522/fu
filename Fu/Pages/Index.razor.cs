using System.Collections.Immutable;

using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

using Fu.Core.Models;
using Fu.Core.Models.Dto;
using Fu.Core.Services;
using Fu.Services;

namespace Fu.Pages;

public partial class Index : IAsyncDisposable
{
    [Inject] private ShogiGameService GameService { get; set; } = null!;
    [Inject] private WebRtcService WebRtcService { get; set; } = null!;
    [Inject] private ShogiEngineService EngineService { get; set; } = null!;
    [Inject] private IJSRuntime JS { get; set; } = null!;
    [Inject] private NavigationManager Navigation { get; set; } = null!;

    [Parameter] public string? RoomIdParam { get; set; }

    // 観戦者・検討中用の盤面反転状態
    private bool SpectatorFlipped { get; set; }

    // 対局者は自分が後手なら反転（検討中は手動切り替え）、観戦者は手動切り替え
    private bool IsFlipped => this.IsPlayer && !this.GameService.State.IsReviewing
        ? this.GameService.State.LocalPlayer == Player.Gote
        : this.SpectatorFlipped;

    private string? InitError { get; set; }
    private string BaseUrl => this.Navigation.BaseUri;

    // 対局者情報
    private string? SentePeerId { get; set; }
    private string? GotePeerId { get; set; }
    private string SenteNickname { get; set; } = "先手";
    private string GoteNickname { get; set; } = "後手";

    // 新規対局ダイアログ
    private bool ShowNewGameDialog { get; set; }
    private string SelectedSentePeerId { get; set; } = "";
    private string SelectedGotePeerId { get; set; } = "";

    // 対局者向け評価値表示オプション（ダイアログ用）
    private bool OptShowAdvantage { get; set; }
    private bool OptShowEvaluationValue { get; set; }
    private bool OptShowHasMate { get; set; }
    private bool OptShowMateCount { get; set; }

    // 現在のゲームに適用されている評価値表示オプション
    private EvaluationDisplayOptions CurrentEvaluationOptions { get; set; } = new();

    // 役割判定
    private bool IsPlayer => this.WebRtcService.MyPeerId == this.SentePeerId ||
                             this.WebRtcService.MyPeerId == this.GotePeerId;
    private bool IsSpectator => !this.IsPlayer && this.GameService.State.Status == GameStatus.Playing;
    private bool IsGameEnded => this.GameService.State.Status.IsGameOver();

    // 参加者一覧表示用
    private string GetParticipantRole(string peerId) =>
        peerId == this.SentePeerId ? "先手" : peerId == this.GotePeerId ? "後手" : "観戦";

    private int GetParticipantSortOrder(string peerId) =>
        peerId == this.SentePeerId ? 0 : peerId == this.GotePeerId ? 1 : 2;

    private IEnumerable<(Participant Participant, string Role, bool IsConnected, bool IsMe)> GetAllParticipantsInfo()
    {
        var connectedPeerIds = this.WebRtcService.Participants.Select(p => p.PeerId).ToHashSet();
        var myPeerId = this.WebRtcService.MyPeerId;

        // 接続中の参加者（対局者優先でソート）
        foreach (var p in this.WebRtcService.Participants.OrderBy(p => this.GetParticipantSortOrder(p.PeerId))) {
            yield return (p, this.GetParticipantRole(p.PeerId), true, p.PeerId == myPeerId);
        }

        // 切断された対局者を表示（ゲーム中の場合のみ）
        if (this.GameService.State.Status is not (GameStatus.Playing or GameStatus.Reviewing)) {
            yield break;
        }

        if (this.SentePeerId is not null && !connectedPeerIds.Contains(this.SentePeerId)) {
            yield return (new Participant(this.SentePeerId, this.SenteNickname, false), "先手", false, false);
        }
        if (this.GotePeerId is not null && !connectedPeerIds.Contains(this.GotePeerId)) {
            yield return (new Participant(this.GotePeerId, this.GoteNickname, false), "後手", false, false);
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

    // 通知音設定
    private bool SoundEnabled { get; set; } = true;

    // タイマー更新用
    private Timer? _uiTimer;
    private TimeSpan _currentTurnElapsed;

    protected override async Task OnInitializedAsync()
    {
        try {
            // Cross-Origin Isolationのチェック（Service Workerが有効か）
            await this.CheckAndReloadForCrossOriginIsolationAsync();
            if (this.NeedsReload) {
                return; // リロード中なので以降の初期化をスキップ
            }

            await this.WebRtcService.InitializeAsync();

            // 通知音設定を読み込み
            this.SoundEnabled = await this.LoadSoundSettingAsync();

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
        // 対局中のみ更新
        if (this.GameService.State.Status == GameStatus.Playing && !this.GameService.State.IsReviewing) {
            this._currentTurnElapsed = this.GameService.GetCurrentTurnElapsed();
            _ = this.InvokeAsync(this.StateHasChanged);
        }
    }

    private async Task OnDataChannelReadyAsync()
    {
        await this.InvokeAsync(async () => {
            if (!this.WebRtcService.IsHost) {
                // 非ホストの場合は現在のゲーム状態をリクエスト
                await this.WebRtcService.SendGameStateRequestAsync();
            }
            // ホストの場合は空の盤面を表示（「新規対局」ボタンから対局設定を開く）
            this.StateHasChanged();
        });
    }

    private async Task OnRemoteGameStartAsync()
    {
        await this.GameService.NewGameAsync();
        await this.InvokeAsync(this.StateHasChanged);
    }

    private Task OnGameStartWithPlayersAsync(GameStartInfo info) =>
        this.InvokeAsync(() => this.SetupGameAsync(info.SentePeerId, info.GotePeerId, info.SenteNickname, info.GoteNickname, info.EvaluationOptions));

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

    private async Task PlayTurnNotificationAsync()
    {
        if (!this.SoundEnabled) {
            return;
        }

        try {
            await this.JS.InvokeVoidAsync("TurnNotification.play");
        }
        catch {
            // 音声再生に失敗しても無視
        }
    }

    private async Task ToggleSoundAsync()
    {
        this.SoundEnabled = !this.SoundEnabled;
        await this.SaveSoundSettingAsync(this.SoundEnabled);
    }

    private async Task<bool> LoadSoundSettingAsync()
    {
        try {
            var value = await this.JS.InvokeAsync<string?>("SoundSettings.load");
            return value != "false"; // デフォルトはtrue
        }
        catch {
            return true;
        }
    }

    private async Task SaveSoundSettingAsync(bool enabled)
    {
        try {
            await this.JS.InvokeVoidAsync("SoundSettings.save", enabled ? "true" : "false");
        }
        catch {
            // 保存失敗は無視
        }
    }

    private async Task SaveGameSessionAsync()
    {
        try {
            var nickname = await this.JS.InvokeAsync<string>("NicknameStorage.load");
            await this.JS.InvokeVoidAsync("GameSession.save", this.WebRtcService.RoomId?.AsPrimitive(), nickname, this.WebRtcService.MyPeerId);
        }
        catch {
            // 保存失敗は無視
        }
    }

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

    private async Task ClearGameSessionAsync()
    {
        try {
            await this.JS.InvokeVoidAsync("GameSession.clear");
        }
        catch {
            // クリア失敗は無視
        }
    }

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
        var participants = this.WebRtcService.Participants.ToList();
        if (participants.Count >= 2) {
            // 自分を先手、相手を後手にデフォルト設定
            var me = participants.FirstOrDefault(p => p.PeerId == this.WebRtcService.MyPeerId);
            var opponent = participants.FirstOrDefault(p => p.PeerId != this.WebRtcService.MyPeerId);
            this.SelectedSentePeerId = me?.PeerId ?? participants[0].PeerId;
            this.SelectedGotePeerId = opponent?.PeerId ?? participants[1].PeerId;
        } else {
            this.SelectedSentePeerId = "";
            this.SelectedGotePeerId = "";
        }
        this.OptShowAdvantage = false;
        this.OptShowEvaluationValue = false;
        this.OptShowHasMate = false;
        this.OptShowMateCount = false;
        this.ShowNewGameDialog = true;
    }

    private async Task StartNewGameAsync()
    {
        var senteParticipant = this.WebRtcService.Participants.FirstOrDefault(p => p.PeerId == this.SelectedSentePeerId);
        var goteParticipant = this.WebRtcService.Participants.FirstOrDefault(p => p.PeerId == this.SelectedGotePeerId);

        if (senteParticipant is null || goteParticipant is null) {
            return;
        }

        var options = new EvaluationDisplayOptions(
            this.OptShowAdvantage,
            this.OptShowEvaluationValue,
            this.OptShowHasMate,
            this.OptShowMateCount);

        await this.SetupGameAsync(
            this.SelectedSentePeerId,
            this.SelectedGotePeerId,
            senteParticipant.Nickname,
            goteParticipant.Nickname,
            options);
        await this.WebRtcService.SendGameStartAsync(this.SentePeerId!, this.GotePeerId!, options);
    }

    private async Task SetupGameAsync(string sentePeerId, string gotePeerId, string senteNickname, string goteNickname, EvaluationDisplayOptions? evaluationOptions = null)
    {
        this.SentePeerId = sentePeerId;
        this.GotePeerId = gotePeerId;
        this.SenteNickname = senteNickname;
        this.GoteNickname = goteNickname;
        this.CurrentEvaluationOptions = evaluationOptions ?? new EvaluationDisplayOptions();

        var localPlayer = this.WebRtcService.MyPeerId == sentePeerId ? Player.Sente
            : this.WebRtcService.MyPeerId == gotePeerId ? Player.Gote
            : Player.None;

        await this.GameService.SetLocalPlayerAsync(localPlayer);
        await this.GameService.NewGameAsync();

        // 対局者の場合、セッション情報を保存（リロード時の再接続用）
        if (localPlayer != Player.None && this.WebRtcService.RoomId is not null) {
            await this.SaveGameSessionAsync();
        }

        // 自分が先手（最初の手番）なら通知音を鳴らす
        if (localPlayer == Player.Sente) {
            await this.PlayTurnNotificationAsync();
        }

        this.ShowNewGameDialog = false;
        this.StateHasChanged();
    }

    private async Task ResignAsync()
    {
        await this.GameService.ResignAsync();
        await this.WebRtcService.SendResignAsync();
        await this.ClearGameSessionAsync();
    }

    private async Task OnRemoteResignReceivedAsync()
    {
        await this.GameService.ResignAsync();
        await this.ClearGameSessionAsync();
        await this.InvokeAsync(this.StateHasChanged);
    }

    private async Task OnGameStateRequestedAsync()
    {
        // ホストがゲーム状態リクエストを受信したら、現在の状態を送信
        if (this.WebRtcService.IsHost && this.GameService.State.Status != GameStatus.WaitingForConnection) {
            await this.SendGameStateSyncAsync();
        }
    }

    private Task OnBecameHostAsync()
    {
        // ホストを引き継いだ時点で特に処理は不要
        // 新しい参加者がGameStateRequestを送ってきたら応答する
        Console.WriteLine("Became host");
        return Task.CompletedTask;
    }

    private Task SendGameStateSyncAsync() =>
        this.WebRtcService.SendGameStateSyncAsync(
            this.GameService.State.MoveHistory,
            this.SentePeerId ?? "",
            this.GotePeerId ?? "",
            this.SenteNickname,
            this.GoteNickname,
            this.GameService.State.Status,
            this.CurrentEvaluationOptions,
            this.GameService.State.Times
        );

    private async Task OnGameStateSyncReceivedAsync(GameStateSyncInfo info)
    {
        await this.InvokeAsync(async () => {
            // 対局者情報を設定
            this.SentePeerId = info.SentePeerId;
            this.GotePeerId = info.GotePeerId;
            this.SenteNickname = info.SenteNickname;
            this.GoteNickname = info.GoteNickname;
            this.CurrentEvaluationOptions = info.EvaluationOptions ?? new EvaluationDisplayOptions();

            // 自分が対局者かどうかを判定
            var localPlayer = this.WebRtcService.MyPeerId == info.SentePeerId ? Player.Sente
                : this.WebRtcService.MyPeerId == info.GotePeerId ? Player.Gote
                : Player.None;

            await this.GameService.SetLocalPlayerAsync(localPlayer);

            // ゲーム状態を復元（持ち時間含む）
            await this.GameService.RestoreStateAsync(info.MoveHistory, info.Status, info.MoveTimes);

            // 対局者かつ対局中ならセッション保存、終了していればクリア
            if (localPlayer != Player.None) {
                if (info.Status == GameStatus.Playing) {
                    await this.SaveGameSessionAsync();
                } else if (info.Status.IsGameOver()) {
                    await this.ClearGameSessionAsync();
                }
            }

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

    private async ValueTask OnBranchResumedAsync(ImmutableList<Move> moveHistory)
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
            this.EngineService.OnEvaluationUpdated += this.OnEvaluationUpdatedAsync;
        }
        catch {
            // エンジン初期化失敗は無視
        }
    }

    private async Task OnEvaluationUpdatedAsync()
    {
        await this.InvokeAsync(this.StateHasChanged);
    }

    private async Task RequestEvaluationAsync()
    {
        if (!this.ShowEvaluation || !this.EngineService.IsAvailable) {
            return;
        }

        var state = this.GameService.State;
        var (board, senteCaptured, goteCaptured, currentPlayer) = state.IsReviewing
            ? this.GameService.GetBoardAtMove(state.DisplayMoveIndex)
            : (state.Board, state.SenteCaptured, state.GoteCaptured, state.CurrentPlayer);

        // 観戦者または対局終了後は候補手を3つ表示
        var multiPv = this.ShowCandidateArrows ? 3 : 1;

        // depth: 0 = 無限探索（局面が変わるまで継続）
        // 詰み表示が有効な場合は詰めろチェックも行う
        await this.EngineService.AnalyzePositionAsync(
            board,
            currentPlayer,
            senteCaptured,
            goteCaptured,
            multiPv: multiPv,
            checkThreatening: this.ShowHasMate);
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
            this.SenteNickname,
            this.GoteNickname,
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

    // 検討モード関連
    private async Task StartReviewFromCurrentAsync()
    {
        await this.GameService.StartReviewFromCurrentPositionAsync();
        // 相手に検討モード開始を通知（イベントハンドラで行う）
    }

    private async ValueTask OnReviewStartedAsync(ImmutableList<Move> moveHistory)
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
    private TimeSpan GetPlayerTime(Player player)
    {
        var totalTime = player == Player.Sente
            ? this.GameService.State.SenteTotalTime
            : this.GameService.State.GoteTotalTime;

        // 対局中で、このプレイヤーが現在の手番なら経過時間を加算
        if (this.GameService.State.Status == GameStatus.Playing &&
            !this.GameService.State.IsReviewing &&
            this.GameService.State.CurrentPlayer == player) {
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
        this.WebRtcService.OnMoveReceived += this.OnRemoteMoveReceivedAsync;
        this.WebRtcService.OnGameStart += this.OnRemoteGameStartAsync;
        this.WebRtcService.OnDataChannelReady += this.OnDataChannelReadyAsync;
        this.WebRtcService.OnGameStartWithPlayers += this.OnGameStartWithPlayersAsync;
        this.WebRtcService.OnResignReceived += this.OnRemoteResignReceivedAsync;
        this.WebRtcService.OnGameStateRequested += this.OnGameStateRequestedAsync;
        this.WebRtcService.OnGameStateSyncReceived += this.OnGameStateSyncReceivedAsync;
        this.WebRtcService.OnBranchResumeReceived += this.OnBranchResumeReceivedAsync;
        this.WebRtcService.OnRematchReceived += this.OnRematchReceivedAsync;
        this.WebRtcService.OnReviewStartReceived += this.OnReviewStartReceivedAsync;
        this.WebRtcService.OnReviewMoveReceived += this.OnReviewMoveReceivedAsync;
        this.WebRtcService.OnBecameHost += this.OnBecameHostAsync;
        this.GameService.OnStateChangedAsync += this.OnGameStateChangedAsync;
        this.GameService.OnBranchResumedAsync += this.OnBranchResumedAsync;
        this.GameService.OnReviewStartedAsync += this.OnReviewStartedAsync;
        this.GameService.OnReviewMoveAsync += this.OnReviewMoveAsync;
    }

    private void UnsubscribeFromEvents()
    {
        this.WebRtcService.OnMoveReceived -= this.OnRemoteMoveReceivedAsync;
        this.WebRtcService.OnGameStart -= this.OnRemoteGameStartAsync;
        this.WebRtcService.OnDataChannelReady -= this.OnDataChannelReadyAsync;
        this.WebRtcService.OnGameStartWithPlayers -= this.OnGameStartWithPlayersAsync;
        this.WebRtcService.OnResignReceived -= this.OnRemoteResignReceivedAsync;
        this.WebRtcService.OnGameStateRequested -= this.OnGameStateRequestedAsync;
        this.WebRtcService.OnGameStateSyncReceived -= this.OnGameStateSyncReceivedAsync;
        this.WebRtcService.OnBranchResumeReceived -= this.OnBranchResumeReceivedAsync;
        this.WebRtcService.OnRematchReceived -= this.OnRematchReceivedAsync;
        this.WebRtcService.OnReviewStartReceived -= this.OnReviewStartReceivedAsync;
        this.WebRtcService.OnReviewMoveReceived -= this.OnReviewMoveReceivedAsync;
        this.WebRtcService.OnBecameHost -= this.OnBecameHostAsync;
        this.GameService.OnStateChangedAsync -= this.OnGameStateChangedAsync;
        this.GameService.OnBranchResumedAsync -= this.OnBranchResumedAsync;
        this.GameService.OnReviewStartedAsync -= this.OnReviewStartedAsync;
        this.GameService.OnReviewMoveAsync -= this.OnReviewMoveAsync;
        this.EngineService.OnEvaluationUpdated -= this.OnEvaluationUpdatedAsync;
    }
}
