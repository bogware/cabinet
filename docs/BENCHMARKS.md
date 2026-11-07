# Benchmarks

These are figures from one machine, to show the scale Cabinet works at and how the backends compare. They aren't a promise for your hardware or your logs. To measure your own:

```bash
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
| OpenSearch | 7,308 events/s |
| PostgreSQL | 6,125 events/s |
| SQLite | 3,798 events/s |

Parsing alone runs at 25,000 to 39,000 lines a second on one thread. The server parses each request independently, so parsing scales with cores. Storage is the limit for PostgreSQL, OpenSearch and SQLite here, mostly because of index maintenance.

## Queries

| Query | SQLite | PostgreSQL | OpenSearch | In-memory |
|---|---:|---:|---:|---:|
| Newest 100 | 3.0 ms | 9.0 ms | 12 ms | 331 ms |
| Newest 100 on one host | 3.9 ms | 9.5 ms | 32 ms | 260 ms |
| Text search for a rare word, first 100 | 279 ms | 12 ms | 14 ms | 538 ms |
| Errors and worse, first 100 | 9.3 ms | 13 ms | 21 ms | 173 ms |
| Count everything | 148 ms | 91 ms | 12 ms | 156 ms |
| Last hour: count by host | 123 ms | 204 ms | 235 ms | 212 ms |
| All events: count by host | 13 s | 13 s | 33 s | 37 s |
| All events: hourly timechart | 13 s | 13 s | 28 s | 30 s |
| All events: avg and p95 by host | 13 s | 13 s | 30 s | 32 s |
| All events: top 10 paths | 13 s | 12 s | 33 s | 27 s |

In-memory storage sorts every match on each read, so it's for tests rather than volume.

## Memory

| Backend | Peak process memory, ingest and queries |
|---|---:|
| SQLite | 316 MB |
| OpenSearch | 316 MB |
| PostgreSQL | 360 MB |
| In-memory | 4,255 MB, which is the events themselves |

The important result is that aggregates over a million events used no more memory than the first page of a search, because they hold what they compute rather than the rows. Before this work, a query read at most 100,000 events and held them all.

The SQLite database for a million events was 838 MB, about 840 bytes an event with indexes.

## What this means

- **Finding things stays fast as data grows.** Reads for the newest matches use an index on time and ID, so they don't depend on how much is stored.
- **Aggregates are linear in events read**, at about 80,000 events a second on PostgreSQL and SQLite, and 30,000 on OpenSearch. A time range is the way to keep them quick: a query's 60-second limit on `/query/execute` covers about 5 million events, and a 10-minute search job about 50 million.
- **Beyond that, aggregates need pushing down to the database**, which isn't done yet. It's the next big step for scale. Data models can pre-aggregate in the meantime.
