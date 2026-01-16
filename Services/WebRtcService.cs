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

    public event Action<Move>? OnMoveReceived;
    public event Action<ConnectionState>? OnStateChanged;
    public event Action? OnGameStart;
    public event Action? OnDataChannelReady;

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
        Console.WriteLine($"SendMoveAsync: sending {json}");
        await jsRuntime.InvokeVoidAsync("WebRtc.sendMessage", json);
    }

    public async Task SendGameStartAsync()
    {
        var message = new GameStartMessage();
        var json = JsonSerializer.Serialize(message, JsonConfig.Options);
        Console.WriteLine($"SendGameStartAsync: sending {json}");
        await jsRuntime.InvokeVoidAsync("WebRtc.sendMessage", json);
    }

    [JSInvokable]
    public void OnConnectionStateChanged(string state)
    {
        var newState = state switch {
            "connected" => ConnectionState.Connected,
            "connecting" => ConnectionState.Connecting,
            _ => ConnectionState.Disconnected
        };
        if (this.State != newState) {
            this.State = newState;
            OnStateChanged?.Invoke(this.State);
        }
    }

    [JSInvokable]
    public void OnDataChannelOpen()
    {
        Console.WriteLine("OnDataChannelOpen called");
        this._dataChannelOpen = true;
        if (this.State != ConnectionState.Connected) {
            this.State = ConnectionState.Connected;
            OnStateChanged?.Invoke(this.State);
        }
        OnDataChannelReady?.Invoke();
    }

    [JSInvokable]
    public void OnDataChannelClose()
    {
        Console.WriteLine("OnDataChannelClose called");
        this._dataChannelOpen = false;
        if (this.State != ConnectionState.Disconnected) {
            this.State = ConnectionState.Disconnected;
            OnStateChanged?.Invoke(this.State);
        }
    }

    [JSInvokable]
    public void OnMessageReceived(string message)
    {
        try {
            using var doc = JsonDocument.Parse(message);
            var type = doc.RootElement.GetProperty("Type").GetString();

            Console.WriteLine($"OnMessageReceived: type={type}");
            switch (type) {
                case "move":
                    Console.WriteLine($"Deserializing move message: {message}");
                    var moveMessage = JsonSerializer.Deserialize<MoveMessage>(message, JsonConfig.Options);
                    Console.WriteLine($"Deserialized: moveMessage={moveMessage is not null}");
                    if (moveMessage is { } msg) {
                        var move = Move.FromDto(msg.Move);
                        Console.WriteLine($"Move details: From=({move.From?.Col},{move.From?.Row}) To=({move.To.Col},{move.To.Row}) PieceType={move.PieceType}");
                        Console.WriteLine($"Invoking OnMoveReceived");
                        OnMoveReceived?.Invoke(move);
                    }
                    break;

                case "gameStart":
                    Console.WriteLine("Received gameStart, invoking OnGameStart");
                    OnGameStart?.Invoke();
                    break;
            }
        }
        catch (Exception ex) {
            Console.WriteLine($"Error parsing message: {ex.Message}");
        }
    }

    public async Task DisconnectAsync()
    {
        await jsRuntime.InvokeVoidAsync("WebRtc.disconnect");
        this.State = ConnectionState.Disconnected;
        OnStateChanged?.Invoke(this.State);
    }

    public async ValueTask DisposeAsync()
    {
        await this.DisconnectAsync();
        this._dotNetRef?.Dispose();
        GC.SuppressFinalize(this);
    }
}