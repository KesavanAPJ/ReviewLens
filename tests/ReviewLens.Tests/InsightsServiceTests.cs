using ReviewLens.Web.Models;
using ReviewLens.Web.Services;

namespace ReviewLens.Tests;

public class InsightsServiceTests
{
    // 8 rows: 6 ok, 1 needs_review, 1 not_processed.
    private static readonly List<EnrichedReview> Rows =
    [
        Ok("R-1", "Zomato", Sentiment.Positive, Category.FoodQuality, Urgency.Low, "2026-10-03"),
        Ok("R-2", "Swiggy", Sentiment.Negative, Category.Delivery, Urgency.Medium, "2026-10-03"),
        Ok("R-3", "Swiggy", Sentiment.Negative, Category.Delivery, Urgency.High, "2026-10-04"),
        Ok("R-4", "Google", Sentiment.Negative, Category.Price, Urgency.Medium, "2026-10-05"),
        Ok("R-5", "Zomato", Sentiment.Positive, Category.Packaging, Urgency.Low, "2026-10-06"),
        Ok("R-6", "Google", Sentiment.Neutral, Category.FoodQuality, Urgency.High, "2026-10-09"),
        Other("R-7", "Swiggy", ReviewStatus.NeedsReview),
        Other("R-8", "Amazon", ReviewStatus.NotProcessed),
    ];

    [Fact]
    public void Aggregate_CountsOnlyOkRowsForAiFields()
    {
        var s = InsightsService.Aggregate(Rows);

        Assert.Equal(8, s.TotalReviews);
        Assert.Equal(6, s.AnalysedCount);
        Assert.Equal(1, s.NeedsReviewCount);
        Assert.Equal(1, s.NotProcessedCount);
        Assert.Equal(2, s.UrgentCount);

        Assert.Equal(6, s.Sentiment.Sum(c => c.Count));
        Assert.Equal(3, s.Sentiment.Single(c => c.Label == "negative").Count);
        Assert.Equal(50.0, s.Sentiment.Single(c => c.Label == "negative").Percent);
        Assert.Equal(33.3, s.Sentiment.Single(c => c.Label == "positive").Percent);
        Assert.Equal(16.7, s.Sentiment.Single(c => c.Label == "neutral").Percent);
    }

    [Fact]
    public void Aggregate_ListsEveryAllowedValue_EvenWhenZero()
    {
        var s = InsightsService.Aggregate(Rows);

        Assert.Equal(6, s.Category.Count);
        Assert.Equal(0, s.Category.Single(c => c.Label == "app").Count);
        Assert.Equal(2, s.Category.Single(c => c.Label == "food_quality").Count);
        Assert.Equal(["high", "medium", "low"], s.Urgency.Select(u => u.Label));
    }

    [Fact]
    public void Aggregate_PlatformUsesAllRows()
    {
        var s = InsightsService.Aggregate(Rows);

        Assert.Equal(8, s.Platform.Sum(p => p.Count));
        Assert.Equal("Swiggy", s.Platform[0].Label);
        Assert.Equal(3, s.Platform[0].Count);
        Assert.Equal(37.5, s.Platform[0].Percent);
    }

    [Fact]
    public void Aggregate_DateRange()
    {
        var s = InsightsService.Aggregate(Rows);
        Assert.Equal("3 Oct 2026", s.DateFrom);
        Assert.Equal("9 Oct 2026", s.DateTo);
    }

    [Fact]
    public void Aggregate_EmptyInput_NoDivideByZero()
    {
        var s = InsightsService.Aggregate([]);
        Assert.Equal(0, s.TotalReviews);
        Assert.All(s.Sentiment, c => Assert.Equal(0, c.Percent));
    }

    [Theory]
    [InlineData(1, 8, 12.5)]
    [InlineData(1, 3, 33.3)]
    [InlineData(2, 3, 66.7)]
    [InlineData(0, 0, 0)]
    public void Percent_RoundsToOneDecimal(int count, int total, double expected) =>
        Assert.Equal(expected, InsightsService.Percent(count, total));

    [Fact]
    public void CountThemes_CountsDistinctValidIdsOnly()
    {
        var themes = new List<ReportTheme>
        {
            // duplicate id, wrong case, a made-up id and a needs_review id: only R-2 and R-3 count.
            new() { Theme = "Late delivery", ReviewIds = ["R-2", "r-3", "R-3", "R-999", "R-7"] },
            new() { Theme = "Ghost theme", ReviewIds = ["R-404"] },
        };

        var lines = InsightsService.CountThemes(themes, Rows);

        var line = Assert.Single(lines);
        Assert.Equal("Late delivery", line.Theme);
        Assert.Equal(2, line.Count);
    }

    [Fact]
    public void FormatReport_UsesCSharpNumbers_NotTheModels()
    {
        var s = InsightsService.Aggregate(Rows);
        var report = new WeeklyReport
        {
            TopComplaints = [new() { Theme = "Late delivery", ReviewIds = ["R-2", "R-3"], Note = "mostly Swiggy" }],
            TopPraises = [new() { Theme = "Biryani taste", ReviewIds = ["R-1"] }],
            ActionItems = ["Add a rider", "Show charges", "Reply to urgent reviews"],
        };

        var text = InsightsService.FormatReport(s, report, Rows);

        Assert.Contains("WEEKLY FEEDBACK REPORT | 3 Oct 2026 to 9 Oct 2026", text);
        Assert.Contains("Total reviews: 8   Positive 33.3%  Neutral 16.7%  Negative 50%", text);
        Assert.Contains("needs_review: 1, not_processed: 1", text);
        Assert.Contains("PARTIAL REPORT", text);
        Assert.Contains("2 reviews (mostly Swiggy)", text);
        Assert.Contains("Biryani taste (1 mention)", text);
        Assert.Contains("URGENT: 2 reviews need a reply within 24 hours", text);
        Assert.Contains("3. Reply to urgent reviews", text);
    }

    private static EnrichedReview Ok(string id, string platform, Sentiment s, Category c, Urgency u, string date) => new()
    {
        Review = new Review(id, date, platform, "3", "text"),
        Analysis = new ReviewAnalysis
        {
            Sentiment = s, Category = c, Urgency = u, Language = ReviewLanguage.English,
            Summary = "Summary.", SuggestedReply = "Reply.",
        },
    };

    private static EnrichedReview Other(string id, string platform, string status) => new()
    {
        Review = new Review(id, "2026-10-05", platform, "3", "text"),
        Status = status,
    };
}
