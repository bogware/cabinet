# BQL Query Language Reference

BQL (Bog Query Language) is Cabinet's native query language. It's designed to be natural-language-inspired while maintaining full backward compatibility with Splunk's SPL (Search Processing Language).

Every query in this guide runs as part of the test suite, and `BqlLanguageTests` checks what each function and stage returns.

## Quick Start

```
find errors from last 24h                            # Search with time range
find * | where severity >= 4 and host = "web-01"     # Filter
find * | stats count by host                         # Aggregate
find * | sort by timestamp desc | limit 20           # Sort + limit
```

## Syntax Overview

A BQL query consists of a **search command** followed by zero or more **pipeline stages** separated by `|`:

```
<search> [from <time-range>] [| <stage> [| <stage> ...]]
```

### Search Commands

| Syntax | Description |
|--------|-------------|
| `find <text>` | BQL-style search (natural language) |
| `search <text>` | SPL-style search |
| `* \| ...` | Wildcard search (all events) |
| `<text>` | Implicit search (auto-detected) |

### Field Names

Field names ignore case, so `host` and `Host` are the same field. `source_type`, `raw_line`, `source_file` and `line_number` name the built-in columns, and `level` names the level a parser extracted. `message` always has a value: the parsed message, or the raw line.

`search status=500` and `search a=1 OR b=2` filter on fields, as `where` does, with unquoted values read as text. Other search text is a text search.

A text search means the same on every backend. The text splits into words at anything that isn't a letter or digit, case doesn't matter, and every word has to appear in the line as a whole word. `connection failed` finds `failed: Connection reset`, but not `reconnection failed`, and `app` finds `/var/log/app.log`. There's no stemming, so `request` doesn't find `requests`.

### Time Ranges

```
find errors from last 24h       # Last 24 hours
find errors from last 7d        # Last 7 days
find errors from last 30m       # Last 30 minutes
find errors from last 2w        # Last 2 weeks
```

Time units: `s` (seconds), `m` (minutes), `h` (hours), `d` (days), `w` (weeks)

## Pipeline Stages

### Filtering

#### `where` — Filter events by condition

```
find * | where severity >= 4
find * | where host = "web-01" and status != "ok"
find * | where severity >= 4 or (host = "web-01" and status = "error")
find * | where message like "%timeout%"
find * | where NOT source_type = "debug"
```

**Operators:** `=`, `!=`, `>`, `>=`, `<`, `<=`, `like`, `in`, `AND`, `OR`, `NOT`

Text compares without regard to case. `severity` accepts level names such as `severity >= warning`. A time field compared with a date string compares as times.

#### `regex` — Filter by regex pattern

```
find * | regex RawLine="error|fail|timeout"
find * | regex host="web-\d+"
find * | regex host!="web-\d+"
find * | regex "disk full"
```

#### `search` / `find` — Text search within pipeline

```
find * | search "connection refused"
```

### Aggregation

#### `stats` — Compute statistics

```
find * | stats count
find * | stats count by host
find * | stats count, avg(duration), max(duration) by host, status
find * | stats dc(user) as unique_users by host
find * | stats median(latency), stdev(latency) by endpoint
find * | stats perc95(response_time) as p95 by service
```

An aggregate's column is named after its alias, or `count` or `avg(duration)` without one. An unknown function is an error.

**Aggregation functions:**

| Function | Description |
|----------|-------------|
| `count` / `count(field)` | Number of events / events with that field |
| `sum(field)` | Sum of numeric field |
| `avg(field)` / `mean(field)` | Average of numeric field |
| `min(field)` | Minimum value; compares as text if no values are numeric |
| `max(field)` | Maximum value; compares as text if no values are numeric |
| `dc(field)` | Distinct count |
| `median(field)` | Median (50th percentile) |
| `stdev(field)` / `stdevp(field)` | Standard deviation (sample/population) |
| `variance(field)` / `varp(field)` | Variance (sample/population) |
| `perc95(field)` / `p99(field)` | Percentile (any: perc50, p75, etc.) |

#### `eventstats` — Append aggregations to each event

```
find * | eventstats avg(duration) as avg_duration by host
# Each event retains its fields + gets avg_duration appended
```

#### `streamstats` — Running/cumulative aggregations

```
find * | streamstats count as running_count by host
find * | streamstats sum(bytes) as total_bytes
```

#### `timechart` — Time-series aggregation

