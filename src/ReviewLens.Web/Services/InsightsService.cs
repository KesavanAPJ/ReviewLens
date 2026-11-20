using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.Options;
using ReviewLens.Web.Models;

namespace ReviewLens.Web.Services;

/// <summary>
/// The "reduce" step. Aggregate() does ALL the numbers with LINQ; the report agent only
/// picks themes and writes action items. FormatReport() puts the C# numbers into the text.
/// </summary>
public class InsightsService
{
    private const string Instructions = """
        You write a short weekly customer-feedback report for a restaurant manager.
        You receive pre-computed statistics and a list of one-line review summaries (already in English),
        each with its review id, sentiment, category, urgency and platform.

        Rules:
        - top_complaints: the 3 most common complaint themes (from negative/neutral reviews), most frequent first.
        - top_praises: the 1-3 most common praise themes (from positive reviews), most frequent first.
        - For every theme, list the review_ids of the summaries that belong to it. Use only ids from the input.
        - Themes should be specific (e.g. "Late delivery", "Extra charges on bill"), not just the category name.
        - note: a short pattern you notice (platform, time, language). Do NOT write numbers in notes.
        - action_items: exactly 3 short, concrete actions. If you mention a number, use ONLY numbers given in
          the statistics (for example the urgent count). Never invent or estimate numbers.
        """;

    private readonly AgentFactory _factory;
    private readonly ReviewLensOptions _options;
    private AIAgent? _agent;   // created on first use so the app still starts without a key

    public InsightsService(AgentFactory factory, IOptions<ReviewLensOptions> options)
    {
        _factory = factory;
        _options = options.Value;
    }

    // ---------------------------------------------------------------- numbers (LINQ only)

    public static InsightsSummary Aggregate(IReadOnlyList<EnrichedReview> rows)
    {
        var ok = rows.Where(r => r.IsOk).Select(r => r.Analysis!).ToList();
        int analysed = ok.Count;

        // Every allowed value is listed (even with 0) so charts always have the same bars in the same order.
        List<CountItem> CountAll<T>(Func<ReviewAnalysis, T> key, Func<T, string> label) where T : struct, Enum =>
            Enum.GetValues<T>()
                .Select(v => new { v, n = ok.Count(a => key(a).Equals(v)) })
                .Select(x => new CountItem(label(x.v), x.n, Percent(x.n, analysed)))
                .ToList();

        var dates = rows
            .Select(r => DateOnly.TryParse(r.Review.Date, CultureInfo.InvariantCulture, out var d) ? d : (DateOnly?)null)
            .Where(d => d is not null)
            .Select(d => d!.Value)
            .ToList();

        return new InsightsSummary
        {
            TotalReviews = rows.Count,
            AnalysedCount = analysed,
            NeedsReviewCount = rows.Count(r => r.Status == ReviewStatus.NeedsReview),
            NotProcessedCount = rows.Count(r => r.Status == ReviewStatus.NotProcessed),
            UrgentCount = ok.Count(a => a.Urgency == Urgency.High),
            DateFrom = dates.Count > 0 ? dates.Min().ToString("d MMM yyyy", CultureInfo.InvariantCulture) : null,
            DateTo = dates.Count > 0 ? dates.Max().ToString("d MMM yyyy", CultureInfo.InvariantCulture) : null,

            Sentiment = CountAll(a => a.Sentiment, v => v.ToText()),
            Category = CountAll(a => a.Category, v => v.ToText())
                .OrderByDescending(c => c.Count).ToList(),
            Urgency = CountAll(a => a.Urgency, v => v.ToText())
                .OrderByDescending(c => c.Label == "high").ThenByDescending(c => c.Label == "medium").ToList(),
            Language = CountAll(a => a.Language, v => v.ToText()),

            // Platform comes from the input, so it is counted over ALL rows.
            Platform = rows
                .GroupBy(r => string.IsNullOrWhiteSpace(r.Review.Platform) ? "(none)" : r.Review.Platform.Trim())
                .Select(g => new CountItem(g.Key, g.Count(), Percent(g.Count(), rows.Count)))
                .OrderByDescending(c => c.Count).ThenBy(c => c.Label)
                .ToList(),
        };
    }

    /// <summary>One decimal, .5 rounds up (so 12.25 -> 12.3, like people expect).</summary>
    public static double Percent(int count, int total) =>
        total == 0 ? 0 : Math.Round(100.0 * count / total, 1, MidpointRounding.AwayFromZero);

