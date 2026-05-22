# RAG over the architecture book corpus

A curated corpus of software-architecture books, indexed into Qdrant and exposed to the researcher agent as `SearchArchitectureCorpus`. The researcher prefers it over Tavily web search for established concepts (patterns, trade-offs, DDD, microservices) and falls back to the web for recent developments and named products.

Two pieces:
- **Indexer** (`tools/AgentScope.Indexer/`) — one-off console app. PDF → chunks → embeddings → Qdrant.
- **Plugin** (`src/AgentScope.Infrastructure/Plugins/ArchitectureSearchPlugin.cs`) — SK function the researcher calls at runtime.

## Prerequisites

- Qdrant running (the existing `docker-compose.yml` covers this).
- The PDF books on disk in one folder. Personal-use only — only index books you legally own.

## One-time setup

1. **Set the indexer's API key**:
   ```powershell
   dotnet user-secrets set "AgentScope:OpenAi:ApiKey" "sk-..." --project tools/AgentScope.Indexer
   ```

2. **Configure the corpus.** Edit `tools/AgentScope.Indexer/appsettings.json` (or override via user secrets):
   ```json
   "ArchitectureCorpus": {
     "Collection": "agentscope-arch-corpus",
     "BooksDirectory": "C:\\EP\\Books",
     "Books": [
       "Software Architecture - The Hard Parts.pdf",
       "Microservices Patterns - Chris Richardson.pdf",
       "Domain-Driven Design Distilled - Vaughn Vernon.pdf",
       "Software Architecture Patterns.pdf",
       "buildingevolutionaryarchitectures2ndedition.pdf"
     ]
   }
   ```
   Listing files explicitly (rather than globbing the directory) keeps the index deterministic and lets you drop other PDFs into the same folder without polluting the corpus.

3. **Run the indexer** (~2–3 min for ~5 books, ~$0.02 in embedding cost):
   ```powershell
   dotnet run --project tools/AgentScope.Indexer
   ```
   Console output per book:
   ```
   Reading Software Architecture - The Hard Parts.pdf
     412 pages → 587 chunks
     upserted batch 1 (64 chunks)
     ...
     done in 38.2s
   ```

   The indexer drops and recreates the collection on every run, so re-running it is the way to refresh. To add a new book, append to the `Books` array and re-run.

## Enabling the plugin in the web app

The plugin is **off by default** even after indexing — toggle it on explicitly so an empty/missing index can't silently degrade answers.

Add to `src/AgentScope.Web/appsettings.Development.json`:

```json
"AgentScope": {
  ...,
  "ArchitectureCorpus": {
    "Enabled": true,
    "Collection": "agentscope-arch-corpus"
  }
}
```

(The web app only needs `Enabled` and `Collection`; `BooksDirectory`/`Books` are read by the indexer only.)

Restart the web app. The researcher's kernel now has `SearchArchitectureCorpus` alongside `WebSearch` and `BookLookup`.

## What to expect at runtime

For architecture questions like *"What are the trade-offs of orchestration vs choreography in microservices?"*, watch the event log:

1. `tool.called` — `ArchitectureCorpus.SearchArchitectureCorpus` with the query
2. `tool.result` — the formatted chunks (book name, page range, score, text)
3. The synthesizer's final answer includes citations like `(Microservices Patterns - Chris Richardson, pp. 124-128)`

For non-architecture questions (latest .NET features, specific products, news), the researcher should still pick `WebSearch`.

## How to tell if RAG is actually helping

Use the [eval harness](./evals.md) to compare with/without RAG on architecture questions:

```powershell
# baseline — Enabled=false in appsettings, restart web/eval
dotnet run --project tests/AgentScope.Evals -- --variant no-rag

# with RAG — Enabled=true, restart, re-run
dotnet run --project tests/AgentScope.Evals -- --variant with-rag
```

Compare judge scores and cost at `/past-runs`. If the corpus doesn't lift quality, either your questions don't need the books (Tavily covers them) or the chunks aren't finding the right context.

## Known limitations

1. **Image-only PDFs yield nothing.** PdfPig extracts text only; scanned books without an OCR layer produce zero chunks. The indexer logs a warning and skips.

2. **Naive sliding-window chunking.** Flat character-based chunks (~700 tokens / 100-token overlap) with no section/sentence awareness. Good enough for prose; can split a code example or table awkwardly. Easy follow-on: respect chapter headings.

3. **No incremental re-index.** Always drops + rebuilds the collection. To add one book you re-index everything (~3 min). Fine for a 5-book corpus, slower if you grow it.

4. **Toggle is app-restart only.** `Enabled` is read at startup; flipping it requires restarting the web app or the eval CLI.

5. **No de-duplication across books.** If two books cover the same concept, both chunks come back — the LLM has to reconcile. In practice this is fine and often desirable (multiple framings of the same idea).

## Troubleshooting

**Researcher never calls `SearchArchitectureCorpus`**
Confirm `Enabled = true` in the appsettings the web app actually reads (Development override usually). Restart the app. Then ask a question that's obviously architecture-flavored — *"What does Vaughn Vernon mean by bounded context?"* — and watch the event log for the tool call.

**`No matches in the architecture corpus`**
Either the collection is empty (re-run the indexer) or the query is too far from anything indexed (rephrase, or that topic isn't in your books — use WebSearch).

**Indexer fails with `Qdrant.Client.Grpc.RpcException`**
Qdrant isn't reachable on the configured host/port. Check `docker compose ps` and that `AgentScope:Qdrant:Host` and `:Port` match the running container.

**Indexer extracts zero pages from a book**
That PDF is image-only (a scan without OCR). Either find a text-bearing copy or add an OCR step (out of scope for v1).
