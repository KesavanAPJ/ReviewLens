using System.Diagnostics;
using ReviewLens.Web.Models;
using ReviewLens.Web.Services;

namespace ReviewLens.Tests;

public class RateLimitAndRetryTests
{
    [Fact]
    public async Task Limiter_NeverAllowsMoreThanLimitPerWindow()
    {
        // Same logic as 8/minute, scaled down: 3 requests per 400 ms window.
        var window = TimeSpan.FromMilliseconds(400);
        var limiter = new RequestRateLimiter(3, window);
        var starts = new List<TimeSpan>();
        var sw = Stopwatch.StartNew();

        // 7 callers at once, like Parallel.ForEachAsync would do.
        await Task.WhenAll(Enumerable.Range(0, 7).Select(async _ =>
        {
            await limiter.WaitAsync();
            lock (starts) starts.Add(sw.Elapsed);
        }));

        starts.Sort();
        Assert.Equal(7, starts.Count);
        // For every request, the one 3 places later must start at least a full window afterwards.
        for (int i = 0; i + 3 < starts.Count; i++)
            Assert.True(starts[i + 3] - starts[i] >= window - TimeSpan.FromMilliseconds(20),
                $"Requests {i} and {i + 3} were only {(starts[i + 3] - starts[i]).TotalMilliseconds} ms apart");
    }

    [Fact]
    public async Task Limiter_UnderTheLimit_DoesNotWait()
    {
        var limiter = new RequestRateLimiter(8);
        var sw = Stopwatch.StartNew();
        for (int i = 0; i < 8; i++) await limiter.WaitAsync();
        Assert.True(sw.ElapsedMilliseconds < 200);
    }

    [Fact]
    public void Limiter_RejectsZero() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new RequestRateLimiter(0));

    [Theory]
    [InlineData("\"quotaId\": \"GenerateRequestsPerDayPerProjectPerModel-FreeTier\"", true)]
    [InlineData("You exceeded the requests per day limit", true)]
    [InlineData("\"quotaId\": \"GenerateRequestsPerMinutePerProjectPerModel-FreeTier\"", false)]
    public void DailyQuota_IsDetectedFromBody(string body, bool expected) =>
        Assert.Equal(expected, AgentFactory.IsDailyQuota(body));

    [Fact]
    public void RetryDelay_IsParsedFromBody()
    {
        Assert.Equal(TimeSpan.FromSeconds(37), AgentFactory.ParseRetryDelay("\"retryDelay\": \"37s\""));
        Assert.Equal(TimeSpan.Zero, AgentFactory.ParseRetryDelay("no hint"));
    }

    [Fact]
    public void Validate_AcceptsGoodAnalysis() => Assert.Null(ReviewAnalyzer.Validate(Good()));

    [Fact]
    public void Validate_RejectsTamilSummary()
    {
        var a = Good();
        a.Summary = "சாப்பாடு குளிர்ந்து போய் வந்தது.";
        Assert.NotNull(ReviewAnalyzer.Validate(a));
    }

    [Fact]
    public void Validate_RejectsEmptyReplyAndUndefinedEnum()
    {
        var a = Good();
        a.SuggestedReply = " ";
        Assert.NotNull(ReviewAnalyzer.Validate(a));

        var b = Good();
        b.Category = (Category)42;
        Assert.NotNull(ReviewAnalyzer.Validate(b));
    }

    private static ReviewAnalysis Good() => new()
    {
        Sentiment = Sentiment.Negative,
        Category = Category.Delivery,
        Urgency = Urgency.Medium,
        Language = ReviewLanguage.English,
        Summary = "Order arrived 70 minutes late and cold.",
        SuggestedReply = "We are sorry. Please DM us your order ID.",
    };
}
