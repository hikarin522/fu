using System.Text.Json;

using Microsoft.JSInterop;

using ShogiGame.Models;
using ShogiGame.Models.Dto;

namespace ShogiGame.Services;

internal static class JsonConfig
{
    public static readonly JsonSerializerOptions Options = new() {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = null  // PascalCase維持
    };
}

public enum ConnectionState
{
    Disconnected,
    Connecting,
    Connected
}

public class WebRtcService(IJSRuntime jsRuntime) : IAsyncDisposable
{
    private DotNetObjectReference<WebRtcService>? _dotNetRef;
    private bool _dataChannelOpen;

    public ConnectionState State { get; private set; } = ConnectionState.Disconnected;
    public bool IsConnected => this.State == ConnectionState.Connected && this._dataChannelOpen;

    public event Func<Move, Task>? OnMoveReceived;
    public event Func<ConnectionState, Task>? OnStateChanged;
    public event Func<Task>? OnGameStart;
    public event Func<Task>? OnDataChannelReady;

    public async Task InitializeAsync()
    {
        this._dotNetRef = DotNetObjectReference.Create(this);
        await jsRuntime.InvokeVoidAsync("WebRtc.initialize", this._dotNetRef);
    }

    /// <summary>ルームを作成（先手用）- 6文字のルームIDを返す</summary>
    public async Task<RoomId> CreateRoomAsync()
    {
        var id = await jsRuntime.InvokeAsync<string>("WebRtc.createRoom");
        return new RoomId(id);
    }

    /// <summary>ルームに参加（後手用）</summary>
    public async Task JoinRoomAsync(RoomId roomId) =>
        await jsRuntime.InvokeVoidAsync("WebRtc.joinRoom", roomId.AsPrimitive());

    public async Task SendMoveAsync(Move move)
    {
        var message = new MoveMessage(move.ToDto());
        var json = JsonSerializer.Serialize(message, JsonConfig.Options);
        await jsRuntime.InvokeVoidAsync("WebRtc.sendMessage", json);
    }

    public async Task SendGameStartAsync()
    {
        var message = new GameStartMessage();
        var json = JsonSerializer.Serialize(message, JsonConfig.Options);
        await jsRuntime.InvokeVoidAsync("WebRtc.sendMessage", json);
    }

    private async Task SetStateAsync(ConnectionState newState)
    {
        if (this.State != newState) {
            this.State = newState;
            if (OnStateChanged is { } handler) {
                await handler(this.State);
            }
        }
    }

    [JSInvokable]
    public Task OnConnectionStateChanged(string state)
    {
        var newState = state.ToLowerInvariant() switch {
            "connected" => ConnectionState.Connected,
            "connecting" => ConnectionState.Connecting,
            _ => ConnectionState.Disconnected
        };
        return this.SetStateAsync(newState);
    }

    [JSInvokable]
    public async Task OnDataChannelOpen()
    {
        this._dataChannelOpen = true;
        await this.SetStateAsync(ConnectionState.Connected);
        if (OnDataChannelReady is { } handler) {
            await handler();
        }
    }

    [JSInvokable]
    public Task OnDataChannelClose()
    {
        this._dataChannelOpen = false;
        return this.SetStateAsync(ConnectionState.Disconnected);
    }

    [JSInvokable]
    public async Task OnMessageReceived(string message)
    {
        try {
            using var doc = JsonDocument.Parse(message);
            var type = doc.RootElement.GetProperty("Type").GetString();

            switch (type) {
                case "move":
                    var moveMessage = JsonSerializer.Deserialize<MoveMessage>(message, JsonConfig.Options);
                    if (moveMessage is { } msg) {
                        var move = Move.FromDto(msg.Move);
                        if (OnMoveReceived is { } moveHandler) {
                            await moveHandler(move);
                        }
                    }
                    break;

                case "gameStart":
                    if (OnGameStart is { } startHandler) {
                        await startHandler();
                    }
                    break;
            }
        }
        catch (JsonException) {
            // Ignore JSON parse errors from malformed messages
        }
    }

    public async Task DisconnectAsync()
    {
        await jsRuntime.InvokeVoidAsync("WebRtc.disconnect");
        await this.SetStateAsync(ConnectionState.Disconnected);
    }

    public async ValueTask DisposeAsync()
    {
        await this.DisconnectAsync();
        this._dotNetRef?.Dispose();
        GC.SuppressFinalize(this);
    }
}