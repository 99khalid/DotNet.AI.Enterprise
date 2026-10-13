# HR Assistant — .NET AI Engineering

An HR assistant built step by step while learning AI engineering in .NET.
Employees ask about leave balances and company policy in Arabic or English. A router sends each question to the right specialist: a profile agent with tools, a policy agent grounded in documents, or a leave-request workflow with human approval.

## Features

- **Provider-agnostic AI** via `Microsoft.Extensions.AI` (`IChatClient`) — switch between **Google Gemini** and any **OpenAI-compatible** endpoint (OpenAI, **Ollama** for local models) from configuration only.
- **Agent built with Microsoft Agent Framework** — `ChatClientAgent` on top of the same `IChatClient`: instructions, tools, a per-conversation `AgentSession`, chat history with a message-count reducer, and classic RAG as an `AIContextProvider`.
- **Multi-agent with a triage router** — `IntentRouter` classifies each message as `Profile`, `Policy`, `LeaveRequest` or `Other`, and the right specialist handles it:

  ```
  message → IntentRouter ─┬─ Profile      → ProfileAgent (read-only tools: balance, profile, holidays, date)
                          ├─ Policy       → PolicyAgent  (RAG, no tools that change data)
                          ├─ LeaveRequest → leave workflow (below)
                          └─ Other        → fixed reply, no model call
  ```

  - **Hybrid routing** — embeddings first (the message vs. example phrases per intent, no LLM call); only when that isn't confident (low score or two intents too close) does it fall back to the LLM with structured output (`enum Intent`).
  - **Multi-intent messages** — "I want leave next week, and also what's the remote-work policy?" is split into parts, each routed to its own specialist and answered in order. Splitting is conservative: only when every part is routed confidently and the intents differ.
  - Each agent has its own short instructions, its own tools and its own history (session) — smaller prompts and fewer tool mix-ups with small models.
  - **Privacy guard** — a question naming another employee ("what's Sara's balance?") gets a fixed reply from code and never reaches the model.
  - The UI and console show the route for every message (intent, method, score).
