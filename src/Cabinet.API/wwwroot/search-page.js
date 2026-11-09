/*
 * The search page: wires CabinetSearch's logic to the page. Log content only reaches the page
 * through esc() or CabinetSearch's escaping. Handlers are named in data-click attributes, as the
 * content security policy forbids inline script.
 */

const searchState = {
    range: { preset: '24h' },
    index: '',
    zoom: [],
    results: null,
    rows: [],
    isEvents: true,
    view: 'events',
    page: 0,
    pageSize: 100,
    sort: { column: null, direction: 1 },
    selectedFields: [],
    openFields: new Set(),
    expanded: new Set(),
    buckets: [],
    spanMs: 0,
    total: null,
    terms: [],
    summary: [],
    running: 0,
    ranQuery: '',
    suggestion: { items: [], active: -1, from: 0, to: 0 },
};

const SEARCH_EXAMPLES = [
    'find *',
    'find * | where severity >= 4',
    'find * | stats count by host',
    'find * | timechart span=1h count by source_type',
    'find * | top 10 host',
];

function storedJson(key, fallback) {
    try { const value = JSON.parse(localStorage.getItem(key)); return value ?? fallback; }
    catch (e) { return fallback; }
}

function eventLimit() {
    return parseInt(localStorage.getItem('cabinet_event_limit'), 10) || 1000;
}

/* ===== Setup ===== */

function initSearchPage() {
    searchState.selectedFields = storedJson('cabinet_selected_fields', ['Host', 'SourceType']);
    searchState.pageSize = parseInt(localStorage.getItem('cabinet_page_size'), 10) || 100;
    document.getElementById('resultPageSize').value = String(searchState.pageSize);
    if (localStorage.getItem('cabinet_wrap') === 'false') document.getElementById('eventList').classList.add('nowrap');
    if (localStorage.getItem('cabinet_fields_open') !== 'false') document.getElementById('fieldSidebar').hidden = false;

    const saved = localStorage.getItem('cabinet_default_range');
    if (saved !== null) searchState.range = saved === '' ? { preset: 'all' } : { preset: saved };

    renderPresets();
    renderTimeButton();
    renderQueryHistory();
    renderEmptyState();
    loadSearchIndexes();

    const input = document.getElementById('queryInput');
    input.addEventListener('input', () => { syncEditor(); updateSuggestions(); });
    input.addEventListener('keydown', onEditorKey);
    input.addEventListener('scroll', () => { document.getElementById('queryHighlight').scrollTop = input.scrollTop; });
    input.addEventListener('focus', updateSuggestions);
    input.addEventListener('blur', () => setTimeout(hideSuggestions, 150));
    input.addEventListener('click', updateSuggestions);

    document.getElementById('searchIndex').addEventListener('change', e => { searchState.index = e.target.value; if (searchState.ranQuery) executeQuery(); });
    document.getElementById('resultPageSize').addEventListener('change', e => {
        searchState.pageSize = parseInt(e.target.value, 10) || 100;
        localStorage.setItem('cabinet_page_size', String(searchState.pageSize));
        searchState.page = 0;
        renderResultsView();
    });
    document.getElementById('fieldFilter').addEventListener('input', renderFields);

    document.addEventListener('click', e => {
        if (!e.target.closest('.sx-time')) closeTimePicker();
        if (!e.target.closest('.sx-more')) closeSearchMenu();
    });

    window.addEventListener('hashchange', () => restoreSearchFromHash(false));
    window.addEventListener('resize', () => { if (searchState.buckets.length && !document.getElementById('timelinePanel').hidden) renderTimeline(); });

    syncEditor();
    restoreSearchFromHash(true);
}

/** Runs the search a link describes. True if the page had one. */
function restoreSearchFromHash(onLoad) {
    const state = CabinetSearch.decodeState(window.location.hash);
    if (!state) return false;
    if (state.query === searchState.ranQuery && JSON.stringify(state.range) === JSON.stringify(searchState.range) && state.index === searchState.index) return true;
    if (typeof navigate === 'function' && currentPage !== 'search') navigate('search');
    setQuery(state.query);
    searchState.range = state.range;
    searchState.index = state.index;
    searchState.zoom = [];
    document.getElementById('searchIndex').value = state.index;
    renderTimeButton();
    if (state.query || !onLoad) executeQuery();
    return true;
}

async function loadSearchIndexes() {
    const select = document.getElementById('searchIndex');
    try {
        const data = await api('GET', '/api/v1/indexes');
        const items = Array.isArray(data) ? data : (data.indexes || data.data || []);
        const names = items.map(i => typeof i === 'string' ? i : (i.name || i.index || i.Name)).filter(Boolean);
        select.innerHTML = '<option value="">All indexes</option>' + names.map(n => `<option value="${esc(n)}">${esc(n)}</option>`).join('');
        select.value = searchState.index;
    } catch (e) {
        // Without the list, searching every index still works
        select.title = 'Indexes could not be listed: ' + e.message;
    }
}

/* ===== Editor ===== */

function setQuery(text) {
    const input = document.getElementById('queryInput');
    input.value = text;
    syncEditor();
}

function syncEditor() {
    const input = document.getElementById('queryInput');
    // A trailing newline keeps the overlay's last line as tall as the textarea's
    document.getElementById('queryHighlight').innerHTML = CabinetSearch.highlightQuery(input.value) + '\n';
    input.style.height = 'auto';
    input.style.height = Math.min(200, Math.max(38, input.scrollHeight + 2)) + 'px';
    document.getElementById('queryHighlight').scrollTop = input.scrollTop;
}

