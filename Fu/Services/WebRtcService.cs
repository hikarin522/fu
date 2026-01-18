using System.Text.Json;

using MessagePipe;
using Microsoft.JSInterop;

using Fu.Core.Abstractions;
using Fu.Core.Events;
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

/// <summary>
/// WebRTC (Trystero) 経由のゲーム通信サービス
/// </summary>
public class WebRtcService : IGameTransport
{
    private readonly IJSRuntime _jsRuntime;
    private DotNetObjectReference<WebRtcService>? _dotNetRef;
    private bool _dataChannelOpen;
    private readonly Dictionary<PlayerId, TransportParticipantInfo> _participants = [];
    private Dictionary<string, Action<string>>? _messageHandlers;

    // MessagePipe Publishers
    private readonly IPublisher<TransportConnectionStateChangedEvent> _connectionStateChanged;
    private readonly IPublisher<TransportReadyEvent> _ready;
    private readonly IPublisher<TransportParticipantJoinedEvent> _participantJoined;
    private readonly IPublisher<TransportParticipantLeftEvent> _participantLeft;
    private readonly IPublisher<TransportBecameHostEvent> _becameHost;
    private readonly IPublisher<TransportMoveReceivedEvent> _moveReceived;
    private readonly IPublisher<TransportGameStartReceivedEvent> _gameStartReceived;
    private readonly IPublisher<TransportGameStartWithPlayersReceivedEvent> _gameStartWithPlayersReceived;
    private readonly IPublisher<TransportResignReceivedEvent> _resignReceived;
    private readonly IPublisher<TransportGameStateRequestedEvent> _gameStateRequested;
    private readonly IPublisher<TransportGameStateSyncReceivedEvent> _gameStateSyncReceived;
    private readonly IPublisher<TransportBranchResumeReceivedEvent> _branchResumeReceived;
    private readonly IPublisher<TransportRematchReceivedEvent> _rematchReceived;
    private readonly IPublisher<TransportReviewStartReceivedEvent> _reviewStartReceived;
    private readonly IPublisher<TransportReviewMoveReceivedEvent> _reviewMoveReceived;

    public WebRtcService(
        IJSRuntime jsRuntime,
        IPublisher<TransportConnectionStateChangedEvent> connectionStateChanged,
        IPublisher<TransportReadyEvent> ready,
        IPublisher<TransportParticipantJoinedEvent> participantJoined,
        IPublisher<TransportParticipantLeftEvent> participantLeft,
        IPublisher<TransportBecameHostEvent> becameHost,
        IPublisher<TransportMoveReceivedEvent> moveReceived,
        IPublisher<TransportGameStartReceivedEvent> gameStartReceived,
        IPublisher<TransportGameStartWithPlayersReceivedEvent> gameStartWithPlayersReceived,
        IPublisher<TransportResignReceivedEvent> resignReceived,
        IPublisher<TransportGameStateRequestedEvent> gameStateRequested,
        IPublisher<TransportGameStateSyncReceivedEvent> gameStateSyncReceived,
        IPublisher<TransportBranchResumeReceivedEvent> branchResumeReceived,
        IPublisher<TransportRematchReceivedEvent> rematchReceived,
        IPublisher<TransportReviewStartReceivedEvent> reviewStartReceived,
        IPublisher<TransportReviewMoveReceivedEvent> reviewMoveReceived)
    {
        this._jsRuntime = jsRuntime;
        this._connectionStateChanged = connectionStateChanged;
        this._ready = ready;
        this._participantJoined = participantJoined;
        this._participantLeft = participantLeft;
        this._becameHost = becameHost;
        this._moveReceived = moveReceived;
        this._gameStartReceived = gameStartReceived;
        this._gameStartWithPlayersReceived = gameStartWithPlayersReceived;
        this._resignReceived = resignReceived;
        this._gameStateRequested = gameStateRequested;
        this._gameStateSyncReceived = gameStateSyncReceived;
        this._branchResumeReceived = branchResumeReceived;
        this._rematchReceived = rematchReceived;
        this._reviewStartReceived = reviewStartReceived;
        this._reviewMoveReceived = reviewMoveReceived;
    }

