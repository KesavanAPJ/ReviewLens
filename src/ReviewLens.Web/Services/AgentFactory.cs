using System.ClientModel;
using System.ClientModel.Primitives;
using System.Text.RegularExpressions;
using Microsoft.Agents.AI;
using Microsoft.Extensions.Options;
using OpenAI;
using OpenAI.Chat;

namespace ReviewLens.Web.Services;

/// <summary>
/// One place that knows how to talk to Gemini:
///  - builds agents on Gemini's OpenAI-compatible endpoint (Chat Completions API), and
///  - runs every call through the rate limiter with our own HTTP 429 handling.
/// </summary>
public partial class AgentFactory
{
    public const string GeminiEndpoint = "https://generativelanguage.googleapis.com/v1beta/openai/";

    private readonly ChatClient? _chatClient;
    private readonly RequestRateLimiter _limiter;
    private readonly ReviewLensOptions _options;
    private readonly ILogger<AgentFactory> _logger;

    public AgentFactory(IConfiguration config, IOptions<ReviewLensOptions> options,
        RequestRateLimiter limiter, ILogger<AgentFactory> logger)
    {
        _options = options.Value;
        _limiter = limiter;
        _logger = logger;

        // The key comes from .env (loaded into environment variables in Program.cs), never from code.
        var apiKey = config["GEMINI_API_KEY"];
        ModelName = string.IsNullOrWhiteSpace(config["GEMINI_MODEL"]) ? _options.Model : config["GEMINI_MODEL"]!;
        if (string.IsNullOrWhiteSpace(apiKey))
            return;   // IsConfigured = false; the UI shows a message instead of crashing

        var clientOptions = new OpenAIClientOptions
        {
            Endpoint = new Uri(GeminiEndpoint),
            NetworkTimeout = TimeSpan.FromSeconds(_options.RequestTimeoutSeconds),
            // Turn OFF the SDK's hidden retries: they would resend requests without asking our
            // rate limiter and break the "max N requests per minute" promise. We retry ourselves.
            RetryPolicy = new ClientRetryPolicy(maxRetries: 0),
        };
        _chatClient = new OpenAIClient(new ApiKeyCredential(apiKey), clientOptions).GetChatClient(ModelName);
    }

    public bool IsConfigured => _chatClient is not null;
    public string ModelName { get; }

    public AIAgent CreateAgent(string name, string instructions)
    {
        if (_chatClient is null)
            throw new InvalidOperationException("GEMINI_API_KEY is missing. Copy .env.example to .env and add your key.");

        // Microsoft Agent Framework: wrap the OpenAI ChatClient as an agent.
        return _chatClient.AsAIAgent(instructions: instructions, name: name);
    }

    /// <summary>
    /// Runs the agent and returns the typed result. Waits for the rate limiter before every attempt.
    /// On HTTP 429 it backs off and tries again; if the daily quota is gone, or 429 keeps coming,
    /// it throws <see cref="QuotaExhaustedException"/>. Other errors (bad JSON etc.) are thrown as-is
    /// so the caller can decide to retry.
    /// </summary>
    public async Task<T> RunStructuredAsync<T>(AIAgent agent, string prompt, CancellationToken ct)
    {
        for (int attempt = 0; ; attempt++)
        {
            await _limiter.WaitAsync(ct);
            try
            {
                AgentResponse<T> response = await agent.RunAsync<T>(prompt, cancellationToken: ct);
                return response.Result;   // deserialises the JSON; throws if it doesn't match T
            }
            catch (Exception ex) when (TryGetRateLimitBody(ex, out var body))
            {
                if (IsDailyQuota(body))
                    throw new QuotaExhaustedException(
                        "Gemini daily quota exhausted (HTTP 429, per-day limit reached).", ex);

                if (attempt >= _options.Max429Retries)
                    throw new QuotaExhaustedException(
                        $"Gemini kept returning HTTP 429 after {attempt + 1} tries — the quota looks exhausted.", ex);

                // Exponential backoff 2s, 4s, 8s... plus jitter; respect Gemini's own retryDelay if longer.
                var backoff = TimeSpan.FromSeconds(2 * Math.Pow(2, attempt) + Random.Shared.NextDouble());
                var serverHint = ParseRetryDelay(body);
                var delay = serverHint > backoff ? serverHint : backoff;
                _logger.LogWarning("HTTP 429 from Gemini, waiting {Delay:0.0}s (try {Try}/{Max})",
                    delay.TotalSeconds, attempt + 1, _options.Max429Retries);
                await Task.Delay(delay, ct);
            }
            catch (ClientResultException ex) when (ex.Status is 401 or 403 or 404)
            {
                // Same answer for every review -> stop the batch instead of burning retries.
                var reason = ex.Status == 404
                    ? $"model '{ModelName}' was not found or is retired. Set GEMINI_MODEL in .env (or Model in appsettings.json) to an available model"
                    : "the API key was rejected. Check GEMINI_API_KEY in .env";
                throw new GeminiConfigurationException($"Gemini returned HTTP {ex.Status}: {reason}.", ex);
            }
        }
    }

    /// <summary>True when the exception (or an inner one) is an HTTP 429. Returns the error text for inspection.</summary>
    internal static bool TryGetRateLimitBody(Exception ex, out string body)
    {
        for (Exception? e = ex; e is not null; e = e.InnerException)
        {
            if (e is ClientResultException cre && cre.Status == 429)
            {
                string raw = "";
                try { raw = cre.GetRawResponse()?.Content?.ToString() ?? ""; } catch { /* body not buffered */ }
                body = cre.Message + "\n" + raw;
                return true;
            }
        }
        body = "";
        return false;
    }

    /// <summary>Gemini puts the quota id in the 429 body, e.g. "GenerateRequestsPerDayPerProjectPerModel-FreeTier".</summary>
    internal static bool IsDailyQuota(string body) =>
        body.Contains("PerDay", StringComparison.OrdinalIgnoreCase) ||
        body.Contains("per day", StringComparison.OrdinalIgnoreCase);

    internal static TimeSpan ParseRetryDelay(string body)
    {
        var m = RetryDelayRegex().Match(body);
        return m.Success && double.TryParse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture, out var s)
            ? TimeSpan.FromSeconds(Math.Min(s, 120))
            : TimeSpan.Zero;
    }

    [GeneratedRegex("\"retryDelay\"\\s*:\\s*\"(\\d+(?:\\.\\d+)?)s\"")]
    private static partial Regex RetryDelayRegex();
}
