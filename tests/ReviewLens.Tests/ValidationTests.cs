using System.Text.Json;
using ReviewLens.Web.Models;

namespace ReviewLens.Tests;

/// <summary>The enums are our "allowed list": anything else must fail to parse so it triggers a retry.</summary>
public class ValidationTests
{
    private const string Valid = """
        {"sentiment":"negative","category":"food_quality","urgency":"high","language":"Tanglish",
         "summary":"Food was cold.","suggested_reply":"Sorry!"}
        """;

    [Fact]
    public void ValidJson_ParsesToEnums()
    {
        var a = JsonSerializer.Deserialize<ReviewAnalysis>(Valid)!;

        Assert.Equal(Sentiment.Negative, a.Sentiment);
        Assert.Equal(Category.FoodQuality, a.Category);
        Assert.Equal(Urgency.High, a.Urgency);
        Assert.Equal(ReviewLanguage.Tanglish, a.Language);
    }

    [Theory]
    [InlineData("\"category\":\"food_quality\"", "\"category\":\"Food\"")]
    [InlineData("\"urgency\":\"high\"", "\"urgency\":\"urgent\"")]
    [InlineData("\"sentiment\":\"negative\"", "\"sentiment\":\"mixed\"")]
    [InlineData("\"language\":\"Tanglish\"", "\"language\":\"Hindi\"")]
    public void OutOfListValue_FailsToParse(string good, string bad)
    {
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<ReviewAnalysis>(Valid.Replace(good, bad)));
    }

    [Fact]
    public void Enums_SerializeToContractText()
    {
        var json = JsonSerializer.Serialize(new ReviewAnalysis { Category = Category.FoodQuality, Urgency = Urgency.Medium });

        Assert.Contains("\"category\":\"food_quality\"", json);
        Assert.Contains("\"urgency\":\"medium\"", json);
        Assert.Equal("food_quality", Category.FoodQuality.ToText());
    }
}