function knownFields() {
    const names = new Set(Object.keys(CabinetSearch.BUILTIN_FIELDS));
    searchState.summary.forEach(f => names.add(f.name));
    return [...names];
}

function updateSuggestions() {
    const input = document.getElementById('queryInput');
    if (document.activeElement !== input) return;
    const box = document.getElementById('querySuggestions');
    let items;
    let from = 0;
    let to = input.value.length;
    if (input.value.trim() === '') {
        const history = storedJson('cabinet_query_history', []);
        items = [...history.slice(0, 6).map(q => ({ label: q, kind: 'recent', insert: q })),
            ...SEARCH_EXAMPLES.filter(q => !history.includes(q)).map(q => ({ label: q, kind: 'example', insert: q }))];
    } else {
        const result = CabinetSearch.complete(input.value, input.selectionStart, knownFields());
        items = result.items;
        from = result.from;
        to = result.to;
    }

    searchState.suggestion = { items, active: items.length ? 0 : -1, from, to };
    if (items.length === 0) { hideSuggestions(); return; }
    box.innerHTML = items.map((item, i) =>
        `<div class="sx-suggest-item" role="option" id="sx-suggest-${i}" aria-selected="${i === 0}" data-click="acceptSuggestion" data-args='[${i}]'>` +
        `<span class="sx-suggest-kind">${esc(item.kind)}</span><span class="sx-suggest-label">${esc(item.label)}</span>` +
        `${item.detail ? `<span class="sx-suggest-detail">${esc(item.detail)}</span>` : ''}</div>`).join('');
    box.classList.add('show');
    input.setAttribute('aria-expanded', 'true');
    input.setAttribute('aria-activedescendant', 'sx-suggest-0');
}

function hideSuggestions() {
    document.getElementById('querySuggestions').classList.remove('show');
    const input = document.getElementById('queryInput');
    input.setAttribute('aria-expanded', 'false');
    input.removeAttribute('aria-activedescendant');
    searchState.suggestion.active = -1;
}

function moveSuggestion(step) {
    const s = searchState.suggestion;
    if (!s.items.length) return;
    s.active = (s.active + step + s.items.length) % s.items.length;
    document.querySelectorAll('#querySuggestions .sx-suggest-item').forEach((el, i) => el.setAttribute('aria-selected', String(i === s.active)));
    const active = document.getElementById(`sx-suggest-${s.active}`);
    if (active) active.scrollIntoView({ block: 'nearest' });
    document.getElementById('queryInput').setAttribute('aria-activedescendant', `sx-suggest-${s.active}`);
}

function acceptSuggestion(index) {
    const s = searchState.suggestion;
    const item = s.items[index ?? s.active];
    if (!item) return;
    const input = document.getElementById('queryInput');
    if (item.kind === 'recent' || item.kind === 'example') {
        input.value = item.insert;
    } else {
        const before = input.value.slice(0, s.from);
        const after = input.value.slice(s.to);
        const insert = item.insert + (item.insert.endsWith('(') || item.insert.endsWith(' ') || item.insert.endsWith('=') ? '' : ' ');
        input.value = before + insert + after;
        const caret = before.length + insert.length;
        input.setSelectionRange(caret, caret);
    }

    input.focus();
    syncEditor();
    hideSuggestions();
}

function onEditorKey(e) {
    const open = document.getElementById('querySuggestions').classList.contains('show') && searchState.suggestion.items.length > 0;
    if (open && e.key === 'ArrowDown') { e.preventDefault(); moveSuggestion(1); return; }
    if (open && e.key === 'ArrowUp') { e.preventDefault(); moveSuggestion(-1); return; }
    if (open && e.key === 'Tab') { e.preventDefault(); acceptSuggestion(); return; }
    if (e.key === 'Escape') { hideSuggestions(); return; }
    if (e.key === 'Enter' && !e.shiftKey && !e.ctrlKey && !e.metaKey) {
        e.preventDefault();
        hideSuggestions();
        executeQuery();
    }
}

/* ===== Time ===== */

function renderPresets() {
    document.getElementById('timePresets').innerHTML = CabinetSearch.PRESETS.map(p =>
        `<button type="button" data-click="applyPreset" data-args='${esc(JSON.stringify([p.id]))}' aria-pressed="${searchState.range.preset === p.id}">${esc(p.label)}</button>`).join('');
}

function queryHasOwnTime(query) {
    return /\bfrom\s+last\s+\d+\s*[smhdw]\b/i.test(query || '');
}

function renderTimeButton() {
    const button = document.getElementById('timePickerButton');
    const query = document.getElementById('queryInput').value;
    const label = queryHasOwnTime(query) ? 'Time set in query' : CabinetSearch.resolveRange(searchState.range).label;
    button.innerHTML = `${esc(label)} &#9662;`;
    renderPresets();
    const range = CabinetSearch.resolveRange(searchState.range);
    document.getElementById('timeFrom').value = CabinetSearch.toLocalInput(range.start);
    document.getElementById('timeTo').value = CabinetSearch.toLocalInput(range.end);
}

function toggleTimePicker() {
    const panel = document.getElementById('timePicker');
    panel.hidden = !panel.hidden;
    document.getElementById('timePickerButton').setAttribute('aria-expanded', String(!panel.hidden));
    if (!panel.hidden) {
        const first = panel.querySelector('button[aria-pressed="true"]') || panel.querySelector('button');
        if (first) first.focus();
    }
}

