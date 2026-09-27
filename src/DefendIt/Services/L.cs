namespace DefendIt.Services;

/// <summary>Tiny EN/FR string helper for the UI.</summary>
public static class L
{
    public static string T(string lang, string en, string fr) => lang == "fr" ? fr : en;
    public static string SpeechLang(string lang) => lang == "fr" ? "fr-FR" : "en-US";
}
