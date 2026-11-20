using Microsoft.Extensions.Options;
using ReviewLens.Web;
using ReviewLens.Web.Components;
using ReviewLens.Web.Services;

// Load .env (searching up from the current folder) into environment variables BEFORE the
// builder is created, so GEMINI_API_KEY is visible through IConfiguration. Never hard-code the key.
DotNetEnv.Env.TraversePath().Load();

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<ReviewLensOptions>(builder.Configuration.GetSection(ReviewLensOptions.Section));

// Singletons: one rate limiter for the whole app, so the per-minute cap holds across all users/batches.
builder.Services.AddSingleton(sp =>
    new RequestRateLimiter(sp.GetRequiredService<IOptions<ReviewLensOptions>>().Value.RequestsPerMinute));
builder.Services.AddSingleton<AgentFactory>();
builder.Services.AddSingleton<ReviewAnalyzer>();
builder.Services.AddSingleton<InsightsService>();
builder.Services.AddSingleton<CsvService>();

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

var app = builder.Build();

// 3-row smoke test against Gemini: dotnet run --project src/ReviewLens.Web -- --smoke
if (args.Contains("--smoke"))
    return await SmokeTest.RunAsync(app.Services);

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseHttpsRedirection();

app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
return 0;
