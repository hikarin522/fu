using System.Text.Json;

using Microsoft.JSInterop;

using Fu.Core.Abstractions;

namespace Fu.Services;

/// <summary>
/// localStorage を使用するストレージサービス実装
/// </summary>
public sealed class LocalStorageService(IJSRuntime js) : IStorageService
{
    private const string PersistentPrefix = "fu:";
    private const string SessionPrefix = "fu_session:";

    public async ValueTask<T?> GetAsync<T>(string key, StorageScope scope = StorageScope.Persistent)
    {
        var fullKey = GetFullKey(key, scope);
        var json = await js.InvokeSafeAsync<string?>("localStorage.getItem", fullKey);
        if (json is null) {
            return default;
        }

        try {
            return JsonSerializer.Deserialize<T>(json);
        }
        catch {
            return default;
        }
    }

    public async ValueTask SetAsync<T>(string key, T value, StorageScope scope = StorageScope.Persistent)
    {
        var fullKey = GetFullKey(key, scope);
        var json = JsonSerializer.Serialize(value);
        await js.InvokeVoidSafeAsync("localStorage.setItem", fullKey, json);
    }

    public async ValueTask RemoveAsync(string key, StorageScope scope = StorageScope.Persistent)
    {
        var fullKey = GetFullKey(key, scope);
        await js.InvokeVoidSafeAsync("localStorage.removeItem", fullKey);
    }

    public async ValueTask<bool> ContainsAsync(string key, StorageScope scope = StorageScope.Persistent)
    {
        var fullKey = GetFullKey(key, scope);
        var value = await js.InvokeSafeAsync<string?>("localStorage.getItem", fullKey);
        return value is not null;
    }

    private static string GetFullKey(string key, StorageScope scope) =>
        scope == StorageScope.Session ? $"{SessionPrefix}{key}" : $"{PersistentPrefix}{key}";
}
