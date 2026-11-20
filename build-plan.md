# ReviewLens build plan (.NET 10 + Microsoft Agent Framework 1.28.0 + Gemini)

## Context
The folder holds only `CLAUDE.md`, `usecase.txt` and `.env` (it has `GEMINI_API_KEY`). We are building the whole ReviewLens app from scratch. The user uploads a reviews CSV, the AI tags each review, LINQ does the counting, a second agent writes the weekly report, and the user can download the enriched CSV. We follow the 5-stage order in CLAUDE.md §7 and run `dotnet build` after each stage.

## Verified API (checked in the packages, not guessed)
- Installed SDK: **10.0.404**, so the target is `net10.0`.
- `Microsoft.Agents.AI` 1.28.0 (already in the NuGet cache). `Microsoft.Agents.AI.OpenAI` 1.28.0 (inspected the nupkg; it depends on `Microsoft.Extensions.AI.OpenAI` 10.11.0).
- Agent creation: `OpenAI.Chat.ChatClient.AsAIAgent(instructions: ..., name: ...)` from `OpenAIChatClientExtensions`. We use the **ChatClient** (Chat Completions) and not `ResponsesClient`, because Gemini's OpenAI-compatible endpoint only supports Chat Completions.
- Structured output: `agent.RunAsync<T>(string message, AgentSession? session = null, JsonSerializerOptions? serializerOptions = null, AgentRunOptions? options = null, CancellationToken ct)` returns `AgentResponse<T>`, and the typed value is in `.Result`.
- Client: `new OpenAIClient(new ApiKeyCredential(key), new OpenAIClientOptions { Endpoint = new Uri("https://generativelanguage.googleapis.com/v1beta/openai/") }).GetChatClient(model)`.
- The session is null on every call, so each review gets its own fresh conversation. This keeps parallel calls on one agent safe.

## NuGet packages
| Project | Package | Version |
|---|---|---|
| Web | Microsoft.Agents.AI | 1.28.0 |
| Web | Microsoft.Agents.AI.OpenAI | 1.28.0 |
| Web | OpenAI | 2.15.0 (or whatever the adapter resolves to, if that is higher) |
| Web | CsvHelper | 34.1.0 |
| Web | DotNetEnv | 3.2.0 |
| Tests | xunit, xunit.runner.visualstudio, Microsoft.NET.Test.Sdk (template defaults) + project reference to Web | — |

## Files
```
ReviewLens.sln
.gitignore                      (.env, bin/, obj/)
.env.example                    GEMINI_API_KEY=your-key-here  GEMINI_MODEL=gemini-2.5-flash
README.md
sample-data/reviews_week40.csv  ~25 rows: English, Tamil script, Tanglish; one empty review_text row
sample-data/reviews_200.csv     220 rows for the Done-checklist load test (generated from varied templates)
src/ReviewLens.Web/
  Program.cs                    DotNetEnv load, DI registration, 5 MB SignalR message size
  appsettings.json              ReviewLens: { Model, MaxDegreeOfParallelism: 5, MaxRetries: 2 }
  Models/Enums.cs               Sentiment, Category, Urgency, ReviewLanguage, using [JsonStringEnumMemberName("food_quality")] etc.
  Models/Review.cs              input row record
  Models/ReviewAnalysis.cs      LLM output type (no review_id; the LLM does not set it)
  Models/EnrichedReview.cs      Review + analysis fields + Status ("ok" | "needs_review") + Error
  Models/InsightsSummary.cs     counts/percentages (CountItem: Label, Count, Percent)
  Models/WeeklyReport.cs        LLM report output: TopComplaints, TopPraises, UrgentCount, ActionItems
  Services/CsvService.cs        Read(Stream) → (rows, droppedCount, errors); Export(rows) → byte[] UTF-8 with BOM
  Services/ReviewAnalyzer.cs    AnalyzeAllAsync(rows, IProgress<int>, ct), AnalyzeOneAsync with retry
  Services/InsightsService.cs   Aggregate(rows) (pure LINQ), GenerateReportAsync(summary, rows), FormatReport(...)
  Services/AgentFactory.cs      builds the OpenAI ChatClient → AIAgent from config (one place for endpoint/key)
  Components/Pages/Home.razor   upload → validate msg → progress bar → dashboard → report → download
  Components/App.razor, Routes.razor, Layout/*   (from the template, trimmed)
  wwwroot/app.css               CSS bar charts
tests/ReviewLens.Tests/
  CsvServiceTests.cs            missing column → error; empty review_text dropped + counted; Tamil round-trip; BOM present
  InsightsServiceTests.cs       counts/percentages exact; needs_review rows handled; platform grouping
  ValidationTests.cs            enum JSON parsing rejects out-of-list values ("Food", "urgent")
```

## Stages (run `dotnet build` after each one; `dotnet test` from stage 1 onward)

