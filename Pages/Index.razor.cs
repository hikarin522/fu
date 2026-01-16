using Microsoft.AspNetCore.Components;

using ShogiGame.Models;
using ShogiGame.Services;

namespace ShogiGame.Pages;

public partial class Index : IDisposable
{
    [Inject] private ShogiGameService GameService { get; set; } = null!;
    [Inject] private WebRtcService WebRtcService { get; set; } = null!;

    private bool IsFlipped => this.GameService.State.LocalPlayer == Player.Gote;
    private string? InitError { get; set; }

    protected override async Task OnInitializedAsync()
    {
        try {
            await this.WebRtcService.InitializeAsync();
            this.WebRtcService.OnMoveReceived += this.OnRemoteMoveReceived;
            this.WebRtcService.OnGameStart += this.OnRemoteGameStart;
            this.WebRtcService.OnDataChannelReady += this.OnDataChannelReady;
            this.GameService.OnStateChanged += this.OnGameStateChanged;
        }
        catch (Exception ex) {
            this.InitError = ex.Message;
            Console.WriteLine($"Init error: {ex}");
        }
    }

    private void OnPlayerAssigned(bool isSente)
    {
        var player = isSente ? Player.Sente : Player.Gote;
        Console.WriteLine($"OnPlayerAssigned: isSente={isSente}, setting LocalPlayer to {player}");
        this.GameService.SetLocalPlayer(player);
    }

    private async Task OnConnected(bool connected)
    {
        Console.WriteLine($"OnConnected: connected={connected}, LocalPlayer={this.GameService.State.LocalPlayer}");
        // DataChannelの準備完了を待つため、ここではゲーム開始しない
        await this.InvokeAsync(this.StateHasChanged);
    }

    private void OnDataChannelReady()
    {
        Console.WriteLine($"OnDataChannelReady: LocalPlayer={this.GameService.State.LocalPlayer}");
        this.InvokeAsync(async () => {
            if (this.GameService.State.LocalPlayer is Player.Sente) {
                Console.WriteLine("Sente: DataChannel ready, starting new game and sending gameStart");
                this.GameService.NewGame();
                await this.WebRtcService.SendGameStartAsync();
            }
            else {
                Console.WriteLine("Gote: DataChannel ready, waiting for gameStart from Sente");
            }
            this.StateHasChanged();
        });
    }

    private void OnRemoteGameStart()
    {
        Console.WriteLine($"OnRemoteGameStart received, LocalPlayer={this.GameService.State.LocalPlayer}");
        this.GameService.NewGame();
        Console.WriteLine($"After NewGame: Status={this.GameService.State.Status}, LocalPlayer={this.GameService.State.LocalPlayer}");
        this.InvokeAsync(this.StateHasChanged);
    }

    private async Task OnMoveMade(Move move)
    {
        Console.WriteLine($"OnMoveMade: {move.ToNotation()}, sending to opponent");
        await this.WebRtcService.SendMoveAsync(move);
        this.StateHasChanged();
    }

    private void OnRemoteMoveReceived(Move move)
    {
        Console.WriteLine($"OnRemoteMoveReceived: {move.ToNotation()}");
        this.GameService.ApplyRemoteMove(move);
        Console.WriteLine($"After ApplyRemoteMove: CurrentPlayer={this.GameService.State.CurrentPlayer}");
        this.InvokeAsync(this.StateHasChanged);
    }

    private void OnGameStateChanged() => this.InvokeAsync(this.StateHasChanged);

    private async Task NewGame()
    {
        this.GameService.NewGame();
        await this.WebRtcService.SendGameStartAsync();
    }

    private void Resign() => this.GameService.Resign();

    public void Dispose()
    {
        this.WebRtcService.OnMoveReceived -= this.OnRemoteMoveReceived;
        this.WebRtcService.OnGameStart -= this.OnRemoteGameStart;
        this.WebRtcService.OnDataChannelReady -= this.OnDataChannelReady;
        this.GameService.OnStateChanged -= this.OnGameStateChanged;
        GC.SuppressFinalize(this);
    }
}