using System.Text.Json;

using Microsoft.JSInterop;

using Fu.Core.Models;
using Fu.Core.Models.Dto;

namespace Fu.Services;

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
    public RoomId? RoomId { get; private set; }
    public IReadOnlyCollection<Participant> Participants => this._participants.Values;

    public event Func<Move, TimeSpan, Task>? OnMoveReceived;
    public event Func<ConnectionState, Task>? OnStateChanged;
    public event Func<Task>? OnGameStart;
    public event Func<Task>? OnDataChannelReady;
    public event Func<Participant, Task>? OnParticipantJoined;
    public event Func<string, Task>? OnParticipantLeft;
    public event Func<GameStartInfo, Task>? OnGameStartWithPlayers;
    public event Func<Task>? OnResignReceived;
    public event Func<Task>? OnGameStateRequested;
    public event Func<GameStateSyncInfo, Task>? OnGameStateSyncReceived;
    public event Func<IReadOnlyList<Move>, Task>? OnBranchResumeReceived;
    public event Func<IReadOnlyList<Move>, Task>? OnRematchReceived;
    public event Func<IReadOnlyList<Move>, Task>? OnReviewStartReceived;
    public event Func<Move, Task>? OnReviewMoveReceived;

    public async Task InitializeAsync()
    {
        this._dotNetRef = DotNetObjectReference.Create(this);
        await jsRuntime.InvokeVoidAsync("WebRtc.initialize", this._dotNetRef);
    }

    /// <summary>ルームを作成（ホスト用）- 6文字のルームIDを返す</summary>
    public async Task<RoomId> CreateRoomAsync(string nickname, string? savedPeerId = null)
    {
        this.IsHost = true;
        this.MyNickname = nickname;
        var roomId = await jsRuntime.InvokeAsync<string>("WebRtc.createRoom", nickname, savedPeerId);
        this.RoomId = new RoomId(roomId);
        this.MyPeerId = await jsRuntime.InvokeAsync<string>("WebRtc.getMyPeerId");
        return this.RoomId.Value;
    }

    /// <summary>ルームに参加</summary>
    public async Task JoinRoomAsync(RoomId roomId, string nickname, string? savedPeerId = null)
    {
        this.IsHost = false;
        this.MyNickname = nickname;
        this.RoomId = roomId;
        await jsRuntime.InvokeVoidAsync("WebRtc.joinRoom", roomId.AsPrimitive(), nickname, savedPeerId);
        this.MyPeerId = await jsRuntime.InvokeAsync<string>("WebRtc.getMyPeerId");
    }

    public Task SendMoveAsync(Move move, TimeSpan elapsedTime) =>
        this.SendMessageAsync(new MoveMessage(move.ToDto(), (int)elapsedTime.TotalSeconds));

    public Task SendGameStartAsync(string sentePeerId, string gotePeerId, EvaluationDisplayOptions? evaluationOptions = null) =>
        this.SendMessageAsync(new GameStartWithPlayersMessage(sentePeerId, gotePeerId, evaluationOptions));

    public Task SendGameStartAsync() =>
        this.SendMessageAsync(new GameStartMessage());

    public Task SendResignAsync() =>
        this.SendMessageAsync(new ResignMessage());

    public Task SendGameStateRequestAsync() =>
        this.SendMessageAsync(new GameStateRequestMessage());

    public Task SendGameStateSyncAsync(IEnumerable<Move> moveHistory, string sentePeerId, string gotePeerId, string senteNickname, string goteNickname, GameStatus status, EvaluationDisplayOptions? evaluationOptions = null) =>
        this.SendMessageAsync(new GameStateSyncMessage(
            moveHistory.Select(m => m.ToDto()).ToArray(),
            sentePeerId,
            gotePeerId,
            senteNickname,
            goteNickname,
            status.ToString(),
            evaluationOptions
        ));

    public Task SendBranchResumeAsync(IEnumerable<Move> moveHistory) =>
        this.SendMessageAsync(new BranchResumeMessage(
            moveHistory.Select(m => m.ToDto()).ToArray()
        ));

    public Task SendRematchAsync(IEnumerable<Move> moveHistory) =>
        this.SendMessageAsync(new RematchMessage(
            moveHistory.Select(m => m.ToDto()).ToArray()
        ));

    public Task SendReviewStartAsync(IEnumerable<Move> moveHistory) =>
        this.SendMessageAsync(new ReviewStartMessage(
            moveHistory.Select(m => m.ToDto()).ToArray()
        ));

    public Task SendReviewMoveAsync(Move move) =>
        this.SendMessageAsync(new ReviewMoveMessage(move.ToDto()));

    private async Task SendMessageAsync<T>(T message) where T : WebRtcMessage
    {
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
                    await this.HandleMoveMessageAsync(message);
                    break;
                case "gameStart":
                    if (OnGameStart is { } h) {
                        await h();
                    }
                    break;
                case "gameStartWithPlayers":
                    await this.HandleGameStartWithPlayersAsync(message);
                    break;
                case "resign":
                    if (OnResignReceived is { } h2) {
                        await h2();
                    }
                    break;
                case "gameStateRequest":
                    if (OnGameStateRequested is { } h3) {
                        await h3();
                    }
                    break;
                case "gameStateSync":
                    await this.HandleGameStateSyncAsync(message);
                    break;
                case "branchResume":
                    await this.HandleBranchResumeAsync(message);
                    break;
                case "rematch":
                    await this.HandleRematchAsync(message);
                    break;
                case "reviewStart":
                    await this.HandleReviewStartAsync(message);
                    break;
                case "reviewMove":
                    await this.HandleReviewMoveAsync(message);
                    break;
            }
        }
        catch (JsonException) {
            // Ignore JSON parse errors from malformed messages
        }
    }

    private async Task HandleMoveMessageAsync(string message)
    {
        var msg = JsonSerializer.Deserialize<MoveMessage>(message, JsonConfig.Options);
        if (msg is not null && OnMoveReceived is { } handler) {
            var elapsedTime = TimeSpan.FromSeconds(msg.ElapsedSeconds);
            await handler(Move.FromDto(msg.Move), elapsedTime);
        }
    }

    private async Task HandleGameStartWithPlayersAsync(string message)
    {
        var msg = JsonSerializer.Deserialize<GameStartWithPlayersMessage>(message, JsonConfig.Options);
        if (msg is not null && OnGameStartWithPlayers is { } handler) {
            var senteNickname = this._participants.GetValueOrDefault(msg.SentePeerId)?.Nickname ?? "先手";
            var goteNickname = this._participants.GetValueOrDefault(msg.GotePeerId)?.Nickname ?? "後手";
            await handler(new GameStartInfo(msg.SentePeerId, msg.GotePeerId, senteNickname, goteNickname, msg.EvaluationOptions));
        }
    }

    private async Task HandleGameStateSyncAsync(string message)
    {
        var msg = JsonSerializer.Deserialize<GameStateSyncMessage>(message, JsonConfig.Options);
        if (msg is not null && OnGameStateSyncReceived is { } handler) {
            var moves = msg.MoveHistory.Select(Move.FromDto).ToList();
            var status = Enum.TryParse<GameStatus>(msg.Status, out var s) ? s : GameStatus.WaitingForConnection;
            await handler(new GameStateSyncInfo(moves, msg.SentePeerId, msg.GotePeerId, msg.SenteNickname, msg.GoteNickname, status, msg.EvaluationOptions));
        }
    }

    private async Task HandleBranchResumeAsync(string message)
    {
        var msg = JsonSerializer.Deserialize<BranchResumeMessage>(message, JsonConfig.Options);
        if (msg is not null && OnBranchResumeReceived is { } handler) {
            await handler(msg.MoveHistory.Select(Move.FromDto).ToList());
        }
    }

    private async Task HandleRematchAsync(string message)
    {
        var msg = JsonSerializer.Deserialize<RematchMessage>(message, JsonConfig.Options);
        if (msg is not null && OnRematchReceived is { } handler) {
            await handler(msg.MoveHistory.Select(Move.FromDto).ToList());
        }
    }

    private async Task HandleReviewStartAsync(string message)
    {
        var msg = JsonSerializer.Deserialize<ReviewStartMessage>(message, JsonConfig.Options);
        if (msg is not null && OnReviewStartReceived is { } handler) {
            await handler(msg.MoveHistory.Select(Move.FromDto).ToList());
        }
    }

    private async Task HandleReviewMoveAsync(string message)
    {
        var msg = JsonSerializer.Deserialize<ReviewMoveMessage>(message, JsonConfig.Options);
        if (msg is not null && OnReviewMoveReceived is { } handler) {
            await handler(Move.FromDto(msg.Move));
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
public record GameStartInfo(string SentePeerId, string GotePeerId, string SenteNickname, string GoteNickname, EvaluationDisplayOptions? EvaluationOptions);

/// <summary>ゲーム状態同期情報</summary>
public record GameStateSyncInfo(
    IReadOnlyList<Move> MoveHistory,
    string SentePeerId,
    string GotePeerId,
    string SenteNickname,
    string GoteNickname,
    GameStatus Status,
    EvaluationDisplayOptions? EvaluationOptions
);
