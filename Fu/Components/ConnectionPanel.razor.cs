using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

using Fu.Core.Models;
using Fu.Services;

namespace Fu.Components;

public partial class ConnectionPanel : IDisposable
{
    [Parameter] public WebRtcService WebRtcService { get; set; } = null!;
    [Parameter] public EventCallback OnConnected { get; set; }
    [Parameter] public string? InitialRoomId { get; set; }
    [Parameter] public string BaseUrl { get; set; } = "";

    [Inject] private IJSRuntime JS { get; set; } = null!;

    private ConnectionState ConnectionState => this.WebRtcService?.State ?? ConnectionState.Disconnected;

    private string Nickname { get; set; } = "";
    private string InputNickname { get; set; } = "";
    private string RoomId { get; set; } = "";
    private string InputRoomId { get; set; } = "";
    private string ErrorMessage { get; set; } = "";
    private bool IsProcessing { get; set; }
    private bool HasNotifiedConnected { get; set; }
    private bool IsJoiningRoom { get; set; }

    private string RoomUrl => string.IsNullOrEmpty(this.RoomId) ? "" : $"{this.BaseUrl}{this.RoomId}";
    private bool HasInitialRoomId => !string.IsNullOrEmpty(this.InitialRoomId);

    protected override void OnInitialized()
    {
        if (this.WebRtcService is not null) {
            this.WebRtcService.OnStateChanged += this.OnStateChangedAsync;
            this.WebRtcService.OnParticipantJoined += this.OnParticipantChangedAsync;
            this.WebRtcService.OnParticipantLeft += this.OnParticipantLeftAsync;
        }

        // URL パラメータからルーム ID が指定されている場合は設定
        if (this.HasInitialRoomId) {
            this.InputRoomId = this.InitialRoomId!.ToUpperInvariant();
        }
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender) {
            // localStorage から前回のニックネームを読み込む
            var savedNickname = await this.JS.InvokeAsync<string>("NicknameStorage.load");
            if (!string.IsNullOrEmpty(savedNickname)) {
                this.InputNickname = savedNickname;
            }

            // sessionStorage から再接続用のルームIDを確認
            var reconnectRoomId = await this.JS.InvokeAsync<string>("eval", @"
                (function() {
                    const roomId = sessionStorage.getItem('reconnect-room-id');
                    sessionStorage.removeItem('reconnect-room-id');
                    return roomId || '';
                })()
            ");

            if (!string.IsNullOrEmpty(reconnectRoomId) && !string.IsNullOrEmpty(savedNickname)) {
                // 自動再接続
                this.InputRoomId = reconnectRoomId;
                this.Nickname = savedNickname;
                this.StateHasChanged();
                await this.JoinRoom();
            }
            else {
                this.StateHasChanged();
            }
        }
    }

    private async Task OnStateChangedAsync(ConnectionState state)
    {
        await this.InvokeAsync(async () => {
            if (state == ConnectionState.Connected && !this.HasNotifiedConnected) {
                this.HasNotifiedConnected = true;
                await this.OnConnected.InvokeAsync();
            }
            this.StateHasChanged();
        });
    }

    private Task OnParticipantChangedAsync(Participant participant)
    {
        return this.InvokeAsync(this.StateHasChanged);
    }

    private Task OnParticipantLeftAsync(string peerId)
    {
        return this.InvokeAsync(this.StateHasChanged);
    }

    private async Task ShowJoinForm()
    {
        this.Nickname = this.InputNickname.Trim();
        await this.SaveNicknameAsync(this.Nickname);
        this.IsJoiningRoom = true;
    }

    private async Task SaveNicknameAsync(string nickname)
    {
        await this.JS.InvokeVoidAsync("NicknameStorage.save", nickname);
    }

    private Task CreateRoomWithNickname() => this.ExecuteWithProcessing(async () => {
        this.Nickname = this.InputNickname.Trim();
        await this.SaveNicknameAsync(this.Nickname);
        this.RoomId = (await this.WebRtcService.CreateRoomAsync(this.Nickname)).AsPrimitive();
    });

    private Task JoinRoomWithNickname() => this.ExecuteWithProcessing(async () => {
        this.Nickname = this.InputNickname.Trim();
        await this.SaveNicknameAsync(this.Nickname);
        await this.WebRtcService.JoinRoomAsync(
            new RoomId(this.InputRoomId.Trim().ToUpperInvariant()),
            this.Nickname);
    });

    private Task JoinRoom() => this.ExecuteWithProcessing(async () => {
        if (string.IsNullOrWhiteSpace(this.InputRoomId)) {
            throw new InvalidOperationException("ルームIDを入力してください");
        }
        await this.WebRtcService.JoinRoomAsync(
            new RoomId(this.InputRoomId.Trim().ToUpperInvariant()),
            this.Nickname);
    });

    private async Task ExecuteWithProcessing(Func<Task> action)
    {
        try {
            this.IsProcessing = true;
            this.ErrorMessage = "";
            this.StateHasChanged();
            await action();
        }
        catch (Exception ex) {
            this.ErrorMessage = $"エラー: {ex.Message}";
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
        this.ResetAll();
    }

    private void ResetAll()
    {
        this.Nickname = "";
        this.InputNickname = "";
        this.RoomId = "";
        this.InputRoomId = "";
        this.ErrorMessage = "";
        this.IsJoiningRoom = false;
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
            this.WebRtcService.OnStateChanged -= this.OnStateChangedAsync;
            this.WebRtcService.OnParticipantJoined -= this.OnParticipantChangedAsync;
            this.WebRtcService.OnParticipantLeft -= this.OnParticipantLeftAsync;
        }
        GC.SuppressFinalize(this);
    }
}
