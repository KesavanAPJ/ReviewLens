# CLAUDE.md — ReviewLens (.NET + Microsoft Agent Framework)

This is the blueprint for this project. Read it fully before planning or writing code.
The full use case is in `usecase.txt` in this folder. That file is the source of truth for
features, fields and the Done checklist.

## 1. What we are building (one line)
Upload a CSV of customer reviews → the AI tags every review (sentiment, category, urgency,
language, summary, suggested reply) → the app shows a dashboard, a weekly report, and lets the
user download the enriched CSV.

## 2. Tech stack (fixed — do not change without asking)
| Need | Use |
|---|---|
| Language / runtime | C#, .NET 8 or higher (use the installed SDK version) |
| Web UI | Blazor Web App, Interactive Server render mode (replaces Streamlit) |
| AI agent | Microsoft Agent Framework: NuGet `Microsoft.Agents.AI` + `Microsoft.Agents.AI.OpenAI` |
| LLM | Google Gemini via its OpenAI-compatible endpoint `https://generativelanguage.googleapis.com/v1beta/openai/` using the official `OpenAI` .NET client with a custom `Endpoint`. Model name comes from config (default `gemini-3.6-flash`). |
| Structured output | C# record/class + `agent.RunAsync<T>(...)`, typed value read from `response.Result` |
| CSV read/write | `CsvHelper` |
| Aggregation (replaces pandas) | Plain LINQ |
| Charts | Simple CSS bar charts in Razor (no JS chart library needed) |
| Secrets | `.env` file loaded with `DotNetEnv`; key name `GEMINI_API_KEY`. Never hard-code the key. Add `.env` to `.gitignore`. Provide `.env.example`. |
| Tests | xUnit project for validation, aggregation and CSV export logic |

If the Agent Framework API names differ from what is written here (packages are updated often),
check the installed package and use the current API. Do not invent methods.

## 3. Solution layout (keep it small)
```
ReviewLens.sln
src/ReviewLens.Web/          Blazor app (UI + services)
  Models/                    Review, ReviewAnalysis, WeeklyReport, enums
  Services/
    CsvService.cs            read + validate + export CSV
    ReviewAnalyzer.cs        per-review agent (the "map" step)
    InsightsService.cs       LINQ counts + report agent (the "reduce" step)
  Components/Pages/Home.razor  upload, dashboard, filter, report, download
tests/ReviewLens.Tests/      xUnit tests
sample-data/reviews_week40.csv  20+ rows incl. English, Tamil, Tanglish
.env.example
README.md                    how to run in 3 commands
```

## 4. Data contract (must match exactly)
Input CSV columns: `review_id,date,platform,rating,review_text`

Per-review AI output (`ReviewAnalysis`):
| Field | Allowed values |
|---|---|
| review_id | copied from input (set in code, not by the LLM) |
| sentiment | positive, neutral, negative |
| category | food_quality, delivery, price, staff, packaging, app |
| urgency | low, medium, high (high = health issue, refund, abuse, wrong order) |
| language | English, Tamil, Tanglish |
| summary | one English sentence, even for Tamil/Tanglish reviews |
| suggested_reply | polite, ready-to-post reply in English |

Use C# enums for sentiment/category/urgency/language so invalid values fail to parse.

## 5. How the pipeline works
1. **Upload** — Blazor `InputFile`, allow files up to 5 MB.
2. **Validate** — required columns present; drop rows with empty `review_text`; show how many rows were dropped.
3. **Classify (map)** — one `ReviewAnalyzer` agent. Process reviews in parallel with
   `Parallel.ForEachAsync` and `MaxDegreeOfParallelism = 5` (configurable). Show a progress bar.
4. **Retry rule** — if the response fails to parse or a value is outside the allowed list,
   retry that review up to 2 more times. If it still fails, keep the row with
   `status = "needs_review"` and continue. Never crash the whole batch for one bad review.
   Also back off and retry on HTTP 429 (rate limit).
5. **Aggregate** — LINQ counts and percentages for sentiment, category, urgency, platform.
   **All numbers are calculated in C#, never by the LLM.**
6. **Report (reduce)** — a second agent receives the pre-computed counts plus the list of
   one-line summaries (not the full reviews) and writes: top 3 complaints, top praises,
   urgent count, 3 action items. The report must reuse the given numbers, not make new ones.
7. **Show + Export** — dashboard (sentiment split, category bars, HIGH-urgency filter),
   weekly report text, and a "Download enriched CSV" button. Export as UTF-8 **with BOM**
   so Tamil text opens correctly in Excel.

## 6. Done checklist (the app is finished only when all pass)
- [ ] Processes a 200+ row CSV without crashing or timing out
- [ ] Every review has all fields, every value from the allowed list (or marked needs_review)
- [ ] Tamil and Tanglish reviews get a correct English summary
- [ ] Report numbers exactly match the table (calculated in LINQ)
- [ ] HIGH urgency filter works; enriched CSV opens correctly in Excel
- [ ] API key loaded from `.env`, never hard-coded

## 7. Working rules for Claude Code
- Environment: Windows 10, PowerShell. Give PowerShell-friendly commands.
- Build in this order and run `dotnet build` after each stage:
  1) solution + models + CSV service + tests
  2) agent + retry logic (test with 3 sample rows first)
  3) aggregation + report agent
  4) Blazor UI
  5) README + final run against the Done checklist
- Keep code simple and readable — this is a learning project. Short comments explaining *why*.
- Do not add databases, authentication, Docker, or extra frameworks.
- Stretch goals (week-over-week trends, replies in the review's language, Monday email) are
  NOT in scope unless asked.
