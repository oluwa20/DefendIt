using System.Net;
using System.Text;
using DefendIt.Models;
using DefendIt.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace DefendIt.Tests;

public class JsonParsingTests
{
    private record Dto(string Name, int Score);

    [Theory]
    [InlineData("{\"name\":\"a\",\"score\":3}")]
    [InlineData("```json\n{\"name\":\"a\",\"score\":3}\n```")]
    [InlineData("```\n{\"name\":\"a\",\"score\":3}\n```")]
    [InlineData("Here is the JSON:\n{\"name\":\"a\",\"score\":3}\nHope this helps.")]
    [InlineData("{\"Name\":\"a\",\"Score\":\"3\",}")]
    public void Parses_fenced_and_chatty_json(string raw)
    {
        Assert.True(LlmClient.TryParse<Dto>(raw, out var dto));
        Assert.Equal("a", dto!.Name);
        Assert.Equal(3, dto.Score);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Sorry, I cannot help with that.")]
    [InlineData("{\"name\": ")]
    public void Rejects_invalid_json(string raw) => Assert.False(LlmClient.TryParse<Dto>(raw, out _));
}

public class DeliveryMetricsTests
{
    [Fact]
    public void Counts_english_fillers()
    {
        var text = "Um, so the model, uh, was like really good, you know. I like it. Umbrella is not a filler.";
        Assert.Equal(5, DeliveryMetrics.CountFillers(text, "en")); // um, uh, like, you know, like
    }

    [Fact]
    public void Counts_french_fillers()
    {
        var text = "Euh, en fait le modèle, du coup, marche bien, genre vraiment. Heu voilà. Le générique n'est pas compté.";
        Assert.Equal(5, DeliveryMetrics.CountFillers(text, "fr")); // euh, en fait, du coup, genre, heu
    }

    [Fact]
    public void Counts_arabic_fillers()
    {
        var text = "يعني النموذج، امم، كان جيداً. طيب، يعني النتائج واضحة. التعليم ليس حشواً.";
        Assert.Equal(4, DeliveryMetrics.CountFillers(text, "ar")); // يعني ×2, امم, طيب
    }

    [Fact]
    public void Arabic_ui_strings_and_rtl()
    {
        Assert.Equal("ابدأ مناقشتي", L.T("ar", "Start my defense", "Commencer ma soutenance"));
        Assert.Equal("Untranslated", L.T("ar", "Untranslated", "Non traduit")); // falls back to English
        Assert.Equal("rtl", L.Dir("ar"));
        Assert.Equal("ar-SA", L.SpeechLang("ar"));
    }

    [Fact]
    public void Computes_pace_from_spoken_answers_only()
    {
        var turns = new List<Turn>
        {
            new("supervisor", "q", "x", false, string.Join(' ', Enumerable.Repeat("word", 120)), 60),
            new("methodologist", "q", "x", false, string.Join(' ', Enumerable.Repeat("typed", 300)), 60),
        };
        var stats = DeliveryMetrics.Compute(turns, "en", new HashSet<int> { 1 });
        Assert.Equal(120, stats.WordsPerMinute);
        Assert.Equal(60, stats.AvgAnswerSeconds);
    }

    [Fact]
    public void Empty_answers_do_not_crash()
    {
        var stats = DeliveryMetrics.Compute([new Turn("supervisor", "q", "x", false, "", 0)], "en");
        Assert.Equal(0, stats.WordsPerMinute);
        Assert.Equal(0, stats.FillerWords);
    }
}

public class PanelRulesTests
{
    private static DocumentBrief Brief(int inconsistencies) => new("T", "F", "S", [], "M", [],
        Enumerable.Range(0, inconsistencies).Select(i => new Inconsistency($"issue {i}", "p.1", "p.2", i == 0 ? "high" : "medium")).ToList(), []);

    private static Turn T(string ex, bool followUp = false, string area = "Methodology") => new(ex, "q", area, followUp, "a", 10);

    [Fact]
    public void Supervisor_opens()
    {
        var plan = PanelService.MakePlan(Brief(0), []);
        Assert.Equal("supervisor", plan.NextInRotation);
        Assert.Null(plan.FollowUpCandidate);
    }

