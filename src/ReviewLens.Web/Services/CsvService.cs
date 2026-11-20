using CsvHelper;
using ReviewLens.Web.Models;

namespace ReviewLens.Web.Services;

/// <summary>Result of reading + validating an uploaded CSV.</summary>
public record CsvReadResult(List<Review> Rows, int DroppedCount, string? Error)
{
    public bool IsValid => Error is null;
}

/// <summary>Reads, validates and exports review CSV files (CsvHelper 34 API).</summary>
public class CsvService
{
    public static readonly string[] RequiredColumns = ["review_id", "date", "platform", "rating", "review_text"];

    public static readonly string[] ExportColumns =
    [
        "review_id", "date", "platform", "rating", "review_text",
        "sentiment", "category", "urgency", "language", "summary", "suggested_reply", "status",
    ];

    private static readonly CsvReaderOptions ReadOptions = CsvReaderOptions.Default with
    {
        Separator = ',',          // don't auto-detect: a review full of ';' could fool detection
        TrimWhitespace = true,
    };

    private static readonly CsvWriterOptions WriteOptions = CsvWriterOptions.Default with
    {
        // Without the BOM, Excel guesses the wrong encoding and Tamil text turns into garbage.
        WriteByteOrderMark = true,
        // A review starting with "=" or "+" would run as a formula in Excel; prefix it with ' instead.
        Injection = CsvInjection.Escape,
    };

    public CsvReadResult Read(Stream stream)
    {
        try
        {
            // UTF-8 by default; a UTF-8 BOM (files saved by Excel) is handled by the reader.
            using var csv = CsvReader.Create(stream, ReadOptions, leaveOpen: true);

            // Header names are compared ignoring case and surrounding spaces.
            var headers = csv.Headers.Select(h => h.Trim().ToLowerInvariant()).ToHashSet();
            if (headers.Count == 0)
                return new CsvReadResult([], 0, "The file is empty or has no header row.");

            var missing = RequiredColumns.Where(c => !headers.Contains(c)).ToList();
            if (missing.Count > 0)
                return new CsvReadResult([], 0,
                    $"Missing required column(s): {string.Join(", ", missing)}. Expected: {string.Join(",", RequiredColumns)}");

            var rows = new List<Review>();
            int dropped = 0;
            while (csv.Read())
            {
                var text = csv.GetString("review_text").Trim();
                if (text.Length == 0)
                {
                    dropped++;   // rule: drop rows with empty review_text, but report how many
                    continue;
                }

                rows.Add(new Review(
                    csv.GetString("review_id"),
                    csv.GetString("date"),
                    csv.GetString("platform"),
                    csv.GetString("rating"),
                    text));
            }

            return new CsvReadResult(rows, dropped, null);
        }
        catch (CsvFormatException ex)
        {
            return new CsvReadResult([], 0, $"The file is not a valid CSV: {ex.Message}");
        }
    }

    /// <summary>Writes the enriched table as UTF-8 WITH BOM so it opens correctly in Excel.</summary>
    public byte[] Export(IEnumerable<EnrichedReview> rows)
    {
        using var ms = new MemoryStream();
        using (var csv = CsvWriter.Create(ms, WriteOptions, leaveOpen: true))
        {
            csv.WriteRecord(ExportColumns);

            foreach (var r in rows)
            {
                var a = r.IsOk ? r.Analysis : null;
                csv.WriteRecord(
                [
                    r.Review.ReviewId, r.Review.Date, r.Review.Platform, r.Review.Rating, r.Review.ReviewText,
                    a?.Sentiment.ToText() ?? "",
                    a?.Category.ToText() ?? "",
                    a?.Urgency.ToText() ?? "",
                    a?.Language.ToText() ?? "",
                    a?.Summary ?? "",
                    a?.SuggestedReply ?? "",
                    r.Status,
                ]);
            }
        } // disposing flushes the buffer into the MemoryStream
        return ms.ToArray();
    }
}
