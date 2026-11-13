# Searching in Cabinet

Ask Cabinet a question in plain words, or write a query in BQL, its query language. Both run the same engine, and Splunk's SPL works too.

```
errors on web servers since yesterday by host        # plain words
find * | where severity >= error | stats count by host   # BQL
search error | stats count by host | sort -count       # SPL
```

Every query in this guide runs as part of the test suite, and `BqlLanguageTests` checks what each function and stage returns.

## Ask in Plain Words (Natural Language)

Type what you want to know. Cabinet reads it into BQL, runs that, and shows you what it understood: the query, what it does in words, and any words it corrected. Word order doesn't matter, and many words mean the same thing: *show*, *list*, *find* and *give me* all just ask; *by*, *per* and *for each* all group.

| You type | Cabinet runs |
|---|---|
| `errors on web servers since yesterday by host` | `find * earliest=-1d@d \| where severity >= error and host like "web%" \| stats count by host \| sort by count desc` |
| `how many errors per host in the last 24 hours` | `find * from last 24h \| where severity >= error \| stats count by host \| sort by count desc` |
| `show me warnings and errors between 2pm and 4pm` | `find * earliest=2026-11-12T14:00:00Z latest=2026-11-12T16:00:00Z \| where severity >= warning` |
| `what happened on web-02 in the last 15 minutes` | `find * from last 15m \| where host = "web-02"` |
| `average response time by endpoint over the last 7 days` | `find * from last 7d \| stats avg(duration_ms) as avg_duration_ms by path \| sort by avg_duration_ms desc` |
| `p99 latency per host` | `find * \| stats perc99(duration_ms) as p99_duration_ms by host \| sort by p99_duration_ms desc` |
| `top 5 slowest endpoints` | `find * \| stats avg(duration_ms) as avg_duration_ms by path \| sort by avg_duration_ms desc \| head 5` |
| `how many unique users per day` | `find * \| timechart span=1d dc(user)` |
| `hourly errors by host` | `find * \| where severity >= error \| timechart span=1h count by host` |
| `which host has the most errors` | `find * \| where severity >= error \| top 1 host` |
| `least common status codes` | `find * \| rare 10 status` |
| `count of 5xx errors per endpoint` | `find * \| where status >= 500 and status <= 599 \| stats count by path \| sort by count desc` |
| `POST requests over 1000 ms` | `find * \| where duration_ms > 1000 and method = "POST"` |
| `events where user is alice or bob` | `find * \| where user in ("alice", "bob")` |
| `errors excluding web-01 and web-02` | `find * \| where severity >= error and NOT host in ("web-01", "web-02")` |
| `show host, path and status for errors` | `find * \| where severity >= error \| table host, path, status` |
| `latest event per host` | `find * \| dedup host` |
| `oldest 5 events` | `find * \| sort by _time asc \| head 5` |

The exact query depends on your data: field names come from what your events have, so *response time* becomes `duration_ms` in one place and `latency` in another, and *alice* is recognized as a user because your events have a user called alice.

### What it understands

| Kind | Examples |
|---|---|
| Times | *last 24 hours*, *past week*, *in the last 15 min*, *today*, *yesterday*, *this week*, *since monday*, *since 10am*, *between 2pm and 4pm*, *2 hours ago* |
| Levels | *errors*, *warnings*, *critical*, *fatal*, each meaning that level or worse; *errors and warnings* means warning or worse |
| Hosts | *on web-01*, *from db-2*, *on web servers* (every host starting with web), *not on web-01*, *excluding web-01 and web-02* |
| Comparisons | *status is 500*, *status 500*, *status is at least 500*, *over 1000 ms*, *greater than 2 seconds*, *between 200 and 299*, *user is alice or bob*, *path starts with /api*, *message contains timeout*, *404s*, *5xx* |
| Counting and measures | *how many*, *count of*, *number of*, *how many unique users*, *average*, *total*, *max*, *min*, *median*, *p95*, *95th percentile*, *standard deviation* |
| Grouping | *by host*, *per endpoint*, *for each user*, *grouped by*, *broken down by*, *split by* |
| Over time | *over time*, *trend*, *timeline*, *per hour*, *hourly*, *daily*, *every 5 minutes*, *in 10 minute buckets*, *error rate* |
| Most and least | *top 10 hosts*, *most common paths*, *busiest hosts*, *which host has the most errors*, *least common status codes*, *slowest endpoints*, *biggest responses* |
| Columns and order | *show host, path and status*, *list hosts*, *unique ips*, *sorted by bytes*, *highest first*, *oldest first*, *latest 20*, *first 10* |
| Text | *containing "connection refused"*, and any other words, which are searched for |

Fields have everyday names too: *server*, *machine* and *node* mean `host`; *level* means `severity`; *ip* and *client ip* mean `src_ip` or whichever your data has; *endpoint*, *url* and *page* mean `path`; *latency* and *response time* mean a duration field.

### When it isn't sure

- **Small fixes are applied and shown.** *erors by hots* runs as errors by host, and says so. A mistyped command (`| sotr by count`) or a field your data doesn't have but nearly matches (`by hostt`) is corrected the same way.
- **Words can mean two things.** *errors* is read as the error level, and the plain word *error* is offered as an alternative with one click. So is searching for exactly what you typed.
- **If it can't tell, it doesn't run.** You get the readings it considered, the part it didn't follow, and the option to ask the AI assistant.
- **Strict queries always run as written.** Anything with a pipe, `field=value`, `OR`, `AND`, `NOT`, wildcards or BQL time syntax is BQL or SPL, and plain words like `connection refused timeout` stay a word search.
- **Saved searches keep their BQL.** Saving a question stores the query it was understood as, with your words as its description, and alerts, reports and detection rules never reinterpret.

