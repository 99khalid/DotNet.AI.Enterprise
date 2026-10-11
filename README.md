# HR Assistant — .NET AI Engineering

An HR assistant built step by step while learning AI engineering in .NET.
Employees ask about leave balances and company policy in Arabic or English; the assistant answers using tools, and turns free-text leave requests into validated, typed objects.

## Features

- **Provider-agnostic AI** via `Microsoft.Extensions.AI` (`IChatClient`) — switch between **Google Gemini** and any **OpenAI-compatible** endpoint (OpenAI, **Ollama** for local models) from configuration only.
- **Function calling** — the model asks for `GetMyLeaveBalance`, `GetMyProfile`, `GetPublicHolidays`, `GetCurrentDate`, `SearchPolicies`; the app runs them. Identity comes from `ICurrentUser`, never from model-supplied parameters.
- **RAG over HR policy documents** — Markdown policies are chunked per article (with a contextual header), embedded via `IEmbeddingGenerator` (Gemini or Ollama `bge-m3`), and searched by cosine similarity. Answers cite their source, e.g. `[سياسة الإجازات › المادة 1: الإجازة السنوية]`.
  - **Two retrieval modes**: `Agentic` (the model decides when to call `SearchPolicies`) and `Always` (classic RAG — the code retrieves on every question; more reliable with small models).
  - **Score threshold** (`MinScore`) so the code itself can say "not found", plus retrieval logging of every candidate and its score.
- **Structured output** — free text → `LeaveRequestDraft` record, then business rules (balance, 7-day notice, past dates) are validated in code.
- **Streaming** responses (Server-Sent Events) with visible tool calls and token usage.
- **Middleware pipeline** — function invocation, logging, and a language-pinning middleware that keeps small models answering in Arabic after tool calls.
- **Two front ends** sharing the same core: a console app and an ASP.NET Core Web API with a test web UI.

## Solution structure

```
DotNet.AI.Enterprise.Domain          Business rules (Employee, LeaveType, LeaveRequestDraft) — no AI
DotNet.AI.Enterprise.Application     HrAssistant, HrTools, Prompts, options, middleware, Rag/ (chunking, indexing, retrieval)
DotNet.AI.Enterprise.Infrastructure  Gemini / OpenAI-compatible clients, vector store, policy documents (Data/Policies), DI wiring
DotNet.AI.Enterprise.ConsoleApp      Console front end
DotNet.AI.Enterprise.WebApi          Minimal API + SSE streaming + test UI (wwwroot)
```

Dependencies point inward: `Presentation → Infrastructure → Application → Domain`.

## Getting started

Requirements: .NET 10 SDK, and either a Gemini API key or [Ollama](https://ollama.com).

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
| `Dimensions` | Optional vector size; null = model default |

`Rag` section:

| Key | Description |
|---|---|
| `DocumentsPath` | Folder of policy `.md` files (one `## ` heading per chunk) |
| `TopK` | Chunks returned per search |
| `Mode` | `Agentic` or `Always` |
| `MinScore` | Cosine-similarity cutoff. **Model-specific** — calibrate it from the retrieval log when changing the embedding model |

The `Ollama` launch profile runs fully local: chat `qwen2.5:3b`, embeddings `bge-m3`, `Mode = Always`, `MinScore = 0.40`.

## Roadmap

- [x] LLM fundamentals: prompts, tokens, temperature, structured output, function calling
- [x] AI + .NET: `Microsoft.Extensions.AI`, ASP.NET Core, streaming
- [x] Embeddings + in-memory vector store
- [x] RAG over company policy documents (with citations)
- [ ] Persistent vector database (pgvector / Qdrant)
- [ ] Agents with Microsoft Agent Framework
- [ ] MCP server
- [ ] Advanced RAG (query rewriting, hybrid search, reranking) + evaluation
- [ ] Production: auth, persistence, Docker, observability

## Notes

- Demo data and a fixed demo user (`DemoCurrentUser`) stand in for a real database and authentication.
- The vector store is in memory: documents are re-embedded on every startup.
- Check each model's license before commercial use (e.g. `qwen2.5:3b` is research/non-commercial only).