    [Fact]
    public void Rotation_and_single_follow_up()
    {
        var plan = PanelService.MakePlan(Brief(0), [T("supervisor")]);
        Assert.Equal("methodologist", plan.NextInRotation);
        Assert.Equal("supervisor", plan.FollowUpCandidate);

        var afterFollowUp = PanelService.MakePlan(Brief(0), [T("supervisor"), T("supervisor", followUp: true)]);
        Assert.Null(afterFollowUp.FollowUpCandidate); // at most one follow-up
        Assert.Equal("methodologist", afterFollowUp.NextInRotation);
    }

    [Fact]
    public void External_raises_inconsistency_on_first_turn()
    {
        var plan = PanelService.MakePlan(Brief(2), [T("supervisor"), T("methodologist")]);
        Assert.Equal("external", plan.NextInRotation);
        Assert.Equal("issue 0", plan.MustRaise!.Description); // high severity first
        Assert.Null(plan.FollowUpCandidate);
    }

    [Fact]
    public void Inconsistency_forced_before_running_out()
    {
        // Lots of follow-ups pushed the external examiner out; the panel must still raise it.
        var turns = new List<Turn> { T("supervisor"), T("supervisor", true), T("methodologist"), T("methodologist", true), T("supervisor") };
        // Next in rotation would be methodologist? main count = 3 -> supervisor... force anyway at turn 6 of 7
        var plan = PanelService.MakePlan(Brief(1), turns);
        Assert.NotNull(plan.MustRaise);
        Assert.Equal("external", plan.NextInRotation);
    }

    [Fact]
    public void Enforce_corrects_model_drift()
    {
        var plan = new PanelService.Plan("methodologist", null, null, false);
        var q = PanelService.Enforce(new NextQuestion("external", " Why? ", "Originality", true, null), plan);
        Assert.Equal("methodologist", q.ExaminerId);
        Assert.False(q.IsFollowUp);
        Assert.Equal("Why?", q.Question);
    }

    [Fact]
    public void No_inconsistency_means_none_forced()
    {
        var plan = PanelService.MakePlan(Brief(0), [T("supervisor"), T("methodologist")]);
        Assert.Null(plan.MustRaise);
    }
}

public class FailoverTests
{
    private sealed class FakeHandler(Func<HttpRequestMessage, int, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<string> Hosts { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Hosts.Add(request.RequestUri!.Host);
            return Task.FromResult(respond(request, Hosts.Count));
        }
    }

    private static HttpResponseMessage Ok(string content) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(
            "{\"choices\":[{\"message\":{\"content\":" + System.Text.Json.JsonSerializer.Serialize(content) + "}}],\"usage\":{\"total_tokens\":42}}",
            Encoding.UTF8, "application/json"),
    };

    private static LlmClient Client(FakeHandler h, string? simulate = null) => new(new HttpClient(h),
        Options.Create(new AiOptions
        {
            TimeoutSeconds = 5,
            SimulateOutage = simulate,
            Providers =
            [
                new() { Name = "nvidia", Label = "NVIDIA", BaseUrl = "https://primary.test/v1", Model = "m", ApiKey = "k" },
                new() { Name = "groq", Label = "Groq", BaseUrl = "https://fallback1.test/v1", Model = "m", ApiKey = "k" },
                new() { Name = "gemini", Label = "Gemini", BaseUrl = "https://fallback2.test/v1", Model = "m", ApiKey = "k" },
            ],
        }), NullLogger<LlmClient>.Instance);

    private record Dto(string Name);

    [Fact]
    public async Task Primary_answers_when_healthy()
    {
        var h = new FakeHandler((_, _) => Ok("{\"name\":\"ok\"}"));
        var r = await Client(h).CompleteJsonAsync<Dto>("t", "s", "u");
        Assert.Equal("NVIDIA", r.Provider);
        Assert.False(r.IsFallback);
        Assert.Equal(["primary.test"], h.Hosts);
    }

