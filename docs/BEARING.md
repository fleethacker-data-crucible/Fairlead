# Bearing: cited answers

Bearing answers a question from a local folder of documents and shows where each claim came from. It is **off by
default**: nothing changes unless `FAIRLEAD_BEARING_DIR` names a folder *and* a request opts in.

## Turn it on

```powershell
$env:FAIRLEAD_BEARING_DIR = "C:\Users\me\Documents\policies"   # or a line in config\fairlead.env
.\Launch-Fairlead.bat
.\fairlead.cmd bearing                                          # files and passages indexed
.\fairlead.cmd bearing search "how many days of leave"          # what a question would retrieve (no model is called)
```

Per request, send `X-Fairlead-Bearing: cite` (and, optionally, `X-Fairlead-Bearing-Top: 1..8`, default 4) on
`/v1/chat/completions` or `/v1/messages`, with `"stream": false` or `true`:

```powershell
curl http://127.0.0.1:8776/v1/chat/completions -H "Authorization: Bearer fl-..." -H "X-Fairlead-Bearing: cite" `
  -H "Content-Type: application/json" -d '{"model":"local","messages":[{"role":"user","content":"How many days of leave do contractors get?"}]}'
```

Without the header the request is untouched. With the header but no folder the answer is a 400 that says what to change.

## What happens

1. The last user message is searched against an in-memory index of the folder (BM25 over paragraph-sized passages; no
   embedding model and no retrieval service). `.md .markdown .txt .text .csv .log .rst` files up to 1 MB, 2000 files, no
   hidden folders, no links that leave the folder. The index rebuilds when files change.
2. The best passages go to the model as numbered sources, with an instruction to answer only from them, to quote the words it
   relies on and to put `[n]` after each claim. Because excerpts of your documents are now in the prompt, the request is
   treated as private: it is served by local models only (unless privacy routing is switched off in Settings).
3. When nothing in the folder matches, Fairlead answers "I found nothing in the documents that answers this question."
   itself, without calling a model.
4. The answer is checked and annotated. The response keeps its normal shape and gains:

   - `fairlead_citations`: one entry per source the answer cited: `n`, `source` (path in the folder), `location`
     (`lines 12-18`), `heading`, `quote`, `verified` and `link` (a `file:///` link with `#L12-L18`).
     `verified` is true when the quote the model gave occurs word for word in that passage; otherwise `quote` is the passage
     sentence closest to the claim.
   - `fairlead_bearing`: how many sources were retrieved and cited, and any `[n]` the model invented (`unknownSources`).
   - A `Sources:` list appended to the answer text, so a plain client shows the same information.
   - An `X-Fairlead-Bearing: sources=4;cited=2` response header.

Bearing never leaves the machine on its own and stores no document text in the request log; the log records the route
(`bearing` for an answer given without a model) like any other request.

## Streaming

With `"stream": true` the answer streams as usual and the citations follow once it is complete, as one last event: for an OpenAI client a chunk with `"choices": []` carrying `fairlead_citations` and `fairlead_bearing` (before `[DONE]`), for an Anthropic client an `event: fairlead_citations` (before `message_stop`). The `Sources:` text is not appended to a stream; read the event. The response header says `X-Fairlead-Bearing: sources=N;citations=at-end`. When nothing in the folder matches, a streaming caller gets the one-sentence answer as a short stream with an empty citations event.

## Limits

- Plain-text formats only (no PDF, Word or HTML). Matching is by words, not meaning.
- The model's `[n]` markers are checked against what was retrieved, not for whether the claim is true.
