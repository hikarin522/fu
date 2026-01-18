using System.Text.Json;

using Microsoft.JSInterop;

using Fu.Core.Abstractions;

namespace Fu.Services;

/// <summary>
/// localStorage を使用するストレージサービス実装
/// </summary>
public sealed class LocalStorageService : IStorageService
{
    private readonly IJSRuntime _js;

    public LocalStorageService(IJSRuntime js) => this._js = js;

    public async ValueTask<T?> GetAsync<T>(string key)
    {
        var json = await this._js.InvokeAsync<string?>("FuStorage.get", key);
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

    public async ValueTask SetAsync<T>(string key, T value)
    {
        var json = JsonSerializer.Serialize(value);
        await this._js.InvokeVoidAsync("FuStorage.set", key, json);
    }

    public async ValueTask RemoveAsync(string key) =>
        await this._js.InvokeVoidAsync("FuStorage.remove", key);

    public async ValueTask<bool> ContainsAsync(string key) =>
        await this._js.InvokeAsync<bool>("FuStorage.contains", key);
}
