namespace DefendIt.Models;

public record DocumentBrief(
    string Title, string Field, string Summary,
    List<string> ResearchQuestions, string Methodology,
    List<KeyClaim> KeyClaims,
    List<Inconsistency> Inconsistencies,
    List<string> WeakSpots);

public record KeyClaim(string Claim, string Location);

public record Inconsistency(string Description, string LocationA, string LocationB, string Severity);

public record Examiner(string Id, string Name, string Role, string Style, float VoicePitch, float VoiceRate, string Color, string Initials);

public record Turn(string ExaminerId, string Question, string TargetArea, bool IsFollowUp,
    string Answer, double AnswerSeconds);

public record DefenseSession(string Id, DateTime CreatedUtc, string Language, string Difficulty,
    DocumentBrief Brief, List<Turn> Turns, DefenseReport? Report);

public record DefenseReport(int ReadinessScore, string Verdict,
    Dictionary<string, int> Dimensions,
    List<QuestionFeedback> Feedback,
    List<string> TopRisks, List<string> PrepPlan,
    DeliveryStats Delivery, string ProviderUsed);

public record QuestionFeedback(string Question, int Score, string WhatWorked, string WhatWasMissing, string StrongerAnswer);

public record DeliveryStats(double WordsPerMinute, int FillerWords, double AvgAnswerSeconds);

public record NextQuestion(string ExaminerId, string Question, string TargetArea, bool IsFollowUp, string? Reasoning);

public record ExtractedDocument(string Text, int PageCount, bool Truncated);

public static class Dimensions
{
    public static readonly string[] Keys =
        ["Problem & motivation", "Methodology", "Results & evidence", "Originality", "Clarity", "Composure"];

    public static readonly Dictionary<string, string> French = new()
    {
        ["Problem & motivation"] = "Problème & motivation",
        ["Methodology"] = "Méthodologie",
        ["Results & evidence"] = "Résultats & preuves",
        ["Originality"] = "Originalité",
        ["Clarity"] = "Clarté",
        ["Composure"] = "Aisance",
    };

    public static readonly Dictionary<string, string> Arabic = new()
    {
        ["Problem & motivation"] = "المشكلة والدافع",
        ["Methodology"] = "المنهجية",
        ["Results & evidence"] = "النتائج والأدلة",
        ["Originality"] = "الأصالة",
        ["Clarity"] = "الوضوح",
        ["Composure"] = "الثبات",
    };
}

public static class Panel
{
    public static readonly List<Examiner> Examiners =
    [
        new("supervisor", "Prof. Supervisor", "The Supervisor",
            "Knows the work well, fair but demanding; asks about motivation, objectives and contribution.",
            1.0f, 1.0f, "#6b2737", "SU"),
        new("methodologist", "Dr. Methodologist", "The Methodologist",
            "Attacks research design, data, sample size, validity and evaluation metrics.",
            0.8f, 0.95f, "#2f3e6b", "ME"),
        new("external", "Prof. External", "The External Examiner",
            "Skeptical outsider; asks what is actually new, limitations, real-world value, and raises inconsistencies found in the document.",
            1.2f, 1.05f, "#8a5a1f", "EX"),
    ];

    public static Examiner Get(string id) =>
        Examiners.FirstOrDefault(e => e.Id == id) ?? Examiners[0];

    public const int TotalQuestions = 7;
}
