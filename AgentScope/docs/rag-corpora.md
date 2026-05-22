# RAG over curated book corpora

Two curated corpora of books, indexed into Qdrant and exposed to the researcher as separate kernel plugins. The researcher picks the most specific corpus per sub-question, falling back to Tavily web search when nothing fits.

| Corpus | Plugin name (LLM sees) | Qdrant collection | Books |
|---|---|---|---|
| **Architecture** | `ArchitectureCorpus.Search` | `agentscope-arch-corpus` | 8 books (Hard Parts, Microservices Patterns, DDD Distilled, Software Architecture Patterns, Evolutionary Architectures 2nd, Event-Driven Microservices, Event-Driven Data Mesh, Monolith to Microservices) |
| **System Design** | `SystemDesignCorpus.Search` | `agentscope-system-design-corpus` | 2 books (ByteByteGo, Alex Xu *System Design Interview*) |

Two pieces:
- **Indexer** (`tools/AgentScope.Indexer/`) — one-off console app. Reads `AgentScope:Corpora[]` from config, indexes each enabled corpus into its own Qdrant collection.
- **Plugin** (`src/AgentScope.Infrastructure/Plugins/CorpusSearchPlugin.cs`) — generic SK function parameterised by `CorpusOptions`. The web app instantiates one per enabled corpus at startup.

## What is RAG, and what does it buy us

**Retrieval-Augmented Generation** = before the LLM answers, grab relevant chunks from a corpus you control and stuff them into the prompt. The model answers from those chunks instead of from its training-data memory. It exists because LLMs (a) don't know your private data, (b) are months stale, (c) hallucinate when asked outside their knowledge.

In AgentScope, the researcher agent picks per sub-question which tool to call — `ArchitectureCorpus.Search`, `SystemDesignCorpus.Search`, `WebSearch`, or `BookLookup`. Three parallel researchers can each pick differently. The prompt nudges it toward the most specific corpus when one fits.

### What we actually gain

1. **Authority.** Kleppmann / Vernon / Fowler / Alex Xu beats blog post #47. For *"what does Vernon mean by bounded context?"* the corpus returns Vernon's actual definition, not a paraphrase.
2. **Citation traceability.** Answers include `(Microservices Patterns - Chris Richardson, pp. 124-128)` — verifiable, and book citations don't rot the way URLs do.
3. **Trade-off depth.** Book chapters carry nuance that 200-token web snippets can't. Most architecture / system-design questions are "X vs Y, when and why" — exactly the shape that wants long-form context.
4. **Determinism.** Same question retrieves the same chunks. Easier to debug answer quality than chasing whatever Tavily ranked highest today.
5. **Privacy / offline.** The corpora live in your local Qdrant. Architecture/system-design questions don't leak to a third-party search API.

### What it costs

- **More LLM input tokens.** Bigger chunks → bigger prompts. Observed in practice: ~50% cost bump per question vs Tavily-only ($0.0024 vs $0.0016 on a sample question). Tavily snippets are condensed; book chunks aren't.
- **Maintenance.** Adding/removing a book requires re-indexing that corpus (~30 s/book for embeddings).
- **No real-time data.** RAG only knows what you indexed — recent product news, new framework releases still need WebSearch.
- **Quality depends on chunking + `topK`.** A bad chunker (splitting mid-sentence, swallowing code blocks) or wrong `topK` gives the LLM noisy context and answers degrade even though the corpus is "good."

### When each corpus earns its keep

| Question type | Recommended tool |
|---|---|
| Architecture concepts, pattern definitions, DDD terminology, microservices design, event-driven patterns | `ArchitectureCorpus.Search` |
| Sharding, replication, queueing, caching, capacity estimation, system-design-interview-style design questions | `SystemDesignCorpus.Search` |
| Recent product news, framework versions, ops/runbook questions | `WebSearch.Search` |
| Question mentions a specific book by name and you need its TOC/summary | `BookLookup.GetBookMetadata` |
| Anything not in the indexed books and not time-sensitive | `WebSearch.Search` |

**The honest test isn't "is RAG cheaper" — it's "do judge scores go up by enough to justify the cost?"** Use the eval harness ([docs/evals.md](./evals.md)) with corpora enabled vs disabled and compare mean judge scores at `/past-runs`.

## Prerequisites

- Qdrant running (the existing `docker-compose.yml` covers this).
- The PDF books on disk in one folder. Personal-use only — only index books you legally own.

## One-time setup

1. **Set the indexer's API key**:
   ```powershell
   dotnet user-secrets set "AgentScope:OpenAi:ApiKey" "sk-..." --project tools/AgentScope.Indexer
   ```

2. **Verify the corpora config** in `tools/AgentScope.Indexer/appsettings.json`. The default ships with two corpora — architecture (8 books) and system-design (2 books). Edit `BooksDirectory`, add/remove `Books[]` entries, or set `Enabled: false` on either corpus to skip it.

