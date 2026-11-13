/*
 * The search page's logic: time ranges, the timeline, filters from clicks, field summaries,
 * query highlighting and autocomplete, shareable links and export. Everything here is a pure
 * function of its arguments, so DashboardTests can run it without a browser; app.js wires it to
 * the page.
 */
const CabinetSearch = (() => {
    const MINUTE = 60000;
    const HOUR = 60 * MINUTE;
    const DAY = 24 * HOUR;
    const UNIT_MS = { s: 1000, m: MINUTE, h: HOUR, d: DAY, w: 7 * DAY };
    const UNIT_NAMES = { s: 'second', m: 'minute', h: 'hour', d: 'day', w: 'week' };

    /** The time picker's quick choices. */
    const PRESETS = [
        { id: '15m', label: 'Last 15 minutes' },
        { id: '1h', label: 'Last hour' },
        { id: '4h', label: 'Last 4 hours' },
        { id: '24h', label: 'Last 24 hours' },
        { id: '7d', label: 'Last 7 days' },
        { id: '30d', label: 'Last 30 days' },
        { id: 'today', label: 'Today' },
        { id: 'yesterday', label: 'Yesterday' },
        { id: 'all', label: 'All time' },
    ];

    /** Stages that keep the rows events, so the timeline and fields still make sense. */
    const EVENT_STAGES = new Set(['where', 'search', 'regex', 'rex', 'eval', 'rename', 'fillnull', 'filldown', 'lookup', 'fieldalias', 'mvexpand', 'eventstats', 'streamstats', 'dedup', 'sort', 'head', 'tail', 'limit', 'convert', 'extract', 'expand', 'fill']);

    /** Stages that only remove events, so the timeline can count them. */
    const FILTER_STAGES = new Set(['where', 'search', 'regex', 'rex', 'eval', 'rename', 'fillnull', 'lookup', 'fieldalias', 'convert', 'extract']);

    /** BQL's pipeline stages, for highlighting and autocomplete. */
    const COMMANDS = [
        'where', 'search', 'stats', 'timechart', 'chart', 'top', 'rare', 'eval', 'rex', 'regex', 'rename', 'table', 'fields',
        'sort', 'head', 'tail', 'dedup', 'eventstats', 'streamstats', 'transaction', 'lookup', 'inputlookup', 'join', 'append',
        'fillnull', 'filldown', 'mvexpand', 'bucket', 'bin', 'makeresults', 'fieldsummary', 'xyseries', 'untable', 'addcoltotals',
        'fieldalias', 'convert', 'macro', 'limit',
    ];

    const COMMAND_HELP = {
        where: 'keep rows where an expression is true', search: 'keep rows matching text or field=value',
        stats: 'aggregate: count, sum, avg, ... by fields', timechart: 'aggregate over time buckets',
        chart: 'aggregate over two fields', top: 'most common values', rare: 'least common values',
        eval: 'compute a field', rex: 'extract fields with a regular expression', regex: 'keep rows matching a regex',
        rename: 'rename fields', table: 'show only these fields', fields: 'keep or drop fields', sort: 'order rows',
        head: 'first N rows', tail: 'last N rows', dedup: 'drop rows with repeated values', eventstats: 'add aggregates to each row',
        streamstats: 'running aggregates', transaction: 'group related events', lookup: 'add fields from a lookup',
        join: 'join with a subsearch', append: 'append a subsearch', fillnull: 'fill empty fields', mvexpand: 'one row per value',
        bucket: 'round values into buckets', fieldsummary: 'statistics for every field', makeresults: 'make test rows',
    };

    const AGGREGATES = ['count', 'dc', 'sum', 'avg', 'min', 'max', 'median', 'mode', 'stdev', 'stdevp', 'var', 'varp', 'perc95', 'p99', 'earliest', 'latest', 'values', 'list', 'range'];

    const FUNCTIONS = [
        'if', 'case', 'coalesce', 'isnull', 'isnotnull', 'nullif', 'like', 'match', 'len', 'lower', 'upper', 'substr', 'replace',
        'split', 'trim', 'ltrim', 'rtrim', 'startswith', 'endswith', 'tonumber', 'tostring', 'round', 'floor', 'ceil', 'abs',
        'sqrt', 'pow', 'log', 'exp', 'now', 'strftime', 'strptime', 'relative_time', 'json_extract', 'spath', 'mvcount',
        'mvindex', 'mvjoin', 'mvfilter', 'urldecode', 'printf', 'typeof', 'md5', 'sha256',
    ];

    const KEYWORDS = new Set(['by', 'as', 'and', 'or', 'not', 'from', 'last', 'span', 'over', 'limit', 'in', 'find', 'to']);

    /** The query names fields built into every event by these, ignoring case. */
    const BUILTIN_FIELDS = { Host: 'host', SourceType: 'source_type', Severity: 'severity', Index: 'index', SourceFile: 'source_file', LineNumber: 'line_number', Timestamp: 'timestamp' };

    /** Never summarized: unique per event, or the text itself. */
    const HIDDEN_FIELDS = new Set(['Id', 'RawLine', '_raw', '_time', '_indextime', 'Timestamp']);

    const SEVERITIES = ['Trace', 'Debug', 'Info', 'Warning', 'Error', 'Critical', 'Fatal'];

    /** A time from the API: ISO text (UTC when it has no zone), milliseconds, or a Date. */
    function parseTime(value) {
        if (value === null || value === undefined || value === '') return NaN;
        if (typeof value === 'string' && /^\d{4}-\d\d-\d\dT[\d:.]+$/.test(value)) return new Date(value + 'Z').getTime();
        return new Date(value).getTime();
    }

    function escapeHtml(value) {
        return String(value ?? '')
            .replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;')
            .replace(/"/g, '&quot;').replace(/'/g, '&#39;');
    }

    /* ===== Time ===== */

    function startOfDay(ms) {
        const d = new Date(ms);
        d.setHours(0, 0, 0, 0);
        return d.getTime();
    }

    /**
     * Turns a choice from the time picker into times. A range is { preset }, { last, unit }, or
     * { from, to } as ISO strings or milliseconds. Returns { start, end, label } in milliseconds,
     * with null for an open end.
     */
    function resolveRange(range, now) {
        const at = now ?? Date.now();
        if (!range || range.preset === 'all') return { start: null, end: null, label: 'All time' };

        if (range.preset) {
            if (range.preset === 'today') return { start: startOfDay(at), end: null, label: 'Today' };
            if (range.preset === 'yesterday') {
                const today = startOfDay(at);
                return { start: startOfDay(today - DAY / 2), end: today, label: 'Yesterday' };
            }

            const match = /^(\d+)([smhdw])$/.exec(range.preset);
            if (match) return resolveRange({ last: Number(match[1]), unit: match[2] }, at);
            return { start: null, end: null, label: 'All time' };
        }

        if (range.last) {
            const amount = Math.max(1, Math.floor(Number(range.last)));
            const unit = UNIT_MS[range.unit] ? range.unit : 'h';
            const preset = PRESETS.find(p => p.id === `${amount}${unit}`);
            const label = preset ? preset.label : `Last ${amount} ${UNIT_NAMES[unit]}${amount === 1 ? '' : 's'}`;
            return { start: at - amount * UNIT_MS[unit], end: null, label };
        }

        const start = range.from === undefined || range.from === null || range.from === '' ? null : new Date(range.from).getTime();
        const end = range.to === undefined || range.to === null || range.to === '' ? null : new Date(range.to).getTime();
        const valid = v => v === null || !Number.isNaN(v);
        if (!valid(start) || !valid(end)) return { start: null, end: null, label: 'All time' };
        return { start, end, label: `${start === null ? 'Start' : formatDateTime(start)} to ${end === null ? 'now' : formatDateTime(end)}` };
    }

    function pad(n) { return String(n).padStart(2, '0'); }

    function formatDateTime(ms) {
        const d = new Date(ms);
        return `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())} ${pad(d.getHours())}:${pad(d.getMinutes())}`;
    }

    /** A value for an <input type="datetime-local">. */
    function toLocalInput(ms) {
        if (ms === null || ms === undefined) return '';
        const d = new Date(ms);
        return `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())}T${pad(d.getHours())}:${pad(d.getMinutes())}`;
    }

    const SPANS = [
        ['1s', 1000], ['5s', 5000], ['10s', 10000], ['30s', 30000],
        ['1m', MINUTE], ['5m', 5 * MINUTE], ['10m', 10 * MINUTE], ['30m', 30 * MINUTE],
        ['1h', HOUR], ['3h', 3 * HOUR], ['6h', 6 * HOUR], ['12h', 12 * HOUR],
        ['1d', DAY], ['7d', 7 * DAY], ['30d', 30 * DAY],
    ];

    /** The smallest round bucket that fits the range into at most maxBuckets bars. */
    function chooseSpan(startMs, endMs, maxBuckets = 60) {
        const width = Math.max(1000, endMs - startMs);
        const found = SPANS.find(([, ms]) => width / ms <= maxBuckets) || SPANS[SPANS.length - 1];
        return { span: found[0], ms: found[1] };
    }

    /* ===== Pipelines ===== */

    /** Splits a query at pipes outside quotes and brackets. */
    function splitPipeline(query) {
        const parts = [];
        let current = '';
        let quote = null;
        let depth = 0;
        for (let i = 0; i < query.length; i++) {
            const c = query[i];
            if (quote) {
                current += c;
                if (c === '\\' && i + 1 < query.length) { current += query[++i]; continue; }
                if (c === quote) quote = null;
                continue;
            }

            if (c === '"' || c === "'") { quote = c; current += c; continue; }
            if (c === '[' || c === '(') depth++;
            if (c === ']' || c === ')') depth = Math.max(0, depth - 1);
            if (c === '|' && depth === 0) { parts.push(current); current = ''; continue; }
            current += c;
        }

        parts.push(current);
        return parts.map(text => text.trim());
    }

    function commandOf(stage) {
        const match = /^([A-Za-z_]+)/.exec(stage);
        return match ? match[1].toLowerCase() : '';
    }

    /** Whether the query's rows are still events, rather than a table it computed. */
    function returnsEvents(query) {
        return splitPipeline(query || '').slice(1).every(stage => stage === '' || EVENT_STAGES.has(commandOf(stage)));
    }

    /** The leading search and filters: what decides which events match. */
    function baseSearch(query) {
        const stages = splitPipeline(query || '');
        let end = 1;
        while (end < stages.length && FILTER_STAGES.has(commandOf(stages[end]))) end++;
        return stages.slice(0, end).filter((s, i) => i === 0 || s !== '').join(' | ');
    }

    /** The query counting matching events per bucket, for the timeline. */
    function timelineQuery(query, span) {
        const base = baseSearch(query).trim();
        if (base === '' || /^makeresults\b|^inputlookup\b|^\|/i.test(base)) return null;
        return `${base} | timechart span=${span} count`;
    }

    /** How the query names a field, or null if BQL can't name it. */
    function fieldName(field) {
        if (BUILTIN_FIELDS[field]) return BUILTIN_FIELDS[field];
        return /^[A-Za-z_][A-Za-z0-9_]*(\.[A-Za-z_][A-Za-z0-9_]*)*$/.test(field) ? field : null;
    }

    function literal(value) {
        if (typeof value === 'number' && Number.isFinite(value)) return String(value);
        if (typeof value === 'boolean') return value ? 'true' : 'false';
        return `"${String(value).replace(/\\/g, '\\\\').replace(/"/g, '\\"')}"`;
    }

    /**
     * Adds a filter for a field's value, or against it, where filters belong: after the leading
     * search and filters, before anything that aggregates. Returns null for a field BQL can't name.
     */
    function addFilter(query, field, value, exclude) {
        const name = fieldName(field);
        if (name === null) return null;
        const condition = value === null || value === undefined || value === ''
            ? `${exclude ? 'isnotnull' : 'isnull'}(${name})`
            : `${name} ${exclude ? '!=' : '='} ${literal(field === 'Severity' && typeof value === 'string' ? Math.max(0, SEVERITIES.indexOf(value)) : value)}`;
        const stage = `where ${condition}`;
        const stages = splitPipeline(query && query.trim() ? query : 'find *');
        if (stages.slice(1).includes(stage)) return stages.join(' | ');

        let at = 1;
        while (at < stages.length && FILTER_STAGES.has(commandOf(stages[at]))) at++;
        stages.splice(at, 0, stage);
        return stages.filter((s, i) => i === 0 || s !== '').join(' | ');
    }

    /** The words and phrases a query searches for, to highlight in results. */
    function searchTerms(query) {
        const first = splitPipeline(query || '')[0] || '';
        const text = first.replace(/^\s*(find|search)\b/i, '').replace(/\bfrom\s+last\s+\d+[smhdw]\b/gi, '');
        const terms = [];
        const pattern = /"((?:[^"\\]|\\.)*)"|(\S+)/g;
        let match;
        while ((match = pattern.exec(text)) !== null) {
            if (match[1] !== undefined) { if (match[1].trim()) terms.push(match[1]); continue; }
            const word = match[2];
            if (word === '*' || word.includes('=') || /^(and|or|not)$/i.test(word) || /[<>!]/.test(word)) continue;
            const cleaned = word.replace(/^[()]+|[()]+$/g, '').replace(/\*/g, '');
            if (cleaned.length > 0) terms.push(cleaned);
        }

        return [...new Set(terms)];
    }

    function escapeRegex(text) {
        return text.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
    }

    /** Escaped text with each search term marked, matched as BQL does: whole words, any case. */
    function highlight(text, terms) {
        const value = String(text ?? '');
        const useful = (terms || []).filter(t => t && t.length > 0);
        if (useful.length === 0) return escapeHtml(value);
        const pattern = new RegExp(`(?<![A-Za-z0-9])(${useful.map(escapeRegex).sort((a, b) => b.length - a.length).join('|')})(?![A-Za-z0-9])`, 'gi');
        let html = '';
        let last = 0;
        let match;
        while ((match = pattern.exec(value)) !== null) {
            if (match[0].length === 0) { pattern.lastIndex++; continue; }
            html += escapeHtml(value.slice(last, match.index)) + `<mark>${escapeHtml(match[0])}</mark>`;
            last = match.index + match[0].length;
        }

        return html + escapeHtml(value.slice(last));
    }

    /* ===== Fields ===== */

    function displayValue(value) {
        if (value === null || value === undefined) return null;
        if (Array.isArray(value)) return value.join(', ');
        if (typeof value === 'object') return JSON.stringify(value);
        return value;
    }

    /**
     * Each field across a set of events: how many have it, how many values it takes, its most
     * common values with shares, and min, max and mean for numbers. Most common fields first.
     */
    function summarizeFields(events, options = {}) {
        const topCount = options.top || 10;
        const maxDistinct = options.maxDistinct || 1000;
        const fields = new Map();
        const total = (events || []).length;

        (events || []).forEach(event => {
            Object.keys(event).forEach(key => {
                if (HIDDEN_FIELDS.has(key)) return;
                const raw = key === 'Severity' && typeof event[key] === 'number' ? (SEVERITIES[event[key]] || event[key]) : event[key];
                const value = displayValue(raw);
                if (value === null || value === '') return;
                let field = fields.get(key);
                if (!field) {
                    field = { name: key, count: 0, values: new Map(), overflow: false, numbers: 0, min: Infinity, max: -Infinity, sum: 0 };
                    fields.set(key, field);
                }

                field.count++;
                const text = String(value);
                if (field.values.has(text)) field.values.set(text, field.values.get(text) + 1);
                else if (field.values.size < maxDistinct) field.values.set(text, 1);
                else field.overflow = true;

                if (typeof raw === 'number' && Number.isFinite(raw)) {
                    field.numbers++;
                    field.min = Math.min(field.min, raw);
                    field.max = Math.max(field.max, raw);
                    field.sum += raw;
                }
            });
        });

        return [...fields.values()]
            .map(field => ({
                name: field.name,
                count: field.count,
                coverage: total === 0 ? 0 : field.count / total,
                distinct: field.values.size,
                distinctIsLowerBound: field.overflow,
                top: [...field.values.entries()]
                    .sort((a, b) => b[1] - a[1] || String(a[0]).localeCompare(String(b[0])))
                    .slice(0, topCount)
                    .map(([value, count]) => ({ value, count, share: count / field.count })),
                numeric: field.numbers > 0 && field.numbers === field.count && field.name !== 'Severity'
                    ? { min: field.min, max: field.max, mean: field.sum / field.numbers }
                    : null,
            }))
            .sort((a, b) => b.count - a.count || a.name.localeCompare(b.name));
    }

    /** The original value behind a summarized one, for filtering on it. */
    function filterValue(field, text, events) {
        if (field === 'Severity') return Math.max(0, SEVERITIES.indexOf(text));
        const sample = (events || []).find(e => e[field] !== undefined && String(displayValue(e[field])) === text);
        return sample === undefined ? text : sample[field];
    }

    /* ===== Query editor ===== */

    /** Splits a query into tokens for highlighting. Joining their text gives back the query. */
    function tokenize(query) {
        const tokens = [];
        const text = query || '';
        let i = 0;
        let expectCommand = true;
        while (i < text.length) {
            const rest = text.slice(i);
            let match;
            if ((match = /^\s+/.exec(rest))) {
                tokens.push({ type: 'space', text: match[0] });
            } else if ((match = /^"(?:[^"\\]|\\.)*"?/.exec(rest)) || (match = /^'(?:[^'\\]|\\.)*'?/.exec(rest))) {
                tokens.push({ type: 'string', text: match[0] });
                expectCommand = false;
            } else if (rest[0] === '|') {
                tokens.push({ type: 'pipe', text: '|' });
                expectCommand = true;
            } else if ((match = /^-?\d+(\.\d+)?([smhdw]\b)?/.exec(rest)) && !/^[A-Za-z_]/.test(rest)) {
                tokens.push({ type: 'number', text: match[0] });
                expectCommand = false;
            } else if ((match = /^[A-Za-z_][A-Za-z0-9_]*(\.[A-Za-z_][A-Za-z0-9_]*)*/.exec(rest))) {
                const word = match[0];
                const lower = word.toLowerCase();
                const next = text.slice(i + word.length).match(/^\s*(.)/);
                let type = 'field';
                const lastCommand = [...tokens].reverse().find(t => t.type === 'command');
                if (expectCommand && (COMMANDS.includes(lower) || lower === 'find' || lower === 'search')) type = 'command';
                else if (next && next[1] === '(') type = 'function';
                else if (lastCommand && ['stats', 'timechart', 'chart', 'eventstats', 'streamstats'].includes(lastCommand.text.toLowerCase()) && AGGREGATES.includes(lower)) type = 'function';
                else if (KEYWORDS.has(lower)) type = 'keyword';
                else if (tokens.length === 0 || (tokens.length > 0 && tokens.filter(t => t.type === 'pipe').length === 0)) type = 'term';
                tokens.push({ type, text: word });
                expectCommand = false;
            } else if ((match = /^(==|!=|<=|>=|[=<>+\-*/%,().[\]])/.exec(rest))) {
                tokens.push({ type: 'operator', text: match[0] });
                expectCommand = false;
            } else {
                tokens.push({ type: 'term', text: rest[0] });
                expectCommand = false;
            }

            i += tokens[tokens.length - 1].text.length;
        }

        return tokens;
    }

    /** The query as escaped HTML with a span per token type, for the editor's overlay. */
    function highlightQuery(query) {
        return tokenize(query).map(t => t.type === 'space' ? escapeHtml(t.text) : `<span class="q-${t.type}">${escapeHtml(t.text)}</span>`).join('');
    }

    /**
     * Suggestions for the word at the caret: stage names after a pipe, aggregates after stats and
     * timechart, fields after "by" and in expressions, and functions. Returns where the word is,
     * so choosing a suggestion replaces it.
     */
    function complete(query, caret, fields) {
        const text = query || '';
        const position = Math.max(0, Math.min(caret ?? text.length, text.length));
        const before = text.slice(0, position);
        const wordMatch = /[A-Za-z_][A-Za-z0-9_.]*$/.exec(before);
        const word = wordMatch ? wordMatch[0] : '';
        const from = position - word.length;
        const lowerWord = word.toLowerCase();
        const stagesBefore = splitPipeline(before);
        const stage = stagesBefore[stagesBefore.length - 1] || '';
        const command = stagesBefore.length > 1 ? commandOf(stage) : '';
        const stageWords = stage.slice(0, stage.length - word.length).trim();
        const fieldNames = (fields || []).map(f => fieldName(f) || null).filter(Boolean);
        const items = [];
        const add = (label, kind, detail, insert) => {
            if (word === '' || label.toLowerCase().startsWith(lowerWord) && label.toLowerCase() !== lowerWord) {
                items.push({ label, kind, detail: detail || '', insert: insert ?? label });
            }
        };

        const atCommand = stagesBefore.length > 1 && stageWords === '';
        if (atCommand) {
            COMMANDS.forEach(c => add(c, 'command', COMMAND_HELP[c]));
        } else if (/\bby\s+(?:[\w.]+\s*,\s*)*$/i.test(stageWords + ' ') || /\bby$/i.test(stageWords)) {
            [...new Set(fieldNames)].forEach(f => add(f, 'field'));
        } else if (['stats', 'timechart', 'chart', 'eventstats', 'streamstats'].includes(command)) {
            AGGREGATES.forEach(a => add(a, 'function', 'aggregate', `${a}(`));
            add('by', 'keyword', 'group by fields', 'by ');
            add('as', 'keyword', 'name the result', 'as ');
            if (command === 'timechart') add('span', 'keyword', 'bucket size, like span=1h', 'span=');
            [...new Set(fieldNames)].forEach(f => add(f, 'field'));
        } else if (stagesBefore.length > 1) {
            [...new Set(fieldNames)].forEach(f => add(f, 'field'));
            if (['where', 'eval', 'search'].includes(command)) FUNCTIONS.forEach(f => add(f, 'function', '', `${f}(`));
            if (['top', 'rare', 'sort', 'table', 'fields', 'dedup', 'rename'].includes(command)) add('by', 'keyword');
        } else {
            add('from', 'keyword', 'time range, like from last 24h', 'from last ');
        }

        return { from, to: position, items: items.slice(0, 50) };
    }

    /* ===== Links and export ===== */

    /** The search as a URL hash: query, time range, index. */
    function encodeState(state) {
        const params = [];
        const set = (key, value) => params.push(`${key}=${encodeURIComponent(value)}`);
        if (state.query) set('q', state.query);
        const range = state.range || {};
        if (range.preset) set('t', range.preset);
        else if (range.last) set('t', `${range.last}${range.unit || 'h'}`);
        else if (range.from !== undefined || range.to !== undefined) {
            if (range.from) set('from', new Date(range.from).toISOString());
            if (range.to) set('to', new Date(range.to).toISOString());
        }

        if (state.index) set('index', state.index);
        return `#search?${params.join('&')}`;
    }

    function decodeState(hash) {
        const text = String(hash || '').replace(/^#/, '');
        if (!text.startsWith('search')) return null;
        const params = {};
        text.replace(/^search\??/, '').split('&').filter(Boolean).forEach(pair => {
            const at = pair.indexOf('=');
            const key = decodeURIComponent(at < 0 ? pair : pair.slice(0, at));
            try { params[key] = at < 0 ? '' : decodeURIComponent(pair.slice(at + 1).replace(/\+/g, ' ')); }
            catch (e) { params[key] = ''; }
        });

        let range = { preset: '24h' };
        const t = params.t;
        if (t) {
            const match = /^(\d+)([smhdw])$/.exec(t);
            range = PRESETS.some(p => p.id === t) ? { preset: t } : match ? { last: Number(match[1]), unit: match[2] } : { preset: '24h' };
        } else if (params.from || params.to) {
            range = { from: params.from || null, to: params.to || null };
        }

        return { query: params.q || '', range, index: params.index || '' };
    }

    function columnsOf(rows) {
        const seen = new Set();
        const columns = [];
        (rows || []).slice(0, 1000).forEach(row => Object.keys(row).forEach(key => {
            if (!seen.has(key) && !key.startsWith('_')) { seen.add(key); columns.push(key); }
        }));
        return columns;
    }

    /** Rows as CSV, with formulas neutralized so a spreadsheet can't run them. */
    function toCsv(rows, columns) {
        const cols = columns || columnsOf(rows);
        const cell = value => {
            let text = displayValue(value);
            text = text === null ? '' : String(text);
            if (/^[=+\-@\t\r]/.test(text)) text = `'${text}`;
            return /[",\r\n]/.test(text) ? `"${text.replace(/"/g, '""')}"` : text;
        };
        return [cols.map(cell).join(','), ...(rows || []).map(row => cols.map(c => cell(row[c])).join(','))].join('\r\n');
    }

    /* ===== Timeline ===== */

    /**
     * Counts per bucket from a timechart's rows, with a zero for every empty bucket between
     * start and end so gaps show.
     */
    function timelineBuckets(rows, startMs, endMs, spanMs) {
        const counts = new Map();
        (rows || []).forEach(row => {
            const time = parseTime(row._time ?? row.Timestamp ?? row.time);
            if (Number.isNaN(time)) return;
            const value = Number(row.count ?? row.Count ?? 0) || 0;
            counts.set(time, (counts.get(time) || 0) + value);
        });

        const known = [...counts.keys()].sort((a, b) => a - b);
        // Every counted bucket gets a bar, even past the end asked for: a sender's clock can run
        // ahead of this one, and those events were still matched
        const lastKnown = known.length ? known[known.length - 1] + spanMs : undefined;
        let first = startMs ?? known[0];
        if (known.length && first !== undefined) first = Math.min(first, known[0]);
        let last = endMs === undefined || endMs === null ? lastKnown : Math.max(endMs, lastKnown ?? endMs);
        if (first === undefined || last === undefined || !(spanMs > 0)) return [];
        first = Math.floor(first / spanMs) * spanMs;
        if ((last - first) / spanMs > 2000) last = first + 2000 * spanMs;

        const buckets = [];
        for (let t = first; t < last; t += spanMs) {
            // A bucket's count is whatever the query put at its start, or anything inside it
            let count = 0;
            counts.forEach((value, time) => { if (time >= t && time < t + spanMs) count += value; });
            buckets.push({ start: t, end: t + spanMs, count });
        }

        return buckets;
    }

    /** The timeline's geometry, shared by drawing, hovering and dragging. */
    function timelineLayout(buckets, width, height) {
        const margin = { top: 8, right: 8, bottom: 20, left: 44 };
        const plotWidth = Math.max(10, width - margin.left - margin.right);
        const plotHeight = Math.max(10, height - margin.top - margin.bottom);
        const max = Math.max(1, ...buckets.map(b => b.count));
        const step = buckets.length ? plotWidth / buckets.length : plotWidth;
        // Thin bars with a 2px gap; the gap shrinks away when bars get very narrow
        const gap = step > 6 ? 2 : step > 3 ? 1 : 0;
        return {
            margin, plotWidth, plotHeight, max, step, gap,
            x: i => margin.left + i * step,
            barWidth: Math.max(1, step - gap),
            y: value => margin.top + plotHeight - (value / max) * plotHeight,
            indexAt: px => Math.max(0, Math.min(buckets.length - 1, Math.floor((px - margin.left) / step))),
        };
    }

    function formatCount(value) {
        if (value >= 1e6) return (value / 1e6).toFixed(value >= 1e7 ? 0 : 1) + 'M';
        if (value >= 1e4) return Math.round(value / 1e3) + 'K';
        return Number(value).toLocaleString('en-US');
    }

    function axisTime(ms, spanMs) {
        const d = new Date(ms);
        const time = `${pad(d.getHours())}:${pad(d.getMinutes())}`;
        const day = `${pad(d.getMonth() + 1)}-${pad(d.getDate())}`;
        return spanMs >= DAY ? day : (d.getHours() === 0 && d.getMinutes() === 0 ? day : time);
    }

    /**
     * The timeline as SVG: one bar per bucket with rounded tops on a shared baseline, a
     * recessive count axis, time labels, and an optional highlighted selection.
     */
    function timelineSvg(buckets, width, height, selection) {
        if (!buckets || buckets.length === 0) return '';
        const l = timelineLayout(buckets, width, height);
        const parts = [];
        const top = l.max;
        [0, top / 2, top].forEach(v => {
            const py = l.y(v);
            parts.push(`<line x1="${l.margin.left}" x2="${l.margin.left + l.plotWidth}" y1="${py.toFixed(1)}" y2="${py.toFixed(1)}" stroke="var(--chart-grid)" stroke-width="1"/>`);
            parts.push(`<text x="${l.margin.left - 6}" y="${(py + 3).toFixed(1)}" text-anchor="end" class="chart-axis">${escapeHtml(formatCount(Math.round(v)))}</text>`);
        });

        if (selection && selection.from !== selection.to) {
            const a = Math.min(selection.from, selection.to);
            const b = Math.max(selection.from, selection.to);
            parts.push(`<rect class="timeline-selection" x="${l.x(a).toFixed(1)}" y="${l.margin.top}" width="${(l.x(b) + l.step - l.x(a)).toFixed(1)}" height="${l.plotHeight}"/>`);
        }

        const span = buckets[0].end - buckets[0].start;
        buckets.forEach((bucket, i) => {
            if (bucket.count <= 0) return;
            const x = l.x(i) + l.gap / 2;
            const y = l.y(bucket.count);
            const h = Math.max(1, l.margin.top + l.plotHeight - y);
            const r = Math.min(4, l.barWidth / 2, h);
            // Rounded at the top only, square on the baseline
            parts.push(`<path class="timeline-bar" d="M${x.toFixed(1)},${(y + h).toFixed(1)} V${(y + r).toFixed(1)} Q${x.toFixed(1)},${y.toFixed(1)} ${(x + r).toFixed(1)},${y.toFixed(1)} H${(x + l.barWidth - r).toFixed(1)} Q${(x + l.barWidth).toFixed(1)},${y.toFixed(1)} ${(x + l.barWidth).toFixed(1)},${(y + r).toFixed(1)} V${(y + h).toFixed(1)} Z" fill="var(--series-1)"/>`);
        });

        const labels = Math.min(6, buckets.length);
        for (let t = 0; t < labels; t++) {
            const i = labels === 1 ? 0 : Math.round((t / (labels - 1)) * (buckets.length - 1));
            const anchor = t === 0 && labels > 1 ? 'start' : t === labels - 1 && labels > 1 ? 'end' : 'middle';
            const x = anchor === 'start' ? l.x(i) : anchor === 'end' ? l.x(i) + l.step : l.x(i) + l.step / 2;
            parts.push(`<text x="${x.toFixed(1)}" y="${height - 5}" text-anchor="${anchor}" class="chart-axis">${escapeHtml(axisTime(buckets[i].start, span))}</text>`);
        }

        const total = buckets.reduce((sum, b) => sum + b.count, 0);
        return `<svg class="chart-svg timeline-svg" viewBox="0 0 ${width} ${height}" width="100%" height="${height}" role="img" tabindex="0" ` +
            `aria-label="${escapeHtml(`${formatCount(total)} events over ${buckets.length} periods of ${spanLabel(span)}`)}">${parts.join('')}<g class="chart-hover"></g></svg>`;
    }

    function spanLabel(ms) {
        const found = SPANS.find(([, v]) => v === ms);
        if (!found) return `${Math.round(ms / 1000)} seconds`;
        const match = /^(\d+)([smhd])$/.exec(found[0]);
        const amount = Number(match[1]);
        return `${amount} ${UNIT_NAMES[match[2]]}${amount === 1 ? '' : 's'}`;
    }

    /** Builder time choices, as BQL. */
    const BUILDER_TIMES = {
        '': '', '15m': ' from last 15m', '1h': ' from last 1h', '4h': ' from last 4h', '24h': ' from last 24h', '7d': ' from last 7d',
        '30d': ' from last 30d', today: ' earliest=@d', yesterday: ' earliest=-1d@d latest=@d', week: ' earliest=@w',
    };

    /** Chart buckets that suit each builder time. */
    const BUILDER_SPANS = { '': '1h', '15m': '1m', '1h': '1m', '4h': '5m', '24h': '1h', '7d': '1d', '30d': '1d', today: '1h', yesterday: '1h', week: '1d' };

    function literal(value) {
        const text = String(value ?? '');
        return /^-?\d+(\.\d+)?$/.test(text) ? text : '"' + text.replace(/\\/g, '\\\\').replace(/"/g, '\\"') + '"';
    }

    function condition(c) {
        const like = String(c.value ?? '').replace(/"/g, '').replace(/\*/g, '%');
        switch (c.op) {
            case 'contains': return `${c.field} like "%${like}%"`;
            case 'not contains': return `NOT ${c.field} like "%${like}%"`;
            case 'starts': return `${c.field} like "${like}%"`;
            case 'exists': return `isnotnull(${c.field})`;
            case 'missing': return `isnull(${c.field})`;
            default: return `${c.field} ${c.op || '='} ${literal(c.value)}`;
        }
    }

    /**
     * The BQL for a search built by picking: words, conditions, a time, and what to show (the
     * events, a count, a measure, top or rare values, or a chart over time), grouped and limited.
     */
    function buildQuery(spec) {
        const words = (spec.words || '').trim();
        let query = 'find ' + (words || '*') + (BUILDER_TIMES[spec.time || ''] ?? '');
        const conditions = (spec.conditions || []).filter(c => c.field && (c.op === 'exists' || c.op === 'missing' || String(c.value ?? '').trim() !== ''));
        if (conditions.length) query += ' | where ' + conditions.map(condition).join(' and ');

        const by = (spec.by || []).filter(Boolean);
        const byText = by.length ? ' by ' + by.join(', ') : '';
        const measure = spec.measure && spec.measure.fn && spec.measure.field
            ? { bql: `${spec.measure.fn}(${spec.measure.field})`, column: `${spec.measure.fn}_${spec.measure.field}` }
            : null;
        const limit = parseInt(spec.limit, 10);
        switch (spec.show) {
            case 'count':
                query += ' | stats count' + byText + (by.length ? ' | sort by count desc' : '');
                break;
            case 'measure':
                if (measure) query += ` | stats ${measure.bql} as ${measure.column}${byText}` + (by.length ? ` | sort by ${measure.column} desc` : '');
                break;
            case 'top':
            case 'rare':
                if (spec.field) query += ` | ${spec.show} ${limit > 0 ? limit : 10} ${spec.field}`;
                return query;
            case 'overtime':
                query += ` | timechart span=${BUILDER_SPANS[spec.time || ''] || '1h'} ${measure ? measure.bql : 'count'}${by.length ? ' by ' + by[0] : ''}`;
                return query;
            case 'table':
                if ((spec.columns || []).length) query += ' | table ' + spec.columns.join(', ');
                break;
        }

        if (limit > 0) query += ` | head ${limit}`;
        return query;
    }

    /**
     * The text with each problem marked, for an overlay under the editor. Text is escaped; only the
     * marks are markup. Problems are [{start, length, message}], in any order.
     */
    function markProblems(text, problems) {
        const sorted = (problems || []).filter(p => p.length > 0 && p.start >= 0 && p.start < text.length).sort((a, b) => a.start - b.start);
        let html = '';
        let at = 0;
        for (const problem of sorted) {
            if (problem.start < at) continue;
            const end = Math.min(text.length, problem.start + problem.length);
            html += escapeHtml(text.slice(at, problem.start));
            html += `<mark class="sx-problem" title="${escapeHtml(problem.message || '')}">${escapeHtml(text.slice(problem.start, end))}</mark>`;
            at = end;
        }

        return html + escapeHtml(text.slice(at));
    }

    /**
     * Suggestions from the server merged into the page's own, keeping the page's first: server
     * ones bring values from the data and question starters. Each keeps where it applies.
     */
    function mergeCompletions(local, server, from, to) {
        const fromServer = (server || []).map(c => ({ label: c.text, kind: c.kind, detail: c.description, insert: c.text, from: c.start, to: c.start + c.length }));
        // Values from the data are the most specific suggestion there is, so they lead
        const ordered = [...fromServer.filter(i => i.kind === 'value'), ...local.map(item => ({ ...item, from: item.from ?? from, to: item.to ?? to })), ...fromServer.filter(i => i.kind !== 'value')];
        const seen = new Set();
        return ordered.filter(item => {
            const key = String(item.insert).toLowerCase().trim();
            if (seen.has(key)) return false;
            seen.add(key);
            return true;
        }).slice(0, 14);
    }

    return {
        PRESETS, COMMANDS, FUNCTIONS, AGGREGATES, SEVERITIES, BUILTIN_FIELDS,
        buildQuery, markProblems, mergeCompletions,
        escapeHtml, parseTime, resolveRange, formatDateTime, toLocalInput, chooseSpan, spanLabel,
        splitPipeline, returnsEvents, baseSearch, timelineQuery, fieldName, addFilter,
        searchTerms, highlight, summarizeFields, filterValue, displayValue,
        tokenize, highlightQuery, complete,
        encodeState, decodeState, columnsOf, toCsv,
        timelineBuckets, timelineLayout, timelineSvg, formatCount,
    };
})();
