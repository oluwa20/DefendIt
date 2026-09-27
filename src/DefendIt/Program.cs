using DefendIt.Components;
using DefendIt.Services;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents()
    .AddHubOptions(o => o.MaximumReceiveMessageSize = 32 * 1024 * 1024); // PDF upload over the circuit

builder.Services.Configure<AiOptions>(builder.Configuration.GetSection("Ai"));
builder.Services.AddHttpClient("llm", c => c.Timeout = Timeout.InfiniteTimeSpan);
builder.Services.AddScoped(sp => new LlmClient(
    sp.GetRequiredService<IHttpClientFactory>().CreateClient("llm"),
    sp.GetRequiredService<IOptions<AiOptions>>(),
    sp.GetRequiredService<ILogger<LlmClient>>()));
builder.Services.AddSingleton<PdfTextService>();
builder.Services.AddScoped<DocumentAnalyzer>();
builder.Services.AddScoped<PanelService>();
builder.Services.AddScoped<EvaluationService>();
builder.Services.AddScoped<SessionState>();
builder.Services.AddScoped<SessionStore>();

builder.Services.Configure<Microsoft.AspNetCore.Builder.ForwardedHeadersOptions>(o =>
{
    o.ForwardedHeaders = Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedFor | Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedProto;
    o.KnownIPNetworks.Clear();
    o.KnownProxies.Clear();
});

var app = builder.Build();
app.UseForwardedHeaders();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
if (app.Environment.IsDevelopment()) app.UseHttpsRedirection(); // the host terminates TLS in production

app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.MapGet("/healthz", (IOptions<AiOptions> o) => Results.Ok(new
{
    status = "ok",
    providers = o.Value.Providers.Select(p => new { p.Name, p.Model, configured = !string.IsNullOrWhiteSpace(p.ApiKey) }),
}));

app.Run();

public partial class Program;
