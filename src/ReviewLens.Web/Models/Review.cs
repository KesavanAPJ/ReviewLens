namespace ReviewLens.Web.Models;

/// <summary>One row of the input CSV: review_id,date,platform,rating,review_text.</summary>
public record Review(string ReviewId, string Date, string Platform, string Rating, string ReviewText);
