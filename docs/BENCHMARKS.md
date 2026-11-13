# Benchmarks

These are figures from one machine, to show the scale Cabinet works at and how the backends compare. They aren't a promise for your hardware or your logs. To measure your own:

```bash
dotnet run -c Release --project benchmarks/Cabinet.Benchmarks -- cabinet 1000000
dotnet run -c Release --project benchmarks/Cabinet.Benchmarks -- sqlite 1000000
dotnet run -c Release --project benchmarks/Cabinet.Benchmarks -- postgres 1000000 "Host=...;Database=...;Username=...;Password=..."
dotnet run -c Release --project benchmarks/Cabinet.Benchmarks -- opensearch 1000000 http://localhost:9200
```

The tool generates logfmt lines spread over 7 days, parses them with the real parsers, stores them in batches of 1,000 from one writer, and runs each query through the real engine three times, reporting the median. It calls the engine directly, so the figures leave out HTTP and authentication.

## Setup

| | |
|---|---|
| CPU | AMD Ryzen 7 2700X, 8 cores and 16 threads |
| Memory | 64 GB |
| OS | Windows 10, .NET 10 in Release mode with server GC |
| PostgreSQL | 16, default settings, in Docker Desktop |
| OpenSearch | 2.19, one node, 512 MB heap, in Docker Desktop |
| Events | 1,000,000 |

The machine was also running other work, and Docker Desktop adds its own overhead, so a dedicated server should do better.

## Ingest

| Backend | Parse and store, one writer |
|---|---:|
| In-memory | 39,988 events/s |
| Cabinet engine | 30,954 events/s |
| OpenSearch | 7,308 events/s |
| PostgreSQL | 6,125 events/s |
| SQLite | 3,798 events/s |

Parsing alone runs at 25,000 to 42,000 lines a second on one thread. The server parses each request independently, so parsing scales with cores. Storage is the limit for PostgreSQL, OpenSearch and SQLite here, mostly because of index maintenance. For the Cabinet engine, parsing is the limit: storing already-parsed events from one writer ran at 120,649 events a second, each batch on disk (fsync) before it was acknowledged, including writing the compressed segments.

## Queries

| Query | Cabinet engine | SQLite | PostgreSQL | OpenSearch | In-memory |
|---|---:|---:|---:|---:|---:|
| Newest 100 | 2.8 ms | 3.1 ms | 11 ms | 36 ms | 331 ms |
| Newest 100 on one host | 11 ms | 3.9 ms | 14 ms | 49 ms | 260 ms |
| Text search for a rare word, first 100 | 217 ms | 288 ms | 11 ms | 27 ms | 538 ms |
| Errors and worse, first 100 | 15 ms | 10 ms | 12 ms | 24 ms | 173 ms |
| Count everything | 0.1 ms | 144 ms | 44 ms | 19 ms | 156 ms |
| Last hour: count by host | 3.1 ms | 27 ms | 7.4 ms | 29 ms | 212 ms |
| All events: count by host | 107 ms | 4.6 s | 292 ms | 17 ms | 37 s |
| All events: hourly timechart | 34 ms | 3.0 s | 720 ms | 29 ms | 30 s |
| All events: avg and p95 by host | 201 ms | 14 s | 3.8 s | 30 s | 32 s |
| All events: top 10 paths | 144 ms | 13 s | 15 s | 33 s | 27 s |

The Cabinet engine is Cabinet's own storage (`Storage:Type=Cabinet`): a write-ahead log, then compressed column segments with summaries and word and ID filters per block of 4,096 events. It counts blocks every event of which matches from their summaries, without reading them, and reads the rest in parallel. The rare word appears in about two blocks in five, so finding 100 of them reads most of the text. The engine searches the stored UTF-8 bytes in parallel without decoding lines, and decodes only the lines it returns; PostgreSQL and OpenSearch answer it from a full-text index.

In-memory storage sorts every match on each read, so it's for tests rather than volume. The in-memory and OpenSearch `avg and p95` and `top` figures are from the run before aggregates moved into storage, since neither is pushed down there.

### Aggregates in the database

`stats` and `timechart` run inside the database when that gives the same answer (see ARCHITECTURE.md, "Aggregate Pushdown"). Before, every aggregate read all its events through the API:

