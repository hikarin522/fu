namespace Fu.Core.Abstractions;

/// <summary>
/// ストレージサービスのインターフェース
/// </summary>
public interface IStorageService
{
    /// <summary>値を取得</summary>
    ValueTask<T?> GetAsync<T>(string key);

    /// <summary>値を保存</summary>
    ValueTask SetAsync<T>(string key, T value);

    /// <summary>値を削除</summary>
    ValueTask RemoveAsync(string key);

    /// <summary>キーが存在するか</summary>
    ValueTask<bool> ContainsAsync(string key);
}