```
find * | timechart span=1h count
find * | timechart span=5m count by host
find * | timechart span=1d avg(duration) by status
```

#### `top` — Most common values

```
find * | top 10 host
find * | top 5 status by host
```

Events without the field are ignored. With `by`, values are ranked within each group.

#### `rare` — Least common values

```
find * | rare host
find * | rare 10 status
```

#### `chart` — Cross-tabulation

```
find * | chart count over host by status
find * | chart avg(duration) over endpoint by method
```

Each `over` value is a row, and each `by` value is a column.

### Transformation

#### `eval` / `compute` — Create computed fields

```
find * | eval duration_sec = duration / 1000
find * | eval full_name = first_name . " " . last_name
find * | eval is_error = if(severity >= 4, "yes", "no")
find * | compute total = price * quantity, tax = total * 0.1
```

**Multi-field:** Comma-separated assignments:
```
find * | eval total = price * qty, tax = total * 0.1, final = total + tax
```

#### `rex` / `extract` — Regex field extraction

```
find * | rex field=message "user=(?<username>\w+)"
find * | extract "(?<ip>\d+\.\d+\.\d+\.\d+):(?<port>\d+)" from message
```

#### `rename` — Rename fields

```
find * | rename old_field as new_field
find * | rename host as server, source_type as type
```

#### `convert` — Convert field formats

```
find * | convert ctime(timestamp)          # Epoch → human-readable time
find * | convert dur2sec(duration)         # Duration string → seconds
find * | convert memk(memory)             # Memory string → kilobytes
find * | convert rmunit(value)            # Remove unit suffix
find * | convert rmcomma(number)          # Remove commas from numbers
```

The field is converted in place. Use `convert memk(memory) as kb` to keep the original.

#### `fieldalias` — Create field aliases

```
find * | fieldalias server=host, src=source_file
```

#### `fillnull` — Replace null values

```
find * | fillnull value=0 duration, latency
find * | fillnull value="unknown"
```

#### `filldown` — Propagate values downward

```
find * | filldown host, source
```

#### `bucket` / `bin` — Bucket numeric/time fields

```
find * | bucket _time span=1h
find * | bin duration span=100
```

#### `mvexpand` — Expand multi-valued field

```
find * | mvexpand tags
```

#### `xyseries` — Pivot data into table format

```
find * | xyseries host status count
# Rows=host, Columns=status values, Cells=count values
```

#### `untable` — Unpivot table (inverse of xyseries)

```
find * | untable host status count
```

### Selection & Ordering

#### `fields` / `select` / `table` — Select specific fields

```
find * | fields timestamp, host, message
find * | select severity, source_type, raw_line
find * | table host, status, duration
```

#### `sort` — Sort results

```
find * | sort by timestamp desc
find * | sort by host asc, severity desc
find * | sort by count desc
```

#### `head` / `limit` / `tail` — Limit results

```
find * | head 100        # First 100 events
find * | limit 50        # Same as head 50
find * | tail 20         # Last 20 events
```

#### `dedup` / `unique` — Remove duplicates

```
find * | dedup host
find * | dedup host, status
find * | unique by host, source_type
```

### Advanced

#### `transaction` — Group events into transactions

```
find * | transaction session_id maxspan=1h maxevents=100
find * | transaction host startswith="BEGIN" endswith="COMMIT"
```

Each result has `_eventcount` and `_duration`. The markers match text in the raw line. With `startswith`, events before a start marker are dropped. With `endswith`, a transaction that never reaches its end marker is dropped.

#### `lookup` — Enrich from lookup table

```
find * | lookup users user_id OUTPUT email, name
```

#### `inputlookup` — Read from lookup table

```
| inputlookup my_lookup_table
| inputlookup users | where active = true
```

#### `addcoltotals` — Add column totals row

```
find * | stats count by host | addcoltotals
```

#### `makeresults` — Generate synthetic events

```
| makeresults count=10
```

#### `fieldsummary` — Field statistics

```
find * | fieldsummary
# Returns: field name, count, distinct count, type, min, max, mean, stdev
```

## Expression Functions

An unknown function or an invalid regular expression makes the query fail with an error. A function given a missing value returns a missing value, and a result that isn't a real number, such as `sqrt(-1)`, is missing too. Join text with `.`, as in `host . ":" . port`; if either side is missing, so is the result.

### String Functions

