using Microsoft.JSInterop;
using ShogiGame.Models;
using System.Text.Json;

namespace ShogiGame.Services;

// .NET 10対応: デシリアライズオプション
internal static class JsonConfig
{
    public static readonly JsonSerializerOptions Options = new()
    {
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

// Primary constructor (C# 12+)
public class WebRtcService(IJSRuntime jsRuntime) : IAsyncDisposable
{
    private DotNetObjectReference<WebRtcService>? _dotNetRef;
    private bool _dataChannelOpen;

    public ConnectionState State { get; private set; } = ConnectionState.Disconnected;
    public bool IsConnected => State == ConnectionState.Connected && _dataChannelOpen;

    public event Action<Move>? OnMoveReceived;
    public event Action<ConnectionState>? OnStateChanged;
    public event Action? OnGameStart;
    public event Action? OnDataChannelReady;  // DataChannelが開いた時に発火

    public async Task InitializeAsync()
    {
        _dotNetRef = DotNetObjectReference.Create(this);
        await jsRuntime.InvokeVoidAsync("WebRtc.initialize", _dotNetRef);
    }

    // PeerJS: ルームを作成（先手用）- 6文字のルームIDを返す
    public async Task<string> CreateRoomAsync() =>
        await jsRuntime.InvokeAsync<string>("WebRtc.createRoom");

    // PeerJS: ルームに参加（後手用）
    public async Task JoinRoomAsync(string roomId) =>
        await jsRuntime.InvokeVoidAsync("WebRtc.joinRoom", roomId);

    public async Task SendMoveAsync(Move move)
    {
        var json = JsonSerializer.Serialize(new MoveMessage { Type = "move", Move = move }, JsonConfig.Options);
        Console.WriteLine($"SendMoveAsync: sending {json}");
        await jsRuntime.InvokeVoidAsync("WebRtc.sendMessage", json);
    }

    public async Task SendGameStartAsync()
    {
        var json = JsonSerializer.Serialize(new { Type = "gameStart" });
        Console.WriteLine($"SendGameStartAsync: sending {json}");
        await jsRuntime.InvokeVoidAsync("WebRtc.sendMessage", json);
    }

    [JSInvokable]
    public void OnConnectionStateChanged(string state)
    {
        var newState = state switch
        {
            "connected" => ConnectionState.Connected,
            "connecting" => ConnectionState.Connecting,
            _ => ConnectionState.Disconnected
        };
        if (State != newState)
        {
            State = newState;
            OnStateChanged?.Invoke(State);
        }
    }

    [JSInvokable]
    public void OnDataChannelOpen()
    {
        Console.WriteLine("OnDataChannelOpen called");
        _dataChannelOpen = true;
        if (State != ConnectionState.Connected)
        {
            State = ConnectionState.Connected;
            OnStateChanged?.Invoke(State);
        }
        OnDataChannelReady?.Invoke();
    }

    [JSInvokable]
    public void OnDataChannelClose()
    {
        Console.WriteLine("OnDataChannelClose called");
        _dataChannelOpen = false;
        if (State != ConnectionState.Disconnected)
        {
            State = ConnectionState.Disconnected;
            OnStateChanged?.Invoke(State);
        }
    }

    [JSInvokable]
    public void OnMessageReceived(string message)
    {
        try
        {
            using var doc = JsonDocument.Parse(message);
            var type = doc.RootElement.GetProperty("Type").GetString();

            Console.WriteLine($"OnMessageReceived: type={type}");
            switch (type)
            {
                case "move":
                    Console.WriteLine($"Deserializing move message: {message}");
                    var moveMessage = JsonSerializer.Deserialize<MoveMessage>(message, JsonConfig.Options);
                    Console.WriteLine($"Deserialized: moveMessage={moveMessage is not null}, Move={moveMessage?.Move is not null}");
                    if (moveMessage?.Move is { } move)  // Pattern matching with property pattern
                    {
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
        catch (Exception ex)
        {
            Console.WriteLine($"Error parsing message: {ex.Message}");
        }
    }

    public async Task DisconnectAsync()
    {
        await jsRuntime.InvokeVoidAsync("WebRtc.disconnect");
        State = ConnectionState.Disconnected;
        OnStateChanged?.Invoke(State);
    }

    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync();
        _dotNetRef?.Dispose();
    }

    private sealed class MoveMessage  // sealed for performance
    {
        public string Type { get; set; } = "";
        public Move? Move { get; set; }
    }
}
