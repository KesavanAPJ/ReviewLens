using System.Text.Json.Serialization;

namespace ReviewLens.Web.Models;

// Enums (not strings) so that any value outside the allowed list fails to parse.
// [JsonStringEnumMemberName] makes the JSON text match the data contract exactly
// (e.g. "food_quality"), both in the schema sent to the LLM and when parsing its reply.

[JsonConverter(typeof(JsonStringEnumConverter<Sentiment>))]
public enum Sentiment
{
    [JsonStringEnumMemberName("positive")] Positive,
    [JsonStringEnumMemberName("neutral")] Neutral,
    [JsonStringEnumMemberName("negative")] Negative,
}

[JsonConverter(typeof(JsonStringEnumConverter<Category>))]
public enum Category
{
    [JsonStringEnumMemberName("food_quality")] FoodQuality,
    [JsonStringEnumMemberName("delivery")] Delivery,
    [JsonStringEnumMemberName("price")] Price,
    [JsonStringEnumMemberName("staff")] Staff,
    [JsonStringEnumMemberName("packaging")] Packaging,
    [JsonStringEnumMemberName("app")] App,
}

[JsonConverter(typeof(JsonStringEnumConverter<Urgency>))]
public enum Urgency
{
    [JsonStringEnumMemberName("low")] Low,
    [JsonStringEnumMemberName("medium")] Medium,
    [JsonStringEnumMemberName("high")] High,
}

[JsonConverter(typeof(JsonStringEnumConverter<ReviewLanguage>))]
public enum ReviewLanguage
{
    [JsonStringEnumMemberName("English")] English,
    [JsonStringEnumMemberName("Tamil")] Tamil,
    [JsonStringEnumMemberName("Tanglish")] Tanglish,
}

/// <summary>Helpers to turn enum values back into their contract text (for CSV and UI).</summary>
public static class EnumText
{
    public static string ToText(this Sentiment v) => v.ToString().ToLowerInvariant();
    public static string ToText(this Urgency v) => v.ToString().ToLowerInvariant();
    public static string ToText(this ReviewLanguage v) => v.ToString();

    public static string ToText(this Category v) => v switch
    {
        Category.FoodQuality => "food_quality",
        _ => v.ToString().ToLowerInvariant(),
    };
}
