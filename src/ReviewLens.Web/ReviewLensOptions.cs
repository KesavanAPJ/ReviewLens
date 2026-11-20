namespace ReviewLens.Web;

/// <summary>Settings from the "ReviewLens" section of appsettings.json.</summary>
public class ReviewLensOptions
{
    public const string Section = "ReviewLens";

    /// <summary>Gemini model name. GEMINI_MODEL in .env overrides it.</summary>
    public string Model { get; set; } = "gemini-3.6-flash";

    /// <summary>How many reviews are analysed at the same time (calls in flight).</summary>
    public int MaxDegreeOfParallelism { get; set; } = 5;

    /// <summary>Hard cap on Gemini requests in any 60-second window (free tier is strict).</summary>
    public int RequestsPerMinute { get; set; } = 8;

    /// <summary>Extra attempts when the answer fails to parse or has an invalid value.</summary>
    public int MaxRetries { get; set; } = 2;

    /// <summary>How many times one call is retried after HTTP 429 before we give up (quota exhausted).</summary>
    public int Max429Retries { get; set; } = 5;

    /// <summary>Timeout for a single HTTP call to Gemini.</summary>
    public int RequestTimeoutSeconds { get; set; } = 120;
}
