namespace Fu.Core.Abstractions;

/// <summary>
/// ストレージのスコープ
/// </summary>
public enum StorageScope
{
    /// <summary>永続（ユーザー設定など、ずっと残る）</summary>
    Persistent,

    /// <summary>セッション（対局ごとにクリア）</summary>
    Session
}

/// <summary>
/// ストレージサービスのインターフェース
/// </summary>
public interface IStorageService
{
    /// <summary>値を取得</summary>
    ValueTask<T?> GetAsync<T>(string key, StorageScope scope = StorageScope.Persistent);

    /// <summary>値を保存</summary>
    ValueTask SetAsync<T>(string key, T value, StorageScope scope = StorageScope.Persistent);

    /// <summary>値を削除</summary>
    ValueTask RemoveAsync(string key, StorageScope scope = StorageScope.Persistent);

    /// <summary>キーが存在するか</summary>
    ValueTask<bool> ContainsAsync(string key, StorageScope scope = StorageScope.Persistent);

    /// <summary>セッションデータを一括クリア</summary>
    ValueTask ClearSessionAsync();
}