    [Fact]
    public async Task Retries_once_then_fails_over_in_order()
    {
        var h = new FakeHandler((req, _) => req.RequestUri!.Host == "fallback2.test"
            ? Ok("{\"name\":\"ok\"}")
            : new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        var r = await Client(h).CompleteJsonAsync<Dto>("t", "s", "u");
        Assert.Equal("Gemini", r.Provider);
        Assert.True(r.IsFallback);
        Assert.Equal(["primary.test", "primary.test", "fallback1.test", "fallback1.test", "fallback2.test"], h.Hosts);
    }

    [Fact]
    public async Task Fails_over_immediately_on_auth_error()
    {
        var h = new FakeHandler((req, _) => req.RequestUri!.Host == "primary.test"
            ? new HttpResponseMessage(HttpStatusCode.Unauthorized)
            : Ok("{\"name\":\"ok\"}"));
        var r = await Client(h).CompleteJsonAsync<Dto>("t", "s", "u");
        Assert.Equal("Groq", r.Provider);
        Assert.Equal(["primary.test", "fallback1.test"], h.Hosts);
    }

    [Fact]
    public async Task Invalid_json_is_retried_once_on_same_provider()
    {
        var h = new FakeHandler((_, n) => n == 1 ? Ok("not json at all") : Ok("```json\n{\"name\":\"fixed\"}\n```"));
        var r = await Client(h).CompleteJsonAsync<Dto>("t", "s", "u");
        Assert.Equal("fixed", r.Value.Name);
        Assert.Equal(["primary.test", "primary.test"], h.Hosts);
    }

    [Fact]
    public async Task Simulated_outage_skips_primary()
    {
        var h = new FakeHandler((_, _) => Ok("{\"name\":\"ok\"}"));
        var r = await Client(h, "primary").CompleteJsonAsync<Dto>("t", "s", "u");
        Assert.Equal("Groq", r.Provider);
        Assert.True(r.IsFallback);
        Assert.DoesNotContain("primary.test", h.Hosts);
    }

    [Fact]
    public async Task Per_call_order_routes_to_preferred_provider()
    {
        var h = new FakeHandler((_, _) => Ok("{\"name\":\"ok\"}"));
        var opts = new AiOptions
        {
            CallOrder = new() { ["analyze"] = ["groq"] },
            Providers =
            [
                new() { Name = "nvidia", Label = "NVIDIA", BaseUrl = "https://primary.test/v1", Model = "m", ApiKey = "k" },
                new() { Name = "groq", Label = "Groq", BaseUrl = "https://fallback1.test/v1", Model = "m", ApiKey = "k" },
            ],
        };
        var c = new LlmClient(new HttpClient(h), Options.Create(opts), NullLogger<LlmClient>.Instance);
        var a = await c.CompleteJsonAsync<Dto>("analyze", "s", "u");
        var q = await c.CompleteJsonAsync<Dto>("next-question", "s", "u");
        Assert.Equal("Groq", a.Provider);
        Assert.False(a.IsFallback);
        Assert.Equal("NVIDIA", q.Provider);
    }

    [Fact]
    public async Task All_down_throws_friendly_exception()
    {
        var h = new FakeHandler((_, _) => new HttpResponseMessage(HttpStatusCode.TooManyRequests));
        await Assert.ThrowsAsync<AllProvidersFailedException>(() => Client(h).CompleteJsonAsync<Dto>("t", "s", "u"));
    }
}

public class PdfTests
{
    private static string Fixture(params string[] parts)
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "DefendIt.slnx"))) dir = Path.GetDirectoryName(dir);
        return Path.Combine([dir!, .. parts]);
    }

    [Fact]
    public void Sample_thesis_is_page_tagged()
    {
        using var f = File.OpenRead(Fixture("src", "DefendIt", "wwwroot", "samples", "sample-thesis.pdf"));
        var doc = new PdfTextService().Extract(f);
        Assert.Contains("[p.2]", doc.Text);
        Assert.Contains("95%", doc.Text);
        Assert.Contains("89.3%", doc.Text);
        Assert.False(doc.Truncated);
    }

    [Fact]
    public void Scanned_pdf_gives_friendly_error()
    {
        using var f = File.OpenRead(Fixture("tests", "fixtures", "scanned.pdf"));
        var ex = Assert.Throws<NoReadableTextException>(() => new PdfTextService().Extract(f));
        Assert.Contains("no readable text", ex.Message);
    }

    [Fact]
    public void Long_pdf_is_truncated_but_keeps_abstract_and_conclusion()
    {
        using var f = File.OpenRead(Fixture("tests", "fixtures", "long.pdf"));
        var doc = new PdfTextService().Extract(f);
        Assert.True(doc.Truncated);
        Assert.True(doc.Text.Length <= PdfTextService.MaxChars);
        Assert.Contains("[p.1]", doc.Text);
        Assert.Contains($"[p.{doc.PageCount}]", doc.Text);
    }

    [Fact]
    public void Garbage_bytes_are_rejected_cleanly()
    {
        using var ms = new MemoryStream(Encoding.UTF8.GetBytes("this is not a pdf"));
        Assert.Throws<InvalidDataException>(() => new PdfTextService().Extract(ms));
    }
}