| All events, 1,000,000 | Before: SQLite | Now: SQLite | Before: PostgreSQL | Now: PostgreSQL | Before: OpenSearch | Now: OpenSearch |
|---|---:|---:|---:|---:|---:|---:|
| Count by host | 13 s | 4.6 s | 13 s | 292 ms | 33 s | 17 ms |
| Hourly timechart | 13 s | 3.0 s | 13 s | 720 ms | 28 s | 29 ms |
| Avg and p95 of a JSON field by host | 13 s | 14 s | 13 s | 3.8 s | 30 s | 30 s |

SQLite has no percentiles, so the last query still runs in the pipeline there, and OpenSearch pushes down counts only. `top` and `rare` count each value as .NET writes it as text, which SQL doesn't always match, so only the Cabinet engine computes them; elsewhere `stats count by path | sort -count | head 10` gives the same rows and is pushed down.

### Ten million events, Cabinet engine

| Query | 10,000,000 events |
|---|---:|
| Newest 100 | 1.3 ms |
| Newest 100 on one host | 5.2 ms |
| Text search for a rare word, first 100 | 780 ms |
| Errors and worse, first 100 | 15 ms |
| Count everything | 0.4 ms |
| Last hour: count by host | 15 ms |
| All events: count by host | 879 ms |
| All events: hourly timechart | 34 ms |
| All events: avg and p95 by host | 2.1 s |

Searches for the newest matches don't depend on how much is stored, and aggregates grow linearly, at about 11 million events a second for a count by host on this machine. The files took 493 MB. This run predates `top` in the engine. Storing alone ran at 86,676 events a second, lower than at a million because segments were being merged meanwhile. Peak memory was 1.7 GB.

## Memory

| Backend | Peak process memory, ingest and queries |
|---|---:|
| Cabinet engine | 698 MB, mostly the 100,000 newest events it holds before writing a segment |
| SQLite | 316 MB |
| OpenSearch | 316 MB |
| PostgreSQL | 360 MB |
| In-memory | 4,255 MB, which is the events themselves |

The important result is that aggregates over a million events used no more memory than the first page of a search, because they hold what they compute rather than the rows. Before this work, a query read at most 100,000 events and held them all.

The SQLite database for a million events was 838 MB, about 840 bytes an event with indexes. The Cabinet engine's files for the same events were 50 MB, about 52 bytes an event, filters included.

## Soak

`tests/EndToEnd/soak.sh 30m 1000 300` ran two API instances on one PostgreSQL database for 30 minutes, at 1,000 events a second in batches of 200 from several writers, with queries running alongside. Every five minutes one instance was killed with `kill -9` and restarted 15 seconds later. Clients retried on the other instance, as they would behind a load balancer.

| | |
|---|---:|
| Events acknowledged | 1,800,000 |
| Events stored | 1,800,000, each once |
| Instances killed | 5 |
| Ingest requests | 9,018, of which 18 got no answer from a dying instance and were retried |
| Ingest latency, p50 / p95 / p99 | 183 ms / 846 ms / 1.6 s |
| Queries | 1,631, none failed; 50 were retried on the other instance |
| Query latency, p50 / p95 / p99 | 292 ms / 18 s / 56 s |
| Queries over the 60-second limit | 13, all the one-hour average and percentile by path |
| Memory per instance | 93 to 522 MB |

Nothing was lost or stored twice, and leadership moved each time its holder died. What this doesn't show:

- **Days of uptime.** Thirty minutes with restarts every five can't rule out slow leaks. One instance's memory rose at about 116 MB an hour after warm-up, the other's at 17 MB. Run the Soak workflow for longer before relying on long uptimes.
- **Heavy queries under ingest.** The one-hour percentile query reads about 3.6 million events while they're being written, and sometimes needed more than 60 seconds. Use a search job for queries like it.
- **More than two instances, or more than one database.**

## What this means

- **Finding things stays fast as data grows.** Reads for the newest matches use an index on time and ID, so they don't depend on how much is stored.
- **Aggregates the database can compute scale with the database.** On PostgreSQL, counting a million events by host takes 0.3 s and a JSON-field average and percentile 3.8 s; OpenSearch counts in tens of milliseconds.
- **Aggregates that run in the pipeline are linear in events read**, at about 80,000 events a second on PostgreSQL and SQLite, and 30,000 on OpenSearch: `top`, `rare`, anything after `eval` or `rex`, percentiles on SQLite, and everything but counts on OpenSearch. A time range keeps them quick: the 60-second limit on `/query/execute` covers about 5 million events, and a 10-minute search job about 50 million. Data models can pre-aggregate beyond that.
