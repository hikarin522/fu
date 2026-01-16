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

public class WebRtcService : IAsyncDisposable
{
    private readonly IJSRuntime _jsRuntime;
    private DotNetObjectReference<WebRtcService>? _dotNetRef;

    public ConnectionState State { get; private set; } = ConnectionState.Disconnected;
    public bool IsConnected => State == ConnectionState.Connected;

    public event Action<Move>? OnMoveReceived;
    public event Action<ConnectionState>? OnStateChanged;
    public event Action? OnGameStart;

    public WebRtcService(IJSRuntime jsRuntime)
    {
        _jsRuntime = jsRuntime;
    }

    public async Task InitializeAsync()
    {
        _dotNetRef = DotNetObjectReference.Create(this);
        await _jsRuntime.InvokeVoidAsync("WebRtc.initialize", _dotNetRef);
    }

    public async Task<string> CreateOfferAsync()
    {
        // Offer生成中は状態を変えない（UIを維持）
        var offer = await _jsRuntime.InvokeAsync<string>("WebRtc.createOffer");
        return offer;
    }

    public async Task<string> CreateAnswerAsync(string offer)
    {
        // Answer生成中は状態を変えない（UIを維持）
        var answer = await _jsRuntime.InvokeAsync<string>("WebRtc.createAnswer", offer);
        return answer;
    }

    public async Task AcceptAnswerAsync(string answer)
    {
        await _jsRuntime.InvokeVoidAsync("WebRtc.acceptAnswer", answer);
    }

    public async Task SendMoveAsync(Move move)
    {
        var json = JsonSerializer.Serialize(new MoveMessage
        {
            Type = "move",
            Move = move
        });
        await _jsRuntime.InvokeVoidAsync("WebRtc.sendMessage", json);
    }

    public async Task SendGameStartAsync()
    {
        var json = JsonSerializer.Serialize(new { Type = "gameStart" });
        Console.WriteLine($"SendGameStartAsync: sending {json}");
        await _jsRuntime.InvokeVoidAsync("WebRtc.sendMessage", json);
    }

    [JSInvokable]
    public void OnConnectionStateChanged(string state)
    {
        State = state switch
        {
            "connected" => ConnectionState.Connected,
            "connecting" => ConnectionState.Connecting,
            _ => ConnectionState.Disconnected
        };
        OnStateChanged?.Invoke(State);
    }

    [JSInvokable]
    public void OnDataChannelOpen()
    {
        State = ConnectionState.Connected;
        OnStateChanged?.Invoke(State);
    }

    [JSInvokable]
    public void OnDataChannelClose()
    {
        State = ConnectionState.Disconnected;
        OnStateChanged?.Invoke(State);
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
                    var moveMessage = JsonSerializer.Deserialize<MoveMessage>(message);
                    if (moveMessage?.Move != null)
                    {
                        OnMoveReceived?.Invoke(moveMessage.Move);
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
        await _jsRuntime.InvokeVoidAsync("WebRtc.disconnect");
        State = ConnectionState.Disconnected;
        OnStateChanged?.Invoke(State);
    }

    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync();
        _dotNetRef?.Dispose();
    }

    private class MoveMessage
    {
        public string Type { get; set; } = "";
        public Move? Move { get; set; }
    }
}
