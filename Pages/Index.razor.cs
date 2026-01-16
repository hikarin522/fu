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

    protected override async Task OnInitializedAsync()
    {
        try {
            await this.WebRtcService.InitializeAsync();
            this.WebRtcService.OnMoveReceived += this.OnRemoteMoveReceivedAsync;
            this.WebRtcService.OnGameStart += this.OnRemoteGameStartAsync;
            this.WebRtcService.OnDataChannelReady += this.OnDataChannelReadyAsync;
            this.GameService.OnStateChangedAsync += this.OnGameStateChangedAsync;
        }
        catch (Exception ex) {
            this.InitError = ex.Message;
        }
    }

    private async Task OnPlayerAssignedAsync(bool isSente)
    {
        var player = isSente ? Player.Sente : Player.Gote;
        await this.GameService.SetLocalPlayerAsync(player);
    }

    private async Task OnConnected()
    {
        await this.InvokeAsync(this.StateHasChanged);
    }

    private async Task OnDataChannelReadyAsync()
    {
        await this.InvokeAsync(async () => {
            if (this.GameService.State.LocalPlayer is Player.Sente) {
                await this.GameService.NewGameAsync();
                await this.WebRtcService.SendGameStartAsync();
            }
            this.StateHasChanged();
        });
    }

    private async Task OnRemoteGameStartAsync()
    {
        await this.GameService.NewGameAsync();
        await this.InvokeAsync(this.StateHasChanged);
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

    private async Task NewGameAsync()
    {
        await this.GameService.NewGameAsync();
        await this.WebRtcService.SendGameStartAsync();
    }

    private Task ResignAsync() => this.GameService.ResignAsync();

    private async Task DownloadKifAsync()
    {
        var kif = KifExporter.Export(this.GameService.State.MoveHistory, this.GameService.State.Status);
        var fileName = $"shogi_{DateTime.Now:yyyyMMdd_HHmmss}.kif";
        await this.JS.InvokeVoidAsync("downloadTextFile", fileName, kif);
    }

    public async ValueTask DisposeAsync()
    {
        this.WebRtcService.OnMoveReceived -= this.OnRemoteMoveReceivedAsync;
        this.WebRtcService.OnGameStart -= this.OnRemoteGameStartAsync;
        this.WebRtcService.OnDataChannelReady -= this.OnDataChannelReadyAsync;
        this.GameService.OnStateChangedAsync -= this.OnGameStateChangedAsync;
        await this.WebRtcService.DisposeAsync();
        GC.SuppressFinalize(this);
    }
}