    /// <summary>メッセージタイプとハンドラのマッピングを取得</summary>
    private Dictionary<string, Action<string>> MessageHandlers => this._messageHandlers ??= new() {
        ["move"] = this.HandleMoveMessage,
        ["gameStart"] = _ => this._gameStartReceived.Publish(new TransportGameStartReceivedEvent()),
        ["gameStartWithPlayers"] = this.HandleGameStartWithPlayersMessage,
        ["resign"] = _ => this._resignReceived.Publish(new TransportResignReceivedEvent()),
        ["gameStateRequest"] = _ => this._gameStateRequested.Publish(new TransportGameStateRequestedEvent()),
        ["gameStateSync"] = this.HandleGameStateSyncMessage,
        ["branchResume"] = this.HandleBranchResumeMessage,
        ["rematch"] = this.HandleRematchMessage,
        ["reviewStart"] = this.HandleReviewStartMessage,
        ["reviewMove"] = this.HandleReviewMoveMessage,
    };

    public TransportConnectionState ConnectionState { get; private set; } = TransportConnectionState.Disconnected;
    public bool IsConnected => this.ConnectionState == TransportConnectionState.Connected || this._dataChannelOpen;
    public bool IsHost { get; private set; }
    public PlayerId? MyPlayerId { get; private set; }
    public string? MyNickname { get; private set; }
    public RoomId? RoomId { get; private set; }
    public IReadOnlyCollection<TransportParticipantInfo> Participants => this._participants.Values;

    public TransportParticipantInfo? GetParticipant(PlayerId playerId) =>
        this._participants.GetValueOrDefault(playerId);

    #region 接続管理

    public async Task InitializeAsync()
    {
        // 二重初期化時のリーク防止
        this._dotNetRef?.Dispose();
        this._dotNetRef = DotNetObjectReference.Create(this);
        await this._jsRuntime.InvokeVoidAsync("WebRtc.initialize", this._dotNetRef);
    }

    public async Task<RoomId> CreateRoomAsync(string nickname, PlayerId? savedPlayerId = null)
    {
        this.IsHost = true;
        this.MyNickname = nickname;
        this.RoomId = Core.Models.RoomId.Generate();
        this.MyPlayerId = savedPlayerId ?? PlayerId.Generate(nickname);
        await this._jsRuntime.InvokeVoidAsync("WebRtc.joinRoom", this.RoomId.Value.AsPrimitive(), this.MyPlayerId.Value.AsPrimitive(), nickname, true);
        return this.RoomId.Value;
    }

    public async Task JoinRoomAsync(RoomId roomId, string nickname, PlayerId? savedPlayerId = null)
    {
        this.IsHost = false;
        this.MyNickname = nickname;
        this.RoomId = roomId;
        this.MyPlayerId = savedPlayerId ?? PlayerId.Generate(nickname);
        await this._jsRuntime.InvokeVoidAsync("WebRtc.joinRoom", roomId.AsPrimitive(), this.MyPlayerId.Value.AsPrimitive(), nickname, false);
    }

    public async Task DisconnectAsync()
    {
        await this._jsRuntime.InvokeVoidAsync("WebRtc.disconnect");
        this._participants.Clear();
        this.IsHost = false;
        this.MyPlayerId = null;
        this.MyNickname = null;
        this.SetState(TransportConnectionState.Disconnected);
    }

    #endregion

    #region メッセージ送信

    public Task SendMoveAsync(Move move, TimeSpan elapsedTime) =>
        this.SendMessageAsync(new MoveMessage(move.ToDto(), (int)elapsedTime.TotalSeconds));

    public Task SendGameStartAsync() =>
        this.SendMessageAsync(new GameStartMessage());

    public Task SendGameStartAsync(PlayerId sentePlayerId, PlayerId gotePlayerId, EvaluationDisplayOptions? evaluationOptions = null) =>
        this.SendMessageAsync(new GameStartWithPlayersMessage(sentePlayerId, gotePlayerId, evaluationOptions));

    public Task SendResignAsync() =>
        this.SendMessageAsync(new ResignMessage());

