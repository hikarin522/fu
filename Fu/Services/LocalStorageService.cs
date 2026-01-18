using System.Text.Json;

using Microsoft.JSInterop;

using Fu.Core.Abstractions;

namespace Fu.Services;

/// <summary>
/// localStorage を使用するストレージサービス実装
/// </summary>
public sealed class LocalStorageService : IStorageService
{
    private const string PersistentPrefix = "fu:";
    private const string SessionPrefix = "fu_session:";

    private readonly IJSRuntime _js;

    public LocalStorageService(IJSRuntime js) => this._js = js;

    public async ValueTask<T?> GetAsync<T>(string key, StorageScope scope = StorageScope.Persistent)
    {
        var fullKey = GetFullKey(key, scope);
        var json = await this._js.InvokeAsync<string?>("FuStorage.get", fullKey);
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
        await this._js.InvokeVoidAsync("FuStorage.set", fullKey, json);
    }

    public async ValueTask RemoveAsync(string key, StorageScope scope = StorageScope.Persistent)
    {
        var fullKey = GetFullKey(key, scope);
        await this._js.InvokeVoidAsync("FuStorage.remove", fullKey);
    }

    public async ValueTask<bool> ContainsAsync(string key, StorageScope scope = StorageScope.Persistent)
    {
        var fullKey = GetFullKey(key, scope);
        return await this._js.InvokeAsync<bool>("FuStorage.contains", fullKey);
    }

    public async ValueTask ClearSessionAsync() =>
        await this._js.InvokeVoidAsync("FuStorage.clearByPrefix", SessionPrefix);

    private static string GetFullKey(string key, StorageScope scope) =>
        scope == StorageScope.Session ? $"{SessionPrefix}{key}" : $"{PersistentPrefix}{key}";
}
