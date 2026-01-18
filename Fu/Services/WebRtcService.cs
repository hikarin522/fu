using System.Text.Json;

using Microsoft.JSInterop;

using R3;

using Fu.Core.Abstractions;
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
/// WebRTC経由のゲーム通信サービス
/// </summary>
public class WebRtcService(IJSRuntime jsRuntime) : IGameTransport
{
    private DotNetObjectReference<WebRtcService>? _dotNetRef;
    private bool _dataChannelOpen;
    private readonly Dictionary<PlayerId, TransportParticipant> _participants = [];
    private Dictionary<string, Action<string>>? _messageHandlers;

    // R3 Subjects
    private readonly Subject<TransportConnectionState> _connectionStateChanged = new();
    private readonly Subject<Unit> _ready = new();
    private readonly Subject<TransportParticipant> _participantJoined = new();
    private readonly Subject<PlayerId> _participantLeft = new();
    private readonly Subject<Unit> _becameHost = new();
    private readonly Subject<(Move Move, TimeSpan Elapsed)> _moveReceived = new();
    private readonly Subject<Unit> _gameStartReceived = new();
    private readonly Subject<GameStartInfo> _gameStartWithPlayersReceived = new();
    private readonly Subject<Unit> _resignReceived = new();
    private readonly Subject<Unit> _gameStateRequested = new();
    private readonly Subject<GameStateSyncInfo> _gameStateSyncReceived = new();
    private readonly Subject<IReadOnlyList<Move>> _branchResumeReceived = new();
    private readonly Subject<IReadOnlyList<Move>> _rematchReceived = new();
    private readonly Subject<IReadOnlyList<Move>> _reviewStartReceived = new();
    private readonly Subject<Move> _reviewMoveReceived = new();

