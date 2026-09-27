using DefendIt.Models;
using Microsoft.JSInterop;

namespace DefendIt.Services;

/// <summary>Makes an examiner speak: neural TTS when available, the browser's voice otherwise.</summary>
public class VoiceService(TtsService tts, IJSRuntime js)
{
    public string LastEngine { get; private set; } = "";

    /// <param name="levelTarget">DOM id of the element whose --level CSS variable follows the voice volume.</param>
    public async Task SpeakAsync(Examiner ex, string text, string lang, string levelTarget, CancellationToken ct = default)
    {
        var audio = await tts.SynthesizeAsync(text, ex.Id, lang, ct);
        if (audio is not null)
        {
            LastEngine = audio.Engine;
            var ok = await js.InvokeAsync<bool>("defendit.playAudio", ct, audio.Bytes, audio.Mime, levelTarget);
            if (ok) return;
        }
        LastEngine = "Browser voice";
        await js.InvokeAsync<bool>("defendit.speak", ct, text, L.SpeechLang(lang), ex.VoicePitch, ex.VoiceRate,
            Panel.Examiners.IndexOf(ex), levelTarget);
    }

    public ValueTask StopAsync() => js.InvokeVoidAsync("defendit.stopSpeaking");
}