function closeTimePicker() {
    const panel = document.getElementById('timePicker');
    if (panel && !panel.hidden) {
        panel.hidden = true;
        document.getElementById('timePickerButton').setAttribute('aria-expanded', 'false');
    }
}

function setRange(range) {
    searchState.range = range;
    searchState.zoom = [];
    closeTimePicker();
    renderTimeButton();
    executeQuery();
}

function applyPreset(id) { setRange({ preset: id }); }

function applyRelativeTime() {
    const amount = parseInt(document.getElementById('timeLastAmount').value, 10);
    if (!(amount > 0)) { toast('Enter how many', 'error'); return; }
    setRange({ last: amount, unit: document.getElementById('timeLastUnit').value });
}

function applyAbsoluteTime() {
    const from = document.getElementById('timeFrom').value;
    const to = document.getElementById('timeTo').value;
    if (!from && !to) { toast('Choose a start or end time', 'error'); return; }
    if (from && to && new Date(from) >= new Date(to)) { toast('The start must be before the end', 'error'); return; }
    setRange({ from: from ? new Date(from).toISOString() : null, to: to ? new Date(to).toISOString() : null });
}

function zoomTo(startMs, endMs) {
    searchState.zoom.push(searchState.range);
    searchState.range = { from: new Date(startMs).toISOString(), to: new Date(endMs).toISOString() };
    renderTimeButton();
    executeQuery();
}

function zoomOut() {
    if (!searchState.zoom.length) return;
    searchState.range = searchState.zoom.pop();
    renderTimeButton();
    executeQuery();
}

/* ===== Running ===== */

async function executeQuery() {
    const input = document.getElementById('queryInput');
    const query = input.value.trim() || 'find *';
    const errorBox = document.getElementById('searchError');
    const button = document.getElementById('searchBtn');
    hideSuggestions();
    errorBox.style.display = 'none';
    renderTimeButton();

    const run = ++searchState.running;
    button.disabled = true;
    renderStatus({ loading: true });

    const range = queryHasOwnTime(query) ? { start: null, end: null } : CabinetSearch.resolveRange(searchState.range);
    const body = { query, index: searchState.index || undefined };
    if (range.start !== null) body.startTime = new Date(range.start).toISOString();
    if (range.end !== null) body.endTime = new Date(range.end).toISOString();
    const isEvents = CabinetSearch.returnsEvents(query);
    body.limit = isEvents ? eventLimit() : 50000;

    // The timeline counts every match, so it also says how many there are in all
    const timelineStart = range.start ?? null;
    const timelineEnd = range.end ?? Date.now();
    let timelinePromise = Promise.resolve(null);
    let spanMs = 0;
    if (isEvents) {
        const span = CabinetSearch.chooseSpan(timelineStart ?? timelineEnd - 30 * 86400000, timelineEnd);
        spanMs = span.ms;
        const timelineQuery = CabinetSearch.timelineQuery(query, span.span);
        if (timelineQuery) {
            const timelineBody = { ...body, query: timelineQuery, limit: 50000 };
            timelinePromise = api('POST', '/api/v1/query/execute', timelineBody).catch(() => null);
        }
    }

    try {
        const [data, timeline] = await Promise.all([api('POST', '/api/v1/query/execute', body), timelinePromise]);
        if (run !== searchState.running) return;
        searchState.results = data;
        searchState.rows = data.results || [];
        searchState.isEvents = isEvents;
        searchState.page = 0;
        searchState.expanded = new Set();
        searchState.sort = { column: null, direction: 1 };
        searchState.ranQuery = input.value.trim();
        searchState.terms = CabinetSearch.searchTerms(query);
        searchState.summary = isEvents ? CabinetSearch.summarizeFields(searchState.rows) : [];
        searchState.spanMs = spanMs;
        if (timeline && Array.isArray(timeline.results)) {
            const first = timelineStart ?? (timeline.results.length ? CabinetSearch.parseTime(timeline.results[0]._time ?? timeline.results[0].Timestamp) : null);
            searchState.buckets = first === null ? [] : CabinetSearch.timelineBuckets(timeline.results, first, timelineEnd, spanMs);
            searchState.total = searchState.buckets.reduce((sum, b) => sum + b.count, 0);
        } else {
            searchState.buckets = [];
            searchState.total = null;
        }

        addToHistory(searchState.ranQuery || query);
        history.replaceState({ page: 'search' }, '', '/' + CabinetSearch.encodeState({ query: searchState.ranQuery, range: searchState.range, index: searchState.index }));
        // Events open on Events (or the table, if chosen); a computed result opens as a chart when it can be drawn
        if (isEvents && searchState.view === 'chart') searchState.view = 'events';
        if (!isEvents && searchState.view === 'events') searchState.view = CabinetCharts.shape(searchState.rows).kind === 'table' ? 'table' : 'chart';
        renderSearch();
    } catch (e) {
        if (run !== searchState.running) return;
        errorBox.textContent = e.message;
        errorBox.style.display = 'block';
        renderStatus({ error: true });
    } finally {
        if (run === searchState.running) button.disabled = false;
    }
}

