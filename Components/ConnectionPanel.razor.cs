using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

using ShogiGame.Models;
using ShogiGame.Services;

namespace ShogiGame.Components;

public partial class ConnectionPanel : IDisposable
{
    [Parameter] public WebRtcService WebRtcService { get; set; } = null!;
    [Parameter] public EventCallback<bool> OnConnected { get; set; }
    [Parameter] public EventCallback<bool> OnPlayerAssigned { get; set; }

    [Inject] private IJSRuntime JS { get; set; } = null!;

    private ConnectionState ConnectionState => this.WebRtcService?.State ?? ConnectionState.Disconnected;

    private Player? SelectedPlayer { get; set; }
    private string RoomId { get; set; } = "";
    private string InputRoomId { get; set; } = "";
    private string ErrorMessage { get; set; } = "";
    private bool IsProcessing { get; set; }
    private bool HasNotifiedConnected { get; set; }

    private void SelectPlayer(Player player) => this.SelectedPlayer = player;

    private void ResetSelection()
    {
        this.SelectedPlayer = null;
        this.ClearAll();
    }

    protected override void OnInitialized()
    {
        if (this.WebRtcService is not null) {
            this.WebRtcService.OnStateChanged += this.OnStateChanged;
        }
    }

    private void OnStateChanged(ConnectionState state)
    {
        this.InvokeAsync(async () => {
            if (state == ConnectionState.Connected && !this.HasNotifiedConnected) {
                this.HasNotifiedConnected = true;
                await this.OnConnected.InvokeAsync(true);
            }
            this.StateHasChanged();
        });
    }

    private async Task CreateRoom()
    {
        try {
            this.IsProcessing = true;
            this.ErrorMessage = "";
            this.StateHasChanged();
            this.RoomId = (await this.WebRtcService.CreateRoomAsync()).AsPrimitive();
            await this.OnPlayerAssigned.InvokeAsync(true);  // Sente
        }
        catch (Exception ex) {
            this.ErrorMessage = $"エラー: {ex.Message}";
            Console.WriteLine($"CreateRoom error: {ex}");
        }
        finally {
            this.IsProcessing = false;
            this.StateHasChanged();
        }
    }

    private async Task JoinRoom()
    {
        try {
            this.IsProcessing = true;
            this.ErrorMessage = "";
            this.StateHasChanged();
            await this.WebRtcService.JoinRoomAsync(new RoomId(this.InputRoomId.ToUpperInvariant()));
            await this.OnPlayerAssigned.InvokeAsync(false);  // Gote
        }
        catch (Exception ex) {
            this.ErrorMessage = $"エラー: {ex.Message}";
            Console.WriteLine($"JoinRoom error: {ex}");
        }
        finally {
            this.IsProcessing = false;
            this.StateHasChanged();
        }
    }

    private async Task Disconnect()
    {
        await this.WebRtcService.DisconnectAsync();
        this.HasNotifiedConnected = false;
        this.ClearAll();
    }

    private void ClearAll()
    {
        this.RoomId = "";
        this.InputRoomId = "";
        this.ErrorMessage = "";
    }

    private async Task CopyToClipboard(string text)
    {
        await this.JS.InvokeVoidAsync("navigator.clipboard.writeText", text);
    }

    private string GetStatusText() => this.ConnectionState switch {
        ConnectionState.Disconnected => "未接続",
        ConnectionState.Connecting => "接続中",
        ConnectionState.Connected => "接続済み",
        _ => "不明"
    };

    public void Dispose()
    {
        if (this.WebRtcService is not null) {
            this.WebRtcService.OnStateChanged -= this.OnStateChanged;
        }
        GC.SuppressFinalize(this);
    }
}