using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;

namespace DefendIt.Services;

public class ProviderOptions
{
    public string Name { get; set; } = "";
    public string Label { get; set; } = "";
    public string BaseUrl { get; set; } = "";
    public string Model { get; set; } = "";
    public string ApiKey { get; set; } = "";
    /// <summary>Optional per-call model overrides, e.g. { "analyze": "big-reasoning-model" }.</summary>
    public Dictionary<string, string> CallModels { get; set; } = [];

    public string ModelFor(string call) => CallModels.TryGetValue(call, out var m) && !string.IsNullOrWhiteSpace(m) ? m : Model;
}

public class AiOptions
{
    public List<ProviderOptions> Providers { get; set; } = [];
    public int TimeoutSeconds { get; set; } = 45;
    /// <summary>Per-call timeouts. Live questions get a short one so a slow provider fails over before the silence gets awkward.</summary>
    public Dictionary<string, int> CallTimeoutSeconds { get; set; } = [];
    /// <summary>Optional per-call provider order by Name; providers not listed keep their config order after the listed ones.</summary>
    public Dictionary<string, List<string>> CallOrder { get; set; } = [];

    public List<ProviderOptions> OrderFor(string call)
    {
        if (!CallOrder.TryGetValue(call, out var order) || order.Count == 0) return Providers;
        return Providers.OrderBy(p => { var i = order.IndexOf(p.Name); return i < 0 ? int.MaxValue : i; }).ToList();
    }
    public double Temperature { get; set; } = 0.4;
    /// <summary>Config-level outage switch, e.g. "primary". The ?simulateOutage=primary query string does the same per session.</summary>
    public string? SimulateOutage { get; set; }
}

public record LlmResult<T>(T Value, string Provider, bool IsFallback, long LatencyMs, int TotalTokens);

public class AllProvidersFailedException(string message) : Exception(message);

/// <summary>
/// One OpenAI-compatible chat-completions client. Tries providers in order, retries once on
/// timeout/5xx/429, retries once on invalid JSON, then fails over to the next provider.
/// </summary>
public class LlmClient(HttpClient http, IOptions<AiOptions> options, ILogger<LlmClient> logger)
{
    private readonly AiOptions _opt = options.Value;

    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowReadingFromString,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>Per-circuit switch set from ?simulateOutage=primary.</summary>
    public bool SimulatePrimaryOutage { get; set; }

    public int SessionTokens { get; private set; }
    public List<(string Call, string Provider, long Ms, int Tokens)> CallLog { get; } = [];

    public async Task<LlmResult<T>> CompleteJsonAsync<T>(string callName, string system, string user, CancellationToken ct = default)
    {
        var ordered = _opt.OrderFor(callName);
        var providers = ordered.Where(p => !string.IsNullOrWhiteSpace(p.ApiKey)).ToList();
        if (providers.Count == 0)
            throw new AllProvidersFailedException("No AI provider is configured. Set at least one API key.");

        var simulate = SimulatePrimaryOutage ||
                       string.Equals(_opt.SimulateOutage, "primary", StringComparison.OrdinalIgnoreCase);
        var errors = new List<string>();

        for (var i = 0; i < providers.Count; i++)
        {
            var p = providers[i];
            // "primary" always means the configured primary (Providers[0]), whatever the call's routing order.
            if (simulate && ReferenceEquals(p, _opt.Providers.FirstOrDefault()))
            {
                logger.LogWarning("[LLM] {Call}: simulated outage on primary provider {Provider}", callName, p.Name);
                errors.Add($"{p.Name}: simulated outage");
                continue;
            }

            var messages = new List<object>
            {
                new { role = "system", content = system },
                new { role = "user", content = user },
            };

            var jsonRetried = false;
            var transientRetried = false;
            while (true)
            {
                string raw;
                long ms;
                int tokens;
                try
                {
                    (raw, ms, tokens) = await SendAsync(p, p.ModelFor(callName), messages, _opt.CallTimeoutSeconds.GetValueOrDefault(callName, _opt.TimeoutSeconds), ct);
                }
                catch (TransientException ex) when (!transientRetried && ex.Message != "timeout")
                {
                    transientRetried = true;
                    logger.LogWarning("[LLM] {Call}: {Provider} transient failure ({Err}), retrying once", callName, p.Name, ex.Message);
                    await Task.Delay(800, ct);
                    continue;
                }
                catch (Exception ex) when (ex is TransientException or HttpRequestException or FatalProviderException)
                {
                    logger.LogWarning("[LLM] {Call}: {Provider} failed ({Err}), failing over", callName, p.Name, ex.Message);
                    errors.Add($"{p.Name}: {ex.Message}");
                    break;
                }

                if (TryParse<T>(raw, out var value))
                {
                    SessionTokens += tokens;
                    CallLog.Add((callName, p.Name, ms, tokens));
                    logger.LogInformation("[LLM] {Call}: answered by {Provider} ({Model}) in {Ms} ms, {Tokens} tokens",
                        callName, p.Name, p.ModelFor(callName), ms, tokens);
                    return new LlmResult<T>(value!, string.IsNullOrEmpty(p.Label) ? p.Name : p.Label, i > 0, ms, tokens);
                }

                if (jsonRetried)
                {
                    logger.LogWarning("[LLM] {Call}: {Provider} returned invalid JSON twice, failing over", callName, p.Name);
                    errors.Add($"{p.Name}: invalid JSON");
                    break;
                }
                jsonRetried = true;
                logger.LogWarning("[LLM] {Call}: {Provider} returned invalid JSON, asking again", callName, p.Name);
                messages.Add(new { role = "assistant", content = raw });
                messages.Add(new { role = "user", content = "Your last reply was invalid JSON. Return only the JSON." });
            }
        }

        throw new AllProvidersFailedException("All AI providers failed: " + string.Join("; ", errors));
    }

