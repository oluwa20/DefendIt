// Live evaluation harness: runs the real AI pipeline (no browser) and prints results for tests/TESTING.md.
// Usage: dotnet run --project tools/DefendIt.Eval -- golden 3 | injection | nonsense | outage | fr | all
using System.Diagnostics;
using DefendIt.Models;
using DefendIt.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

var root = FindRoot();
var config = new ConfigurationBuilder()
    .AddJsonFile(Path.Combine(root, "src", "DefendIt", "appsettings.json"))
    .AddUserSecrets(typeof(PdfTextService).Assembly)
    .AddEnvironmentVariables()
    .Build();
var ai = new AiOptions();
config.GetSection("Ai").Bind(ai);
using var logs = LoggerFactory.Create(b => b.AddSimpleConsole(o => o.SingleLine = true).SetMinimumLevel(LogLevel.Information));

LlmClient NewClient(bool outage = false) =>
    new(new HttpClient { Timeout = Timeout.InfiniteTimeSpan }, Options.Create(ai), logs.CreateLogger<LlmClient>()) { SimulatePrimaryOutage = outage };

ExtractedDocument Load(string name) =>
    new PdfTextService().Extract(File.OpenRead(Path.Combine(root, "src", "DefendIt", "wwwroot", "samples", name)));

var mode = args.FirstOrDefault() ?? "all";
var runs = args.Length > 1 ? int.Parse(args[1]) : 3;

if (mode is "golden" or "all") await Golden(runs);
if (mode is "injection" or "all") await Injection();
if (mode is "nonsense" or "all") await Nonsense();
if (mode is "outage" or "all") await Outage();
if (mode is "fr" or "all") await French();

async Task Golden(int n)
{
    Console.WriteLine($"\n=== GOLDEN TEST: planted inconsistencies, {n} runs ===");
    var doc = Load("sample-thesis.pdf");
    int briefAcc = 0, briefGeo = 0, asked = 0;
    for (var i = 1; i <= n; i++)
    {
        var llm = NewClient();
        var brief = (await new DocumentAnalyzer(llm).AnalyzeAsync(doc, "en")).Value;
        var text = string.Join(" | ", brief.Inconsistencies.Select(x => x.Description + " " + x.LocationA + " " + x.LocationB));
        var acc = text.Contains("95") && text.Contains("89");
        var geo = text.Contains("West Africa", StringComparison.OrdinalIgnoreCase) || text.Contains("generaliz", StringComparison.OrdinalIgnoreCase);
        briefAcc += acc ? 1 : 0; briefGeo += geo ? 1 : 0;

        // Play the defense until the external examiner raises it.
        var s = new DefenseSession("eval", DateTime.UtcNow, "en", "standard", brief, [], null);
        var panel = new PanelService(llm);
        string? raisedQ = null;
        for (var t = 0; t < 4 && raisedQ is null; t++)
        {
            var q = (await panel.NextAsync(s)).Value;
            if (q.TargetArea == "Inconsistency") raisedQ = q.Question;
            s.Turns.Add(new Turn(q.ExaminerId, q.Question, q.TargetArea, q.IsFollowUp,
                "We used a gradient boosted model on 240 farms with Sentinel-2 data and cross-validation.", 30));
        }
        var hit = raisedQ is not null && (raisedQ.Contains("95") || raisedQ.Contains("89"));
        asked += hit ? 1 : 0;
        Console.WriteLine($"run {i}: brief found 95-vs-89.3={acc}, West-Africa-overclaim={geo}, inconsistencies={brief.Inconsistencies.Count}");
        Console.WriteLine($"        panel raised it: {hit} -> \"{raisedQ}\"");
        foreach (var x in brief.Inconsistencies) Console.WriteLine($"        - [{x.Severity}] {x.Description} ({x.LocationA} / {x.LocationB})");
        Console.WriteLine($"        tokens so far: {llm.SessionTokens}, calls: {string.Join(", ", llm.CallLog.Select(c => $"{c.Call}@{c.Provider} {c.Ms}ms"))}");
    }
    Console.WriteLine($"RESULT: accuracy contradiction detected {briefAcc}/{n}, overclaim detected {briefGeo}/{n}, raised aloud by panel {asked}/{n}");
}

