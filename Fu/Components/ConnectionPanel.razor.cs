using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

using R3;

using Fu.Core.Abstractions;
using Fu.Core.Models;
using Fu.Services;

namespace Fu.Components;

public partial class ConnectionPanel : IDisposable
{
    private readonly CompositeDisposable _disposables = [];

    [Parameter] public EventCallback OnConnected { get; set; }
    [Parameter] public string? InitialRoomId { get; set; }
    [Parameter] public string BaseUrl { get; set; } = "";

    [Inject] private LobbyService Lobby { get; set; } = null!;
    [Inject] private IJSRuntime JS { get; set; } = null!;
    [Inject] private NavigationManager Navigation { get; set; } = null!;

    private TransportConnectionState ConnectionState => this.Lobby?.ConnectionState ?? TransportConnectionState.Disconnected;

    private string Nickname { get; set; } = "";
    private string InputNickname { get; set; } = "";
    private RoomId? RoomId { get; set; }
    private string InputRoomId { get; set; } = "";
    private string ErrorMessage { get; set; } = "";
    private bool IsProcessing { get; set; }
    private bool HasNotifiedConnected { get; set; }
    private bool IsJoiningRoom { get; set; }
    private string? SavedPeerId { get; set; }

    private string RoomUrl => this.RoomId is null ? "" : $"{this.BaseUrl}{this.RoomId.Value.AsPrimitive()}";
    private bool HasInitialRoomId => !string.IsNullOrEmpty(this.InitialRoomId);

    protected override void OnInitialized()
    {
        // R3 で購読
        this.Lobby.ConnectionStateChanged
            .Subscribe(this.OnStateChanged)
            .AddTo(this._disposables);

        this.Lobby.ParticipantJoined
            .Subscribe(_ => this.InvokeAsync(this.StateHasChanged))
            .AddTo(this._disposables);

        this.Lobby.ParticipantLeft
            .Subscribe(_ => this.InvokeAsync(this.StateHasChanged))
            .AddTo(this._disposables);

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

    private void OnStateChanged(TransportConnectionState state)
    {
        this.InvokeAsync(async () => {
            if (state == TransportConnectionState.Connected && !this.HasNotifiedConnected) {
                this.HasNotifiedConnected = true;
                await this.OnConnected.InvokeAsync();
            }
            this.StateHasChanged();
        });
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
        this.RoomId = await this.Lobby.CreateRoomAsync(this.Nickname);
        this.UpdateUrlWithRoomId(this.RoomId!.Value);
    });

    private Task JoinRoomWithNickname() => this.ExecuteWithProcessing(async () => {
        this.Nickname = this.InputNickname.Trim();
        await this.SaveNicknameAsync(this.Nickname);
        this.RoomId = new RoomId(this.InputRoomId.Trim().ToUpperInvariant());
        await this.Lobby.JoinRoomAsync(this.RoomId.Value, this.Nickname);
        this.UpdateUrlWithRoomId(this.RoomId.Value);
    });

    private Task JoinRoom() => this.ExecuteWithProcessing(async () => {
        if (string.IsNullOrWhiteSpace(this.InputRoomId)) {
            throw new InvalidOperationException("ルームIDを入力してください");
        }
        this.RoomId = new RoomId(this.InputRoomId.Trim().ToUpperInvariant());
        await this.Lobby.JoinRoomAsync(this.RoomId.Value, this.Nickname, this.SavedPeerId);
        this.UpdateUrlWithRoomId(this.RoomId.Value);
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
        await this.Lobby.DisconnectAsync();
        this.HasNotifiedConnected = false;
        this.ResetAll();
    }

    private void ResetAll()
    {
        this.Nickname = "";
        this.InputNickname = "";
        this.RoomId = null;
        this.InputRoomId = "";
        this.ErrorMessage = "";
        this.IsJoiningRoom = false;
    }

    private async Task CopyToClipboard(string text)
    {
        await this.JS.InvokeVoidAsync("navigator.clipboard.writeText", text);
    }

    private void UpdateUrlWithRoomId(RoomId roomId)
    {
        // URLバーを更新（ページリロードなし）
        var newUrl = $"{this.Navigation.BaseUri}{roomId.AsPrimitive()}";
        this.Navigation.NavigateTo(newUrl, forceLoad: false, replace: true);
    }

    private string GetStatusText() => this.ConnectionState switch {
        TransportConnectionState.Disconnected => "未接続",
        TransportConnectionState.Connecting => "接続中",
        TransportConnectionState.Connected => "接続済み",
        _ => "不明"
    };

    public void Dispose()
    {
        this._disposables.Dispose();
        GC.SuppressFinalize(this);
    }
}
