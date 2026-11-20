using Microsoft.Agents.AI;
using Microsoft.Extensions.Options;
using ReviewLens.Web.Models;

namespace ReviewLens.Web.Services;

/// <summary>
/// Outcome of a whole batch. Stopped = a fatal error (quota or configuration) ended the batch early
/// and the UI must say so; QuotaExhausted tells which kind it was.
/// </summary>
public record AnalysisBatchResult(List<EnrichedReview> Rows, bool Stopped, bool QuotaExhausted, int ProcessedCount, string? Message);

/// <summary>The "map" step: one agent tags each review with sentiment, category, urgency, etc.</summary>
public class ReviewAnalyzer
{
    private const string Instructions = """
        You analyse customer reviews for a restaurant / online food business in Tamil Nadu.
        Reviews may be in English, Tamil (Tamil script) or Tanglish (Tamil written in English letters, often mixed with English).

        Return ONLY the JSON object described by the schema, using exactly these values:
        - sentiment: positive | neutral | negative
        - category (pick the ONE main topic): food_quality | delivery | price | staff | packaging | app
        - urgency: high | medium | low
            high   = health issue (food poisoning, hair/insect in food, allergy), refund request,
                     abuse or rude/threatening staff, or wrong order delivered
            medium = a clear complaint that needs follow-up (late, cold, overcharged, damaged, app bug)
            low    = praise, neutral remarks, small suggestions
        - language: English | Tamil | Tanglish
        - summary: exactly ONE sentence in ENGLISH, even when the review is Tamil or Tanglish. Translate the meaning; do not copy Tamil words.
        - suggested_reply: a polite, warm, ready-to-post reply in ENGLISH, 1-3 sentences. Apologise for complaints and
          invite the customer to share their order ID; thank them for praise. Never promise specific compensation amounts.
        """;

    private readonly AgentFactory _factory;
    private readonly ReviewLensOptions _options;
    private readonly ILogger<ReviewAnalyzer> _logger;
    private AIAgent? _agent;   // created on first use so the app still starts without a key

    public ReviewAnalyzer(AgentFactory factory, IOptions<ReviewLensOptions> options, ILogger<ReviewAnalyzer> logger)
    {
        _factory = factory;
        _options = options.Value;
        _logger = logger;
    }

    private AIAgent Agent => _agent ??= _factory.CreateAgent("ReviewAnalyzer", Instructions);

    /// <summary>
    /// Analyses all reviews in parallel (MaxDegreeOfParallelism) under the requests-per-minute limit.
    /// One bad review never stops the batch; only an exhausted quota does, and then the result says so.
    /// </summary>
    public async Task<AnalysisBatchResult> AnalyzeAllAsync(
        IReadOnlyList<Review> reviews, IProgress<int>? progress = null, CancellationToken ct = default)
    {
        var results = new EnrichedReview?[reviews.Count];   // by index, so output order = input order
        int done = 0;
        GeminiFatalException? fatal = null;

        // Linked token: lets us stop the remaining work when the quota runs out (or the key/model is wrong).
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var parallel = new ParallelOptions
        {
            MaxDegreeOfParallelism = Math.Max(1, _options.MaxDegreeOfParallelism),
            CancellationToken = stop.Token,
        };

        try
        {
            await Parallel.ForEachAsync(Enumerable.Range(0, reviews.Count), parallel, async (i, token) =>
            {
                try
                {
                    results[i] = await AnalyzeOneAsync(reviews[i], token);
                    progress?.Report(Interlocked.Increment(ref done));
                }
                catch (GeminiFatalException ex)
                {
                    Interlocked.CompareExchange(ref fatal, ex, null);
                    stop.Cancel();
                }
                catch (OperationCanceledException) when (stop.IsCancellationRequested && !ct.IsCancellationRequested)
                {
                    // Cancelled because another review hit a fatal error: leave this row as not_processed.
                }
            });
        }
        catch (OperationCanceledException) when (fatal is not null && !ct.IsCancellationRequested)
        {
            // Expected after stop.Cancel(); we report it below instead of throwing.
        }

        bool quota = fatal is QuotaExhaustedException;
        var rows = results
            .Select((r, i) => r ?? new EnrichedReview
            {
                Review = reviews[i],
                Status = ReviewStatus.NotProcessed,
                Error = "Not analysed: the batch stopped early. " + fatal?.Message,
            })
            .ToList();

        int processed = rows.Count(r => r.Status != ReviewStatus.NotProcessed);
        string? message = fatal is null
            ? null
            : $"{fatal.Message} Analysed {processed} of {reviews.Count} reviews. " +
              $"The remaining {reviews.Count - processed} are marked not_processed. " +
              (quota ? "Try again after the quota resets, or use another API key." : "Fix the setting and run again.");

        return new AnalysisBatchResult(rows, fatal is not null, quota, processed, message);
    }

    /// <summary>
    /// Analyses one review: 1 try + MaxRetries more if the answer is invalid.
    /// Never throws for a bad answer (returns needs_review); only throws for fatal errors or cancellation.
    /// </summary>
    public async Task<EnrichedReview> AnalyzeOneAsync(Review review, CancellationToken ct = default)
    {
        string prompt = $"""
            Platform: {review.Platform}
            Star rating: {review.Rating}
            Review text:
            {review.ReviewText}
            """;

        string lastError = "";
        for (int attempt = 0; attempt <= _options.MaxRetries; attempt++)
        {
            try
            {
                var analysis = await _factory.RunStructuredAsync<ReviewAnalysis>(Agent, prompt, ct);

                var problem = Validate(analysis);
                if (problem is null)
                    return new EnrichedReview { Review = review, Analysis = analysis, Status = ReviewStatus.Ok };

                lastError = problem;
            }
            catch (GeminiFatalException) { throw; }                                      // stop the batch
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                // Bad JSON, value outside an enum, timeout, 5xx... -> count as a failed attempt.
                lastError = $"{ex.GetType().Name}: {ex.Message}";
            }

            _logger.LogWarning("Review {Id}: attempt {Attempt} failed: {Error}", review.ReviewId, attempt + 1, lastError);
        }

        return new EnrichedReview { Review = review, Status = ReviewStatus.NeedsReview, Error = lastError };
    }

    /// <summary>Checks things the JSON schema alone cannot guarantee. Returns null when valid.</summary>
    public static string? Validate(ReviewAnalysis? a)
    {
        if (a is null) return "Empty response.";
        if (!Enum.IsDefined(a.Sentiment)) return $"Invalid sentiment '{a.Sentiment}'.";
        if (!Enum.IsDefined(a.Category)) return $"Invalid category '{a.Category}'.";
        if (!Enum.IsDefined(a.Urgency)) return $"Invalid urgency '{a.Urgency}'.";
        if (!Enum.IsDefined(a.Language)) return $"Invalid language '{a.Language}'.";
        if (string.IsNullOrWhiteSpace(a.Summary)) return "Summary is empty.";
        if (string.IsNullOrWhiteSpace(a.SuggestedReply)) return "Suggested reply is empty.";
        // Summary and reply must be English: reject any Tamil-script characters (U+0B80..U+0BFF).
        if (ContainsTamilScript(a.Summary)) return "Summary is not in English.";
        if (ContainsTamilScript(a.SuggestedReply)) return "Suggested reply is not in English.";
        return null;
    }

    private static bool ContainsTamilScript(string s) => s.Any(c => c is >= '஀' and <= '௿');
}
