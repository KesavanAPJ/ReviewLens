namespace ReviewLens.Web.Models;

/// <summary>Status values for an enriched row.</summary>
public static class ReviewStatus
{
    public const string Ok = "ok";
    /// <summary>The AI answer failed to parse / validate after all retries.</summary>
    public const string NeedsReview = "needs_review";
    /// <summary>Never sent to the AI because the daily quota ran out first.</summary>
    public const string NotProcessed = "not_processed";
}

/// <summary>An input review plus the AI analysis (null when not ok).</summary>
public class EnrichedReview
{
    public required Review Review { get; init; }
    public ReviewAnalysis? Analysis { get; init; }
    public string Status { get; init; } = ReviewStatus.Ok;
    public string? Error { get; init; }

    public bool IsOk => Status == ReviewStatus.Ok && Analysis is not null;
}