**1. Solution, models, CSV service and tests**
- `dotnet new sln`, `dotnet new blazor -int Server --empty -o src/ReviewLens.Web` (fallback: the template without `--empty`), `dotnet new xunit -o tests/ReviewLens.Tests`, add the references and packages.
- Enums use `[JsonStringEnumMemberName]` (supported in .NET 9+), so `food_quality`, `high` and `Tanglish` serialize exactly as the contract says. An unknown value throws `JsonException`, which is our "invalid value" signal.
- CsvService: CsvHelper with a header-name map. It checks that all 5 columns exist (case-insensitive) and drops whitespace-only `review_text` rows. Export writes `new UTF8Encoding(true)`, with columns `review_id,date,platform,rating,review_text,sentiment,category,urgency,language,summary,suggested_reply,status`.
- Add the sample CSV and the tests.

**2. Agent and retry logic**
- `ReviewAnalyzer` holds one agent. Its instructions give the allowed values, the urgency rule (high = health issue, refund, abuse, wrong order), "summary: one English sentence even for Tamil/Tanglish", and "reply: polite, English, ready to post".
- `AnalyzeOneAsync`: up to 1 + 2 attempts. It catches `JsonException`/`InvalidOperationException` (bad parse or bad enum) and also checks that summary and reply are not empty. On failure it returns `Status = "needs_review"` with the error text. It never throws.
- 429 handling: the OpenAI client's built-in retry pipeline already backs off on 429. On top of that, if a `ClientResultException` with `Status == 429` gets through, we wait with exponential backoff (2s, 4s, 8s + jitter). This wait does not use up a parse-retry attempt; it has its own cap (5).
- `AnalyzeAllAsync`: `Parallel.ForEachAsync` with `MaxDegreeOfParallelism` from config. Results go into an array by index so the order stays stable, and an `Interlocked` counter feeds `IProgress<int>`.
- Smoke test: a small `--smoke` switch in Program.cs (or a temporary console call) runs the **3 sample rows** from usecase.txt against Gemini and prints the JSON. This needs your real key and costs a few API calls.

**3. Aggregation and report agent**
- `Aggregate`: LINQ GroupBy for sentiment, category, urgency and platform. It counts only `ok` rows in the AI fields, reports `needs_review` separately, gives percentages rounded to 1 decimal, and builds the urgent list (urgency == high).
- Report agent: the input is the pre-computed numbers as JSON plus the one-line summaries (with category/sentiment/platform tags, not the full text). `RunAsync<WeeklyReport>` returns top 3 complaints (theme + count + note), top praises, and 3 action items. The instructions say to use only the provided numbers.
- Numbers are guaranteed: the header lines (total, %, urgent count) are rendered **by C#** from `InsightsSummary`, not taken from the LLM. The LLM's `UrgentCount` is ignored and overwritten. The report format matches Output B in usecase.txt.

**4. Blazor UI (Home.razor, InteractiveServer)**
- `InputFile` (5 MB max), then a validation message ("N rows loaded, M dropped (empty review_text)").
- An Analyze button and a progress bar (`<progress>` + "x / N"), with `InvokeAsync(StateHasChanged)` from the progress callback.
- Dashboard: sentiment split (stacked CSS bar + %), category bars, urgency and platform counts, needs_review count.
- Table with a "HIGH urgency only" toggle filter.
- Weekly report in a `<pre>` block, and a "Download enriched CSV" button using a small JS-interop download helper (a ~5-line `downloadFile` function in `App.razor`; this is not a chart library).
- Raise `MaximumReceiveMessageSize` on the hub so the 5 MB upload works.

**5. README and final run**
- README: prerequisites, then `copy .env.example .env` → `dotnet run --project src/ReviewLens.Web` (+ `dotnet test`).
- Run against the Done checklist: the 220-row file finishes with every row either fully filled in or `needs_review`; Tamil/Tanglish summaries are checked by hand; report numbers match the table; the HIGH filter works; the CSV opens in Excel with Tamil intact (BOM checked in a test); grep confirms the key is not in the source.

## Verification
- `dotnet build` and `dotnet test` (all green) after each stage.
- Stage 2: a 3-row smoke run prints valid JSON for R-1041/1042/1043. R-1042 should come back as delivery/negative, and R-1043 as Tanglish/price.
- Stage 5: launch the app, upload `reviews_200.csv`, and confirm the progress bar finishes, the counts in the report header equal the dashboard, the HIGH filter works, and the downloaded CSV starts with the BOM `EF BB BF`.

## Notes / assumptions
- Gemini free-tier rate limits may make the 220-row run slow. With 5 in parallel plus 429 backoff it completes, just not quickly.
- No database, auth, Docker or stretch goals.
- The folder is not a git repo. I won't run `git init` unless you ask; `.gitignore` is still added.
