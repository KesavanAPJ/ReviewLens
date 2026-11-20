using System.ComponentModel;
using System.Text.Json.Serialization;

namespace ReviewLens.Web.Models;

/// <summary>What the report ("reduce") agent returns. Only text + themes; headline numbers come from C#.</summary>
public class WeeklyReport
{
    [JsonPropertyName("top_complaints")]
    [Description("The 3 most common complaint themes, most frequent first.")]
    public List<ReportTheme> TopComplaints { get; set; } = [];

    [JsonPropertyName("top_praises")]
    [Description("The 1 to 3 most common praise themes, most frequent first.")]
    public List<ReportTheme> TopPraises { get; set; } = [];

    [JsonPropertyName("action_items")]
    [Description("Exactly 3 short, concrete action items for the manager.")]
    public List<string> ActionItems { get; set; } = [];
}

public class ReportTheme
{
    [JsonPropertyName("theme")]
    [Description("Short name of the theme, e.g. 'Late delivery'.")]
    public string Theme { get; set; } = "";

    // The LLM lists WHICH reviews belong to the theme; C# counts them.
    // That way the "N reviews" number in the report is calculated, not guessed.
    [JsonPropertyName("review_ids")]
    [Description("The review ids (from the input list) that mention this theme.")]
    public List<string> ReviewIds { get; set; } = [];

    [JsonPropertyName("note")]
    [Description("Optional short detail, e.g. 'mostly Swiggy, evenings'. Must not contain numbers.")]
    public string Note { get; set; } = "";
}

/// <summary>A theme line ready to show: the count is computed in C# from valid, distinct review ids.</summary>
public record ThemeLine(string Theme, int Count, string Note);
