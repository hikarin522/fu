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
        object? arg0)
    {
        try {
            return await js.InvokeAsync<T>(identifier, arg0);
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

    /// <summary>戻り値ありのJS呼び出し（エラー時はデフォルト値を返す、引数2つ）</summary>
    public static async ValueTask<T?> InvokeSafeAsync<T>(
        this IJSRuntime js,
        string identifier,
        object? arg0,
        object? arg1)
    {
        try {
            return await js.InvokeAsync<T>(identifier, arg0, arg1);
        }
        catch (JSException ex) {
            LogError(identifier, ex);
            return default;
        }
        catch (TaskCanceledException) {
            return default;
        }
    }

    /// <summary>戻り値なしのJS呼び出し（エラー時はログ出力のみ）</summary>
    public static async ValueTask InvokeVoidSafeAsync(
        this IJSRuntime js,
        string identifier,
        object? arg0)
    {
        try {
            await js.InvokeVoidAsync(identifier, arg0);
        }
        catch (JSException ex) {
            LogError(identifier, ex);
        }
        catch (TaskCanceledException) {
            // 画面遷移時などのキャンセルは無視
        }
    }

    /// <summary>戻り値なしのJS呼び出し（エラー時はログ出力のみ、引数2つ）</summary>
    public static async ValueTask InvokeVoidSafeAsync(
        this IJSRuntime js,
        string identifier,
        object? arg0,
        object? arg1)
    {
        try {
            await js.InvokeVoidAsync(identifier, arg0, arg1);
        }
        catch (JSException ex) {
            LogError(identifier, ex);
        }
        catch (TaskCanceledException) {
            // 画面遷移時などのキャンセルは無視
        }
    }

    private static void LogError(string identifier, Exception ex) =>
        Console.WriteLine($"[JSInterop] {identifier} failed: {ex.Message}");
}
