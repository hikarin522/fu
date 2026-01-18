using System.Globalization;
using System.Runtime.CompilerServices;
using System.Threading.Channels;

using Microsoft.JSInterop;

using Fu.Core.Abstractions;

namespace Fu.Services;

/// <summary>
/// YaneuraOu WASM エンジンのJS interop実装
/// USIプロトコルの状態管理は全てC#側で行う
/// </summary>
public sealed class YaneuraOuEngine : IUsiEngine, IAsyncDisposable
{
    private readonly IJSRuntime _jsRuntime;

    private DotNetObjectReference<YaneuraOuEngine>? _dotNetRef;
    private TaskCompletionSource? _readyTcs;
    private TaskCompletionSource? _usiOkTcs;
    private Channel<UsiGoResult>? _goChannel;
    private bool _stopRequested;
    private bool _initialized;
    private bool _isReady;
    private bool _isAnalyzing;

    // USIハンドシェイク中に収集するエンジン情報
    private string? _pendingEngineName;
    private string? _pendingEngineAuthor;

    public bool IsAvailable { get; private set; }
    public bool IsAnalyzing => this._isAnalyzing;
    public UsiEngineId? EngineId { get; private set; }

    public YaneuraOuEngine(IJSRuntime jsRuntime)
    {
        this._jsRuntime = jsRuntime;
    }

    #region Lifecycle

    public async Task<UsiEngineId?> InitializeAsync()
    {
        if (this._initialized) {
            return this.IsAvailable ? this.EngineId : null;
        }

        this._initialized = true;

        try {
            // Cross-Origin Isolation確認（SharedArrayBufferに必要）
            if (!await this._jsRuntime.InvokeAsync<bool>("ShogiEngine.isCrossOriginIsolated")) {
                this.IsAvailable = false;
                return null;
            }

            return await this.InitializeEngineAsync();
        }
        catch {
            this.IsAvailable = false;
            return null;
        }
    }

    public async Task<bool> RestartAsync()
    {
        this._isReady = false;
        this._isAnalyzing = false;
        this._goChannel?.Writer.TryComplete();
        this._goChannel = null;

        try {
            if (!await this._jsRuntime.InvokeAsync<bool>("ShogiEngine.restart")) {
                return this.IsAvailable = false;
            }

            // コールバック再設定
            this._dotNetRef?.Dispose();
            this._dotNetRef = DotNetObjectReference.Create(this);
            await this._jsRuntime.InvokeVoidAsync("ShogiEngine.setCallback", this._dotNetRef);

            // USIハンドシェイクをやり直す
            var engineId = await this.PerformUsiHandshakeAsync();
            if (engineId is null) {
                return this.IsAvailable = false;
            }

            this.EngineId = engineId;
            return await this.WaitForReadyAsync();
        }
        catch {
            return this.IsAvailable = false;
        }
    }

    public async Task QuitAsync()
    {
        if (!this.IsAvailable) {
            return;
        }

        this._goChannel?.Writer.TryComplete();
        this._goChannel = null;
        this._isAnalyzing = false;
        this._isReady = false;
        this.IsAvailable = false;

        await this._jsRuntime.InvokeAsync<bool>("ShogiEngine.sendCommand", "quit");
    }

    private async Task<UsiEngineId?> InitializeEngineAsync()
    {
        // JS側でWASMモジュール初期化
        if (!await this._jsRuntime.InvokeAsync<bool>("ShogiEngine.init")) {
            this.IsAvailable = false;
            return null;
        }

        // コールバック設定
        this._dotNetRef = DotNetObjectReference.Create(this);
        await this._jsRuntime.InvokeVoidAsync("ShogiEngine.setCallback", this._dotNetRef);

        this.IsAvailable = true;

        // USIハンドシェイク（C#側で実行）
        var engineId = await this.PerformUsiHandshakeAsync();
        if (engineId is null) {
            this.IsAvailable = false;
            return null;
        }

        this.EngineId = engineId;

        if (!await this.WaitForReadyAsync()) {
            return null;
        }

        return this.EngineId;
    }