| Function | Description | Example |
|----------|-------------|---------|
| `len(s)` | String length | `eval l = len(message)` |
| `lower(s)` | Lowercase | `eval lc = lower(host)` |
| `upper(s)` | Uppercase | `eval uc = upper(status)` |
| `trim(s)` | Trim whitespace | `eval t = trim(name)` |
| `ltrim(s)` / `rtrim(s)` | Left/right trim | `eval t = ltrim(name)` |
| `substr(s, start, len)` | Substring | `eval prefix = substr(id, 0, 4)` |
| `replace(s, regex, repl)` | Regex replace. In `repl`, `\1` (or `$1`) is the first group and `\\` a backslash | `eval user = replace(path, "^/home/([^/]+)/.*", "\1")` |
| `split(s, delim)` | Split to array | `eval parts = split(path, "/")` |
| `startswith(s, prefix)` | Starts with check | `eval is_web = startswith(host, "web")` |
| `endswith(s, suffix)` | Ends with check | `eval is_log = endswith(file, ".log")` |
| `match(s, regex)` | Regex match | `eval has_ip = match(msg, "\d+\.\d+")` |
| `urlencode(s)` / `urldecode(s)` | URL encode/decode | `eval encoded = urlencode(query)` |
| `printf(fmt, args...)` / `format(fmt, args...)` | Formatted string with `%s %d %i %f %e %g %x %%`, flags, width and precision | `eval msg = printf("%s: %d", host, count)` |
| `exact(expr)` | Force exact string match | `eval e = exact(field)` |

### Math Functions

| Function | Description | Example |
|----------|-------------|---------|
| `abs(n)` | Absolute value | `eval a = abs(diff)` |
| `round(n, d)` | Round to d decimals | `eval r = round(avg, 2)` |
| `ceil(n)` / `floor(n)` | Ceiling/floor | `eval c = ceil(ratio)` |
| `pow(base, exp)` | Power | `eval p = pow(2, 10)` |
| `sqrt(n)` | Square root | `eval s = sqrt(variance)` |
| `log(n)` | Natural log | `eval l = log(count)` |
| `exp(n)` | e^n | `eval e = exp(rate)` |
| `min(a, b)` / `max(a, b)` | Min/max of two values | `eval m = max(x, y)` |
| `random()` | Random integer | `eval r = random()` |

### Date/Time Functions

Times are in UTC. Epochs are in seconds. Formats use `%Y %y %m %d %H %I %M %S %p %a %A %b %B %j %F %T %z %Z %s %%`. Relative specs are an offset such as `-1d` or `+30m`, a snap such as `@d`, or both as in `-1d@d`, with units `s m h d w M y`.

| Function | Description | Example |
|----------|-------------|---------|
| `now()` | Current Unix timestamp | `eval t = now()` |
| `time()` | Current Unix timestamp | `eval t = time()` |
| `strftime(t, fmt)` | Format timestamp | `eval date = strftime(_time, "%Y-%m-%d")` |
| `strptime(s, fmt)` | Parse timestamp | `eval t = strptime(date, "%Y-%m-%d")` |
| `relative_time(t, spec)` | Relative time calc | `eval yesterday = relative_time(now(), "-1d")` |
| `ctime(epoch)` | Epoch → readable | `eval readable = ctime(timestamp)` |
| `mktime(readable)` | Readable → epoch | `eval epoch = mktime(date_str)` |
| `dur2sec(duration)` | Duration such as `5m30s`, `1h`, `01:00:05` or `90` → seconds | `eval secs = dur2sec("5m30s")` |

### Type Functions

| Function | Description | Example |
|----------|-------------|---------|
| `typeof(v)` | Type name: `Number`, `String`, `Boolean`, `DateTime`, `MultiValue` or `Null` | `eval t = typeof(field)` |
| `isnum(v)` | Is numeric? | `eval n = isnum(value)` |
| `isstr(v)` | Is string? | `eval s = isstr(value)` |
| `isint(v)` | Is integer? | `eval i = isint(value)` |
| `isbool(v)` | Is boolean? | `eval b = isbool(flag)` |
| `isnull(v)` | Is null? | `eval n = isnull(field)` |
| `isnotnull(v)` | Is not null? | `eval n = isnotnull(field)` |
| `tonumber(s)` | Convert to number | `eval n = tonumber(str)` |
| `tostring(v)` | Convert to string | `eval s = tostring(num)` |

