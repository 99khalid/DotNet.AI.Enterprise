# HR Assistant — .NET AI Engineering

An HR assistant built step by step while learning AI engineering in .NET.
Employees ask about leave balances and company policy in Arabic or English; the assistant answers using tools, and turns free-text leave requests into validated, typed objects.

## Features

- **Provider-agnostic AI** via `Microsoft.Extensions.AI` (`IChatClient`) — switch between **Google Gemini** and any **OpenAI-compatible** endpoint (OpenAI, **Ollama** for local models) from configuration only.
- **Function calling** — the model asks for `GetMyLeaveBalance`, `GetMyProfile`, `GetLeavePolicy`, `GetPublicHolidays`, `GetCurrentDate`; the app runs them. Identity comes from `ICurrentUser`, never from model-supplied parameters.
- **Structured output** — free text → `LeaveRequestDraft` record, then business rules (balance, 7-day notice, past dates) are validated in code.
- **Streaming** responses (Server-Sent Events) with visible tool calls and token usage.
- **Middleware pipeline** — function invocation, logging, and a language-pinning middleware that keeps small models answering in Arabic after tool calls.
- **Two front ends** sharing the same core: a console app and an ASP.NET Core Web API with a test web UI.

## Solution structure

```
DotNet.AI.Enterprise.Domain          Business rules (Employee, LeaveType, LeaveRequestDraft) — no AI
DotNet.AI.Enterprise.Application     HrAssistant, HrTools, Prompts, AiOptions, middleware
DotNet.AI.Enterprise.Infrastructure  Gemini / OpenAI-compatible clients, data access, DI wiring
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

## Roadmap

- [x] LLM fundamentals: prompts, tokens, temperature, structured output, function calling
- [x] AI + .NET: `Microsoft.Extensions.AI`, ASP.NET Core, streaming
- [ ] Embeddings + vector database
- [ ] RAG over company policy documents (with citations)
- [ ] Agents with Microsoft Agent Framework
- [ ] MCP server
- [ ] Advanced RAG + evaluation
- [ ] Production: auth, persistence, Docker, observability

## Notes

- Demo data and a fixed demo user (`DemoCurrentUser`) stand in for a real database and authentication.
- Check each model's license before commercial use (e.g. `qwen2.5:3b` is research/non-commercial only).