    /// <summary>
    /// USIハンドシェイクを実行
    /// usi → id name/id author → usiok
    /// </summary>
    private async Task<UsiEngineId?> PerformUsiHandshakeAsync()
    {
        this._pendingEngineName = null;
        this._pendingEngineAuthor = null;
        this._usiOkTcs = new TaskCompletionSource();

        // usiコマンドを送信
        await this._jsRuntime.InvokeAsync<bool>("ShogiEngine.sendCommand", "usi");

        // usiokを待機（タイムアウト付き）
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try {
            await this._usiOkTcs.Task.WaitAsync(cts.Token);
            return new UsiEngineId(
                this._pendingEngineName ?? "Unknown",
                this._pendingEngineAuthor ?? "Unknown"
            );
        }
        catch (OperationCanceledException) {
            return null;
        }
        finally {
            this._usiOkTcs = null;
        }
    }

    private async Task<bool> WaitForReadyAsync()
    {
        this._readyTcs = new TaskCompletionSource();
        await this._jsRuntime.InvokeAsync<bool>("ShogiEngine.sendCommand", "isready");

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try {
            await this._readyTcs.Task.WaitAsync(cts.Token);
            this._isReady = true;
            return this.IsAvailable = true;
        }
        catch (OperationCanceledException) {
            return this.IsAvailable = false;
        }
    }

    #endregion

    #region Commands

    public async Task<bool> IsReadyAsync()
    {
        if (!this.IsAvailable) {
            return false;
        }

        return await this.WaitForReadyAsync();
    }

    public async Task NewGameAsync()
    {
        this.ThrowIfNotReady();
        await this._jsRuntime.InvokeAsync<bool>("ShogiEngine.sendCommand", "usinewgame");
    }

    public Task SetOptionAsync(string name, string value) =>
        this.SendOptionAsync(name, value);

    public Task SetOptionAsync(string name, int value) =>
        this.SendOptionAsync(name, value.ToString(CultureInfo.InvariantCulture));

    public Task SetOptionAsync(string name, bool value) =>
        this.SendOptionAsync(name, value ? "true" : "false");

    private async Task SendOptionAsync(string name, string value)
    {
        this.ThrowIfNotReady();
        await this._jsRuntime.InvokeAsync<bool>("ShogiEngine.sendCommand", $"setoption name {name} value {value}");
    }

    public async Task PositionAsync(string sfen, IEnumerable<string>? moves = null)
    {
        this.ThrowIfNotReady();

        var command = $"position sfen {sfen}";
        if (moves is not null) {
            var moveList = string.Join(" ", moves);
            if (!string.IsNullOrEmpty(moveList)) {
                command += $" moves {moveList}";
            }
        }

        await this._jsRuntime.InvokeAsync<bool>("ShogiEngine.sendCommand", command);
    }

    public IAsyncEnumerable<UsiGoResult> GoAsync(UsiGoOptions options)
    {
        var command = BuildGoCommand(options);
        return this.GoAsyncCore(null, command);
    }

    public IAsyncEnumerable<UsiGoResult> GoAsync(string sfen, int depth = 0)
    {
        var command = depth > 0 ? $"go depth {depth}" : "go infinite";
        return this.GoAsyncCore(sfen, command);
    }

    private async IAsyncEnumerable<UsiGoResult> GoAsyncCore(
        string? sfen,
        string goCommand,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        this.ThrowIfNotReady();

        // 前回の分析があれば停止待ち
        if (this._isAnalyzing && this._goChannel is not null) {
            await this.StopAndDrainAsync();
        }

        // チャンネルを作成
        this._goChannel = Channel.CreateUnbounded<UsiGoResult>();
        this._stopRequested = false;
        this._isAnalyzing = true;

        // position設定（sfenが指定されている場合）
        if (sfen is not null) {
            await this._jsRuntime.InvokeAsync<bool>("ShogiEngine.sendCommand", $"position sfen {sfen}");
        }

        // go開始
        await this._jsRuntime.InvokeAsync<bool>("ShogiEngine.sendCommand", goCommand);

        try {
            await foreach (var result in this._goChannel.Reader.ReadAllAsync(cancellationToken)) {
                yield return result;

                // bestmoveが来たら終了
                if (result is UsiGoResult.BestMove) {
                    yield break;
                }
            }
        }
        finally {
            // キャンセル時はstopを送信してbestmoveを待つ
            if (this._isAnalyzing && !this._stopRequested) {
                await this.StopAndDrainAsync();
            }
        }
    }

