# Agentic SDLC Orchestrator — URL Shortener

A working prototype that turns a requirement into a **reviewable engineering outcome** (code diff,
test evidence, decision lineage, audit trail and release recommendation) using governed, multi-stage
agent execution. The subject system is a production-style URL shortener built in .NET 10.

- **Agents execute** inside a sandbox, under explicit autonomy boundaries.
- **Humans own** clarifications, design sign-off, high-impact changes and release.

See [ARCHITECTURE.md](ARCHITECTURE.md) for design, orchestration model, decisions, risks and limitations.

## What's in the box

| Path | What it is |
|---|---|
| `src/UrlShortener.Core` | Domain: `LinkService`, `UrlValidator`, Base62 codes, result types |
| `src/UrlShortener.Infrastructure` | SQLite (WAL) and in-memory repositories |
| `src/UrlShortener.Api` | Minimal API: create / redirect / stats / delete, rate limiting, API key, problem details |
| `src/Agentic.Orchestration` | Engine: DAG, gates, retries/fallback/rollback, approvals, policies, re-plan, audit, metrics |
| `src/Agentic.Agents` | SDLC agents (requirements → release) + workflow definition |
| `src/Agentic.Cli` | `agentic` command: runs the three scenarios, metrics, audit verification |
| `tests/UrlShortener.Tests` | 61 unit + HTTP integration + SQLite tests |
| `tests/Agentic.Orchestration.Tests` | 20 tests of orchestration behaviour and governance |

## Setup

Prerequisite: **.NET 10 SDK** (installed with Visual Studio 2026, or from https://dotnet.microsoft.com). No Docker or database server needed.

```bash
dotnet build
```

```bash
dotnet test
```

Run the URL shortener itself (SQLite file `urlshortener.db`, OpenAPI at `/openapi/v1.json` in Development):

```bash
dotnet run --project src/UrlShortener.Api
```

```bash
curl -X POST http://localhost:5080/api/links -H "Content-Type: application/json" -d "{\"url\":\"https://example.com\",\"customAlias\":\"demo\"}"
```

Then open `http://localhost:5080/demo` (302 redirect) and `http://localhost:5080/api/links/demo/stats`.

## Running the three scenarios

Each run executes the full SDLC graph against an isolated sandbox, runs the real `dotnet test`, and
writes a reviewable package to `runs/<runId>/`.

```bash
dotnet run --project src/Agentic.Cli -- run greenfield --auto-approve
```

```bash
dotnet run --project src/Agentic.Cli -- run brownfield --auto-approve
```

```bash
dotnet run --project src/Agentic.Cli -- run ambiguous --auto-approve
```

- `--auto-approve` replays a scripted reviewer (recorded as `scripted:*` in the audit log).
  Omit it to approve/answer interactively in the console. With no console attached, every
  high-impact action is **denied** by default.
- `--chaos=<stageId>[:attempt]` injects a transient fault to exercise retry/MTTR, e.g. `--chaos=build-test:1`.
- Safe-stop at any time: press Ctrl+C, or create the `STOP` file whose path is printed at start.

| Scenario | Requirement | What it demonstrates |
|---|---|---|
| **greenfield** | "Build a URL shortener service… must be reliable and secure." | Minor ambiguity → documented assumptions; design sign-off; planner expands the graph at runtime into `scaffold → domain → (persistence ∥ api ∥ tests)`; real build + 61 tests; security ∥ docs; release approval |
| **brownfield** | "INC-1042: expired short links still redirect…" (bug injected into a copy of the codebase) | Codebase analysis ∥ requirements; **baseline test run reproduces the bug**; impact analysis; 1st fix (HTTP edge) fails a unit test → **feedback loop re-opens the implementation stage, rolls back, retries** with the domain-layer fix; high-impact approval |
| **ambiguous** | "Make the shortener safer… heavy traffic… log everything about who clicks." | **Blocking ambiguity → clarifying questions**; naive logging change **denied by PII policy → fallback** to compliant strategy; reviewer **approves design with changes** (60 → 30 req/min) → upstream artifact changes → **re-plan**; already-satisfied requirement (URL safety) verified, not re-built |

### Reviewing a run

```
runs/<runId>/
  report.md         ← start here: final engineering summary for the run
  changes.diff      ← unified diff of everything the agents changed
  audit.jsonl       ← hash-chained audit trail of every event
  decisions.json    ← decision lineage (who/what/why/based-on which artifact versions)
  artifacts/        ← every artifact version + .meta.json (producer, attempt, inputs, hash)
  metrics.json, stages.json, graph.mmd
```

The sandbox code itself is in `%TEMP%\agentic\<runId>` (kept short to avoid Windows path-length limits).

```bash
dotnet run --project src/Agentic.Cli -- verify-audit <runId>
```

```bash
dotnet run --project src/Agentic.Cli -- metrics
```

`metrics` aggregates all runs: run/attempt success rate, retries, rollbacks, fallbacks, MTTR and p50/p95 end-to-end latency.

## Testing approach

| Layer | How it is verified |
|---|---|
| Domain rules | Unit tests with a manual `TimeProvider` and scripted code generator (expiry, collisions, validation, hashing) |
| HTTP contract | `WebApplicationFactory` integration tests over the real pipeline: status codes, headers, problem details, rate limiting, API key |
| Persistence | SQLite repository tests on a temp database (round-trip, constraints, case-sensitivity, aggregation) |
| Orchestration | Fake agents exercise parallelism + sync, retry + MTTR, fallback, safe-stop, policy deny, write-scope, approval reject + rollback, feedback loop, re-plan lineage, kill switch, attempt budget, audit tamper detection |
| End-to-end | The three scenarios run the real agents and the real `dotnet test` inside the sandbox |

## Limitations and trade-offs (summary)

- Agents use a **deterministic reasoning provider** and a **curated change catalog** (golden-path
  blueprints and patches) instead of an LLM. That is a deliberate choice for reproducibility and
  testability. `IReasoningProvider` is the extension point for an LLM, and all governance is
  unchanged by such a swap.
- It runs as a single process, and run state is not resumable after a crash. The audit log and
  artifacts survive for post-mortem.
- The URL shortener is single-node. Its SQLite storage and synchronous click writes are fine for
  the prototype but not for scale.

Full detail is in [ARCHITECTURE.md](ARCHITECTURE.md#7-limitations).
