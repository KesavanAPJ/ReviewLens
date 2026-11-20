namespace ReviewLens.Web.Models;

/// <summary>One bar in a chart: label, count and percentage of the base total.</summary>
public record CountItem(string Label, int Count, double Percent);

/// <summary>All numbers for the dashboard and report. Calculated in C# (LINQ), never by the LLM.</summary>
public class InsightsSummary
{
    public int TotalReviews { get; init; }      // all rows after validation
    public int AnalysedCount { get; init; }     // status == ok (base for AI-field percentages)
    public int NeedsReviewCount { get; init; }
    public int NotProcessedCount { get; init; }
    public int UrgentCount { get; init; }       // ok rows with urgency == high

    public string? DateFrom { get; init; }
    public string? DateTo { get; init; }

    public List<CountItem> Sentiment { get; init; } = [];
    public List<CountItem> Category { get; init; } = [];
    public List<CountItem> Urgency { get; init; } = [];
    public List<CountItem> Platform { get; init; } = [];   // based on all rows
    public List<CountItem> Language { get; init; } = [];
}