    private async Task StopAndDrainAsync()
    {
        if (!this._isAnalyzing || this._goChannel is null) {
            return;
        }

        this._stopRequested = true;
        await this._jsRuntime.InvokeAsync<bool>("ShogiEngine.sendCommand", "stop");

        // bestmoveが来るまで待機（タイムアウト付き）
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try {
            await foreach (var result in this._goChannel.Reader.ReadAllAsync(cts.Token)) {
                if (result is UsiGoResult.BestMove) {
                    break;
                }
            }
        }
        catch (OperationCanceledException) {
            // タイムアウト
        }

        this._isAnalyzing = false;
        this._goChannel = null;
    }

    private static string BuildGoCommand(UsiGoOptions options)
    {
        var parts = new List<string> { "go" };

        if (options.Mate) {
            parts.Add("mate");
            if (options.MateTime.HasValue) {
                parts.Add(options.MateTime.Value.ToString(CultureInfo.InvariantCulture));
            }
            else {
                parts.Add("infinite");
            }
            return string.Join(" ", parts);
        }

        if (options.Infinite) {
            parts.Add("infinite");
        }

        if (options.Depth.HasValue) {
            parts.Add("depth");
            parts.Add(options.Depth.Value.ToString(CultureInfo.InvariantCulture));
        }

        if (options.Nodes.HasValue) {
            parts.Add("nodes");
            parts.Add(options.Nodes.Value.ToString(CultureInfo.InvariantCulture));
        }

        if (options.Byoyomi.HasValue) {
            parts.Add("byoyomi");
            parts.Add(options.Byoyomi.Value.ToString(CultureInfo.InvariantCulture));
        }

        if (options.BTime.HasValue) {
            parts.Add("btime");
            parts.Add(options.BTime.Value.ToString(CultureInfo.InvariantCulture));
        }

        if (options.WTime.HasValue) {
            parts.Add("wtime");
            parts.Add(options.WTime.Value.ToString(CultureInfo.InvariantCulture));
        }

        if (options.BInc.HasValue) {
            parts.Add("binc");
            parts.Add(options.BInc.Value.ToString(CultureInfo.InvariantCulture));
        }

        if (options.WInc.HasValue) {
            parts.Add("winc");
            parts.Add(options.WInc.Value.ToString(CultureInfo.InvariantCulture));
        }

        if (options.Ponder) {
            parts.Add("ponder");
        }

        return string.Join(" ", parts);
    }

    public async Task PonderHitAsync()
    {
        this.ThrowIfNotReady();
        if (!this._isAnalyzing) {
            throw new InvalidOperationException("Engine is not analyzing");
        }

        await this._jsRuntime.InvokeAsync<bool>("ShogiEngine.sendCommand", "ponderhit");
    }

    public async Task GameOverAsync(UsiGameResult result)
    {
        this.ThrowIfNotReady();

        var resultStr = result switch {
            UsiGameResult.Win => "win",
            UsiGameResult.Lose => "lose",
            UsiGameResult.Draw => "draw",
            _ => "draw"
        };

        await this._jsRuntime.InvokeAsync<bool>("ShogiEngine.sendCommand", $"gameover {resultStr}");
    }

    private void ThrowIfNotReady()
    {
        if (!this.IsAvailable) {
            throw new InvalidOperationException("Engine is not available");
        }
        if (!this._isReady) {
            throw new InvalidOperationException("Engine is not ready");
        }
    }

    #endregion

    #region JS Callbacks

