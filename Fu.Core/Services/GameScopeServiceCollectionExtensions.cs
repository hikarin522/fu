using MessagePipe;
using Microsoft.Extensions.DependencyInjection;

using Fu.Core.Abstractions;
using Fu.Core.Events;

namespace Fu.Core.Services;

/// <summary>
/// 対局スコープのサービス登録拡張
/// </summary>
public static class GameScopeServiceCollectionExtensions
{
    /// <summary>
    /// 対局スコープのファクトリを登録
    /// </summary>
    public static IServiceCollection AddGameScopeFactory(this IServiceCollection services)
    {
        services.AddSingleton<IGameScopeFactory, GameScopeFactory>();
        return services;
    }

    /// <summary>
    /// MessagePipeのゲームイベントを登録（スコープ用）
    /// </summary>
    public static IServiceCollection AddGameEvents(this IServiceCollection services)
    {
        // MessagePipe本体を登録（Scoped lifetime for game scope compatibility）
        // .NETでは AddMessagePipe() だけで IPublisher<T>/ISubscriber<T> が自動解決される
        services.AddMessagePipe(options =>
        {
            // ゲームスコープと整合性を取るため Scoped lifetime を使用
            options.InstanceLifetime = InstanceLifetime.Scoped;
        });

        return services;
    }

    /// <summary>
    /// MessagePipeのトランスポートイベントを登録（シングルトン用）
    /// WebRtcService等のトランスポート層から発行されるイベント
    /// </summary>
    public static IServiceCollection AddTransportEvents(this IServiceCollection services)
    {
        // トランスポートイベントはSingletonで共有
        // 注: MessagePipeは複数回AddMessagePipe()を呼んでも問題ない（最後の設定が優先されるわけではなく、追加設定される）
        // ただし、InstanceLifetimeが異なる場合は注意が必要
        // ここでは別のMessagePipeインスタンスが必要になる場合があるため、
        // Transport用のイベントはScopedでも問題ないように設計する
        return services;
    }

    /// <summary>
    /// 対局スコープ内で使用するサービスを登録
    /// これらのサービスはIGameScopeFactory.CreateScope()で作成されたスコープ内で解決される
    /// </summary>
    public static IServiceCollection AddGameScopedServices(this IServiceCollection services)
    {
        // 対局スコープ内でのみ有効なサービス（Scoped登録だが、IGameScope経由で取得）
        services.AddScoped<ShogiGameState>();
        services.AddScoped<IGameEventPublisher, GameEventPublisher>();
        services.AddScoped<ITurnTimerService, TurnTimerService>();
        services.AddScoped<IBoardCache, BoardCache>();

        // ゲームサービス群
        services.AddScoped<IGameLifecycleService, GameLifecycleService>();
        services.AddScoped<IGameNavigationService, GameNavigationService>();
        services.AddScoped<IBranchService, BranchService>();
        services.AddScoped<ShogiGameService>();

        return services;
    }

    /// <summary>
    /// 対局スコープ関連のサービスをすべて登録
    /// </summary>
    public static IServiceCollection AddGameScope(this IServiceCollection services)
    {
        return services
            .AddGameScopeFactory()
            .AddGameEvents()
            .AddGameScopedServices();
    }
}