    /// <summary>メッセージタイプとハンドラのマッピングを取得</summary>
    private Dictionary<string, Action<string>> MessageHandlers => this._messageHandlers ??= new() {
        ["move"] = this.HandleMoveMessage,
        ["gameStart"] = _ => this._gameStartReceived.OnNext(Unit.Default),
        ["gameStartWithPlayers"] = this.HandleGameStartWithPlayersMessage,
        ["resign"] = _ => this._resignReceived.OnNext(Unit.Default),
        ["gameStateRequest"] = _ => this._gameStateRequested.OnNext(Unit.Default),
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
    public IReadOnlyCollection<TransportParticipant> Participants => this._participants.Values;

    #region Observable

    public Observable<TransportConnectionState> ConnectionStateChanged => this._connectionStateChanged;
    public Observable<Unit> Ready => this._ready;
    public Observable<TransportParticipant> ParticipantJoined => this._participantJoined;
    public Observable<PlayerId> ParticipantLeft => this._participantLeft;
    public Observable<Unit> BecameHost => this._becameHost;
    public Observable<(Move Move, TimeSpan Elapsed)> MoveReceived => this._moveReceived;
    public Observable<Unit> GameStartReceived => this._gameStartReceived;
    public Observable<GameStartInfo> GameStartWithPlayersReceived => this._gameStartWithPlayersReceived;
    public Observable<Unit> ResignReceived => this._resignReceived;
    public Observable<Unit> GameStateRequested => this._gameStateRequested;
    public Observable<GameStateSyncInfo> GameStateSyncReceived => this._gameStateSyncReceived;
    public Observable<IReadOnlyList<Move>> BranchResumeReceived => this._branchResumeReceived;
    public Observable<IReadOnlyList<Move>> RematchReceived => this._rematchReceived;
    public Observable<IReadOnlyList<Move>> ReviewStartReceived => this._reviewStartReceived;
    public Observable<Move> ReviewMoveReceived => this._reviewMoveReceived;

    #endregion

    #region 接続管理

    public async Task InitializeAsync()
    {
        // 二重初期化時のリーク防止
        this._dotNetRef?.Dispose();
        this._dotNetRef = DotNetObjectReference.Create(this);
        await jsRuntime.InvokeVoidAsync("WebRtc.initialize", this._dotNetRef);
    }

    public async Task<RoomId> CreateRoomAsync(string nickname, string? savedPlayerId = null)
    {
        this.IsHost = true;
        this.MyNickname = nickname;
        this.RoomId = await jsRuntime.InvokeAsync<RoomId>("WebRtc.createRoom", nickname, savedPlayerId);
        this.MyPlayerId = await jsRuntime.InvokeAsync<PlayerId>("WebRtc.getMyPeerId");
        return this.RoomId.Value;
    }

    public async Task JoinRoomAsync(RoomId roomId, string nickname, string? savedPlayerId = null)
    {
        this.IsHost = false;
        this.MyNickname = nickname;
        this.RoomId = roomId;
        await jsRuntime.InvokeVoidAsync("WebRtc.joinRoom", roomId, nickname, savedPlayerId);
        this.MyPlayerId = await jsRuntime.InvokeAsync<PlayerId>("WebRtc.getMyPeerId");
    }

    public async Task DisconnectAsync()
    {
        await jsRuntime.InvokeVoidAsync("WebRtc.disconnect");
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
        await jsRuntime.InvokeVoidAsync("WebRtc.sendMessage", json);
    }

    #endregion

    #region 内部状態管理

    private void SetState(TransportConnectionState newState)
    {
        if (this.ConnectionState != newState) {
            this.ConnectionState = newState;
            this._connectionStateChanged.OnNext(this.ConnectionState);
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
        this._ready.OnNext(Unit.Default);
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
        var participant = new TransportParticipant(playerId, nickname, isHost);
        this._participants[playerId] = participant;
        this._participantJoined.OnNext(participant);
        return Task.CompletedTask;
    }

    [JSInvokable]
    public Task OnParticipantLeftCallback(string peerId)
    {
        var playerId = new PlayerId(peerId);
        this._participants.Remove(playerId);
        this._participantLeft.OnNext(playerId);
        return Task.CompletedTask;
    }

    [JSInvokable]
    public Task OnBecameHostCallback()
    {
        this.IsHost = true;
        this._becameHost.OnNext(Unit.Default);
        return Task.CompletedTask;
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
            this._moveReceived.OnNext((Move.FromDto(msg.Move), elapsedTime));
        }
    }

    private void HandleGameStartWithPlayersMessage(string message)
    {
        var msg = JsonSerializer.Deserialize<GameStartWithPlayersMessage>(message, JsonConfig.Options);
        if (msg is not null) {
            var senteNickname = this._participants.GetValueOrDefault(msg.SentePlayerId)?.Nickname ?? "先手";
            var goteNickname = this._participants.GetValueOrDefault(msg.GotePlayerId)?.Nickname ?? "後手";
            this._gameStartWithPlayersReceived.OnNext(new GameStartInfo(msg.SentePlayerId, msg.GotePlayerId, senteNickname, goteNickname, msg.EvaluationOptions));
        }
    }

    private void HandleGameStateSyncMessage(string message)
    {
        var msg = JsonSerializer.Deserialize<GameStateSyncMessage>(message, JsonConfig.Options);
        if (msg is not null) {
            var moves = msg.MoveHistory.Select(Move.FromDto).ToList();
            var status = Enum.TryParse<GameStatus>(msg.Status, out var s) ? s : GameStatus.WaitingForConnection;
            var moveTimes = msg.MoveTimes?.Select(t => TimeSpan.FromSeconds(t)).ToList();
            this._gameStateSyncReceived.OnNext(new GameStateSyncInfo(moves, msg.SentePlayerId, msg.GotePlayerId, msg.SenteNickname, msg.GoteNickname, status, msg.EvaluationOptions, moveTimes));
        }
    }

    private void HandleBranchResumeMessage(string message)
    {
        var msg = JsonSerializer.Deserialize<BranchResumeMessage>(message, JsonConfig.Options);
        if (msg is not null) {
            this._branchResumeReceived.OnNext(msg.MoveHistory.Select(Move.FromDto).ToList());
        }
    }

    private void HandleRematchMessage(string message)
    {
        var msg = JsonSerializer.Deserialize<RematchMessage>(message, JsonConfig.Options);
        if (msg is not null) {
            this._rematchReceived.OnNext(msg.MoveHistory.Select(Move.FromDto).ToList());
        }
    }

    private void HandleReviewStartMessage(string message)
    {
        var msg = JsonSerializer.Deserialize<ReviewStartMessage>(message, JsonConfig.Options);
        if (msg is not null) {
            this._reviewStartReceived.OnNext(msg.MoveHistory.Select(Move.FromDto).ToList());
        }
    }

    private void HandleReviewMoveMessage(string message)
    {
        var msg = JsonSerializer.Deserialize<ReviewMoveMessage>(message, JsonConfig.Options);
        if (msg is not null) {
            this._reviewMoveReceived.OnNext(Move.FromDto(msg.Move));
        }
    }

    #endregion

    public async ValueTask DisposeAsync()
    {
        await this.DisconnectAsync();
        this._dotNetRef?.Dispose();

        // Dispose all subjects
        this._connectionStateChanged.Dispose();
        this._ready.Dispose();
        this._participantJoined.Dispose();
        this._participantLeft.Dispose();
        this._becameHost.Dispose();
        this._moveReceived.Dispose();
        this._gameStartReceived.Dispose();
        this._gameStartWithPlayersReceived.Dispose();
        this._resignReceived.Dispose();
        this._gameStateRequested.Dispose();
        this._gameStateSyncReceived.Dispose();
        this._branchResumeReceived.Dispose();
        this._rematchReceived.Dispose();
        this._reviewStartReceived.Dispose();
        this._reviewMoveReceived.Dispose();

        GC.SuppressFinalize(this);
    }
}