- **Function calling** — the model asks for `GetMyLeaveBalance`, `GetMyProfile`, `GetPublicHolidays`, `GetCurrentDate`, `SearchPolicies`; the app runs them. Identity comes from `ICurrentUser`, never from model-supplied parameters.
- **RAG over HR policy documents** — Markdown policies are chunked per article (with a contextual header), embedded via `IEmbeddingGenerator` (Gemini or Ollama `bge-m3`), and searched by cosine similarity. Answers cite their source, e.g. `[سياسة الإجازات › المادة 1: الإجازة السنوية]`.
  - **Two retrieval modes**: `Agentic` (the model decides when to call `SearchPolicies`) and `Always` (classic RAG — `PolicyContextProvider` retrieves on every question and adds the excerpts to the agent's instructions; more reliable with small models).
  - **Score threshold** (`MinScore`) so the code itself can say "not found", plus retrieval logging of every candidate and its score.
- **Persistent vector store** — PostgreSQL + **pgvector** through `Microsoft.Extensions.VectorData` (`CommunityToolkit.VectorData.PgVector` connector), with an HNSW index. Swappable with the in-memory store from configuration; the Application layer doesn't change.
  - **Incremental indexing** — each chunk stores a SHA-256 hash of its text; on startup only new or changed chunks are embedded, and removed ones are deleted.
  - **One table per embedding model and size** (e.g. `policy_chunks_bge_m3_1024`), so vectors from different models are never mixed.
- **Structured output** — free text → `LeaveRequestDraft` record, then business rules (balance, 7-day notice, past dates) are validated in code.
- **Leave request workflow with human-in-the-loop approval** (Microsoft Agent Framework Workflows) — leave requests typed in the chat are routed here; no agent has a tool that changes data. The code fixes the path and the model does only the language step:
  `Extract (model) → Validate (code) → NeedsInfo | Enrich (code) → ⏸ Approval (human) → Submit (code)`.
  - Conditional edges route invalid requests to `NeedsInfo`; a `RequestPort` pauses the run until the employee approves, and the decision resumes the same run.
  - The approval card shows balance before/after, public holidays in the range and the relevant policy — all computed by code.
  - Approvals come from a button, never from chat text, and a run can be decided once (`409` on reuse). Business rules are re-validated before submitting.
  - One model call per request instead of several, and no invented fields.
- **Streaming** responses (Server-Sent Events) with visible tool calls and token usage.
- **Middleware pipeline** — function invocation, logging, and a language-pinning middleware that keeps small models answering in Arabic after tool calls.
- **Two front ends** sharing the same core: a console app and an ASP.NET Core Web API with a test web UI.

## Solution structure

```
DotNet.AI.Enterprise.Domain          Business rules (Employee, LeaveType, LeaveRequestDraft) — no AI
DotNet.AI.Enterprise.Application     HrAssistant (specialist agents), HrTools, Prompts, options, middleware,
                                     Routing/ (intent router, privacy guard), Leave/ (extractor, rules, workflow),
                                     Rag/ (chunking, indexing, retrieval, context provider)
DotNet.AI.Enterprise.Infrastructure  Gemini / OpenAI-compatible clients, vector stores (in-memory, pgvector), policy documents (Data/Policies), DI wiring
DotNet.AI.Enterprise.ConsoleApp      Console front end
DotNet.AI.Enterprise.WebApi          Minimal API + SSE streaming + test UI (wwwroot)
```

Dependencies point inward: `Presentation → Infrastructure → Application → Domain`.

## Getting started

Requirements: .NET 10 SDK, Docker, and either a Gemini API key or [Ollama](https://ollama.com).

### PostgreSQL + pgvector

```bash
docker run -d --name hr-postgres -e POSTGRES_PASSWORD=dev-password -e POSTGRES_DB=hr -p 5432:5432 -v hr-pgdata:/var/lib/postgresql/data pgvector/pgvector:pg17
dotnet user-secrets set "ConnectionStrings:Policies" "Host=localhost;Port=5432;Database=hr;Username=postgres;Password=dev-password" --project DotNet.AI.Enterprise.WebApi
```

The table and its HNSW index are created on first run. To run without Docker, set `Rag:Store` to `InMemory`.

### Gemini

```bash
dotnet user-secrets set "AI:ApiKey" "<your-key>" --project DotNet.AI.Enterprise.WebApi
dotnet run --project DotNet.AI.Enterprise.WebApi --launch-profile Gemini
```

### Ollama (local, free)

```bash
ollama pull qwen2.5:3b
ollama pull bge-m3
dotnet run --project DotNet.AI.Enterprise.WebApi --launch-profile Ollama
```

Open http://localhost:5097.

The console app reads the same settings:

```bash
dotnet run --project DotNet.AI.Enterprise.ConsoleApp
```

## Configuration

`appsettings.json` → `AI` section:

| Key | Description |
|---|---|
| `Provider` | `Gemini` or `OpenAICompatible` |
| `Model` | e.g. `gemini-3.8-flash`, `qwen2.5:3b` |
| `ApiKey` | **Never in appsettings** — User Secrets (dev) or `AI__ApiKey` env var (prod) |
| `Endpoint` | Required for `OpenAICompatible`, e.g. `http://localhost:11434/v1` |
| `TimeoutSeconds` | Request timeout |
| `MaxHistoryMessages` | Chat history kept per conversation (bounds context size and cost) |

`Embedding` section — the embedding model, independent from the chat model:

| Key | Description |
|---|---|
| `Provider` / `Model` / `Endpoint` | Same meaning as above, e.g. `gemini-embedding-001` or `bge-m3` |
| `ApiKey` | Optional — falls back to `AI:ApiKey` |
| `Dimensions` | Vector size. **Required for the Postgres store** — e.g. `1024` for `bge-m3`, `1536` for `gemini-embedding-001` (pgvector's HNSW index supports up to 2000) |

`Rag` section:

| Key | Description |
|---|---|
| `DocumentsPath` | Folder of policy `.md` files (one `## ` heading per chunk) |
| `TopK` | Chunks returned per search |
| `Mode` | `Agentic` or `Always` |
| `MinScore` | Cosine-similarity cutoff. **Model-specific** — calibrate it from the retrieval log when changing the embedding model |
| `Store` | `Postgres` (needs `ConnectionStrings:Policies` and `Embedding:Dimensions`) or `InMemory` |

The `Ollama` launch profile runs fully local: chat `qwen2.5:3b`, embeddings `bge-m3` (1024 dimensions), `Mode = Always`, `MinScore = 0.40`.

## Roadmap

- [x] LLM fundamentals: prompts, tokens, temperature, structured output, function calling
- [x] AI + .NET: `Microsoft.Extensions.AI`, ASP.NET Core, streaming
- [x] Embeddings + in-memory vector store
- [x] RAG over company policy documents (with citations)
- [x] Persistent vector database (PostgreSQL + pgvector, incremental indexing)
- [x] Agent with Microsoft Agent Framework (session, history reducer, RAG context provider)
- [x] Human-in-the-loop tool approval (`SubmitLeaveRequest`)
- [x] Leave request workflow (extract → validate → enrich → approve → submit)
- [x] Multi-agent with a triage router (policy / profile / leave), multi-intent messages
- [ ] MCP server
- [ ] Advanced RAG (query rewriting, hybrid search, reranking) + evaluation
- [ ] Production: auth, persistence, Docker, observability

## Notes

- Demo data and a fixed demo user (`DemoCurrentUser`) stand in for a real database and authentication.
- Check each model's license before commercial use (e.g. `qwen2.5:3b` is research/non-commercial only).
