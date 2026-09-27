using System.Text.RegularExpressions;
using DefendIt.Models;

namespace DefendIt.Services;

public static partial class DeliveryMetrics
{
    // Multi-word fillers are matched as phrases; single words on word boundaries.
    private static readonly string[] En = ["um", "umm", "uh", "uhh", "erm", "like", "you know"];
    private static readonly string[] Fr = ["euh", "heu", "genre", "en fait", "du coup"];
    private static readonly string[] Arb = ["يعني", "امم", "اممم", "آه", "اه", "طيب", "ايه"];

    public static int CountFillers(string text, string lang)
    {
        if (string.IsNullOrWhiteSpace(text)) return 0;
        var lower = text.ToLowerInvariant();
        var list = lang switch { "fr" => Fr, "ar" => Arb, _ => En };
        return list.Sum(f => Regex.Matches(lower, $@"(?<![\p{{L}}']){Regex.Escape(f)}(?![\p{{L}}'])").Count);
    }

    public static int CountWords(string text) =>
        string.IsNullOrWhiteSpace(text) ? 0 : Words().Matches(text).Count;

    /// <summary>Only spoken answers count for pace; typed answers would distort words-per-minute.</summary>
    public static DeliveryStats Compute(IEnumerable<Turn> turns, string lang, ISet<int>? typedIndexes = null)
    {
        var list = turns.ToList();
        var spoken = list.Where((t, i) => typedIndexes is null || !typedIndexes.Contains(i)).ToList();
        var words = spoken.Sum(t => CountWords(t.Answer));
        var seconds = spoken.Sum(t => t.AnswerSeconds);
        var wpm = seconds > 1 ? Math.Round(words / (seconds / 60.0), 0) : 0;
        var fillers = list.Sum(t => CountFillers(t.Answer, lang));
        var avg = list.Count > 0 ? Math.Round(list.Average(t => t.AnswerSeconds), 1) : 0;
        return new DeliveryStats(wpm, fillers, avg);
    }

    [GeneratedRegex(@"[\p{L}\p{N}']+")]
    private static partial Regex Words();
}