    private async Task<(string Content, long Ms, int Tokens)> SendAsync(ProviderOptions p, string model, List<object> messages, int timeoutSeconds, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

        var body = JsonSerializer.Serialize(new
        {
            model,
            messages,
            temperature = _opt.Temperature,
            max_tokens = 8000,
        });
        using var req = new HttpRequestMessage(HttpMethod.Post, p.BaseUrl.TrimEnd('/') + "/chat/completions")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", p.ApiKey);

        var sw = Stopwatch.StartNew();
        HttpResponseMessage res;
        try
        {
            res = await http.SendAsync(req, cts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TransientException("timeout");
        }
        catch (HttpRequestException ex)
        {
            throw new TransientException(ex.Message);
        }

        using (res)
        {
            var text = await res.Content.ReadAsStringAsync(cts.Token);
            sw.Stop();
            var code = (int)res.StatusCode;
            if (res.StatusCode == HttpStatusCode.TooManyRequests || code >= 500)
                throw new TransientException($"HTTP {code}");
            if (!res.IsSuccessStatusCode)
                throw new FatalProviderException($"HTTP {code}: {Truncate(text, 200)}");

            var node = JsonNode.Parse(text);
            var content = node?["choices"]?[0]?["message"]?["content"]?.GetValue<string>() ?? "";
            var tokens = node?["usage"]?["total_tokens"]?.GetValue<int>() ?? 0;
            return (content, sw.ElapsedMilliseconds, tokens);
        }
    }

    public static string StripFences(string raw)
    {
        var s = raw.Trim();
        if (s.StartsWith("```"))
        {
            var firstNewline = s.IndexOf('\n');
            s = firstNewline >= 0 ? s[(firstNewline + 1)..] : s.TrimStart('`');
            var end = s.LastIndexOf("```", StringComparison.Ordinal);
            if (end >= 0) s = s[..end];
        }
        s = s.Trim();
        // Models sometimes add a sentence before/after the object; keep the outermost JSON object.
        var start = s.IndexOf('{');
        var last = s.LastIndexOf('}');
        if (start > 0 || (last >= 0 && last < s.Length - 1))
            if (start >= 0 && last > start) s = s[start..(last + 1)];
        return s;
    }

    public static bool TryParse<T>(string raw, out T? value)
    {
        value = default;
        if (string.IsNullOrWhiteSpace(raw)) return false;
        try
        {
            value = JsonSerializer.Deserialize<T>(StripFences(raw), Json);
            return value is not null;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string Truncate(string s, int n) => s.Length <= n ? s : s[..n];

    private class TransientException(string m) : Exception(m);
    private class FatalProviderException(string m) : Exception(m);
}