/** Shows rows that came from elsewhere, such as a search job, as a search's results. */
function showSearchResults(rows, label) {
    searchState.results = { results: rows, count: rows.length, executionTimeMs: 0 };
    searchState.rows = rows;
    searchState.isEvents = rows.length > 0 && ('RawLine' in rows[0] || '_raw' in rows[0]);
    searchState.summary = searchState.isEvents ? CabinetSearch.summarizeFields(rows) : [];
    searchState.buckets = [];
    searchState.total = null;
    searchState.page = 0;
    searchState.terms = [];
    searchState.view = searchState.isEvents ? 'events' : 'table';
    renderSearch();
    renderStatus({ label });
}

function renderSearch() {
    renderStatus();
    renderTimeline();
    renderFields();
    selectTab(searchState.view);
    renderResultsView();
}

function renderStatus(options = {}) {
    const box = document.getElementById('searchStatus');
    if (options.loading) { box.innerHTML = '<span class="sx-spinner" aria-hidden="true"></span><span>Searching...</span>'; return; }
    if (options.error) { box.innerHTML = ''; return; }
    const data = searchState.results;
    if (!data) { box.innerHTML = ''; return; }
    const parts = [];
    const shown = searchState.rows.length;
    if (options.label) parts.push(`<span>${esc(options.label)}</span>`);
    if (searchState.isEvents && searchState.total !== null) {
        parts.push(`<span><strong>${fmt(searchState.total)}</strong> matching events</span>`);
        if (searchState.total > shown) parts.push(`<span>showing the newest ${fmt(shown)}</span>`);
    } else {
        parts.push(`<span><strong>${fmt(data.count)}</strong> ${searchState.isEvents ? 'events' : 'rows'}</span>`);
    }

    if (data.executionTimeMs !== undefined) parts.push(`<span>${fmt(data.executionTimeMs)} ms</span>`);
    if (data.eventsScanned) parts.push(`<span>${fmt(data.eventsScanned)} events read</span>`);
    if (data.pushedDown) parts.push('<span class="sx-note" title="The database computed this, rather than reading each event">computed in the database</span>');
    if (data.truncated) parts.push('<span class="sx-note warn" title="A stage held as many rows as a query may; raise Query limits in Settings, or narrow the time range">incomplete: hit a row limit</span>');
    if (data.approximate) parts.push('<span class="sx-note" title="Percentiles from a sample of the values">approximate percentiles</span>');
    if (searchState.isEvents && searchState.total !== null && searchState.total > shown && shown >= eventLimit()) {
        parts.push(`<span class="sx-note" title="Change it under Settings, Display">loads up to ${fmt(eventLimit())} events</span>`);
    }

    box.innerHTML = parts.join('');
}

/* ===== Timeline ===== */

function renderTimeline() {
    const panel = document.getElementById('timelinePanel');
    const buckets = searchState.buckets;
    if (!searchState.isEvents || buckets.length === 0) { panel.hidden = true; return; }
    panel.hidden = false;
    document.getElementById('timelineZoomOut').hidden = searchState.zoom.length === 0;
    document.getElementById('timelineTitle').innerHTML = `<strong>Events over time</strong>, ${esc(CabinetSearch.spanLabel(searchState.spanMs))} per bar`;

    const plot = document.getElementById('timelinePlot');
    const width = Math.max(320, Math.round(plot.clientWidth || 800));
    const height = 96;
    plot.innerHTML = CabinetSearch.timelineSvg(buckets, width, height, null) + '<div class="chart-tooltip" hidden></div>';
    const svg = plot.querySelector('svg');
    const tooltip = plot.querySelector('.chart-tooltip');
    if (!svg) return;
    const layout = CabinetSearch.timelineLayout(buckets, width, height);
    const hover = svg.querySelector('.chart-hover');
    const toIndex = event => {
        const rect = svg.getBoundingClientRect();
        return layout.indexAt(((event.clientX - rect.left) / rect.width) * width);
    };
    let dragFrom = null;
    let current = -1;

    const show = i => {
        current = i;
        const b = buckets[i];
        hover.innerHTML = `<rect x="${layout.x(i).toFixed(1)}" y="${layout.margin.top}" width="${layout.step.toFixed(1)}" height="${layout.plotHeight}" fill="var(--text-primary)" fill-opacity="0.06"/>`;
        tooltip.innerHTML = `<div class="chart-tooltip-title">${esc(CabinetSearch.formatDateTime(b.start))} to ${esc(CabinetSearch.formatDateTime(b.end))}</div>` +
            `<div class="chart-tooltip-row"><span class="chart-swatch" style="background:var(--series-1)"></span><span>Events</span><strong>${esc(fmt(b.count))}</strong></div>`;
        tooltip.hidden = false;
        const rect = svg.getBoundingClientRect();
        const left = (layout.x(i) / width) * rect.width;
        tooltip.style.left = `${Math.min(Math.max(0, left + 12), Math.max(0, rect.width - tooltip.offsetWidth))}px`;
        tooltip.style.top = '4px';
    };
    const hide = () => { current = -1; hover.innerHTML = ''; tooltip.hidden = true; };
    const drawSelection = (a, b) => {
        const existing = svg.querySelector('.timeline-selection');
        if (existing) existing.remove();
        if (a === null) return;
        const from = Math.min(a, b);
        const to = Math.max(a, b);
        const rect = document.createElementNS('http://www.w3.org/2000/svg', 'rect');
        rect.setAttribute('class', 'timeline-selection');
        rect.setAttribute('x', layout.x(from).toFixed(1));
        rect.setAttribute('y', String(layout.margin.top));
        rect.setAttribute('width', (layout.x(to) + layout.step - layout.x(from)).toFixed(1));
        rect.setAttribute('height', String(layout.plotHeight));
        svg.insertBefore(rect, hover);
    };

    svg.addEventListener('pointerdown', e => { dragFrom = toIndex(e); svg.setPointerCapture(e.pointerId); drawSelection(dragFrom, dragFrom); });
    svg.addEventListener('pointermove', e => {
        const i = toIndex(e);
        show(i);
        if (dragFrom !== null) drawSelection(dragFrom, i);
    });
    svg.addEventListener('pointerup', e => {
        if (dragFrom === null) return;
        const i = toIndex(e);
        const from = Math.min(dragFrom, i);
        const to = Math.max(dragFrom, i);
        dragFrom = null;
        drawSelection(null);
        zoomTo(buckets[from].start, buckets[to].end);
    });
    svg.addEventListener('pointerleave', () => { if (dragFrom === null) hide(); });
    svg.addEventListener('blur', hide);
    svg.addEventListener('keydown', e => {
        if (e.key === 'ArrowRight' || e.key === 'ArrowLeft') {
            e.preventDefault();
            const step = e.key === 'ArrowRight' ? 1 : -1;
            show(Math.min(buckets.length - 1, Math.max(0, (current < 0 ? (step > 0 ? -1 : buckets.length) : current) + step)));
        } else if ((e.key === 'Enter' || e.key === ' ') && current >= 0) {
            e.preventDefault();
            zoomTo(buckets[current].start, buckets[current].end);
        } else if (e.key === 'Escape') {
            hide();
        }
    });
}

