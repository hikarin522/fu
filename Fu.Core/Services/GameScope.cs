using Microsoft.Extensions.DependencyInjection;

using Fu.Core.Abstractions;

namespace Fu.Core.Services;

/// <summary>
/// 対局スコープの実装
/// IServiceScopeをラップして対局固有のサービスを管理
/// </summary>
public sealed class GameScope : IGameScope
{
    private readonly IServiceScope _scope;
    private bool _disposed;

    public GameScope(IServiceScope scope)
    {
        this._scope = scope;
    }

    public bool IsActive => !this._disposed;

    public T GetService<T>() where T : notnull
    {
        ObjectDisposedException.ThrowIf(this._disposed, this);
        return this._scope.ServiceProvider.GetRequiredService<T>();
    }

    public T? GetServiceOrDefault<T>() where T : class
    {
        if (this._disposed) {
            return null;
        }
        return this._scope.ServiceProvider.GetService<T>();
    }

    public async ValueTask DisposeAsync()
    {
        if (this._disposed) {
            return;
        }
        this._disposed = true;

        // スコープ内のIAsyncDisposable/IDisposableを自動的にDisposeする
        if (this._scope is IAsyncDisposable asyncDisposable) {
            await asyncDisposable.DisposeAsync();
        } else {
            this._scope.Dispose();
        }
    }
}

/// <summary>
/// 対局スコープを作成するファクトリの実装
/// </summary>
public sealed class GameScopeFactory : IGameScopeFactory
{
    private readonly IServiceScopeFactory _scopeFactory;

    public GameScopeFactory(IServiceScopeFactory scopeFactory)
    {
        this._scopeFactory = scopeFactory;
    }

    public IGameScope CreateScope()
    {
        var scope = this._scopeFactory.CreateScope();
        return new GameScope(scope);
    }
}
