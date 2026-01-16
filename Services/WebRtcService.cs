using Microsoft.JSInterop;
using ShogiGame.Models;
using System.Text.Json;

namespace ShogiGame.Services;

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

    public ConnectionState State { get; private set; } = ConnectionState.Disconnected;
    public bool IsConnected => State == ConnectionState.Connected;

    public event Action<Move>? OnMoveReceived;
    public event Action<ConnectionState>? OnStateChanged;
    public event Action? OnGameStart;

    public async Task InitializeAsync()
    {
        _dotNetRef = DotNetObjectReference.Create(this);
        await jsRuntime.InvokeVoidAsync("WebRtc.initialize", _dotNetRef);
    }

    public async Task<string> CreateOfferAsync() =>
        await jsRuntime.InvokeAsync<string>("WebRtc.createOffer");

    public async Task<string> CreateAnswerAsync(string offer) =>
        await jsRuntime.InvokeAsync<string>("WebRtc.createAnswer", offer);

    public async Task AcceptAnswerAsync(string answer) =>
        await jsRuntime.InvokeVoidAsync("WebRtc.acceptAnswer", answer);

    public async Task SendMoveAsync(Move move)
    {
        var json = JsonSerializer.Serialize(new MoveMessage { Type = "move", Move = move });
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
        if (State != ConnectionState.Connected)
        {
            State = ConnectionState.Connected;
            OnStateChanged?.Invoke(State);
        }
    }

    [JSInvokable]
    public void OnDataChannelClose()
    {
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
                    var moveMessage = JsonSerializer.Deserialize<MoveMessage>(message);
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