    /// <summary>エンジンがクラッシュした時の処理</summary>
    [JSInvokable]
    public async Task OnEngineCrash()
    {
        Console.WriteLine("Engine crashed, restarting...");
        this._isReady = false;
        this._isAnalyzing = false;
        this.IsAvailable = false;
        this._goChannel?.Writer.TryComplete();
        this._goChannel = null;

        // 自動再起動を試みる
        if (await this.RestartAsync()) {
            Console.WriteLine("Engine restarted successfully");
        }
        else {
            Console.WriteLine("Engine restart failed");
        }
    }

    /// <summary>エンジンからのメッセージを処理</summary>
    [JSInvokable]
    public Task OnEngineMessage(string message)
    {
        // USIハンドシェイク中のメッセージ処理
        if (this._usiOkTcs is not null) {
            if (message.StartsWith("id name ", StringComparison.Ordinal)) {
                this._pendingEngineName = message[8..];
                return Task.CompletedTask;
            }
            if (message.StartsWith("id author ", StringComparison.Ordinal)) {
                this._pendingEngineAuthor = message[10..];
                return Task.CompletedTask;
            }
            if (message == "usiok") {
                this._usiOkTcs.TrySetResult();
                return Task.CompletedTask;
            }
            // USIハンドシェイク中は他のメッセージを無視
            return Task.CompletedTask;
        }

        // readyok待ち
        if (message == "readyok") {
            this._readyTcs?.TrySetResult();
            return Task.CompletedTask;
        }

        if (!this._isReady) {
            return Task.CompletedTask;
        }

        // 通常のメッセージ処理
        if (message.StartsWith("info ", StringComparison.Ordinal)) {
            var info = ParseUsiInfo(message);
            this._goChannel?.Writer.TryWrite(new UsiGoResult.Info(info));
        }
        else if (message.StartsWith("bestmove ", StringComparison.Ordinal)) {
            this._isAnalyzing = false;
            var bestMove = ParseBestMove(message);
            this._goChannel?.Writer.TryWrite(new UsiGoResult.BestMove(bestMove));
            this._goChannel?.Writer.TryComplete();
        }

        return Task.CompletedTask;
    }

    #endregion

    #region Parsing

    private static UsiBestMove ParseBestMove(string message)
    {
        // "bestmove 7g7f ponder 3c3d" or "bestmove 7g7f"
        var parts = message.Split(' ');
        var move = parts.Length >= 2 ? parts[1] : "";
        string? ponder = null;

        for (var i = 0; i < parts.Length - 1; i++) {
            if (parts[i] == "ponder") {
                ponder = parts[i + 1];
                break;
            }
        }

        return new UsiBestMove(move, ponder);
    }

    private static UsiInfo ParseUsiInfo(string message)
    {
        var parts = message.Split(' ');
        int? multipv = null, depth = null, score = null, mateIn = null;
        string? pv = null, move = null;

        for (var i = 0; i < parts.Length; i++) {
            switch (parts[i]) {
                case "multipv" when i + 1 < parts.Length && int.TryParse(parts[i + 1], out var mpv):
                    multipv = mpv;
                    break;

                case "depth" when i + 1 < parts.Length && int.TryParse(parts[i + 1], out var d):
                    depth = d;
                    break;

                case "score" when i + 2 < parts.Length:
                    if (parts[i + 1] == "cp" && int.TryParse(parts[i + 2], out var cp)) {
                        score = cp;
                    }
                    else if (parts[i + 1] == "mate" && int.TryParse(parts[i + 2], out var mate)) {
                        mateIn = mate;
                    }
                    break;

                case "pv" when i + 1 < parts.Length:
                    var pvParts = parts.Skip(i + 1).ToArray();
                    pv = string.Join(" ", pvParts);
                    if (pvParts.Length > 0) {
                        move = pvParts[0];
                    }
                    break;
            }
        }

        return new UsiInfo(multipv, depth, score, mateIn, pv, move);
    }

    #endregion

    public async ValueTask DisposeAsync()
    {
        if (this._isAnalyzing) {
            await this.StopAndDrainAsync();
        }
        this._dotNetRef?.Dispose();
    }
}
