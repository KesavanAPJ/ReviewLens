namespace ReviewLens.Web.Services;

/// <summary>
/// An error that will be the same for EVERY review, so retrying the other reviews is pointless:
/// the batch stops and the UI shows the message.
/// </summary>
public abstract class GeminiFatalException(string message, Exception? inner = null) : Exception(message, inner)
{
}

/// <summary>
/// Gemini keeps answering HTTP 429 (or says the daily quota is used up).
/// </summary>
public class QuotaExhaustedException(string message, Exception? inner = null) : GeminiFatalException(message, inner)
{
}

/// <summary>
/// HTTP 401/403/404: wrong API key, no access, or a model name that does not exist / is retired.
/// </summary>
public class GeminiConfigurationException(string message, Exception? inner = null) : GeminiFatalException(message, inner)
{
}