3. **Run the indexer** (~3–5 min for ~10 books, well under $0.05 in embedding cost):
   ```powershell
   dotnet run --project tools/AgentScope.Indexer
   ```
   Console output per corpus + per book:
   ```
   === Corpus 'architecture' → collection agentscope-arch-corpus (8 book(s)) ===
   Dropping existing collection agentscope-arch-corpus
   Reading Software Architecture - The Hard Parts.pdf
     412 pages → 587 chunks
     upserted batch 1 (64 chunks)
     ...
     done in 38.2s
   ...
   Corpus 'architecture' total: 2104 chunks
   === Corpus 'system-design' → collection agentscope-system-design-corpus (2 book(s)) ===
   ...
   Indexed 2547 chunks across 2 corpus(es) in 218.4s
   ```

   The indexer drops and recreates each enabled corpus's collection on every run, so re-running it is the way to refresh.

## Enabling the corpora in the web app

The web app reads `AgentScope:Corpora[]` from its own config — each entry needs `Enabled: true`, `Collection`, `PluginName`, and `Description` (the description is what the LLM sees when deciding which tool to call). `BooksDirectory` and `Books[]` are indexer-only fields and can be omitted from the web app's config.

Add to `src/AgentScope.Web/appsettings.Development.json`:

```json
"AgentScope": {
  ...,
  "Corpora": [
    {
      "Name": "architecture",
      "Enabled": true,
      "Collection": "agentscope-arch-corpus",
      "PluginName": "ArchitectureCorpus",
      "Description": "Search a curated corpus of software architecture books... (paste the full description from tools/AgentScope.Indexer/appsettings.json)"
    },
    {
      "Name": "system-design",
      "Enabled": true,
      "Collection": "agentscope-system-design-corpus",
      "PluginName": "SystemDesignCorpus",
      "Description": "Search a curated corpus of system design books... (paste the full description from tools/AgentScope.Indexer/appsettings.json)"
    }
  ]
}
```

Restart the web app. The researcher's kernel now has `ArchitectureCorpus.Search` and `SystemDesignCorpus.Search` alongside `WebSearch` and `BookLookup`.

> **Tip:** keep the descriptions in sync between the indexer's appsettings and the web app's appsettings. The description is the dominant signal in the LLM's tool-selection decision — vague descriptions = vague choices.

## What to expect at runtime

For an architecture question like *"What are the trade-offs of orchestration vs choreography in microservices?"*:
1. `tool.called` — `ArchitectureCorpus.Search` with the query
2. `tool.result` — formatted chunks (book name, page range, score, text)
3. Synthesizer's final answer cites `(Microservices Patterns - Chris Richardson, pp. 124-128)`

For a system-design question like *"How would you design a URL shortener that handles 100M URLs/day?"*:
1. `tool.called` — `SystemDesignCorpus.Search`
2. `tool.result` — chunks from ByteByteGo / Alex Xu
3. Citation: `(System Design - ByteByteGo, pp. 87-92)`

For a non-corpus question (latest .NET features, specific products, news), the researcher should still pick `WebSearch`.

## How to tell if RAG is actually helping

Use the [eval harness](./evals.md) with one variant per corpus configuration:

```powershell
# baseline — all corpora Enabled=false in eval appsettings, restart
dotnet run --project tests/AgentScope.Evals -- --variant tavily-only

# with corpora — flip both Enabled=true, restart, re-run
dotnet run --project tests/AgentScope.Evals -- --variant with-corpora
```

Compare judge scores and cost at `/past-runs`. If a corpus doesn't lift quality, either your questions don't need those books or the chunks aren't finding the right context.

## Known limitations

1. **Image-only PDFs yield nothing.** PdfPig extracts text only; scanned books without an OCR layer produce zero chunks. The indexer logs a warning and skips.

2. **Naive sliding-window chunking.** Flat character-based chunks (~700 tokens / 100-token overlap) with no section/sentence awareness. Good enough for prose; can split a code example or table awkwardly. Easy follow-on: respect chapter headings.

3. **No incremental re-index.** Always drops + rebuilds each enabled corpus. To add one book you re-index that whole corpus.

4. **Toggle is app-restart only.** `Enabled` is read at startup; flipping it requires restarting the web app or the eval CLI.

5. **No de-duplication across books or corpora.** If two books (or two corpora) cover the same concept, multiple chunks come back — the LLM has to reconcile. Usually fine and often desirable.

6. **Description drift.** The indexer's appsettings carries the canonical corpus description, but the web app needs its own copy. If they diverge, the LLM's tool choice may not match what was indexed. Lock them together with shared config (e.g. symlink, copy on indexer run) if this becomes painful.

## Troubleshooting

**Researcher never calls a corpus tool**
Confirm both corpora have `Enabled = true` in the appsettings the web app actually reads (Development override usually). Restart the app. Ask an obviously architecture-flavored question (*"What does Vaughn Vernon mean by bounded context?"*) and watch the event log.

**Researcher calls the wrong corpus**
The description is the signal. Sharpen the `Description` field in appsettings to be more specific about what the corpus covers and when *not* to use it. Restart the web app and try again.

**`No matches in the {name} corpus`**
Either the collection is empty (re-run the indexer) or the query is too far from anything indexed (rephrase, or it's not in your books — use WebSearch).

**Indexer fails with `Qdrant.Client.Grpc.RpcException`**
Qdrant isn't reachable on the configured host/port. Check `docker compose ps` and that `AgentScope:Qdrant:Host` and `:Port` match the running container.

**Indexer extracts zero pages from a book**
That PDF is image-only (a scan without OCR). Either find a text-bearing copy or add an OCR step (out of scope).
