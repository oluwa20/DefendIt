using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;

namespace DefendIt.Services;

public class TtsOptions
{
    public bool Enabled { get; set; } = true;
    public string GroqModel { get; set; } = "canopylabs/orpheus-v1-english";
    public string GeminiModel { get; set; } = "gemini-3.8-flash-tts";
    public int GroqTimeoutSeconds { get; set; } = 6;
    public int GeminiTimeoutSeconds { get; set; } = 10;
    /// <summary>examinerId → voice name, per engine.</summary>
    public Dictionary<string, string> GroqVoices { get; set; } = new() { ["supervisor"] = "diana", ["methodologist"] = "daniel", ["external"] = "troy" };
    public Dictionary<string, string> GeminiVoices { get; set; } = new() { ["supervisor"] = "Kore", ["methodologist"] = "Charon", ["external"] = "Fenrir" };
}

public record SpeechAudio(byte[] Bytes, string Mime, string Engine);

/// <summary>
/// Neural text-to-speech for the examiners. Groq Orpheus (English, fast) → Gemini TTS (any language).
/// Returns null when neither answers in time; the browser's own voice is the final fallback.
/// Keys stay on the server; audio is streamed to the browser over the circuit.
/// </summary>
public class TtsService(IHttpClientFactory httpFactory, IOptions<TtsOptions> tts, IOptions<AiOptions> ai, ILogger<TtsService> logger)
{
    private readonly TtsOptions _opt = tts.Value;

    // Engines that answered "terms required" / 4xx are skipped for the rest of the process lifetime.
    private static readonly HashSet<string> Disabled = [];

    private static string Tone(string examinerId) => examinerId switch
    {
        "supervisor" => "warm but demanding, like a senior professor who knows the student's work",
        "methodologist" => "precise and measured, like a careful research methods expert",
        _ => "cool and skeptical, like an external examiner probing for weaknesses",
    };

    public async Task<SpeechAudio?> SynthesizeAsync(string text, string examinerId, string lang, CancellationToken ct = default)
    {
        if (!_opt.Enabled || string.IsNullOrWhiteSpace(text)) return null;

        if (lang == "en" && !Disabled.Contains("groq") && Key("groq") is { } groqKey)
        {
            var a = await Try("groq", () => Groq(groqKey, text, examinerId, ct), _opt.GroqTimeoutSeconds, ct);
            if (a is not null) return a;
        }
        if (!Disabled.Contains("gemini") && (Key("gemini") ?? Key("gemini-backup")) is { } geminiKey)
        {
            var a = await Try("gemini", () => Gemini(geminiKey, text, examinerId, lang, ct), _opt.GeminiTimeoutSeconds, ct);
            if (a is not null) return a;
        }
        return null;
    }

    private string? Key(string providerName) =>
        ai.Value.Providers.FirstOrDefault(p => p.Name == providerName && !string.IsNullOrWhiteSpace(p.ApiKey))?.ApiKey;

