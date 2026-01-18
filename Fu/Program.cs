using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;

using Fu;
using Fu.Core.Services;
using Fu.Services;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

builder.Services.AddScoped(sp => new HttpClient { BaseAddress = new Uri(builder.HostEnvironment.BaseAddress) });
builder.Services.AddScoped<ShogiGameService>();
builder.Services.AddScoped<WebRtcService>();
builder.Services.AddScoped<ShogiEngineService>();
builder.Services.AddScoped<UserSettingsService>();
builder.Services.AddScoped<LobbyService>(sp =>
    new LobbyService(sp.GetRequiredService<WebRtcService>()));
builder.Services.AddScoped<GameSessionService>(sp =>
    new GameSessionService(
        sp.GetRequiredService<WebRtcService>(),
        sp.GetRequiredService<ShogiGameService>(),
        sp.GetRequiredService<LobbyService>(),
        sp.GetRequiredService<Microsoft.JSInterop.IJSRuntime>()));

await builder.Build().RunAsync();
