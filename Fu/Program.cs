using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;

using Fu;
using Fu.Core;
using Fu.Core.Abstractions;
using Fu.Core.Services;
using Fu.Services;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

builder.Services.AddScoped(sp => new HttpClient { BaseAddress = new Uri(builder.HostEnvironment.BaseAddress) });
builder.Services.AddScoped<IStorageService, LocalStorageService>();

// ルール・ユーティリティ（ステートレス、Singleton）
builder.Services.AddSingleton<IShogiRules, ShogiRules>();
builder.Services.AddSingleton<IKifExporter, KifExporter>();
builder.Services.AddSingleton<IUsiParser, UsiParser>();
builder.Services.AddSingleton<ISfenConverter, SfenConverter>();
builder.Services.AddSingleton<IGameTimerFactory, GameTimerFactory>();

// 対局スコープ関連（MessagePipeイベント + スコープ内サービス）
builder.Services.AddGameScope();

// タブスコープのサービス（タブのライフタイム全体で有効）
// WebRtcService は IGameTransport, ITransportConnection, ITransportSender を実装
builder.Services.AddScoped<WebRtcService>();
builder.Services.AddScoped<IGameTransport>(sp => sp.GetRequiredService<WebRtcService>());
builder.Services.AddScoped<ITransportConnection>(sp => sp.GetRequiredService<WebRtcService>());
builder.Services.AddScoped<ITransportSender>(sp => sp.GetRequiredService<WebRtcService>());

builder.Services.AddScoped<ShogiEngineService>();
builder.Services.AddScoped<UserSettingsService>();
builder.Services.AddScoped<LobbyService>();
builder.Services.AddScoped<GameSessionService>();

await builder.Build().RunAsync();