/* ===== Fields ===== */

function toggleFieldSidebar() {
    const sidebar = document.getElementById('fieldSidebar');
    sidebar.hidden = !sidebar.hidden;
    localStorage.setItem('cabinet_fields_open', String(!sidebar.hidden));
    if (!sidebar.hidden) renderFields();
}

function fieldArgs(...values) {
    return esc(JSON.stringify(values));
}

function renderFields() {
    const sidebar = document.getElementById('fieldSidebar');
    const body = document.getElementById('fieldSidebarBody');
    if (sidebar.hidden) return;
    const all = searchState.summary;
    if (!searchState.isEvents || all.length === 0) {
        document.getElementById('fieldCount').textContent = 'Fields';
        body.innerHTML = `<div class="sx-empty">${searchState.results ? 'Fields appear for searches that return events.' : 'Run a search to see its fields.'}</div>`;
        return;
    }

    const filter = document.getElementById('fieldFilter').value.trim().toLowerCase();
    const shown = all.filter(f => !filter || f.name.toLowerCase().includes(filter));
    document.getElementById('fieldCount').textContent = `${all.length} fields`;
    const selected = shown.filter(f => searchState.selectedFields.includes(f.name));
    const others = shown.filter(f => !searchState.selectedFields.includes(f.name));
    const total = searchState.rows.length;

    const fieldHtml = f => {
        const open = searchState.openFields.has(f.name);
        const meta = `${Math.round(f.coverage * 100)}% &middot; ${f.distinctIsLowerBound ? '&gt;' : ''}${fmt(f.distinct)}`;
        let detail = '';
        if (open) {
            const stats = [`In ${fmt(f.count)} of ${fmt(total)} events loaded`, `${f.distinctIsLowerBound ? 'over ' : ''}${fmt(f.distinct)} distinct values`];
            if (f.numeric) stats.push(`min ${fmt(f.numeric.min)}, max ${fmt(f.numeric.max)}, mean ${CabinetCharts.formatNumber(f.numeric.mean)}`);
            const canFilter = CabinetSearch.fieldName(f.name) !== null;
            const values = f.top.map(v => `
                <div class="sx-value">
                    <span class="sx-value-label" title="${esc(v.value)}">${esc(v.value)}</span>
                    <span class="sx-value-count">${fmt(v.count)} &middot; ${(v.share * 100).toFixed(v.share < 0.1 ? 1 : 0)}%</span>
                    <span>${canFilter ? `<button type="button" class="sx-icon-btn" title="Only events with this value" aria-label="Filter on ${esc(f.name)} = ${esc(v.value)}" data-click="filterOnSummary" data-args="${fieldArgs(f.name, v.value, false)}">+</button>
                    <button type="button" class="sx-icon-btn" title="Leave out events with this value" aria-label="Exclude ${esc(f.name)} = ${esc(v.value)}" data-click="filterOnSummary" data-args="${fieldArgs(f.name, v.value, true)}">&minus;</button>` : ''}</span>
                    <span class="sx-value-bar"><span style="width:${(v.share * 100).toFixed(1)}%"></span></span>
                </div>`).join('');
            const name = CabinetSearch.fieldName(f.name);
            detail = `<div class="sx-field-detail">
                <div class="sx-field-stats">${stats.map(esc).join('<br>')}</div>
                ${values}
                <div class="sx-field-actions">
                    <button type="button" class="btn btn-secondary btn-sm" data-click="toggleColumn" data-args="${fieldArgs(f.name)}">${searchState.selectedFields.includes(f.name) ? 'Remove from events' : 'Show in events'}</button>
                    ${name ? `<button type="button" class="btn btn-secondary btn-sm" data-click="runFieldReport" data-args="${fieldArgs(f.name, 'top')}">Top values, all events</button>
                    <button type="button" class="btn btn-secondary btn-sm" data-click="runFieldReport" data-args="${fieldArgs(f.name, 'time')}">${f.numeric ? 'Average over time' : 'Count over time'}</button>` : ''}
                </div>
            </div>`;
        }

        return `<div class="sx-field"><button type="button" aria-expanded="${open}" data-click="toggleFieldDetail" data-args="${fieldArgs(f.name)}">
            <span class="sx-field-name" title="${esc(f.name)}">${esc(f.name)}</span><span class="sx-field-meta" title="Share of events with it, and distinct values">${meta}</span></button>${detail}</div>`;
    };

    body.innerHTML =
        (selected.length ? `<div class="sx-fields-section">Shown in events</div>${selected.map(fieldHtml).join('')}` : '') +
        (others.length ? `<div class="sx-fields-section">${selected.length ? 'Other fields' : 'Fields'}</div>${others.map(fieldHtml).join('')}` : '') +
        (shown.length === 0 ? '<div class="sx-empty">No field matches.</div>' : '');
}

