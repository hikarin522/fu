using Microsoft.JSInterop;

using R3;

using Fu.Core.Abstractions;

namespace Fu.Services;

/// <summary>
/// YaneuraOu WASM エンジンのJS interop実装
/// </summary>
public sealed class YaneuraOuEngine : IUsiEngine, IAsyncDisposable
{
    private readonly IJSRuntime _jsRuntime;
    private readonly Subject<UsiInfo> _infoReceived = new();
    private readonly Subject<UsiBestMove> _bestMoveReceived = new();

    private DotNetObjectReference<YaneuraOuEngine>? _dotNetRef;
    private TaskCompletionSource<UsiBestMove>? _stopTcs;
    private TaskCompletionSource? _readyTcs;
    private bool _initialized;
    private bool _isReady;
    private bool _isAnalyzing;

    public bool IsAvailable { get; private set; }
    public bool IsAnalyzing => this._isAnalyzing;
    public Observable<UsiInfo> InfoReceived => this._infoReceived;
    public Observable<UsiBestMove> BestMoveReceived => this._bestMoveReceived;

    public YaneuraOuEngine(IJSRuntime jsRuntime)
    {
        this._jsRuntime = jsRuntime;
    }

    public async Task<bool> InitializeAsync()
    {
        if (this._initialized) {
            return this.IsAvailable;
        }

        this._initialized = true;

        try {
            // Cross-Origin Isolation確認（SharedArrayBufferに必要）
            if (!await this._jsRuntime.InvokeAsync<bool>("ShogiEngine.isCrossOriginIsolated")) {
                return this.IsAvailable = false;
            }

            return await this.InitializeEngineAsync();
        }
        catch {
            return this.IsAvailable = false;
        }
    }

    public async Task<bool> RestartAsync()
    {
        this._isReady = false;
        this._isAnalyzing = false;

        try {
            if (!await this._jsRuntime.InvokeAsync<bool>("ShogiEngine.restart")) {
                return this.IsAvailable = false;
            }

            // コールバック再設定
            this._dotNetRef?.Dispose();
            this._dotNetRef = DotNetObjectReference.Create(this);
            await this._jsRuntime.InvokeVoidAsync("ShogiEngine.setCallback", this._dotNetRef);

            return await this.WaitForReadyAsync();
        }
        catch {
            return this.IsAvailable = false;
        }
    }

    public async Task<UsiBestMove?> StopAsync()
    {
        if (!this.IsAvailable || !this._isAnalyzing) {
            return null;
        }

        this._stopTcs = new TaskCompletionSource<UsiBestMove>();
        await this._jsRuntime.InvokeAsync<bool>("ShogiEngine.stop");

        // bestmoveを待機（タイムアウト付き）
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try {
            return await this._stopTcs.Task.WaitAsync(cts.Token);
        }
        catch (OperationCanceledException) {
            // タイムアウト時は強制的にリセット
            this._isAnalyzing = false;
            return null;
        }
        finally {
            this._stopTcs = null;
        }
    }

    public async Task<bool> SendCommandAsync(string command)
    {
        if (!this.IsAvailable || !this._isReady) {
            return false;
        }

        return await this._jsRuntime.InvokeAsync<bool>("ShogiEngine.sendCommand", command);
    }

    public async Task<bool> GoAsync(string sfen, int depth = 0)
    {
        if (!this.IsAvailable || !this._isReady) {
            return false;
        }

        // 前回の分析を停止し、bestmoveを待機
        if (this._isAnalyzing) {
            await this.StopAsync();
        }

        this._isAnalyzing = true;
        return await this._jsRuntime.InvokeAsync<bool>("ShogiEngine.requestEvaluation", sfen, depth);
    }

    private async Task<bool> InitializeEngineAsync()
    {
        // エンジン初期化（JSでUSIハンドシェイク完了まで待機）
        if (!await this._jsRuntime.InvokeAsync<bool>("ShogiEngine.init")) {
            return this.IsAvailable = false;
        }

        // コールバック設定
        this._dotNetRef = DotNetObjectReference.Create(this);
        await this._jsRuntime.InvokeVoidAsync("ShogiEngine.setCallback", this._dotNetRef);

        this.IsAvailable = true;

        return await this.WaitForReadyAsync();
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

    /// <summary>エンジンがクラッシュした時の処理</summary>
    [JSInvokable]
    public async Task OnEngineCrash()
    {
        Console.WriteLine("Engine crashed, restarting...");
        this._isReady = false;
        this._isAnalyzing = false;
        this.IsAvailable = false;

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
        if (message == "readyok") {
            this._readyTcs?.TrySetResult();
            return Task.CompletedTask;
        }

        if (!this._isReady) {
            return Task.CompletedTask;
        }

        if (message.StartsWith("info ", StringComparison.Ordinal)) {
            // stop待機中はinfoメッセージを無視
            if (this._stopTcs is null) {
                var info = ParseUsiInfo(message);
                this._infoReceived.OnNext(info);
            }
        }
        else if (message.StartsWith("bestmove ", StringComparison.Ordinal)) {
            this._isAnalyzing = false;
            var bestMove = ParseBestMove(message);

            // stop待機中なら結果を返す
            if (this._stopTcs is not null) {
                this._stopTcs.TrySetResult(bestMove);
            }
            else {
                this._bestMoveReceived.OnNext(bestMove);
            }
        }

        return Task.CompletedTask;
    }

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

    public async ValueTask DisposeAsync()
    {
        if (this.IsAvailable && this._isAnalyzing) {
            await this.StopAsync();
        }
        this._dotNetRef?.Dispose();
        this._infoReceived.Dispose();
        this._bestMoveReceived.Dispose();
    }
}