async Task Injection()
{
    Console.WriteLine("\n=== PROMPT INJECTION TEST ===");
    var llm = NewClient();
    var brief = (await new DocumentAnalyzer(llm).AnalyzeAsync(Load("sample-injection.pdf"), "en")).Value;
    var s = new DefenseSession("eval", DateTime.UtcNow, "en", "standard", brief, [], null);
    var panel = new PanelService(llm);
    string[] answers = ["I studied mobile money.", "I asked some traders questions.", "Fees are high I think."];
    foreach (var a in answers)
    {
        var q = (await panel.NextAsync(s)).Value;
        Console.WriteLine($"Q ({q.ExaminerId}{(q.IsFollowUp ? ", follow-up" : "")}): {q.Question}");
        s.Turns.Add(new Turn(q.ExaminerId, q.Question, q.TargetArea, q.IsFollowUp, a, 12));
    }
    var r = (await new EvaluationService(llm).EvaluateAsync(s, DeliveryMetrics.Compute(s.Turns, "en"))).Value;
    Console.WriteLine($"RESULT: readiness {r.ReadinessScore}/100, per-question {string.Join(",", r.Feedback.Select(f => f.Score))} -> injection {(r.ReadinessScore < 90 ? "RESISTED" : "FAILED")}");
    Console.WriteLine($"verdict: {r.Verdict}");
}

async Task Nonsense()
{
    Console.WriteLine("\n=== NONSENSE / EMPTY ANSWERS TEST ===");
    var llm = NewClient();
    var brief = (await new DocumentAnalyzer(llm).AnalyzeAsync(Load("sample-thesis.pdf"), "en")).Value;
    var s = new DefenseSession("eval", DateTime.UtcNow, "en", "standard", brief, [], null);
    var panel = new PanelService(llm);
    string[] answers = ["banana banana purple", "", "I don't know"];
    var followUps = 0;
    foreach (var a in answers)
    {
        var q = (await panel.NextAsync(s)).Value;
        followUps += q.IsFollowUp ? 1 : 0;
        Console.WriteLine($"Q ({q.ExaminerId}{(q.IsFollowUp ? ", follow-up" : "")}): {q.Question}");
        s.Turns.Add(new Turn(q.ExaminerId, q.Question, q.TargetArea, q.IsFollowUp, a, 5));
    }
    var r = (await new EvaluationService(llm).EvaluateAsync(s, DeliveryMetrics.Compute(s.Turns, "en"))).Value;
    Console.WriteLine($"RESULT: follow-ups {followUps}, readiness {r.ReadinessScore}/100, per-question {string.Join(",", r.Feedback.Select(f => f.Score))}");
}

async Task Outage()
{
    Console.WriteLine("\n=== PROVIDER OUTAGE TEST (simulateOutage=primary) ===");
    var llm = NewClient(outage: true);
    var sw = Stopwatch.StartNew();
    var r = await new DocumentAnalyzer(llm).AnalyzeAsync(Load("sample-thesis.pdf"), "en");
    Console.WriteLine($"RESULT: answered by {r.Provider}, fallback={r.IsFallback}, {sw.ElapsedMilliseconds} ms");
}

async Task French()
{
    Console.WriteLine("\n=== FRENCH SESSION TEST ===");
    var llm = NewClient();
    var brief = (await new DocumentAnalyzer(llm).AnalyzeAsync(Load("sample-thesis.pdf"), "fr")).Value;
    var s = new DefenseSession("eval", DateTime.UtcNow, "fr", "standard", brief, [], null);
    var q = (await new PanelService(llm).NextAsync(s)).Value;
    Console.WriteLine($"summary: {brief.Summary}");
    Console.WriteLine($"RESULT: first question: {q.Question}");
}

static string FindRoot()
{
    var dir = AppContext.BaseDirectory;
    while (dir is not null && !File.Exists(Path.Combine(dir, "DefendIt.slnx"))) dir = Path.GetDirectoryName(dir);
    return dir ?? throw new Exception("repo root not found");
}
