using System.Text;
using System.Text.RegularExpressions;
using DefendIt.Models;
using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;

namespace DefendIt.Services;

public class NoReadableTextException() : Exception("This PDF has no readable text — upload a text-based PDF.");

public partial class PdfTextService
{
    public const int MaxChars = 60_000;

    private static readonly string[] PriorityKeywords =
    [
        "abstract", "résumé", "introduction", "method", "méthod", "approach", "results", "résultats",
        "evaluation", "évaluation", "discussion", "conclusion", "table", "tableau", "limitation",
    ];

    public ExtractedDocument Extract(Stream pdf)
    {
        List<string> pages;
        try
        {
            using var doc = PdfDocument.Open(pdf);
            pages = doc.GetPages().Select(p => Clean(ContentOrderTextExtractor.GetText(p))).ToList();
        }
        catch (Exception ex) when (ex is not NoReadableTextException)
        {
            throw new InvalidDataException("This file could not be read as a PDF.", ex);
        }

        var totalText = pages.Sum(p => p.Length);
        if (totalText < 200) throw new NoReadableTextException();

        return Build(pages);
    }

    /// <summary>Page-tags the text and, if it is too long, keeps the pages an examiner cares about most.</summary>
    public static ExtractedDocument Build(IReadOnlyList<string> pages)
    {
        var tagged = pages.Select((t, i) => $"[p.{i + 1}] {t}").ToList();
        var full = string.Join("\n\n", tagged);
        if (full.Length <= MaxChars) return new ExtractedDocument(full, pages.Count, false);

        // Rank pages: the first pages (abstract/intro), the last pages (conclusion), then keyword-heavy pages.
        var score = new double[pages.Count];
        for (var i = 0; i < pages.Count; i++)
        {
            var lower = pages[i].ToLowerInvariant();
            if (i < 4) score[i] += 100 - i;
            if (i >= pages.Count - 3) score[i] += 50;
            foreach (var k in PriorityKeywords)
                if (lower.Contains(k)) score[i] += 5;
            // Drop reference lists and appendices first.
            if (lower.Contains("references") || lower.Contains("bibliograph") || lower.Contains("appendix")) score[i] -= 30;
        }

        var keep = new SortedSet<int>();
        var used = 0;
        foreach (var i in Enumerable.Range(0, pages.Count).OrderByDescending(i => score[i]))
        {
            var len = tagged[i].Length + 2;
            if (used + len > MaxChars) continue;
            keep.Add(i);
            used += len;
        }

        var sb = new StringBuilder();
        foreach (var i in keep) sb.Append(tagged[i]).Append("\n\n");
        return new ExtractedDocument(sb.ToString(), pages.Count, true);
    }

    private static string Clean(string s) => Whitespace().Replace(s, " ").Trim();

    [GeneratedRegex(@"[ \t\r\f\v]+")]
    private static partial Regex Whitespace();
}