    private async Task<SpeechAudio?> Try(string engine, Func<Task<SpeechAudio?>> run, int timeoutSeconds, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var task = run();
            var done = await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(timeoutSeconds), ct));
            if (done != task)
            {
                logger.LogWarning("[TTS] {Engine} timed out after {S}s", engine, timeoutSeconds);
                return null;
            }
            var audio = await task;
            if (audio is not null) logger.LogInformation("[TTS] {Engine} spoke in {Ms} ms ({Kb} KB)", engine, sw.ElapsedMilliseconds, audio.Bytes.Length / 1024);
            return audio;
        }
        catch (Exception ex)
        {
            logger.LogWarning("[TTS] {Engine} failed: {Err}", engine, ex.Message);
            return null;
        }
    }

    private async Task<SpeechAudio?> Groq(string key, string text, string examinerId, CancellationToken ct)
    {
        var body = JsonSerializer.Serialize(new
        {
            model = _opt.GroqModel,
            voice = _opt.GroqVoices.GetValueOrDefault(examinerId, "troy"),
            input = text,
            response_format = "wav",
        });
        using var req = new HttpRequestMessage(HttpMethod.Post, "https://api.groq.com/openai/v1/audio/speech")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        using var res = await httpFactory.CreateClient("llm").SendAsync(req, ct);
        if (!res.IsSuccessStatusCode)
        {
            var err = await res.Content.ReadAsStringAsync(ct);
            if ((int)res.StatusCode is 401 or 403 or 404 || err.Contains("terms")) Disabled.Add("groq");
            throw new HttpRequestException($"HTTP {(int)res.StatusCode}: {err[..Math.Min(160, err.Length)]}");
        }
        return new SpeechAudio(await res.Content.ReadAsByteArrayAsync(ct), "audio/wav", "Groq Orpheus");
    }

    private async Task<SpeechAudio?> Gemini(string key, string text, string examinerId, string lang, CancellationToken ct)
    {
        var language = Prompts.LanguageName(lang);
        var prompt = $"Read this aloud in {language}, {Tone(examinerId)}. Speak naturally, at a conversational pace:\n{text}";
        var body = JsonSerializer.Serialize(new
        {
            contents = new[] { new { parts = new[] { new { text = prompt } } } },
            generationConfig = new
            {
                responseModalities = new[] { "AUDIO" },
                speechConfig = new { voiceConfig = new { prebuiltVoiceConfig = new { voiceName = _opt.GeminiVoices.GetValueOrDefault(examinerId, "Charon") } } },
            },
        });
        using var req = new HttpRequestMessage(HttpMethod.Post,
            $"https://generativelanguage.googleapis.com/v1beta/models/{_opt.GeminiModel}:generateContent")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        req.Headers.Add("x-goog-api-key", key);
        using var res = await httpFactory.CreateClient("llm").SendAsync(req, ct);
        var json = await res.Content.ReadAsStringAsync(ct);
        if (!res.IsSuccessStatusCode)
        {
            if ((int)res.StatusCode is 401 or 403 or 404) Disabled.Add("gemini");
            throw new HttpRequestException($"HTTP {(int)res.StatusCode}");
        }
        var inline = JsonNode.Parse(json)?["candidates"]?[0]?["content"]?["parts"]?[0]?["inlineData"];
        var data = inline?["data"]?.GetValue<string>();
        var mime = inline?["mimeType"]?.GetValue<string>() ?? "";
        if (string.IsNullOrEmpty(data)) return null;
        var bytes = Convert.FromBase64String(data);
        if (!mime.Contains("wav", StringComparison.OrdinalIgnoreCase)) bytes = WrapPcm(bytes, RateFrom(mime));
        return new SpeechAudio(bytes, "audio/wav", "Gemini TTS");
    }

    private static int RateFrom(string mime)
    {
        var i = mime.IndexOf("rate=", StringComparison.OrdinalIgnoreCase);
        if (i < 0) return 24000;
        var digits = new string(mime[(i + 5)..].TakeWhile(char.IsDigit).ToArray());
        return int.TryParse(digits, out var r) ? r : 24000;
    }

    /// <summary>Wraps raw 16-bit mono little-endian PCM in a WAV header.</summary>
    public static byte[] WrapPcm(byte[] pcm, int sampleRate)
    {
        using var ms = new MemoryStream(44 + pcm.Length);
        using var w = new BinaryWriter(ms);
        w.Write("RIFF"u8); w.Write(36 + pcm.Length); w.Write("WAVE"u8);
        w.Write("fmt "u8); w.Write(16); w.Write((short)1); w.Write((short)1);
        w.Write(sampleRate); w.Write(sampleRate * 2); w.Write((short)2); w.Write((short)16);
        w.Write("data"u8); w.Write(pcm.Length); w.Write(pcm);
        return ms.ToArray();
    }
}
