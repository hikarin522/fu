using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

using ShogiGame.Models;
using ShogiGame.Services;

namespace ShogiGame.Pages;

public partial class Index : IAsyncDisposable
{
    [Inject] private ShogiGameService GameService { get; set; } = null!;
    [Inject] private WebRtcService WebRtcService { get; set; } = null!;
    [Inject] private IJSRuntime JS { get; set; } = null!;

    private bool IsFlipped => this.GameService.State.LocalPlayer == Player.Gote;
    private string? InitError { get; set; }

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
            this.GameService.OnStateChangedAsync += this.OnGameStateChangedAsync;
        }
        catch (Exception ex) {
            this.InitError = ex.Message;
        }
    }

    private async Task OnConnected()
    {
        await this.InvokeAsync(this.StateHasChanged);
    }

    private Task OnDataChannelReadyAsync()
    {
        return this.InvokeAsync(() => {
            // 接続後、最初は対局ダイアログを表示（ホストの場合）
            if (this.WebRtcService.IsHost) {
                this.ShowNewGameDialog = true;
            }
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
            this.SentePeerId = info.SentePeerId;
            this.GotePeerId = info.GotePeerId;
            this.SenteNickname = info.SenteNickname;
            this.GoteNickname = info.GoteNickname;

            // 自分の役割を設定
            if (this.WebRtcService.MyPeerId == info.SentePeerId) {
                await this.GameService.SetLocalPlayerAsync(Player.Sente);
            }
            else if (this.WebRtcService.MyPeerId == info.GotePeerId) {
                await this.GameService.SetLocalPlayerAsync(Player.Gote);
            }
            else {
                // 観戦者
                await this.GameService.SetLocalPlayerAsync(Player.None);
            }

            await this.GameService.NewGameAsync();
            this.ShowNewGameDialog = false;
            this.StateHasChanged();
        });
    }

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

    private async ValueTask OnGameStateChangedAsync() => await this.InvokeAsync(this.StateHasChanged);

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

        this.SentePeerId = this.SelectedSentePeerId;
        this.GotePeerId = this.SelectedGotePeerId;
        this.SenteNickname = senteParticipant.Nickname;
        this.GoteNickname = goteParticipant.Nickname;

        // 自分の役割を設定
        if (this.WebRtcService.MyPeerId == this.SentePeerId) {
            await this.GameService.SetLocalPlayerAsync(Player.Sente);
        }
        else if (this.WebRtcService.MyPeerId == this.GotePeerId) {
            await this.GameService.SetLocalPlayerAsync(Player.Gote);
        }
        else {
            await this.GameService.SetLocalPlayerAsync(Player.None);
        }

        await this.GameService.NewGameAsync();
        await this.WebRtcService.SendGameStartAsync(this.SentePeerId, this.GotePeerId);
        this.ShowNewGameDialog = false;
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

    private async Task DownloadKifAsync()
    {
        var kif = KifExporter.Export(this.GameService.State.MoveHistory, this.GameService.State.Status);
        var fileName = $"shogi_{DateTime.Now:yyyyMMdd_HHmmss}.kifu";
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
        this.GameService.OnStateChangedAsync -= this.OnGameStateChangedAsync;
        await this.WebRtcService.DisposeAsync();
        GC.SuppressFinalize(this);
    }
}