    public Task SendGameStateRequestAsync() =>
        this.SendMessageAsync(new GameStateRequestMessage());

    public Task SendGameStateSyncAsync(
        IEnumerable<Move> moveHistory,
        PlayerId sentePlayerId,
        PlayerId gotePlayerId,
        string senteNickname,
        string goteNickname,
        GameStatus status,
        EvaluationDisplayOptions? evaluationOptions = null,
        IEnumerable<TimeSpan>? moveTimes = null) =>
        this.SendMessageAsync(new GameStateSyncMessage(
            moveHistory.Select(m => m.ToDto()).ToArray(),
            sentePlayerId,
            gotePlayerId,
            senteNickname,
            goteNickname,
            status.ToString(),
            evaluationOptions,
            moveTimes?.Select(t => (int)t.TotalSeconds).ToArray()
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

    private async Task SendMessageAsync<T>(T message) where T : GameMessage
    {
        var json = JsonSerializer.Serialize(message, JsonConfig.Options);
        await this._jsRuntime.InvokeVoidAsync("WebRtc.sendMessage", json);
    }

    #endregion

    #region 内部状態管理

    private void SetState(TransportConnectionState newState)
    {
        if (this.ConnectionState != newState) {
            this.ConnectionState = newState;
            this._connectionStateChanged.Publish(new TransportConnectionStateChangedEvent(this.ConnectionState));
        }
    }

    #endregion

    #region JSInvokable コールバック

    [JSInvokable]
    public Task OnConnectionStateChangedCallback(string state)
    {
        var newState = state.ToLowerInvariant() switch {
            "connected" => TransportConnectionState.Connected,
            "connecting" => TransportConnectionState.Connecting,
            _ => TransportConnectionState.Disconnected
        };
        this.SetState(newState);
        return Task.CompletedTask;
    }

    [JSInvokable]
    public Task OnDataChannelOpen()
    {
        this._dataChannelOpen = true;
        this.SetState(TransportConnectionState.Connected);
        this._ready.Publish(new TransportReadyEvent());
        return Task.CompletedTask;
    }

    [JSInvokable]
    public Task OnDataChannelClose()
    {
        this._dataChannelOpen = false;
        this.SetState(TransportConnectionState.Disconnected);
        return Task.CompletedTask;
    }

    [JSInvokable]
    public Task OnParticipantJoinedCallback(string peerId, string nickname, bool isHost)
    {
        var playerId = new PlayerId(peerId);
        var participant = new TransportParticipantInfo(playerId, nickname, isHost);
        this._participants[playerId] = participant;
        this._participantJoined.Publish(new TransportParticipantJoinedEvent(participant));
        return Task.CompletedTask;
    }

    [JSInvokable]
    public async Task OnParticipantLeftCallback(string peerId, bool wasHost)
    {
        var playerId = new PlayerId(peerId);
        this._participants.Remove(playerId);
        this._participantLeft.Publish(new TransportParticipantLeftEvent(playerId));

        // ホストが退出した場合、C#側でホスト選出
        if (wasHost && !this.IsHost && this._participants.Count > 0) {
            var shouldBecomeHost = this.ElectNewHost();
            if (shouldBecomeHost) {
                this.IsHost = true;
                this._becameHost.Publish(new TransportBecameHostEvent());
                // JS側にホスト昇格を通知（他のピアへの通知用）
                await this._jsRuntime.InvokeVoidAsync("WebRtc.notifyBecameHost");
            }
        }
    }

    /// <summary>自分が新しいホストに選出されるべきか判定</summary>
    private bool ElectNewHost()
    {
        // PeerIDの辞書順で最小のピアがホストになる
        var sortedPlayerIds = this._participants.Keys
            .Select(p => p.AsPrimitive())
            .Order()
            .ToList();

        return sortedPlayerIds.Count > 0 && sortedPlayerIds[0] == this.MyPlayerId?.AsPrimitive();
    }

    [JSInvokable]
    public Task OnHostStatusChanged(bool newIsHost)
    {
        this.IsHost = newIsHost;
        return Task.CompletedTask;
    }

    [JSInvokable]
    public Task OnMessageReceived(string message)
    {
        try {
            using var doc = JsonDocument.Parse(message);
            var type = doc.RootElement.GetProperty("Type").GetString();

            if (type is not null && this.MessageHandlers.TryGetValue(type, out var handler)) {
                handler(message);
            }
        }
        catch (JsonException ex) {
            // 不正なメッセージのログ出力（デバッグ用）
            Console.WriteLine($"[WebRTC] Failed to parse message: {ex.Message}");
        }

        return Task.CompletedTask;
    }

    #endregion

    #region メッセージハンドラー

    private void HandleMoveMessage(string message)
    {
        var msg = JsonSerializer.Deserialize<MoveMessage>(message, JsonConfig.Options);
        if (msg is not null) {
            var elapsedTime = TimeSpan.FromSeconds(msg.ElapsedSeconds);
            this._moveReceived.Publish(new TransportMoveReceivedEvent(Move.FromDto(msg.Move), elapsedTime));
        }
    }

    private void HandleGameStartWithPlayersMessage(string message)
    {
        var msg = JsonSerializer.Deserialize<GameStartWithPlayersMessage>(message, JsonConfig.Options);
        if (msg is not null) {
            var senteNickname = this._participants.GetValueOrDefault(msg.SentePlayerId).Nickname ?? "先手";
            var goteNickname = this._participants.GetValueOrDefault(msg.GotePlayerId).Nickname ?? "後手";
            var info = new TransportGameStartInfo(msg.SentePlayerId, msg.GotePlayerId, senteNickname, goteNickname, msg.EvaluationOptions);
            this._gameStartWithPlayersReceived.Publish(new TransportGameStartWithPlayersReceivedEvent(info));
        }
    }

    private void HandleGameStateSyncMessage(string message)
    {
        var msg = JsonSerializer.Deserialize<GameStateSyncMessage>(message, JsonConfig.Options);
        if (msg is not null) {
            var moves = msg.MoveHistory.Select(Move.FromDto).ToList();
            var status = Enum.TryParse<GameStatus>(msg.Status, out var s) ? s : GameStatus.WaitingForConnection;
            var moveTimes = msg.MoveTimes?.Select(t => TimeSpan.FromSeconds(t)).ToList();
            var info = new TransportGameStateSyncInfo(moves, msg.SentePlayerId, msg.GotePlayerId, msg.SenteNickname, msg.GoteNickname, status, msg.EvaluationOptions, moveTimes);
            this._gameStateSyncReceived.Publish(new TransportGameStateSyncReceivedEvent(info));
        }
    }

    private void HandleBranchResumeMessage(string message)
    {
        var msg = JsonSerializer.Deserialize<BranchResumeMessage>(message, JsonConfig.Options);
        if (msg is not null) {
            this._branchResumeReceived.Publish(new TransportBranchResumeReceivedEvent(msg.MoveHistory.Select(Move.FromDto).ToList()));
        }
    }

    private void HandleRematchMessage(string message)
    {
        var msg = JsonSerializer.Deserialize<RematchMessage>(message, JsonConfig.Options);
        if (msg is not null) {
            this._rematchReceived.Publish(new TransportRematchReceivedEvent(msg.MoveHistory.Select(Move.FromDto).ToList()));
        }
    }

    private void HandleReviewStartMessage(string message)
    {
        var msg = JsonSerializer.Deserialize<ReviewStartMessage>(message, JsonConfig.Options);
        if (msg is not null) {
            this._reviewStartReceived.Publish(new TransportReviewStartReceivedEvent(msg.MoveHistory.Select(Move.FromDto).ToList()));
        }
    }

    private void HandleReviewMoveMessage(string message)
    {
        var msg = JsonSerializer.Deserialize<ReviewMoveMessage>(message, JsonConfig.Options);
        if (msg is not null) {
            this._reviewMoveReceived.Publish(new TransportReviewMoveReceivedEvent(Move.FromDto(msg.Move)));
        }
    }

    #endregion

    public async ValueTask DisposeAsync()
    {
        await this.DisconnectAsync();
        this._dotNetRef?.Dispose();
        GC.SuppressFinalize(this);
    }
}