### Conditional Functions

| Function | Description | Example |
|----------|-------------|---------|
| `if(cond, true, false)` | Ternary | `eval status = if(code >= 400, "error", "ok")` |
| `case(c1, v1, c2, v2, ...)` | Multi-branch | `eval level = case(sev>=4, "high", sev>=2, "med", true, "low")` |
| `coalesce(a, b, ...)` | First non-null | `eval name = coalesce(display_name, username, "unknown")` |
| `nullif(a, b)` | Null if equal | `eval v = nullif(value, 0)` |

### Hash & Encoding Functions

| Function | Description | Example |
|----------|-------------|---------|
| `md5(s)` | MD5 hash | `eval h = md5(password)` |
| `sha1(s)` | SHA-1 hash | `eval h = sha1(data)` |
| `sha256(s)` | SHA-256 hash | `eval h = sha256(secret)` |
| `sha512(s)` | SHA-512 hash | `eval h = sha512(data)` |
| `base64encode(s)` | Base64 encode | `eval b = base64encode(data)` |
| `base64decode(s)` | Base64 decode | `eval d = base64decode(encoded)` |

### JSON Functions

| Function | Description | Example |
|----------|-------------|---------|
| `tojson(v)` | Convert to JSON | `eval j = tojson(fields)` |
| `fromjson(s, path)` / `spath(s, path)` | Extract JSON value | `eval name = fromjson(data, "user.name")` |
| `json_extract(s, path)` | Extract JSON value | `eval v = json_extract(body, "status")` |

### Multi-Value Functions

| Function | Description | Example |
|----------|-------------|---------|
| `mvcount(mv)` | Count values | `eval n = mvcount(tags)` |
| `mvindex(mv, i)` | Get value at index | `eval first = mvindex(tags, 0)` |
| `mvjoin(mv, delim)` | Join values | `eval s = mvjoin(tags, ", ")` |
| `mvappend(a, b)` | Append values | `eval combined = mvappend(tags, labels)` |
| `mvdedup(mv)` | Remove duplicates | `eval unique = mvdedup(tags)` |
| `mvsort(mv)` | Sort values | `eval sorted = mvsort(tags)` |
| `mvfind(mv, regex)` | Find matching index | `eval i = mvfind(tags, "error")` |
| `mvfilter(mv, expr)` | Keep values where `expr` is true; `expr` sees each value under the field's name and as `mv` | `eval filtered = mvfilter(tags, match(tags, "err"))` |

### Utility Functions

| Function | Description | Example |
|----------|-------------|---------|
| `memk(s)` | Memory string → KB | `eval kb = memk("1.5GB")` |
| `rmunit(s)` | Remove unit suffix | `eval n = rmunit("100ms")` |
| `rmcomma(s)` | Remove commas | `eval n = rmcomma("1,234,567")` |

## SPL Compatibility

BQL supports SPL syntax for backward compatibility:

| SPL | BQL Equivalent |
|-----|---------------|
| `search error` | `find error` |
| `* \| head 10` | `find * \| limit 10` |
| `* \| sort -timestamp` | `find * \| sort by timestamp desc` |
| `* \| dedup host` | `find * \| unique by host` |
| `* \| rex field=msg "(?<ip>\\d+\\.\\d+)"` | `find * \| extract "(?<ip>\\d+\\.\\d+)" from msg` |

## Natural Language Queries

Cabinet supports natural language queries that are automatically translated to BQL:

| Natural Language | Translated BQL |
|-----------------|---------------|
| `show me errors from the last 24 hours` | `find * \| where severity >= 4 from last 24h` |
| `how many errors` | `find * \| where severity >= 4 \| stats count` |
| `top 10 errors by host` | `find * \| where severity >= 4 \| top 10 host` |
| `show warnings from web servers` | `find * \| where severity >= 3 and host like "%web%"` |

## Query Optimization Tips

1. **Add time ranges** — `from last 24h` pushes time filter to storage (much faster)
2. **Filter early** — Put `where` clauses before `stats` to reduce data volume
3. **Use limits** — `| limit 1000` prevents unbounded result sets
4. **Specific fields** — `| fields timestamp, host, message` reduces data transfer
5. **Avoid `*` in stats** — `stats count by host` is faster than `stats count by *`
6. **Use indexed fields** — `host`, `severity`, `source_type` have storage-level indexes
