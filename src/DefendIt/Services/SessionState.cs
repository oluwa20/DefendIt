using DefendIt.Models;

namespace DefendIt.Services;

/// <summary>Per-circuit (per browser tab) in-memory state for the current defense. Nothing is persisted server-side.</summary>
public class SessionState
{
    public DefenseSession? Current { get; set; }
    public bool Truncated { get; set; }
    public int PageCount { get; set; }
    public string LastProvider { get; set; } = "";
    public bool LastWasFallback { get; set; }
    public HashSet<int> TypedTurns { get; } = [];

    public void Track<T>(LlmResult<T> r)
    {
        LastProvider = r.Provider;
        LastWasFallback = r.IsFallback;
    }

    public void Start(DefenseSession s, ExtractedDocument doc)
    {
        Current = s;
        Truncated = doc.Truncated;
        PageCount = doc.PageCount;
        TypedTurns.Clear();
    }
}
