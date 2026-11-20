using System.ComponentModel;
using System.Text.Json.Serialization;

namespace ReviewLens.Web.Models;

/// <summary>
/// What the LLM must return for one review. review_id is NOT here on purpose:
/// we copy it from the input in code so the model can never get it wrong.
/// The [Description] texts end up in the JSON schema sent to the model.
/// </summary>
public class ReviewAnalysis
{
    [JsonPropertyName("sentiment")]
    [Description("Overall customer mood: positive, neutral or negative.")]
    public Sentiment Sentiment { get; set; }

    [JsonPropertyName("category")]
    [Description("Main topic: food_quality, delivery, price, staff, packaging or app.")]
    public Category Category { get; set; }

    [JsonPropertyName("urgency")]
    [Description("high = health issue, refund request, abuse/rude staff, or wrong order. medium = clear complaint needing follow-up. low = praise or minor remark.")]
    public Urgency Urgency { get; set; }

    [JsonPropertyName("language")]
    [Description("Language of the review: English, Tamil (Tamil script) or Tanglish (Tamil written in Latin letters, often mixed with English).")]
    public ReviewLanguage Language { get; set; }

    [JsonPropertyName("summary")]
    [Description("Exactly one English sentence summarising the review, even if the review is in Tamil or Tanglish.")]
    public string Summary { get; set; } = "";

    [JsonPropertyName("suggested_reply")]
    [Description("A polite, ready-to-post reply in English.")]
    public string SuggestedReply { get; set; } = "";
}
