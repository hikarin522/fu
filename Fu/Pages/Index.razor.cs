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

    // 観戦者用の盤面反転状態
    private bool SpectatorFlipped { get; set; }

    // 対局者は自分が後手なら反転、観戦者は手動切り替え
    private bool IsFlipped => this.IsPlayer
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

    // 評価値表示（観戦者・対局終了後は常に全表示）
    private bool CanShowAllEvaluation => this.IsSpectator || this.IsGameEnded;
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

    protected override async Task OnInitializedAsync()
    {
        try {
            // Cross-Origin Isolationのチェック（Service Workerが有効か）
            await this.CheckAndReloadForCrossOriginIsolationAsync();
            if (this.NeedsReload) {
                return; // リロード中なので以降の初期化をスキップ
            }

            await this.WebRtcService.InitializeAsync();
            this.WebRtcService.OnMoveReceived += this.OnRemoteMoveReceivedAsync;
            this.WebRtcService.OnGameStart += this.OnRemoteGameStartAsync;
            this.WebRtcService.OnDataChannelReady += this.OnDataChannelReadyAsync;
            this.WebRtcService.OnGameStartWithPlayers += this.OnGameStartWithPlayersAsync;
            this.WebRtcService.OnResignReceived += this.OnRemoteResignReceivedAsync;
            this.WebRtcService.OnGameStateRequested += this.OnGameStateRequestedAsync;
            this.WebRtcService.OnGameStateSyncReceived += this.OnGameStateSyncReceivedAsync;
            this.WebRtcService.OnBranchResumeReceived += this.OnBranchResumeReceivedAsync;
            this.GameService.OnStateChangedAsync += this.OnGameStateChangedAsync;
            this.GameService.OnBranchResumedAsync += this.OnBranchResumedAsync;

            // エンジン初期化（バックグラウンドで実行）
            _ = this.InitializeEngineAsync();
        }
        catch (Exception ex) {
            this.InitError = ex.Message;
        }
    }

    private async Task OnDataChannelReadyAsync()
    {
        await this.InvokeAsync(async () => {
            if (this.WebRtcService.IsHost) {
                // ホストの場合は対局ダイアログを表示
                this.ShowNewGameDialog = true;
            }
            else {
                // 非ホストの場合は現在のゲーム状態をリクエスト
                await this.WebRtcService.SendGameStateRequestAsync();
            }
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
        await this.WebRtcService.SendMoveAsync(move);
        this.StateHasChanged();
    }

    private async Task OnRemoteMoveReceivedAsync(Move move)
    {
        await this.GameService.ApplyRemoteMoveAsync(move);
        await this.InvokeAsync(this.StateHasChanged);
    }

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
        this.ShowNewGameDialog = false;
        this.StateHasChanged();
    }

    private async Task ResignAsync()
    {
        await this.GameService.ResignAsync();
        await this.WebRtcService.SendResignAsync();
    }

    private async Task OnRemoteResignReceivedAsync()
    {
        await this.GameService.ResignAsync();
        await this.InvokeAsync(this.StateHasChanged);
    }

    private async Task OnGameStateRequestedAsync()
    {
        // ホストがゲーム状態リクエストを受信したら、現在の状態を送信
        if (this.WebRtcService.IsHost && this.GameService.State.Status != GameStatus.WaitingForConnection) {
            await this.WebRtcService.SendGameStateSyncAsync(
                this.GameService.State.MoveHistory,
                this.SentePeerId ?? "",
                this.GotePeerId ?? "",
                this.SenteNickname,
                this.GoteNickname,
                this.GameService.State.Status,
                this.CurrentEvaluationOptions
            );
        }
    }

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

            // ゲーム状態を復元
            await this.GameService.RestoreStateAsync(info.MoveHistory, info.Status);

            this.ShowNewGameDialog = false;
            this.StateHasChanged();
        });
    }

    private async Task OnBranchResumeReceivedAsync(IReadOnlyList<Move> moveHistory)
    {
        await this.GameService.ApplyBranchResumeAsync(moveHistory);
        await this.InvokeAsync(this.StateHasChanged);
    }

    private async ValueTask OnBranchResumedAsync(ImmutableList<Move> moveHistory)
    {
        // 分岐再開を相手に通知
        await this.WebRtcService.SendBranchResumeAsync(moveHistory);
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
        await this.EngineService.AnalyzePositionAsync(
            board,
            currentPlayer,
            senteCaptured,
            goteCaptured,
            multiPv: multiPv);
    }

    private void ToggleBoardFlip()
    {
        this.SpectatorFlipped = !this.SpectatorFlipped;
    }

    private async Task DownloadKifAsync()
    {
        var kif = KifExporter.Export(
            this.GameService.State.MoveHistory,
            this.GameService.State.Status,
            this.SenteNickname,
            this.GoteNickname);
        var fileName = $"shogi_{DateTime.Now:yyyyMMdd_HHmmss}.kif";
        await this.JS.InvokeVoidAsync("downloadTextFile", fileName, kif);
    }

    private Task GoBackAsync() => this.GameService.GoBackAsync();

    private Task GoForwardAsync() => this.GameService.GoForwardAsync();

    private Task GoForwardBranchAsync(int branchIndex) => this.GameService.GoForwardBranchAsync(branchIndex);

    private Task GoToLatestAsync() => this.GameService.GoToLatestAsync();

    private Task GoToMoveAsync(int moveIndex) => this.GameService.SetViewingMoveIndexAsync(moveIndex);

    private Task ResumeFromBranchAsync() => this.GameService.ResumeFromBranchAsync();

    private Task OnTreeNodeSelected(MoveNode? node) => this.GameService.GoToNodeAsync(node);

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

    public async ValueTask DisposeAsync()
    {
        this.WebRtcService.OnMoveReceived -= this.OnRemoteMoveReceivedAsync;
        this.WebRtcService.OnGameStart -= this.OnRemoteGameStartAsync;
        this.WebRtcService.OnDataChannelReady -= this.OnDataChannelReadyAsync;
        this.WebRtcService.OnGameStartWithPlayers -= this.OnGameStartWithPlayersAsync;
        this.WebRtcService.OnResignReceived -= this.OnRemoteResignReceivedAsync;
        this.WebRtcService.OnGameStateRequested -= this.OnGameStateRequestedAsync;
        this.WebRtcService.OnGameStateSyncReceived -= this.OnGameStateSyncReceivedAsync;
        this.WebRtcService.OnBranchResumeReceived -= this.OnBranchResumeReceivedAsync;
        this.GameService.OnStateChangedAsync -= this.OnGameStateChangedAsync;
        this.GameService.OnBranchResumedAsync -= this.OnBranchResumedAsync;
        this.EngineService.OnEvaluationUpdated -= this.OnEvaluationUpdatedAsync;
        await this.EngineService.DisposeAsync();
        await this.WebRtcService.DisposeAsync();
        GC.SuppressFinalize(this);
    }
}
