using System.Text;
using ReviewLens.Web.Models;
using ReviewLens.Web.Services;

namespace ReviewLens.Tests;

public class CsvServiceTests
{
    private readonly CsvService _csv = new();

    private static Stream ToStream(string text) => new MemoryStream(Encoding.UTF8.GetBytes(text));

    [Fact]
    public void Read_ValidFile_ReturnsAllRows()
    {
        var input = "review_id,date,platform,rating,review_text\n" +
                    "R-1,2026-10-03,Zomato,5,\"Great, tasty\"\n" +
                    "R-2,2026-10-03,Swiggy,1,Cold food\n";

        var result = _csv.Read(ToStream(input));

        Assert.True(result.IsValid);
        Assert.Equal(2, result.Rows.Count);
        Assert.Equal("Great, tasty", result.Rows[0].ReviewText);
        Assert.Equal(0, result.DroppedCount);
    }

    [Fact]
    public void Read_MissingColumn_ReturnsError()
    {
        var input = "review_id,date,platform,review_text\nR-1,2026-10-03,Zomato,ok\n";

        var result = _csv.Read(ToStream(input));

        Assert.False(result.IsValid);
        Assert.Contains("rating", result.Error);
    }

    [Fact]
    public void Read_HeaderCaseAndSpaces_AreAccepted()
    {
        var input = "Review_ID, Date ,PLATFORM,Rating,Review_Text\nR-1,2026-10-03,Zomato,5,Nice\n";

        var result = _csv.Read(ToStream(input));

        Assert.True(result.IsValid);
        Assert.Single(result.Rows);
    }

    [Fact]
    public void Read_EmptyReviewText_IsDroppedAndCounted()
    {
        var input = "review_id,date,platform,rating,review_text\n" +
                    "R-1,2026-10-03,Zomato,5,Good\n" +
                    "R-2,2026-10-03,Swiggy,1,\n" +
                    "R-3,2026-10-03,Swiggy,1,\"   \"\n";

        var result = _csv.Read(ToStream(input));

        Assert.Single(result.Rows);
        Assert.Equal(2, result.DroppedCount);
    }

    [Fact]
    public void Read_SampleFile_LoadsWithOneDroppedRow()
    {
        var path = Path.Combine(FindRepoRoot(), "sample-data", "reviews_week40.csv");
        using var fs = File.OpenRead(path);

        var result = _csv.Read(fs);

        Assert.True(result.IsValid);
        Assert.Equal(24, result.Rows.Count);
        Assert.Equal(1, result.DroppedCount);
    }

    [Fact]
    public void Read_LoadTestFile_Has220RowsWithTamil()
    {
        // Only parses the file - it does NOT call Gemini. The 220-row AI run is opt-in via the UI.
        var path = Path.Combine(FindRepoRoot(), "sample-data", "reviews_200.csv");
        using var fs = File.OpenRead(path);

        var result = _csv.Read(fs);

        Assert.True(result.IsValid);
        Assert.Equal(220, result.Rows.Count);
        Assert.Contains(result.Rows, r => r.ReviewText.Any(c => c is >= '஀' and <= '௿'));
    }

    [Fact]
    public void Export_StartsWithUtf8Bom()
    {
        var bytes = _csv.Export([Row("R-1", "Nice")]);

        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, bytes.Take(3).ToArray());
    }

    [Fact]
    public void Export_TamilText_RoundTripsAndHasAllColumns()
    {
        const string tamil = "பிரியாணி மிகவும் சுவையாக இருந்தது";
        var bytes = _csv.Export([Row("R-7", tamil)]);

        var text = new UTF8Encoding(true).GetString(bytes).TrimStart('﻿');
        var lines = text.Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal(string.Join(",", CsvService.ExportColumns), lines[0]);
        Assert.Contains(tamil, lines[1]);
        Assert.Contains("food_quality", lines[1]);
        Assert.EndsWith(",ok", lines[1]);
    }

    [Fact]
    public void Export_NeedsReviewRow_HasEmptyAiFields()
    {
        var row = new EnrichedReview
        {
            Review = new Review("R-9", "2026-10-03", "Google", "3", "hmm"),
            Status = ReviewStatus.NeedsReview,
        };

        var text = Encoding.UTF8.GetString(_csv.Export([row]));

        Assert.Contains("R-9,2026-10-03,Google,3,hmm,,,,,,,needs_review", text);
    }

    private static EnrichedReview Row(string id, string text) => new()
    {
        Review = new Review(id, "2026-10-05", "Google", "5", text),
        Analysis = new ReviewAnalysis
        {
            Sentiment = Sentiment.Positive,
            Category = Category.FoodQuality,
            Urgency = Urgency.Low,
            Language = ReviewLanguage.Tamil,
            Summary = "The biryani was very tasty.",
            SuggestedReply = "Thank you!",
        },
    };

    internal static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "ReviewLens.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Repo root not found");
    }
}
