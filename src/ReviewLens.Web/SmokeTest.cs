using System.Text.Json;
using ReviewLens.Web.Models;
using ReviewLens.Web.Services;

namespace ReviewLens.Web;

/// <summary>
/// `dotnet run --project src/ReviewLens.Web -- --smoke`
/// Sends ONLY the 3 sample reviews from usecase.txt to Gemini, prints the JSON, then the weekly
/// report for those 3 rows (about 4-10 API calls in total).
/// </summary>
public static class SmokeTest
{
    private static readonly Review[] Samples =
    [
        new("R-1041", "2026-10-03", "Zomato", "5", "Biryani was amazing, packing also neat. Will order again!"),
        new("R-1042", "2026-10-03", "Swiggy", "1", "Ordered at 8pm, came at 9:10. Food fully cold. Very bad."),
        new("R-1043", "2026-10-04", "Google", "2", "Saapadu nalla irundhuchu but bill la extra charge pottanga"),
    ];

    public static async Task<int> RunAsync(IServiceProvider services)
    {
        var factory = services.GetRequiredService<AgentFactory>();
        if (!factory.IsConfigured)
        {
            Console.WriteLine("GEMINI_API_KEY is missing. Copy .env.example to .env and add your key.");
            return 1;
        }

        Console.WriteLine($"Smoke test: {Samples.Length} reviews, model {factory.ModelName}");
        var analyzer = services.GetRequiredService<ReviewAnalyzer>();
        var result = await analyzer.AnalyzeAllAsync(Samples);

        var json = new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };
        foreach (var row in result.Rows)
        {
            // review_id is set in code, not by the LLM - print it next to the AI fields.
            var output = new Dictionary<string, object?> { ["review_id"] = row.Review.ReviewId, ["status"] = row.Status };
            if (row.Analysis is not null)
                foreach (var p in JsonSerializer.SerializeToElement(row.Analysis).EnumerateObject())
                    output[p.Name] = p.Value;
            if (row.Error is not null) output["error"] = row.Error;
            Console.WriteLine(JsonSerializer.Serialize(output, json));
        }

        if (result.Stopped) Console.WriteLine("STOPPED: " + result.Message);
        if (!result.Rows.All(r => r.IsOk)) return 2;

        // One more call: the report agent over the same 3 rows.
        var insights = services.GetRequiredService<InsightsService>();
        var summary = InsightsService.Aggregate(result.Rows);
        var report = await insights.GenerateReportAsync(summary, result.Rows);
        Console.WriteLine();
        Console.WriteLine(InsightsService.FormatReport(summary, report, result.Rows));
        return 0;
    }
}