function toggleFieldDetail(name) {
    if (searchState.openFields.has(name)) searchState.openFields.delete(name);
    else searchState.openFields.add(name);
    renderFields();
}

function toggleColumn(name) {
    const fields = searchState.selectedFields;
    const at = fields.indexOf(name);
    if (at >= 0) fields.splice(at, 1); else fields.push(name);
    localStorage.setItem('cabinet_selected_fields', JSON.stringify(fields));
    renderFields();
    renderResultsView();
}

function applyFilter(field, value, exclude) {
    const current = document.getElementById('queryInput').value.trim() || searchState.ranQuery || 'find *';
    const next = CabinetSearch.addFilter(current, field, value, exclude);
    if (next === null) { toast(`"${field}" can't be named in a query`, 'error'); return; }
    setQuery(next);
    executeQuery();
}

function filterOnSummary(field, text, exclude) {
    applyFilter(field, CabinetSearch.filterValue(field, text, searchState.rows), exclude);
}

function filterOnEvent(index, field, exclude) {
    const row = searchState.rows[index];
    if (row) applyFilter(field, row[field], exclude);
}

function runFieldReport(field, kind) {
    const name = CabinetSearch.fieldName(field);
    if (!name) return;
    const base = CabinetSearch.baseSearch(document.getElementById('queryInput').value.trim() || searchState.ranQuery || 'find *');
    const summary = searchState.summary.find(f => f.name === field);
    const span = CabinetSearch.chooseSpan(...(() => { const r = CabinetSearch.resolveRange(searchState.range); const end = r.end ?? Date.now(); return [r.start ?? end - 7 * 86400000, end]; })()).span;
    const query = kind === 'top'
        ? `${base} | top 20 ${name}`
        : summary && summary.numeric ? `${base} | timechart span=${span} avg(${name})` : `${base} | timechart span=${span} count by ${name}`;
    setQuery(query);
    executeQuery();
}

/* ===== Results ===== */

function selectTab(view) {
    searchState.view = view;
    document.querySelectorAll('#resultTabs .tab').forEach(tab => {
        const active = tab.dataset.args.includes(`"${view}"`);
        tab.classList.toggle('active', active);
        if (active && typeof markTabSelected === 'function') markTabSelected(tab);
    });
    ['events', 'table', 'chart', 'json'].forEach(v => document.getElementById('resultView-' + v).classList.toggle('hide', v !== view));
}

function switchResultTab(view) {
    selectTab(view);
    renderResultsView();
}

function sortedRows() {
    const { column, direction } = searchState.sort;
    if (!column) return searchState.rows;
    const value = row => row[column];
    return [...searchState.rows].sort((a, b) => {
        const x = value(a);
        const y = value(b);
        if (x === y) return 0;
        if (x === null || x === undefined) return 1;
        if (y === null || y === undefined) return -1;
        return (typeof x === 'number' && typeof y === 'number' ? x - y : String(x).localeCompare(String(y), undefined, { numeric: true })) * direction;
    });
}

function renderPager(count) {
    const pages = Math.max(1, Math.ceil(count / searchState.pageSize));
    searchState.page = Math.min(searchState.page, pages - 1);
    const first = count === 0 ? 0 : searchState.page * searchState.pageSize + 1;
    const last = Math.min(count, (searchState.page + 1) * searchState.pageSize);
    document.getElementById('pageInfo').textContent = count === 0 ? '' : `${fmt(first)}-${fmt(last)} of ${fmt(count)}`;
}

function renderResultsView() {
    const view = searchState.view;
    if (view === 'events') renderEvents();
    else if (view === 'table') renderTable();
    else if (view === 'chart') CabinetCharts.render(document.getElementById('chartContainer'), searchState.rows, { height: 320, title: 'Search results' });
    else document.getElementById('resultView-json').textContent = JSON.stringify(searchState.rows.slice(0, 5000), null, 2) + (searchState.rows.length > 5000 ? '\n// first 5,000 shown; export for all' : '');
    if (view === 'events' || view === 'table') renderPager(searchState.rows.length);
    else document.getElementById('pageInfo').textContent = '';
}

function renderEmptyState() {
    const list = SEARCH_EXAMPLES.map(q => `<li><button type="button" data-click="runExample" data-args="${esc(JSON.stringify([q]))}">${esc(q)}</button></li>`).join('');
    const stored = typeof storedEventCount === 'number' ? `<p>${fmt(storedEventCount)} events stored.</p>` : '';
    document.getElementById('searchEmpty').innerHTML = `${stored}Type a search above, or start with one of these:<ul>${list}</ul>`;
}

