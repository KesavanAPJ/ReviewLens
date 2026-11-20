namespace ReviewLens.Web.Services;

/// <summary>
/// Strict sliding-window limiter: never more than <c>limit</c> requests in any window (default 60 s).
/// It remembers when the last <c>limit</c> requests started; a new request waits until the oldest
/// of them is a full window old. Every Gemini call (first tries, retries, the report) goes through it.
/// MaxDegreeOfParallelism limits calls *in flight*; this limits calls *per minute*. We need both.
/// </summary>
public class RequestRateLimiter
{
    private readonly int _limit;
    private readonly TimeSpan _window;
    private readonly TimeProvider _time;
    private readonly Queue<DateTimeOffset> _starts = new();
    private readonly SemaphoreSlim _gate = new(1, 1);   // one waiter at a time = first come, first served

    public RequestRateLimiter(int limit, TimeSpan? window = null, TimeProvider? time = null)
    {
        if (limit < 1) throw new ArgumentOutOfRangeException(nameof(limit), "RequestsPerMinute must be at least 1.");
        _limit = limit;
        _window = window ?? TimeSpan.FromMinutes(1);
        _time = time ?? TimeProvider.System;
    }

    public int Limit => _limit;

    /// <summary>Waits until a request may be sent, then records it as sent.</summary>
    public async Task WaitAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            while (true)
            {
                var now = _time.GetUtcNow();

                // Forget requests that are older than the window.
                while (_starts.Count > 0 && now - _starts.Peek() >= _window)
                    _starts.Dequeue();

                if (_starts.Count < _limit)
                {
                    _starts.Enqueue(now);
                    return;
                }

                // Full: sleep until the oldest request leaves the window, then check again.
                var wait = _starts.Peek() + _window - now;
                await Task.Delay(wait, _time, ct);
            }
        }
        finally
        {
            _gate.Release();
        }
    }
}
