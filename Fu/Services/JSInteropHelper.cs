using Microsoft.JSInterop;

namespace Fu.Services;

/// <summary>
/// JSInterop呼び出しのエラーハンドリングを統一するヘルパー
/// </summary>
public static class JSInteropHelper
{
    /// <summary>戻り値ありのJS呼び出し（エラー時はデフォルト値を返す）</summary>
    public static async ValueTask<T?> InvokeSafeAsync<T>(
        this IJSRuntime js,
        string identifier,
        params object?[]? args)
    {
        try {
            return await js.InvokeAsync<T>(identifier, args ?? []);
        }
        catch (JSException ex) {
            LogError(identifier, ex);
            return default;
        }
        catch (TaskCanceledException) {
            // 画面遷移時などのキャンセルは無視
            return default;
        }
    }

    /// <summary>戻り値ありのJS呼び出し（エラー時はフォールバック値を返す）</summary>
    public static async ValueTask<T> InvokeSafeAsync<T>(
        this IJSRuntime js,
        string identifier,
        T fallback,
        params object?[]? args)
    {
        try {
            return await js.InvokeAsync<T>(identifier, args ?? []);
        }
        catch (JSException ex) {
            LogError(identifier, ex);
            return fallback;
        }
        catch (TaskCanceledException) {
            return fallback;
        }
    }

    /// <summary>戻り値なしのJS呼び出し（エラー時はログ出力のみ）</summary>
    public static async ValueTask InvokeVoidSafeAsync(
        this IJSRuntime js,
        string identifier,
        params object?[]? args)
    {
        try {
            await js.InvokeVoidAsync(identifier, args ?? []);
        }
        catch (JSException ex) {
            LogError(identifier, ex);
        }
        catch (TaskCanceledException) {
            // 画面遷移時などのキャンセルは無視
        }
    }

    /// <summary>戻り値ありのJS呼び出し（エラーを例外として伝播、ログ付き）</summary>
    public static async ValueTask<T> InvokeWithLoggingAsync<T>(
        this IJSRuntime js,
        string identifier,
        params object?[]? args)
    {
        try {
            return await js.InvokeAsync<T>(identifier, args ?? []);
        }
        catch (JSException ex) {
            LogError(identifier, ex);
            throw;
        }
    }

    /// <summary>戻り値なしのJS呼び出し（エラーを例外として伝播、ログ付き）</summary>
    public static async ValueTask InvokeVoidWithLoggingAsync(
        this IJSRuntime js,
        string identifier,
        params object?[]? args)
    {
        try {
            await js.InvokeVoidAsync(identifier, args ?? []);
        }
        catch (JSException ex) {
            LogError(identifier, ex);
            throw;
        }
    }

    private static void LogError(string identifier, Exception ex) =>
        Console.WriteLine($"[JSInterop] {identifier} failed: {ex.Message}");
}
