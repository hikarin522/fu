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
    [Inject] private NavigationManager Navigation { get; set; } = null!;

    private ConnectionState ConnectionState => this.WebRtcService?.State ?? ConnectionState.Disconnected;

    private string Nickname { get; set; } = "";
    private string InputNickname { get; set; } = "";
    private string RoomId { get; set; } = "";
    private string InputRoomId { get; set; } = "";
    private string ErrorMessage { get; set; } = "";
    private bool IsProcessing { get; set; }
    private bool HasNotifiedConnected { get; set; }
    private bool IsJoiningRoom { get; set; }
    private string? SavedPeerId { get; set; }

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

            // GameSession から再接続情報を確認（招待リンクと一致する場合のみ自動再接続）
            var session = await this.JS.InvokeAsync<GameSessionData?>("GameSession.load");
            var shouldAutoReconnect = session is not null
                && !string.IsNullOrEmpty(session.Nickname)
                && this.HasInitialRoomId
                && string.Equals(session.RoomId, this.InitialRoomId, StringComparison.OrdinalIgnoreCase);

            if (shouldAutoReconnect) {
                // 自動再接続（招待リンクのroomIdとセッションのroomIdが一致）
                this.InputRoomId = session!.RoomId;
                this.Nickname = session.Nickname;
                this.InputNickname = session.Nickname;
                this.SavedPeerId = session.PeerId; // 保存されたPeerIDを再利用
                this.StateHasChanged();
                await this.JoinRoom();
            }
            else {
                this.StateHasChanged();
            }
        }
    }

    private sealed record GameSessionData(string RoomId, string Nickname, string? PeerId, long Timestamp);

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
        this.UpdateUrlWithRoomId(this.RoomId);
    });

    private Task JoinRoomWithNickname() => this.ExecuteWithProcessing(async () => {
        this.Nickname = this.InputNickname.Trim();
        await this.SaveNicknameAsync(this.Nickname);
        var roomId = this.InputRoomId.Trim().ToUpperInvariant();
        await this.WebRtcService.JoinRoomAsync(new RoomId(roomId), this.Nickname);
        this.UpdateUrlWithRoomId(roomId);
    });

    private Task JoinRoom() => this.ExecuteWithProcessing(async () => {
        if (string.IsNullOrWhiteSpace(this.InputRoomId)) {
            throw new InvalidOperationException("ルームIDを入力してください");
        }
        var roomId = this.InputRoomId.Trim().ToUpperInvariant();
        await this.WebRtcService.JoinRoomAsync(new RoomId(roomId), this.Nickname, this.SavedPeerId);
        this.UpdateUrlWithRoomId(roomId);
        this.SavedPeerId = null; // 使用後はクリア
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

    private void UpdateUrlWithRoomId(string roomId)
    {
        // URLバーを更新（ページリロードなし）
        var newUrl = $"{this.Navigation.BaseUri}{roomId}";
        this.Navigation.NavigateTo(newUrl, forceLoad: false, replace: true);
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