In the API, `POST /api/v1/query/understand` says what a query would run as without running it, and `"exact": true` on `/query/execute` runs it exactly as typed. In the CLI, `cabinet query` prints what it understood to stderr, and `--exact` turns reading off.

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

A word means the same on every backend. Text splits into words at anything that isn't a letter or digit, case doesn't matter, and a word has to appear in the line as a whole word. `connection failed` finds `failed: Connection reset`, but not `reconnection failed`, and `app` finds `/var/log/app.log`. There's no stemming, so `request` doesn't find `requests`.

A search combines terms as SPL does:

| Syntax | Matches |
|--------|---------|
| `error timeout` or `error AND timeout` | Lines with both words |
| `error OR warn` | Lines with either |
| `error NOT disk`, `error -disk` | Lines with `error` and without `disk` |
| `(alice OR bob) failed` | Grouping |
| `"connection refused"` | The words in that order, next to each other |
| `fail*`, `*timeout*` | Words starting with `fail`; anything containing `timeout` |
| `host=web-1`, `status!=200`, `bytes>1000`, `user=adm*` | A field's value; a missing field never matches, `!=` included |
| `earliest=-1d@d latest=now` | The search's time range, as in SPL (`-24h`, `-7d@d`, `@h`, `now`, `0`, or a date) |

`OR`, `AND` and `NOT` are operators only in capitals. Storage finds lines with all of a search's plain words; phrases, wildcards, `OR`, `NOT` and fields are checked after, so a search with at least one plain word stays fast.

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
| `values(field)` | Distinct values, sorted |
| `list(field)` | Every value, in order, up to 100 |
| `first(field)` / `last(field)` | The first / last value seen |
| `earliest(field)` / `latest(field)` | The value at the earliest / latest `_time` |
| `range(field)` | `max - min` |
| `mode(field)` | The most common value |
| `count(eval(expr))` | Rows where `expr` is true; any function takes `eval(expr)` in place of a field, and needs `as name` |

`stats ... by` leaves out events without a value for every by-field, as in SPL.
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
| `mvzip(a, b, delim)` | Pair up two fields' values | `eval pairs = mvzip(names, ids, "=")` |
| `mvrange(start, end, step)` | Numbers from start up to, not including, end | `eval hours = mvrange(0, 24)` |
| `json_keys(json)` | Top-level keys of a JSON object | `eval keys = json_keys(payload)` |
| `in(field, v1, v2, ...)` | Whether the field is one of the values | `where in(host, "web-1", "web-2")` |
| `cidrmatch(cidr, ip)` | Whether an address is in a network | `where cidrmatch("10.0.0.0/8", src)` |

### Comparisons and missing values

As in SPL, a comparison with a missing value is false, whatever the operator: `where status != 200` leaves out events with no `status`. Use `isnull(status)` to find those. In a condition, `"false"`, `"0"` and empty text are false. `if`, `case`, `validate` and `coalesce` only evaluate the branch they take.

### Utility Functions

| Function | Description | Example |
|----------|-------------|---------|
| `memk(s)` | Memory string → KB | `eval kb = memk("1.5GB")` |
| `rmunit(s)` | Remove unit suffix | `eval n = rmunit("100ms")` |
| `rmcomma(s)` | Remove commas | `eval n = rmcomma("1,234,567")` |

## Patterns and Places

#### `cluster` — Group lines into patterns

```
find * earliest=-1h | cluster
find * | cluster t=0.7 field=message
find * | cluster labelonly=true | stats count by cluster_pattern
```

One row per pattern, most common first: `cluster_pattern` (the line with `<*>` where lines differ), `cluster_count`, `cluster_label` and a `sample` line. Numbers, IPs, IDs, hex and times count as the same from the start, so `took 35ms` and `took 41ms` agree. `t=` (0.5) is how alike lines must be; `labelonly=true` labels every event instead.

#### `iplocation` — Where an address is

```
find * | iplocation clientip | stats count by Country
find * | iplocation prefix=src_ src | stats count by src_lat, src_lon
```

Adds `City`, `Region`, `Country`, `country_code`, `lat` and `lon`. It needs a MaxMind GeoLite2-City or GeoIP2-City `.mmdb` file in `GeoIp:DatabasePath`; GeoLite2 is free from maxmind.com with an account. A panel shown as a map uses `lat` and `lon`.

## SPL Compatibility

BQL supports SPL syntax for backward compatibility:

| SPL | BQL Equivalent |
|-----|---------------|
| `search error` | `find error` |
| `* \| head 10` | `find * \| limit 10` |
| `* \| sort -timestamp` | `find * \| sort by timestamp desc` |
| `* \| dedup host` | `find * \| unique by host` |
| `* \| rex field=msg "(?<ip>\\d+\\.\\d+)"` | `find * \| extract "(?<ip>\\d+\\.\\d+)" from msg` |

## Query Optimization Tips

1. **Add time ranges** — `from last 24h` pushes time filter to storage (much faster)
2. **Filter early** — Put `where` clauses before `stats` to reduce data volume
3. **Use limits** — `| limit 1000` prevents unbounded result sets
4. **Specific fields** — `| fields timestamp, host, message` reduces data transfer
5. **Avoid `*` in stats** — `stats count by host` is faster than `stats count by *`
6. **Use indexed fields** — `host`, `severity`, `source_type` have storage-level indexes