function runExample(query) {
    setQuery(query);
    executeQuery();
}

function renderEvents() {
    const list = document.getElementById('eventList');
    const empty = document.getElementById('searchEmpty');
    if (!searchState.results) { list.innerHTML = ''; empty.hidden = false; return; }
    empty.hidden = true;
    if (!searchState.isEvents) {
        list.innerHTML = '<li class="sx-empty">This search returns a table. See the Table and Chart tabs.</li>';
        return;
    }

    const rows = searchState.rows;
    if (rows.length === 0) {
        list.innerHTML = `<li class="sx-empty">No events match. Try a longer time range${searchState.index ? ', all indexes,' : ''} or fewer filters.</li>`;
        return;
    }

    const start = searchState.page * searchState.pageSize;
    const page = rows.slice(start, start + searchState.pageSize);
    list.innerHTML = page.map((row, offset) => {
        const i = start + offset;
        const open = searchState.expanded.has(i);
        const raw = row.RawLine ?? row._raw ?? '';
        const chips = searchState.selectedFields
            .filter(f => row[f] !== undefined && row[f] !== null && row[f] !== '')
            .map(f => `<span class="sx-chip">${esc(f)}=<b>${esc(f === 'Severity' ? CabinetSearch.SEVERITIES[row[f]] ?? row[f] : CabinetSearch.displayValue(row[f]))}</b></span>`).join('');
        return `<li class="sx-event${open ? ' open' : ''}">
            <div class="sx-event-row" tabindex="0" role="button" aria-expanded="${open}" data-click="toggleEvent" data-args="[${i}]">
                <span class="sx-caret" aria-hidden="true">&#9654;</span>
                <span class="sx-time-cell">${esc(fmtTime(row.Timestamp ?? row._time))}</span>
                <div><div class="sx-raw">${typeof row.Severity === 'number' && row.Severity >= 3 ? sevBadge(row.Severity) + ' ' : ''}${CabinetSearch.highlight(raw, searchState.terms)}</div>${chips ? `<div class="sx-chips">${chips}</div>` : ''}</div>
            </div>
            ${open ? eventDetailHtml(row, i) : ''}
        </li>`;
    }).join('');
}

function eventDetailHtml(row, i) {
    const keys = Object.keys(row).filter(k => !k.startsWith('_') && k !== 'RawLine').sort((a, b) => {
        const builtin = k => (k in CabinetSearch.BUILTIN_FIELDS || k === 'Id' || k === 'Tags' ? 0 : 1);
        return builtin(a) - builtin(b) || a.localeCompare(b);
    });
    const rows = keys.map(k => {
        const value = k === 'Severity' && typeof row[k] === 'number' ? `${CabinetSearch.SEVERITIES[row[k]] ?? row[k]} (${row[k]})` : k === 'Timestamp' ? fmtTime(row[k]) : CabinetSearch.displayValue(row[k]);
        const canFilter = CabinetSearch.fieldName(k) !== null && k !== 'Id' && k !== 'Timestamp' && typeof row[k] !== 'object';
        const actions = canFilter
            ? `<button type="button" class="sx-icon-btn" title="Only events with this value" aria-label="Filter on ${esc(k)}" data-click="filterOnEvent" data-args="${fieldArgs(i, k, false)}">+</button>
               <button type="button" class="sx-icon-btn" title="Leave out events with this value" aria-label="Exclude ${esc(k)}" data-click="filterOnEvent" data-args="${fieldArgs(i, k, true)}">&minus;</button>`
            : '';
        const column = `<button type="button" class="sx-icon-btn" title="${searchState.selectedFields.includes(k) ? 'Stop showing in events' : 'Show in events'}" aria-label="Show ${esc(k)} in events" data-click="toggleColumn" data-args="${fieldArgs(k)}">${searchState.selectedFields.includes(k) ? '&#9733;' : '&#9734;'}</button>`;
        return `<tr><td>${esc(k)}</td><td class="sx-detail-value">${esc(value ?? '')}</td><td>${actions} ${column}</td></tr>`;
    }).join('');
    return `<div class="sx-event-detail"><table><tbody>${rows}</tbody></table>
        <div class="sx-field-actions"><button type="button" class="btn btn-secondary btn-sm" data-click="copyEvent" data-args="[${i}]">Copy event</button>
        <button type="button" class="btn btn-secondary btn-sm" data-click="showEventContext" data-args="[${i}]">Events around this one</button></div></div>`;
}

function toggleEvent(i) {
    if (searchState.expanded.has(i)) searchState.expanded.delete(i); else searchState.expanded.add(i);
    renderEvents();
}

async function copyEvent(i) {
    const row = searchState.rows[i];
    if (!row) return;
    try { await navigator.clipboard.writeText(JSON.stringify(row, null, 2)); toast('Event copied', 'success'); }
    catch (e) { toast('Copying needs clipboard access: ' + e.message, 'error'); }
}

