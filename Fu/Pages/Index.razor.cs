using System.Collections.Immutable;

using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

using Fu.Core.Models;
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

    private bool IsFlipped => this.GameService.State.LocalPlayer == Player.Gote;
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

    // 対局者かどうか
    private bool IsPlayer => this.WebRtcService.MyPeerId == this.SentePeerId ||
                             this.WebRtcService.MyPeerId == this.GotePeerId;
    private bool IsSpectator => !this.IsPlayer && this.GameService.State.Status == GameStatus.Playing;

    protected override async Task OnInitializedAsync()
    {
        try {
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

    private async Task OnConnected()
    {
        await this.InvokeAsync(this.StateHasChanged);
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
        this.InvokeAsync(() => this.SetupGameAsync(info.SentePeerId, info.GotePeerId, info.SenteNickname, info.GoteNickname));

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

        // 観戦者の場合は評価をリクエスト
        if (this.IsSpectator && this.EngineService.IsAvailable) {
            await this.RequestEvaluationAsync();
        }
    }

    private void OpenNewGameDialog()
    {
        this.SelectedSentePeerId = "";
        this.SelectedGotePeerId = "";
        this.ShowNewGameDialog = true;
    }

    private async Task StartNewGameAsync()
    {
        var senteParticipant = this.WebRtcService.Participants.FirstOrDefault(p => p.PeerId == this.SelectedSentePeerId);
        var goteParticipant = this.WebRtcService.Participants.FirstOrDefault(p => p.PeerId == this.SelectedGotePeerId);

        if (senteParticipant is null || goteParticipant is null) {
            return;
        }

        await this.SetupGameAsync(
            this.SelectedSentePeerId,
            this.SelectedGotePeerId,
            senteParticipant.Nickname,
            goteParticipant.Nickname);
        await this.WebRtcService.SendGameStartAsync(this.SentePeerId!, this.GotePeerId!);
    }

    private async Task SetupGameAsync(string sentePeerId, string gotePeerId, string senteNickname, string goteNickname)
    {
        this.SentePeerId = sentePeerId;
        this.GotePeerId = gotePeerId;
        this.SenteNickname = senteNickname;
        this.GoteNickname = goteNickname;

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
                this.GameService.State.Status
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
        if (!this.IsSpectator || !this.EngineService.IsAvailable) {
            return;
        }

        var state = this.GameService.State;
        var (board, senteCaptured, goteCaptured, currentPlayer) = state.IsReviewing
            ? this.GameService.GetBoardAtMove(state.DisplayMoveIndex)
            : (state.Board, state.SenteCaptured, state.GoteCaptured, state.CurrentPlayer);

        await this.EngineService.AnalyzePositionAsync(
            board,
            currentPlayer,
            senteCaptured,
            goteCaptured,
            depth: 15);
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
