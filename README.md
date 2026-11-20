# ReviewLens

Upload a CSV of customer reviews. The AI tags each review with sentiment, category, urgency, language, a one-line English summary and a suggested reply. The app then shows a dashboard and a weekly report, and you can download the enriched CSV for Excel.

Built with **.NET 10**, **Blazor (Interactive Server)**, the **Microsoft Agent Framework** (`Microsoft.Agents.AI` 1.28) and **Google Gemini** through its OpenAI-compatible endpoint.

## Run it (3 commands, PowerShell)

```powershell
copy .env.example .env          # then open .env and paste your GEMINI_API_KEY
dotnet run --project src/ReviewLens.Web
start http://localhost:5147     # the URL is also printed in the console
```

Optional:

```powershell
dotnet test                                          # unit tests (no API calls)
dotnet run --project src/ReviewLens.Web -- --smoke   # 3 sample reviews + report against Gemini (~4-10 calls)
```

## Configuration

| Where | Setting | Default | Meaning |
|---|---|---|---|
| `.env` | `GEMINI_API_KEY` | (required) | Your Gemini key. Never commit `.env`. |
| `.env` | `GEMINI_MODEL` | (unset) | Overrides `Model` below without editing JSON. |
| `appsettings.json` → `ReviewLens` | `Model` | `gemini-3.6-flash` | Gemini model name. |
| | `MaxDegreeOfParallelism` | 5 | Reviews analysed at the same time. |
| | `RequestsPerMinute` | 8 | Hard cap on Gemini calls in any 60-second window, including retries and the report call. |
| | `MaxRetries` | 2 | Extra tries when an answer fails to parse or has a value that isn't allowed. |
| | `Max429Retries` | 5 | Backoff tries on HTTP 429 before the batch stops as "quota exhausted". |

> **Model note:** the default is `gemini-3.6-flash`. The blueprint's original `gemini-2.5-flash` still shows up in `/models` but returns **HTTP 404** on generation calls (Nov 2026). If you see the "model … was not found or is retired" banner, set `GEMINI_MODEL` in `.env` to a model your key can use.

## How it works

1. **Upload and validate** (`CsvService`): checks that the 5 required columns are present, drops rows with an empty `review_text`, and shows how many rows were dropped.
2. **Classify / map** (`ReviewAnalyzer`): one agent, `RunAsync<ReviewAnalysis>()`, with structured JSON output into C# enums. Reviews run in parallel through `Parallel.ForEachAsync`. Every call goes through `RequestRateLimiter` first.
3. **Retry rule**: if the JSON is bad, a value is outside the allowed list, or the summary/reply is empty or not in English, that review is retried up to 2 more times. If it still fails, the row is kept with `status = needs_review`.
4. **Stop rules**: these errors would be the same for every review, so the batch stops and a **red banner** explains why. Finished rows are kept, and unsent rows become `not_processed`.
   - HTTP 429 that keeps happening, or Gemini's per-day quota → "Gemini daily quota exhausted".
   - HTTP 401/403/404 → wrong key or wrong model.
5. **Aggregate** (`InsightsService.Aggregate`): every count and percentage is calculated with LINQ.
6. **Report / reduce**: a second agent gets the numbers plus the one-line summaries (not the full reviews). It returns themes and the **review IDs** behind each theme. C# counts those IDs, so even "Late delivery … N reviews" is calculated, not guessed.
7. **Show and export**: dashboard, HIGH-urgency filter, report, and the enriched CSV in **UTF-8 with BOM** so Tamil opens correctly in Excel.

## Done checklist (manual, opt-in)

The 200+ row test uses your Gemini quota, so it is **never run automatically**. To run it:

1. Start the app and upload `sample-data/reviews_200.csv` (220 rows: English, Tamil, Tanglish).
   At 8 requests/minute this takes **about 28 minutes**. Keep the browser tab open.
2. Check:
   - [ ] The progress bar reaches 220 / 220 with no crash. Every row is `ok` or `needs_review`, or, if the quota ran out, the red banner is shown and the rest are `not_processed`.
   - [ ] Every `ok` row has all fields, and every value comes from the allowed list.
   - [ ] Tamil and Tanglish rows have a correct **English** summary.
   - [ ] The report header numbers (total, %, urgent) match the dashboard tiles and bars.
   - [ ] "Show only HIGH urgency" lists only high rows, and the count matches the tile.
   - [ ] "Download enriched CSV" opens in Excel with Tamil text shown correctly.
   - [ ] The key exists only in `.env` (`git grep GEMINI_API_KEY` shows only code that reads it).

For a quick check instead, use `sample-data/reviews_week40.csv` (25 rows, one of them empty on purpose, so 24 are analysed in about 3 minutes).

## Project layout

```
src/ReviewLens.Web/        Blazor app
  Models/                  Review, ReviewAnalysis, EnrichedReview, InsightsSummary, WeeklyReport, enums
  Services/                CsvService, ReviewAnalyzer, InsightsService, AgentFactory, RequestRateLimiter
  Components/Pages/Home.razor
tests/ReviewLens.Tests/    xUnit: CSV, validation, aggregation, report numbers, rate limiter
sample-data/               reviews_week40.csv (25 rows), reviews_200.csv (220 rows, opt-in)
```
