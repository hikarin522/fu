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

/// <summary>ルーム参加者の情報</summary>
public record Participant(string PeerId, string Nickname, bool IsHost);

public class WebRtcService(IJSRuntime jsRuntime) : IAsyncDisposable
{
    private DotNetObjectReference<WebRtcService>? _dotNetRef;
    private bool _dataChannelOpen;
    private readonly Dictionary<string, Participant> _participants = [];

    public ConnectionState State { get; private set; } = ConnectionState.Disconnected;
    public bool IsConnected => this.State == ConnectionState.Connected || this._dataChannelOpen;
    public bool IsHost { get; private set; }
    public string? MyPeerId { get; private set; }
    public string? MyNickname { get; private set; }
    public IReadOnlyCollection<Participant> Participants => this._participants.Values;

    public event Func<Move, Task>? OnMoveReceived;
    public event Func<ConnectionState, Task>? OnStateChanged;
    public event Func<Task>? OnGameStart;
    public event Func<Task>? OnDataChannelReady;
    public event Func<Participant, Task>? OnParticipantJoined;
    public event Func<string, Task>? OnParticipantLeft;
    public event Func<GameStartInfo, Task>? OnGameStartWithPlayers;
    public event Func<Task>? OnResignReceived;

    public async Task InitializeAsync()
    {
        this._dotNetRef = DotNetObjectReference.Create(this);
        await jsRuntime.InvokeVoidAsync("WebRtc.initialize", this._dotNetRef);
    }

    /// <summary>ルームを作成（ホスト用）- 6文字のルームIDを返す</summary>
    public async Task<RoomId> CreateRoomAsync(string nickname)
    {
        this.IsHost = true;
        this.MyNickname = nickname;
        var id = await jsRuntime.InvokeAsync<string>("WebRtc.createRoom", nickname);
        this.MyPeerId = id;
        return new RoomId(id);
    }

    /// <summary>ルームに参加</summary>
    public async Task JoinRoomAsync(RoomId roomId, string nickname)
    {
        this.IsHost = false;
        this.MyNickname = nickname;
        await jsRuntime.InvokeVoidAsync("WebRtc.joinRoom", roomId.AsPrimitive(), nickname);
        this.MyPeerId = await jsRuntime.InvokeAsync<string>("WebRtc.getMyPeerId");
    }

    public async Task SendMoveAsync(Move move)
    {
        var message = new MoveMessage(move.ToDto());
        var json = JsonSerializer.Serialize(message, JsonConfig.Options);
        await jsRuntime.InvokeVoidAsync("WebRtc.sendMessage", json);
    }

    public async Task SendGameStartAsync(string sentePeerId, string gotePeerId)
    {
        var message = new GameStartWithPlayersMessage(sentePeerId, gotePeerId);
        var json = JsonSerializer.Serialize(message, JsonConfig.Options);
        await jsRuntime.InvokeVoidAsync("WebRtc.sendMessage", json);
    }

    public async Task SendGameStartAsync()
    {
        var message = new GameStartMessage();
        var json = JsonSerializer.Serialize(message, JsonConfig.Options);
        await jsRuntime.InvokeVoidAsync("WebRtc.sendMessage", json);
    }

    public async Task SendResignAsync()
    {
        var message = new ResignMessage();
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
    public async Task OnParticipantJoinedCallback(string peerId, string nickname, bool isHost)
    {
        var participant = new Participant(peerId, nickname, isHost);
        this._participants[peerId] = participant;

        if (OnParticipantJoined is { } handler) {
            await handler(participant);
        }
    }

    [JSInvokable]
    public async Task OnParticipantLeftCallback(string peerId)
    {
        this._participants.Remove(peerId);

        if (OnParticipantLeft is { } handler) {
            await handler(peerId);
        }
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

                case "gameStartWithPlayers":
                    var gsMessage = JsonSerializer.Deserialize<GameStartWithPlayersMessage>(message, JsonConfig.Options);
                    if (gsMessage is not null && OnGameStartWithPlayers is { } gsHandler) {
                        var senteNickname = this._participants.GetValueOrDefault(gsMessage.SentePeerId)?.Nickname ?? "先手";
                        var goteNickname = this._participants.GetValueOrDefault(gsMessage.GotePeerId)?.Nickname ?? "後手";
                        await gsHandler(new GameStartInfo(gsMessage.SentePeerId, gsMessage.GotePeerId, senteNickname, goteNickname));
                    }
                    break;

                case "resign":
                    if (OnResignReceived is { } resignHandler) {
                        await resignHandler();
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
        this._participants.Clear();
        this.IsHost = false;
        this.MyPeerId = null;
        this.MyNickname = null;
        await this.SetStateAsync(ConnectionState.Disconnected);
    }

    public async ValueTask DisposeAsync()
    {
        await this.DisconnectAsync();
        this._dotNetRef?.Dispose();
        GC.SuppressFinalize(this);
    }
}

/// <summary>対局開始情報</summary>
public record GameStartInfo(string SentePeerId, string GotePeerId, string SenteNickname, string GoteNickname);
