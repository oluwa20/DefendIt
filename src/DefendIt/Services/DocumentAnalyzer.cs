using DefendIt.Models;

namespace DefendIt.Services;

public class DocumentAnalyzer(LlmClient llm)
{
    private const string Schema = """
        {
          "title": "string",
          "field": "string (academic field)",
          "summary": "string (3-4 sentences)",
          "researchQuestions": ["string"],
          "methodology": "string (design, data, sample size, metrics)",
          "keyClaims": [ { "claim": "string, include exact numbers", "location": "e.g. p.3, Abstract" } ],
          "inconsistencies": [ { "description": "string, quote both conflicting statements with their numbers", "locationA": "p.X, section", "locationB": "p.Y, section", "severity": "high|medium|low" } ],
          "weakSpots": ["string: a gap an examiner would attack"]
        }
        """;

    public async Task<LlmResult<DocumentBrief>> AnalyzeAsync(ExtractedDocument doc, string lang, CancellationToken ct = default)
    {
        var system = Prompts.System(
            "You are the chair of a thesis defense jury preparing a briefing for the panel. You read the whole document carefully.",
            lang,
            $"""
            Produce a DocumentBrief as JSON with this schema:
            {Schema}

            Rules:
            - Page markers like [p.12] show where text appears. Cite them in every location field.
            - keyClaims: 6-10 of the most important claims, with exact numbers when the document gives them.
            - inconsistencies: actively hunt for contradictions between numbers, claims, tables and conclusions
              (e.g. abstract vs results table, claimed scope vs actual data, sample sizes that do not match,
              conclusions not supported by the data). Compare every number stated in the abstract, introduction and
              conclusion with the numbers in the results and tables. Only report real contradictions you can point to
              in the text. If none exist, return an empty list. Do not invent.
            - weakSpots: 3-6 gaps a demanding examiner would attack (validity, sample size, baselines, generalization, ethics).
            - Write the brief in {Prompts.LanguageName(lang)}, but keep quoted numbers exact.
            """);

        var user = $"DOCUMENT ({doc.PageCount} pages{(doc.Truncated ? ", key pages only" : "")}):\n<<<DOCUMENT\n{doc.Text}\nDOCUMENT>>>";
        var result = await llm.CompleteJsonAsync<DocumentBrief>("analyze", system, user, ct);
        return result with { Value = Normalize(result.Value) };
    }

    private static DocumentBrief Normalize(DocumentBrief b) => new(
        string.IsNullOrWhiteSpace(b.Title) ? "Untitled document" : b.Title,
        b.Field ?? "",
        b.Summary ?? "",
        b.ResearchQuestions ?? [],
        b.Methodology ?? "",
        b.KeyClaims ?? [],
        (b.Inconsistencies ?? []).Where(i => !string.IsNullOrWhiteSpace(i.Description)).ToList(),
        b.WeakSpots ?? []);
}