/** Everything from the same host and source a minute either side. */
function showEventContext(i) {
    const row = searchState.rows[i];
    if (!row) return;
    const time = CabinetSearch.parseTime(row.Timestamp ?? row._time);
    if (Number.isNaN(time)) return;
    let query = 'find *';
    if (row.Host) query = CabinetSearch.addFilter(query, 'Host', row.Host, false);
    if (row.SourceFile) query = CabinetSearch.addFilter(query, 'SourceFile', row.SourceFile, false);
    searchState.zoom.push(searchState.range);
    searchState.range = { from: new Date(time - 60000).toISOString(), to: new Date(time + 60000).toISOString() };
    setQuery(query);
    renderTimeButton();
    executeQuery();
}

function tableColumns() {
    const columns = CabinetSearch.columnsOf(searchState.rows);
    if (!searchState.isEvents) return columns;
    const chosen = ['Timestamp', ...searchState.selectedFields.filter(f => columns.includes(f))];
    return [...new Set([...chosen, 'RawLine'])].filter(c => columns.includes(c));
}

function renderTable() {
    const head = document.getElementById('tableHead');
    const body = document.getElementById('tableBody');
    const rows = sortedRows();
    if (rows.length === 0) {
        head.innerHTML = '';
        body.innerHTML = `<tr><td class="sx-empty">${searchState.results ? 'No results.' : 'Run a search to see results.'}</td></tr>`;
        return;
    }

    const columns = tableColumns();
    head.innerHTML = `<tr>${columns.map(c => {
        const sort = searchState.sort.column === c ? (searchState.sort.direction > 0 ? 'ascending' : 'descending') : 'none';
        return `<th scope="col" tabindex="0" aria-sort="${sort}" data-click="sortBy" data-args="${fieldArgs(c)}">${esc(c)}</th>`;
    }).join('')}</tr>`;
    const start = searchState.page * searchState.pageSize;
    body.innerHTML = rows.slice(start, start + searchState.pageSize).map(row => `<tr>${columns.map(c => {
        const value = c === 'Timestamp' || c === '_time' ? fmtTime(row[c]) : c === 'Severity' && typeof row[c] === 'number' ? CabinetSearch.SEVERITIES[row[c]] ?? row[c] : CabinetSearch.displayValue(row[c]);
        return `<td title="${esc(value ?? '')}">${esc(value ?? '')}</td>`;
    }).join('')}</tr>`).join('');
}

function sortBy(column) {
    const sort = searchState.sort;
    if (sort.column === column) sort.direction = -sort.direction; else { sort.column = column; sort.direction = 1; }
    searchState.page = 0;
    renderTable();
    renderPager(searchState.rows.length);
}

function nextPage() {
    const pages = Math.ceil(searchState.rows.length / searchState.pageSize);
    if (searchState.page < pages - 1) { searchState.page++; renderResultsView(); document.querySelector('.sx-results').scrollIntoView({ block: 'start' }); }
}

function prevPage() {
    if (searchState.page > 0) { searchState.page--; renderResultsView(); document.querySelector('.sx-results').scrollIntoView({ block: 'start' }); }
}

/* ===== History, menu, export ===== */

function addToHistory(query) {
    const history = storedJson('cabinet_query_history', []).filter(h => h !== query);
    history.unshift(query);
    localStorage.setItem('cabinet_query_history', JSON.stringify(history.slice(0, 30)));
    renderQueryHistory();
}

function renderQueryHistory() {
    const history = storedJson('cabinet_query_history', []);
    document.getElementById('queryHistory').innerHTML = history.slice(0, 6).map(q =>
        `<button type="button" title="${esc(q)}" data-click="runExample" data-args="${esc(JSON.stringify([q]))}">${esc(q)}</button>`).join('');
}

function clearSearchHistory() {
    localStorage.removeItem('cabinet_query_history');
    renderQueryHistory();
    closeSearchMenu();
    toast('Search history cleared', 'success');
}

function toggleSearchMenu() {
    const menu = document.getElementById('searchMenu');
    menu.hidden = !menu.hidden;
    document.getElementById('searchMenuButton').setAttribute('aria-expanded', String(!menu.hidden));
    if (!menu.hidden) menu.querySelector('button').focus();
}

function closeSearchMenu() {
    const menu = document.getElementById('searchMenu');
    if (menu && !menu.hidden) { menu.hidden = true; document.getElementById('searchMenuButton').setAttribute('aria-expanded', 'false'); }
}

async function copySearchLink() {
    closeSearchMenu();
    const link = window.location.origin + '/' + CabinetSearch.encodeState({ query: document.getElementById('queryInput').value.trim(), range: searchState.range, index: searchState.index });
    try { await navigator.clipboard.writeText(link); toast('Link copied', 'success'); }
    catch (e) { toast(`Copy this link: ${link}`, 'info'); }
}

function toggleWrap() {
    closeSearchMenu();
    const nowrap = document.getElementById('eventList').classList.toggle('nowrap');
    localStorage.setItem('cabinet_wrap', String(!nowrap));
}

function exportResults(format) {
    closeSearchMenu();
    const rows = searchState.rows;
    if (!rows || rows.length === 0) { toast('Nothing to export yet', 'info'); return; }
    const text = format === 'json' ? JSON.stringify(rows, null, 2) : CabinetSearch.toCsv(rows);
    const blob = new Blob([text], { type: format === 'json' ? 'application/json' : 'text/csv' });
    const a = document.createElement('a');
    a.href = URL.createObjectURL(blob);
    a.download = `cabinet-${new Date().toISOString().slice(0, 19).replace(/:/g, '')}.${format === 'json' ? 'json' : 'csv'}`;
    a.click();
    setTimeout(() => URL.revokeObjectURL(a.href), 1000);
}
