namespace Fu.Core.Abstractions;

/// <summary>
/// 対局スコープのインターフェース
/// 新規対局ごとに作成され、対局終了時にDisposeされる
/// </summary>
public interface IGameScope : IAsyncDisposable
{
    /// <summary>スコープが有効かどうか</summary>
    bool IsActive { get; }

    /// <summary>スコープ内のサービスを取得</summary>
    T GetService<T>() where T : notnull;

    /// <summary>スコープ内のサービスを取得（見つからない場合はnull）</summary>
    T? GetServiceOrDefault<T>() where T : class;
}

/// <summary>
/// 対局スコープを作成するファクトリ
/// </summary>
public interface IGameScopeFactory
{
    /// <summary>新しい対局スコープを作成</summary>
    IGameScope CreateScope();
}