    /// <summary>Counts each theme's DISTINCT ids that really exist among the analysed rows.</summary>
    public static List<ThemeLine> CountThemes(IEnumerable<ReportTheme> themes, IReadOnlyList<EnrichedReview> rows)
    {
        var validIds = rows.Where(r => r.IsOk).Select(r => r.Review.ReviewId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return themes
            .Where(t => !string.IsNullOrWhiteSpace(t.Theme))
            .Select(t => new ThemeLine(
                t.Theme.Trim(),
                t.ReviewIds.Select(id => id.Trim()).Where(validIds.Contains).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
                t.Note.Trim()))
            .Where(t => t.Count > 0)                 // drop themes the model could not back with real reviews
            .OrderByDescending(t => t.Count)
            .ToList();
    }

    // ---------------------------------------------------------------- report agent

    /// <summary>Sends the computed numbers + one-line summaries (not the full reviews) to the report agent.</summary>
    public async Task<WeeklyReport> GenerateReportAsync(
        InsightsSummary summary, IReadOnlyList<EnrichedReview> rows, CancellationToken ct = default)
    {
        if (summary.AnalysedCount == 0)
            return new WeeklyReport();   // nothing to summarise; don't spend an API call

        _agent ??=_factory.CreateAgent("WeeklyReportWriter", Instructions);

        var input = new
        {
            statistics = new
            {
                total_reviews = summary.TotalReviews,
                analysed_reviews = summary.AnalysedCount,
                urgent_high_count = summary.UrgentCount,
                sentiment = summary.Sentiment.Select(c => new { c.Label, c.Count, percent = c.Percent }),
                category = summary.Category.Select(c => new { c.Label, c.Count, percent = c.Percent }),
                platform = summary.Platform.Select(c => new { c.Label, c.Count, percent = c.Percent }),
            },
            summaries = rows.Where(r => r.IsOk).Select(r => new
            {
                id = r.Review.ReviewId,
                sentiment = r.Analysis!.Sentiment.ToText(),
                category = r.Analysis.Category.ToText(),
                urgency = r.Analysis.Urgency.ToText(),
                platform = r.Review.Platform,
                summary = r.Analysis.Summary,
            }),
        };

        var prompt = "Write the weekly report for this data:\n" + JsonSerializer.Serialize(input);

        // Same retry idea as the per-review step: retry a bad answer, give up on fatal errors.
        Exception? last = null;
        for (int attempt = 0; attempt <= _options.MaxRetries; attempt++)
        {
            try
            {
                var report = await _factory.RunStructuredAsync<WeeklyReport>(_agent, prompt, ct);
                if (report is not null && report.ActionItems.Count > 0) return report;
                last = new InvalidOperationException("Report had no action items.");
            }
            catch (GeminiFatalException) { throw; }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) { last = ex; }
        }
        throw new InvalidOperationException("The report agent did not return a valid report: " + last?.Message, last);
    }

    // ---------------------------------------------------------------- text (numbers from C#)

    /// <summary>
    /// Builds the report text in the "Output B" layout. Every number here comes from
    /// <paramref name="s"/> or from counting ids in C#; the LLM only supplied words.
    /// </summary>
    public static string FormatReport(InsightsSummary s, WeeklyReport report, IReadOnlyList<EnrichedReview> rows)
    {
        var sb = new StringBuilder();
        string range = s.DateFrom is null ? "" : s.DateFrom == s.DateTo ? $" | {s.DateFrom}" : $" | {s.DateFrom} to {s.DateTo}";
        sb.AppendLine($"WEEKLY FEEDBACK REPORT{range}");

        string Pct(string label) => $"{s.Sentiment.First(c => c.Label == label).Percent:0.#}%";
        sb.AppendLine($"Total reviews: {s.TotalReviews}   Positive {Pct("positive")}  Neutral {Pct("neutral")}  Negative {Pct("negative")}");

        if (s.AnalysedCount != s.TotalReviews)
        {
            sb.AppendLine($"(Percentages are of the {s.AnalysedCount} analysed reviews; " +
                          $"needs_review: {s.NeedsReviewCount}, not_processed: {s.NotProcessedCount})");
        }
        if (s.NotProcessedCount > 0)
            sb.AppendLine("NOTE: PARTIAL REPORT - the batch stopped early (see the message at the top of the page).");

        sb.AppendLine();
        sb.AppendLine("TOP 3 COMPLAINTS");
        var complaints = CountThemes(report.TopComplaints, rows).Take(3).ToList();
        if (complaints.Count == 0) sb.AppendLine("(none)");
        for (int i = 0; i < complaints.Count; i++)
        {
            var c = complaints[i];
            var name = $"{i + 1}. {c.Theme} ";
            var dots = new string('.', Math.Max(3, 30 - name.Length));
            var note = string.IsNullOrEmpty(c.Note) ? "" : $" ({c.Note})";
            sb.AppendLine($"{name}{dots} {c.Count} review{(c.Count == 1 ? "" : "s")}{note}");
        }

        sb.AppendLine();
        sb.AppendLine("TOP PRAISE");
        var praises = CountThemes(report.TopPraises, rows).Take(3).ToList();
        sb.AppendLine(praises.Count == 0
            ? "(none)"
            : string.Join(", ", praises.Select(p => $"{p.Theme} ({p.Count} mention{(p.Count == 1 ? "" : "s")})")));

        sb.AppendLine();
        sb.AppendLine($"URGENT: {s.UrgentCount} review{(s.UrgentCount == 1 ? "" : "s")} need a reply within 24 hours");

        sb.AppendLine();
        sb.AppendLine("ACTION ITEMS");
        var actions = report.ActionItems.Where(a => !string.IsNullOrWhiteSpace(a)).Take(3).ToList();
        if (actions.Count == 0) sb.AppendLine("(none)");
        for (int i = 0; i < actions.Count; i++)
            sb.AppendLine($"{i + 1}. {actions[i].Trim()}");

        return sb.ToString();
    }
}
