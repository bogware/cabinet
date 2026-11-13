/*
 * The search page: wires CabinetSearch's logic to the page. Log content only reaches the page
 * through esc() or CabinetSearch's escaping. Handlers are named in data-click attributes, as the
 * content security policy forbids inline script.
 *
 * Layout (Platinum 3a and 3c): the Ask field and the default Search button; "Understood as" with
 * the sentence, one chip per pipeline step and the actions; a result card whose view (Ranked, Over
 * time, Table, Events) is chosen from the query's shape and can be overridden per question; a
 * right column with "Ask next", "About this result" and Save / Add to dashboard / Alert me. In the
 * Events view the right column becomes the inspector: the selected event and the fields in these
 * results.
 */

const searchState = {
    range: { preset: '24h' },
    index: '',
    indexes: [],
    zoom: [],
    results: null,
    rows: [],
    isEvents: true,
    view: 'events',
    auto: { view: 'events', reason: '' },
    overridden: false,
    page: 0,
    pageSize: 100,
    sort: { column: null, direction: 1 },
    selectedFields: [],
    openFields: new Set(),
    selected: -1,
    buckets: [],
    spanMs: 0,
    total: null,
    terms: [],
    summary: [],
    running: 0,
    ranQuery: '',
    query: '',
    suggestion: { items: [], active: -1, from: 0, to: 0 },
    understood: null,
    examples: [],
    next: [],
    catalog: null,
    builder: { conditions: [] },
};

const SEARCH_EXAMPLES = [
    'find *',
    'find * | where severity >= 4',
    'find * | stats count by host',
    'find * | timechart span=1h count by source_type',
    'find * | top 10 host',
];

/** The view someone chose for a question, kept per question in this browser. */
const VIEW_CHOICES_KEY = 'cabinet_view_choices';

function storedJson(key, fallback) {
    try { const value = JSON.parse(localStorage.getItem(key)); return value ?? fallback; }
    catch (e) { return fallback; }
}

function storeJson(key, value) {
    try { localStorage.setItem(key, JSON.stringify(value)); }
    catch (e) { console.warn('This browser would not keep a setting', e); }
}

function eventLimit() {
    return parseInt(localStorage.getItem('cabinet_event_limit'), 10) || 1000;
}

/* ===== Setup ===== */

function initSearchPage() {
    searchState.selectedFields = storedJson('cabinet_selected_fields', ['Host', 'SourceType']);
    searchState.pageSize = parseInt(localStorage.getItem('cabinet_page_size'), 10) || 100;
    document.getElementById('resultPageSize').value = String(searchState.pageSize);
    if (localStorage.getItem('cabinet_wrap') === 'true') document.getElementById('eventList').classList.add('wrap');

    const saved = localStorage.getItem('cabinet_default_range');
    if (saved !== null) searchState.range = saved === '' ? { preset: 'all' } : { preset: saved };

    renderPresets();
    renderTimeButton();
    renderIndexPicker();
    renderQueryHistory();
    renderEmptyState();
    renderResultsChrome();
    renderAskNext();
    loadSearchIndexes();

    const input = document.getElementById('queryInput');
    input.addEventListener('input', () => { syncEditor(); updateSuggestions(); schedulePreview(); });
    input.addEventListener('keydown', onEditorKey);
    input.addEventListener('scroll', () => {
        document.getElementById('queryHighlight').scrollTop = input.scrollTop;
        document.getElementById('queryProblems').scrollTop = input.scrollTop;
    });
    input.addEventListener('focus', updateSuggestions);
    input.addEventListener('blur', () => setTimeout(hideSuggestions, 150));
    input.addEventListener('click', updateSuggestions);

    document.getElementById('resultPageSize').addEventListener('change', e => {
        searchState.pageSize = parseInt(e.target.value, 10) || 100;
        localStorage.setItem('cabinet_page_size', String(searchState.pageSize));
        searchState.page = 0;
        renderResultsView();
    });
    document.getElementById('fieldFilter').addEventListener('input', renderFields);
    document.getElementById('eventList').addEventListener('keydown', onEventListKey);

    document.addEventListener('click', e => {
        if (!e.target.closest('.sx-time')) closeTimePicker();
        if (!e.target.closest('.sx-more')) closeSearchMenu();
        if (!e.target.closest('.sx-pop')) closeIndexPicker();
    });
    document.addEventListener('keydown', e => {
        if (e.key === 'Escape') closeIndexPicker();
    });

    window.addEventListener('hashchange', () => restoreSearchFromHash(false));
    window.addEventListener('resize', () => {
        if (searchState.buckets.length && !document.getElementById('timelinePanel').hidden) renderTimeline();
    });

    ['builderWords', 'builderTime', 'builderShow', 'builderFn', 'builderField', 'builderColumns', 'builderBy', 'builderLimit'].forEach(id => {
        const el = document.getElementById(id);
        el.addEventListener('input', renderBuilderQuery);
        el.addEventListener('change', renderBuilderQuery);
    });

    syncEditor();
    loadExamples();
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
    renderIndexPicker();
    renderTimeButton();
    if (state.query || !onLoad) executeQuery();
    return true;
}

/* ===== Index pop-up ===== */

async function loadSearchIndexes() {
    try {
        const data = await api('GET', '/api/v1/indexes');
        const items = Array.isArray(data) ? data : (data.indexes || data.data || []);
        searchState.indexes = items.map(i => typeof i === 'string' ? i : (i.name || i.index || i.Name)).filter(Boolean);
        renderIndexPicker();
    } catch (e) {
        // Without the list, searching every index still works; the pop-up says why it's short
        searchState.indexes = [];
        document.getElementById('indexPickerButton').title = 'Indexes could not be listed: ' + e.message;
    }
}

function renderIndexPicker() {
    document.getElementById('indexPickerValue').textContent = searchState.index || 'All';
    const names = ['', ...searchState.indexes.filter(n => n !== '')];
    if (searchState.index && !names.includes(searchState.index)) names.push(searchState.index);
    document.getElementById('indexPicker').innerHTML = names.map(name =>
        `<button type="button" class="menu-item" role="menuitemradio" aria-checked="${name === searchState.index}" data-click="chooseIndex" data-args="${esc(JSON.stringify([name]))}">` +
        `<span>${name ? esc(name) : 'All indexes'}</span><span aria-hidden="true">${name === searchState.index ? '&#10003;' : ''}</span></button>`).join('');
}

function toggleIndexPicker() {
    const menu = document.getElementById('indexPicker');
    menu.hidden = !menu.hidden;
    document.getElementById('indexPickerButton').setAttribute('aria-expanded', String(!menu.hidden));
    if (!menu.hidden) {
        closeTimePicker();
        closeSearchMenu();
        const current = menu.querySelector('[aria-checked="true"]') || menu.querySelector('button');
        if (current) current.focus();
    }
}

function closeIndexPicker() {
    const menu = document.getElementById('indexPicker');
    if (menu && !menu.hidden) {
        menu.hidden = true;
        document.getElementById('indexPickerButton').setAttribute('aria-expanded', 'false');
    }
}

function chooseIndex(name) {
    searchState.index = name;
    closeIndexPicker();
    renderIndexPicker();
    if (searchState.ranQuery) executeQuery();
}

/* ===== Editor ===== */

function setQuery(text) {
    const input = document.getElementById('queryInput');
    input.value = text;
    syncEditor();
}

function syncEditor() {
    const input = document.getElementById('queryInput');
    // Marks belong to the text they were made for
    document.getElementById('queryProblems').innerHTML = '';
    // A trailing newline keeps the overlay's last line as tall as the textarea's
    document.getElementById('queryHighlight').innerHTML = CabinetSearch.highlightQuery(input.value) + '\n';
    input.style.height = 'auto';
    input.style.height = Math.min(200, Math.max(44, input.scrollHeight + 2)) + 'px';
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
    let items;
    let from = 0;
    let to = input.value.length;
    if (input.value.trim() === '') {
        const history = storedJson('cabinet_query_history', []);
        items = [...history.slice(0, 6).map(q => ({ label: q, kind: 'recent', insert: q })),
            ...(searchState.examples.length ? searchState.examples.map(e => e.text) : SEARCH_EXAMPLES)
                .filter(q => !history.includes(q)).slice(0, 8).map(q => ({ label: q, kind: 'example', insert: q }))];
    } else {
        const result = CabinetSearch.complete(input.value, input.selectionStart, knownFields());
        items = result.items;
        from = result.from;
        to = result.to;
        requestServerCompletions(input.value, input.selectionStart, items, from, to);
    }

    showSuggestions(items, from, to);
}

let completionRequest = 0;

/** Adds values from the data and question starters to the page's own suggestions, when they arrive. */
async function requestServerCompletions(text, caret, local, from, to) {
    const ask = ++completionRequest;
    try {
        const server = await api('GET', `/api/v1/query/complete?text=${encodeURIComponent(text)}&cursor=${caret}`);
        const input = document.getElementById('queryInput');
        if (ask !== completionRequest || input.value !== text || document.activeElement !== input) return;
        showSuggestions(CabinetSearch.mergeCompletions(local, server, from, to), from, to);
    } catch (e) {
        // The page's own suggestions are enough without these
    }
}

function showSuggestions(items, from, to) {
    const box = document.getElementById('querySuggestions');
    const input = document.getElementById('queryInput');
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
    // Suggestions still on their way are for text that's no longer being typed
    completionRequest++;
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
        const before = input.value.slice(0, item.from ?? s.from);
        const after = input.value.slice(item.to ?? s.to);
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
    const query = document.getElementById('queryInput').value;
    const ownTime = queryHasOwnTime(query) || (query.trim() !== '' && query.trim() === searchState.ranQuery && queryHasOwnTime(searchState.query));
    const label = ownTime ? 'Set in the question' : CabinetSearch.resolveRange(searchState.range).label;
    document.getElementById('timePickerValue').textContent = label;
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
        closeIndexPicker();
        closeSearchMenu();
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

async function executeQuery(exactQuery) {
    const input = document.getElementById('queryInput');
    const typed = input.value.trim() || 'find *';
    const button = document.getElementById('searchBtn');
    hideSuggestions();
    clearTimeout(previewTimer);
    previewRequest++;
    document.getElementById('searchError').style.display = 'none';
    renderTimeButton();

    const run = ++searchState.running;
    button.disabled = true;
    renderStatus({ loading: true });

    // Plain words become BQL first, so the timeline, fields and charts work from the real query
    let query = typeof exactQuery === 'string' ? exactQuery : typed;
    if (typeof exactQuery !== 'string') {
        try {
            const understood = await api('POST', '/api/v1/query/understand', { query: typed });
            if (run !== searchState.running) return;
            renderUnderstood(understood, false);
            renderProblems(understood.problems);
            if (!understood.willRun) {
                // Results of the last search would read as an answer to this one
                searchState.results = null;
                searchState.rows = [];
                searchState.buckets = [];
                searchState.selected = -1;
                renderSearch();
                renderStatus({ unclear: true });
                button.disabled = false;
                return;
            }

            query = understood.query;
        } catch (e) {
            // Without understanding, the server runs what was typed and says if it's wrong
            renderUnderstood({ kind: 'exact', query: typed, willRun: true }, false);
        }
    } else {
        renderUnderstood({ kind: 'exact', query: exactQuery, willRun: true }, false);
        renderProblems([]);
    }

    const range = queryHasOwnTime(query) ? { start: null, end: null } : CabinetSearch.resolveRange(searchState.range);
    const body = { query, exact: true, index: searchState.index || undefined };
    if (range.start !== null) body.startTime = new Date(range.start).toISOString();
    if (range.end !== null) body.endTime = new Date(range.end).toISOString();
    const isEvents = CabinetSearch.returnsEvents(query);
    // Events: as many as the page shows. Anything computed: the server's own maximum.
    if (isEvents) body.limit = eventLimit();

    // The timeline counts every match by severity, so it also says how many there are in all
    const timelineStart = range.start ?? null;
    const timelineEnd = range.end ?? Date.now();
    let timelinePromise = Promise.resolve(null);
    let spanMs = 0;
    const span = CabinetSearch.chooseSpan(timelineStart ?? timelineEnd - 30 * 86400000, timelineEnd);
    if (isEvents) {
        spanMs = span.ms;
        const bySeverity = CabinetSearch.severityTimelineQuery(query, span.span);
        if (bySeverity) {
            const timelineBody = { ...body, query: bySeverity };
            delete timelineBody.limit;
            const plain = { ...timelineBody, query: CabinetSearch.timelineQuery(query, span.span) };
            timelinePromise = api('POST', '/api/v1/query/execute', timelineBody)
                .catch(() => api('POST', '/api/v1/query/execute', plain))
                .catch(() => null);
        }
    }

    try {
        const [data, timeline] = await Promise.all([api('POST', '/api/v1/query/execute', body), timelinePromise]);
        if (run !== searchState.running) return;
        searchState.results = data;
        searchState.rows = data.results || [];
        searchState.isEvents = isEvents;
        searchState.page = 0;
        searchState.selected = isEvents && searchState.rows.length ? 0 : -1;
        searchState.sort = { column: null, direction: 1 };
        searchState.ranQuery = input.value.trim();
        searchState.query = query;
        searchState.span = span.span;
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

        addToHistory(typed);
        history.replaceState({ page: 'search' }, '', '/' + CabinetSearch.encodeState({ query: searchState.ranQuery, range: searchState.range, index: searchState.index }));
        pickView();
        renderSearch();
    } catch (e) {
        if (run !== searchState.running) return;
        searchState.results = null;
        searchState.rows = [];
        searchState.buckets = [];
        renderSearch();
        renderStatus({ error: e.message });
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
    searchState.selected = searchState.isEvents && rows.length ? 0 : -1;
    searchState.terms = [];
    searchState.query = '';
    searchState.auto = searchState.isEvents
        ? { view: 'events', reason: `Showing ${label}.` }
        : { view: CabinetCharts.shape(rows).kind === 'bar' ? 'ranked' : 'table', reason: `Showing ${label}.` };
    searchState.view = searchState.auto.view;
    searchState.overridden = false;
    renderUnderstood(null);
    renderSearch();
    renderStatus({ label });
}

/* ===== Choosing the view ===== */

function viewKey() {
    return (searchState.ranQuery || searchState.query || '').trim().toLowerCase();
}

/** The view from the question's shape, unless someone chose another for this question. */
function pickView() {
    searchState.auto = CabinetSearch.chooseView(searchState.query);
    if (!searchState.isEvents && searchState.auto.view === 'events') searchState.auto = { view: 'table', reason: 'Chosen because this search builds a table of its own.' };
    const chosen = storedJson(VIEW_CHOICES_KEY, {})[viewKey()];
    const usable = chosen && (searchState.isEvents || chosen !== 'events');
    searchState.view = usable ? chosen : searchState.auto.view;
    searchState.overridden = !!usable && chosen !== searchState.auto.view;
}

function chooseResultView(view) {
    const choices = storedJson(VIEW_CHOICES_KEY, {});
    const key = viewKey();
    if (key) {
        if (view === searchState.auto.view) delete choices[key];
        else choices[key] = view;
        // Only the most recent questions are remembered
        const keys = Object.keys(choices);
        if (keys.length > 200) keys.slice(0, keys.length - 200).forEach(k => delete choices[k]);
        storeJson(VIEW_CHOICES_KEY, choices);
    }
    searchState.view = view;
    searchState.overridden = view !== searchState.auto.view;
    searchState.page = 0;
    renderSearch();
}

function useSuggestedView() {
    chooseResultView(searchState.auto.view);
}

function showJsonView() {
    closeSearchMenu();
    searchState.view = 'json';
    searchState.overridden = true;
    renderSearch();
}

/* ===== Drawing the results ===== */

function renderSearch() {
    renderResultsChrome();
    renderStatus();
    renderTimeline();
    renderSide();
    renderResultsView();
}

/** The result card's bar: the view switcher, why this view, and how many. */
function renderResultsChrome() {
    const has = !!searchState.results;
    const view = searchState.view;
    document.getElementById('viewSwitch').hidden = !has;
    document.querySelectorAll('#viewSwitch [data-view]').forEach(b => b.setAttribute('aria-pressed', String(b.dataset.view === view)));
    const reason = document.getElementById('viewReason');
    if (!has) {
        reason.innerHTML = '<span class="sx-reason-intro">Results appear here. The view is chosen from what you ask.</span>';
    } else if (searchState.overridden && view !== 'json') {
        reason.innerHTML = `You chose this view for this question. <button type="button" class="btn-link" data-click="useSuggestedView">Use the suggested view</button>`;
    } else if (view === 'json') {
        reason.innerHTML = `The rows as the server sent them. <button type="button" class="btn-link" data-click="useSuggestedView">Back to the suggested view</button>`;
    } else {
        reason.textContent = searchState.auto.reason;
    }
    const count = document.getElementById('resultCount');
    if (!has) { count.textContent = ''; return; }
    const shown = searchState.rows.length;
    const total = searchState.isEvents && searchState.total !== null ? Math.max(searchState.total, shown) : shown;
    count.textContent = `${fmt(total)} ${searchState.isEvents ? (total === 1 ? 'event' : 'events') : (total === 1 ? 'row' : 'rows')}`;
}

function renderStatus(options = {}) {
    const box = document.getElementById('searchStatus');
    if (options.loading) { box.innerHTML = loadingHtml('Searching…'); box.hidden = false; return; }
    if (options.error) {
        box.innerHTML = errorHtml(`The search didn't run: ${options.error}`, 'Edit the question', 'focusQuery');
        box.hidden = false;
        return;
    }
    if (options.unclear) {
        box.innerHTML = errorHtml('Nothing ran, because the question wasn’t clear. Pick a reading above, or rephrase it.', 'Edit the question', 'focusQuery');
        box.hidden = false;
        return;
    }
    const data = searchState.results;
    const notes = [];
    if (options.label) notes.push(esc(options.label));
    if (data) {
        const shown = searchState.rows.length;
        if (searchState.isEvents && searchState.total !== null && searchState.total > shown) {
            notes.push(`Showing the newest ${fmt(shown)} of ${fmt(searchState.total)}${shown >= eventLimit() ? ` (loads up to ${fmt(eventLimit())}; change it in Settings, Display)` : ''}.`);
        }
        if (data.truncated) notes.push('<span class="sx-note-warn">Incomplete: a step held as many rows as a query may. Narrow the time range, or raise Query limits in Settings.</span>');
    }
    box.innerHTML = notes.map(n => `<span>${n}</span>`).join('');
    box.hidden = notes.length === 0;
}

function focusQuery() {
    const input = document.getElementById('queryInput');
    input.focus();
    input.select();
}

function renderResultsView() {
    const view = searchState.view;
    const shown = { events: view === 'events', table: view === 'table', chart: view === 'ranked' || view === 'timeseries', json: view === 'json' };
    Object.entries(shown).forEach(([v, on]) => document.getElementById('resultView-' + v).classList.toggle('hide', !on));
    if (!searchState.results) {
        document.getElementById('resultView-events').classList.remove('hide');
        ['table', 'chart', 'json'].forEach(v => document.getElementById('resultView-' + v).classList.add('hide'));
        renderEvents();
        document.getElementById('resultPager').hidden = true;
        return;
    }

    if (view === 'events') renderEvents();
    else if (view === 'table') renderTable();
    else if (view === 'ranked' || view === 'timeseries') renderChart(view);
    else document.getElementById('resultView-json').textContent = JSON.stringify(searchState.rows.slice(0, 5000), null, 2) + (searchState.rows.length > 5000 ? '\n// first 5,000 shown; export for all' : '');

    const paged = (view === 'events' && searchState.isEvents) || view === 'table';
    document.getElementById('resultPager').hidden = !paged || searchState.rows.length <= Math.min(25, searchState.pageSize);
    if (paged) renderPager(searchState.rows.length);
}

function renderChart(view) {
    const container = document.getElementById('chartContainer');
    const rows = searchState.rows;
    if (rows.length === 0) { container.innerHTML = '<div class="sx-empty">Nothing matched. Try a longer time range or fewer filters.</div>'; return; }
    const viz = view === 'ranked' ? 'bar' : 'line';
    const kind = CabinetCharts.shape(rows, viz).kind;
    if (kind === 'table') {
        const why = view === 'ranked'
            ? (searchState.isEvents ? 'These are events, so there is nothing to rank yet. Ask for a count by a field, such as "count by host".' : 'These rows have no number to rank by, so here they are as a table.')
            : (searchState.isEvents ? 'These are events. Ask how many over time, such as "count per hour", to see a chart.' : 'These rows have no times, so here they are as a table.');
        container.innerHTML = `<p class="chart-note">${esc(why)}</p>` + CabinetCharts.tableHtml(CabinetSearch.columnsOf(rows), rows, 50);
        return;
    }
    CabinetCharts.render(container, rows, { viz, height: 300, title: 'Search results' });
}

function renderEmptyState() {
    const stored = typeof storedEventCount === 'number' ? `<p>${fmt(storedEventCount)} events stored.</p>` : '';
    const examples = searchState.examples.length
        ? searchState.examples
        : SEARCH_EXAMPLES.map(q => ({ category: 'Examples', text: q, query: q }));
    const groups = {};
    examples.forEach(e => (groups[e.category] = groups[e.category] || []).push(e));
    const gallery = Object.entries(groups).map(([category, items]) =>
        `<div class="sx-gallery-group"><h4>${esc(category)}</h4>` +
        items.map(e => `<button type="button" data-click="runExample" data-args="${esc(JSON.stringify([e.text]))}">${esc(e.text)}` +
            `${e.query && e.query !== e.text ? `<code>${esc(e.query)}</code>` : ''}</button>`).join('') + '</div>').join('');
    document.getElementById('searchEmpty').innerHTML =
        `${stored}<p>Ask in plain words or write BQL. Start with one of these, or build a search by picking fields:</p><div class="sx-gallery">${gallery}</div>`;
}

/** Starter questions that fit the data the caller can read. */
async function loadExamples() {
    try {
        const examples = await api('GET', '/api/v1/query/examples');
        if (Array.isArray(examples)) {
            searchState.examples = examples;
            if (!searchState.results) renderEmptyState();
            renderAskNext();
        }
    } catch (e) {
        // The built-in examples stay
    }
}

/* ===== The right column ===== */

function renderSide() {
    const side = document.getElementById('searchSide');
    const inspecting = !!searchState.results && searchState.view === 'events' && searchState.isEvents;
    side.classList.toggle('sx-side-wide', inspecting);
    document.getElementById('eventInspector').hidden = !inspecting;
    document.getElementById('fieldSidebar').hidden = !inspecting;
    document.getElementById('askNext').hidden = inspecting;
    if (inspecting) { renderInspector(); renderFields(); }
    else renderAskNext();
    renderAbout();
}

function renderAbout() {
    const box = document.getElementById('aboutResult');
    const data = searchState.results;
    box.hidden = !data;
    if (!data) return;
    const item = (label, value) => `<dt>${esc(label)}</dt><dd>${esc(value)}</dd>`;
    const rows = searchState.rows.length;
    const where = data.pushedDown ? 'Computed in storage' : (data.eventsScanned ? 'Read event by event' : (searchState.query ? 'In Cabinet' : 'From a background search'));
    let html = item('Rows', fmt(rows));
    if (data.executionTimeMs !== undefined) html += item('Took', `${fmt(data.executionTimeMs)} ms`);
    if (data.eventsScanned) html += item('Read', `${fmt(data.eventsScanned)} ${data.eventsScanned === 1 ? 'event' : 'events'}`);
    html += item('Where', where);
    if (data.approximate) html += item('Note', 'Percentiles from a sample of the values');
    if (data.truncated) html += item('Note', 'Incomplete: hit a row limit');
    document.getElementById('aboutResultBody').innerHTML = html;
}

function renderAskNext() {
    const body = document.getElementById('askNextBody');
    if (!body) return;
    const items = [];
    if (searchState.results && searchState.query) {
        CabinetSearch.followUps(searchState.query, {
            rows: searchState.rows,
            fields: searchState.summary.map(f => f.name),
            span: searchState.span,
        }).forEach(f => items.push({ label: f.label, query: f.query, exact: true }));
    }
    const current = (searchState.ranQuery || '').toLowerCase();
    const starters = searchState.examples.length ? searchState.examples : SEARCH_EXAMPLES.map(q => ({ text: q }));
    starters.filter(e => e.text.toLowerCase() !== current && !items.some(i => i.label === e.text))
        .slice(0, Math.max(0, 5 - items.length))
        .forEach(e => items.push({ label: e.text, query: e.text, exact: false }));
    searchState.next = items;
    body.innerHTML = items.length
        ? items.map((item, i) => `<button type="button" class="sx-next-item" data-click="runNext" data-args="[${i}]">${esc(item.label)}</button>`).join('')
        : '<p class="sx-side-note">Questions to try appear after your first search.</p>';
}

function runNext(index) {
    const item = searchState.next[index];
    if (!item) return;
    setQuery(item.query);
    if (item.exact) executeQuery(item.query); else executeQuery();
}

/* ===== Timeline ===== */

function renderTimeline() {
    const panel = document.getElementById('timelinePanel');
    const buckets = searchState.buckets;
    if (!searchState.results || !searchState.isEvents || searchState.view !== 'events' || buckets.length === 0) { panel.hidden = true; return; }
    panel.hidden = false;
    document.getElementById('timelineZoomOut').hidden = searchState.zoom.length === 0;
    const warnings = buckets.reduce((sum, b) => sum + (b.warn || 0), 0);
    document.getElementById('timelineTitle').innerHTML = `<strong>${esc(CabinetSearch.spanLabel(searchState.spanMs))} per bar</strong>` +
        (warnings ? ` <span class="sx-legend-warn">${esc(fmt(warnings))} warnings or worse</span>` : '');

    const plot = document.getElementById('timelinePlot');
    const width = Math.max(320, Math.round(plot.clientWidth || 800));
    const height = 64;
    plot.innerHTML = CabinetSearch.timelineSvg(buckets, width, height, null, { compact: true }) + '<div class="chart-tooltip" hidden></div>';
    const svg = plot.querySelector('svg');
    const tooltip = plot.querySelector('.chart-tooltip');
    if (!svg) return;
    const layout = CabinetSearch.timelineLayout(buckets, width, height, true);
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
        hover.innerHTML = `<rect x="${layout.x(i).toFixed(1)}" y="${layout.margin.top}" width="${layout.step.toFixed(1)}" height="${layout.plotHeight}" fill="var(--ink)" fill-opacity="0.06"/>`;
        tooltip.innerHTML = `<div class="chart-tooltip-title">${esc(CabinetSearch.formatDateTime(b.start))} to ${esc(CabinetSearch.formatDateTime(b.end))}</div>` +
            `<div class="chart-tooltip-row"><span class="chart-swatch" style="background:var(--moss)"></span><span>Events</span><strong>${esc(fmt(b.count))}</strong></div>` +
            (b.warn ? `<div class="chart-tooltip-row"><span class="chart-swatch" style="background:var(--amber-bottom)"></span><span>Warnings or worse</span><strong>${esc(fmt(b.warn))}</strong></div>` : '');
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

/* ===== Fields in these results ===== */

function fieldArgs(...values) {
    return esc(JSON.stringify(values));
}

function renderFields() {
    const sidebar = document.getElementById('fieldSidebar');
    const body = document.getElementById('fieldSidebarBody');
    if (sidebar.hidden) return;
    const all = searchState.summary;
    if (all.length === 0) {
        body.innerHTML = '<p class="sx-side-note">Fields appear for searches that return events.</p>';
        return;
    }

    const filter = document.getElementById('fieldFilter').value.trim().toLowerCase();
    const shown = all.filter(f => !filter || f.name.toLowerCase().includes(filter));
    const total = searchState.rows.length;

    const fieldHtml = f => {
        const open = searchState.openFields.has(f.name);
        const values = `${f.distinctIsLowerBound ? 'over ' : ''}${fmt(f.distinct)} ${f.distinct === 1 ? 'value' : 'values'}`;
        let detail = '';
        if (open) {
            const stats = [`In ${fmt(f.count)} of ${fmt(total)} events loaded`];
            if (f.numeric) stats.push(`lowest ${fmt(f.numeric.min)}, highest ${fmt(f.numeric.max)}, average ${CabinetCharts.formatNumber(f.numeric.mean)}`);
            const canFilter = CabinetSearch.fieldName(f.name) !== null;
            const rows = f.top.map(v => `
                <div class="sx-value">
                    <span class="sx-value-label" title="${esc(v.value)}">${esc(v.value)}</span>
                    <span class="sx-value-count">${fmt(v.count)} &middot; ${(v.share * 100).toFixed(v.share < 0.1 ? 1 : 0)}%</span>
                    <span class="sx-mini-group">${canFilter ? `<button type="button" class="sx-mini" aria-label="Only events where ${esc(f.name)} is ${esc(v.value)}" data-click="filterOnSummary" data-args="${fieldArgs(f.name, v.value, false)}">Only</button><button type="button" class="sx-mini" aria-label="Hide events where ${esc(f.name)} is ${esc(v.value)}" data-click="filterOnSummary" data-args="${fieldArgs(f.name, v.value, true)}">Hide</button>` : ''}</span>
                    <span class="sx-value-bar"><span style="width:${(v.share * 100).toFixed(1)}%"></span></span>
                </div>`).join('');
            const name = CabinetSearch.fieldName(f.name);
            detail = `<div class="sx-field-detail">
                <div class="sx-field-stats">${stats.map(esc).join('<br>')}</div>
                ${rows}
                <div class="sx-field-actions">
                    <button type="button" class="btn btn-sm" data-click="toggleColumn" data-args="${fieldArgs(f.name)}">${searchState.selectedFields.includes(f.name) ? 'Remove from the rows' : 'Show in the rows'}</button>
                    ${name ? `<button type="button" class="btn btn-sm" data-click="runFieldReport" data-args="${fieldArgs(f.name, 'top')}">Top values</button>
                    <button type="button" class="btn btn-sm" data-click="runFieldReport" data-args="${fieldArgs(f.name, 'time')}">${f.numeric ? 'Average over time' : 'Count over time'}</button>` : ''}
                </div>
            </div>`;
        }

        return `<div class="sx-field${open ? ' open' : ''}"><button type="button" class="sx-field-row" aria-expanded="${open}" data-click="toggleFieldDetail" data-args="${fieldArgs(f.name)}">
            <span class="sx-field-name" title="${esc(f.name)}">${esc(f.name)}</span><span class="sx-field-meta">${values}</span><span class="sx-field-cover" title="Share of loaded events that have it">${Math.round(f.coverage * 100)}%</span></button>${detail}</div>`;
    };

    document.getElementById('fieldCount').textContent = `Fields in these results`;
    body.innerHTML = shown.length ? shown.map(fieldHtml).join('') : '<p class="sx-side-note">No field matches.</p>';
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

/** Only or Hide: adds the filter to the question and runs it again, so "Understood as" follows. */
function applyFilter(field, value, exclude) {
    const current = searchState.query || document.getElementById('queryInput').value.trim() || 'find *';
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
    const base = CabinetSearch.baseSearch(searchState.query || document.getElementById('queryInput').value.trim() || 'find *');
    const summary = searchState.summary.find(f => f.name === field);
    const span = CabinetSearch.chooseSpan(...(() => { const r = CabinetSearch.resolveRange(searchState.range); const end = r.end ?? Date.now(); return [r.start ?? end - 7 * 86400000, end]; })()).span;
    const query = kind === 'top'
        ? `${base} | top 20 ${name}`
        : summary && summary.numeric ? `${base} | timechart span=${span} avg(${name})` : `${base} | timechart span=${span} count by ${name}`;
    setQuery(query);
    executeQuery();
}

/* ===== Understanding ===== */

let previewTimer = null;
let previewRequest = 0;

/** After a pause in typing, says what the search would run as, and marks problems. */
function schedulePreview() {
    clearTimeout(previewTimer);
    previewTimer = setTimeout(previewUnderstanding, 450);
}

async function previewUnderstanding() {
    const text = document.getElementById('queryInput').value.trim();
    const ask = ++previewRequest;
    if (!text) { renderProblems([]); return; }
    try {
        const understood = await api('POST', '/api/v1/query/understand', { query: text });
        if (ask !== previewRequest || document.getElementById('queryInput').value.trim() !== text) return;
        // Half-typed queries are often unclear; underline them, and say more only when searching
        if (understood.willRun) renderUnderstood(understood, true);
        renderProblems(understood.problems);
    } catch (e) {
        // Previews are a nicety; searching still says what's wrong
    }
}

function renderProblems(problems) {
    const input = document.getElementById('queryInput');
    document.getElementById('queryProblems').innerHTML = CabinetSearch.markProblems(input.value, problems || []) + '\n';
}

/** The alternative that runs the typed words exactly, which has its own button. */
function exactWordsChoice(u) {
    return (u.alternatives || []).findIndex(a => /exact words/i.test(a.description || '') || (a.query === u.input && u.kind === 'natural'));
}

/**
 * Shows what a search was (or would be) understood as: the sentence with its key terms in bold,
 * a chip per pipeline step, and the actions. A query run exactly is shown too, in its own words.
 */
function renderUnderstood(u, preview) {
    const box = document.getElementById('searchUnderstood');
    searchState.understood = u;
    if (!u) { box.hidden = true; box.innerHTML = ''; return; }

    const actions = [];
    const exactIndex = exactWordsChoice(u);
    if (u.willRun && u.query) actions.push('<button type="button" class="btn sx-u-btn" data-click="editUnderstood">Edit query</button>');
    if (exactIndex >= 0) actions.push('<button type="button" class="btn sx-u-btn sx-u-plain" data-click="runExactWords">Search these exact words instead</button>');
    (u.problems || []).forEach((p, i) => {
        if (p.fix) actions.push(`<button type="button" class="sx-chip fix" data-click="applyProblemFix" data-args="[${i}]">Fix: <code>${esc(p.fix)}</code></button>`);
    });
    (u.alternatives || []).forEach((a, i) => {
        if (i === exactIndex) return;
        actions.push(`<button type="button" class="sx-chip" data-click="runAlternative" data-args="[${i}]">${esc(a.description)}<span class="sx-chip-note">${esc(a.query)}</span></button>`);
    });
    if (u.suggestAssistant) actions.push('<button type="button" class="sx-chip" data-click="askAssistant">Ask the AI investigator</button>');
    const typos = (u.corrections || []).map(c => `“${esc(c.from)}” &rarr; ${esc(c.to)}`);
    const typoNote = typos.length ? `<span class="sx-typo">Fixed ${typos.length === 1 ? 'typo' : 'typos'}: ${typos.join(', ')}</span>` : '';

    let body;
    if (!u.willRun) {
        const message = (u.problems || [])[0]?.message || "Couldn't tell what this asks for.";
        body = `<h2 class="group-panel-title">Not sure what you mean</h2><p class="sx-say">${esc(message)}</p>`;
    } else {
        const steps = CabinetSearch.pipelineSteps(u.query || '');
        const sentence = u.explanation ? u.explanation.replace(/\s*$/, '').replace(/([^.!?])$/, '$1.') : CabinetSearch.sentenceFor(u.query || '');
        const chips = steps.map((s, i) =>
            `${i > 0 ? '<span class="sx-pipe" aria-hidden="true">|</span>' : ''}<button type="button" class="sx-step" data-click="editStep" data-args="[${i}]" title="Edit this step">` +
            `<code>${esc(s.code)}</code><span>${esc(s.words)}</span></button>`).join('');
        body = `<h2 class="group-panel-title">${preview ? 'Will search' : 'Understood as'}</h2>` +
            `<p class="sx-say">${CabinetSearch.emphasize(sentence, u.query || '')}</p>` +
            `<div class="sx-steps" aria-label="Steps, each a part of the query">${chips}</div>`;
    }

    box.className = 'group-panel sx-understood' + (u.willRun ? '' : ' unclear') + (preview ? ' preview' : '');
    box.innerHTML = body + (actions.length || typoNote ? `<div class="sx-understood-actions">${actions.join('')}<span class="sx-flex"></span>${typoNote}</div>` : '');
    box.hidden = false;
}

/** Puts the understood BQL in the box to change it by hand. */
function editUnderstood() {
    const u = searchState.understood;
    if (!u || !u.query) return;
    setQuery(u.query);
    renderProblems([]);
    const input = document.getElementById('queryInput');
    input.focus();
    input.setSelectionRange(input.value.length, input.value.length);
}

/** Clicking a step puts the query in the box with that step selected, ready to change. */
function editStep(index) {
    const u = searchState.understood;
    if (!u || !u.query) return;
    const step = CabinetSearch.pipelineSteps(u.query)[index];
    if (!step) return;
    setQuery(u.query);
    renderProblems([]);
    const input = document.getElementById('queryInput');
    input.focus();
    input.setSelectionRange(step.start, step.end);
    document.querySelectorAll('#searchUnderstood .sx-step').forEach((el, i) => el.classList.toggle('editing', i === index));
}

/** Runs what was typed as plain words, not as the reading. */
function runExactWords() {
    const u = searchState.understood;
    if (!u) return;
    const index = exactWordsChoice(u);
    if (index >= 0) runAlternative(index);
    else executeQuery(document.getElementById('queryInput').value.trim());
}

/** Runs one of the other readings, exactly. */
function runAlternative(index) {
    const u = searchState.understood;
    const choice = u && (u.alternatives || [])[index];
    if (!choice) return;
    setQuery(choice.query);
    executeQuery(choice.query);
}

function applyProblemFix(index) {
    const u = searchState.understood;
    const problem = u && (u.problems || [])[index];
    if (!problem || !problem.fix) return;
    setQuery(problem.fix);
    executeQuery();
}

/** Hands the question to the AI investigator, which runs searches with the same access. */
function askAssistant() {
    const question = document.getElementById('queryInput').value.trim();
    if (typeof navigate === 'function') navigate('investigate');
    const box = document.getElementById('investigateQuestion');
    if (box) { box.value = question; box.focus(); }
}

/* ===== Building a search by picking ===== */

async function toggleBuilder() {
    const panel = document.getElementById('queryBuilder');
    const button = document.getElementById('builderButton');
    panel.hidden = !panel.hidden;
    button.setAttribute('aria-expanded', String(!panel.hidden));
    button.textContent = panel.hidden ? 'Build a search by picking fields…' : 'Close the search builder';
    if (panel.hidden) return;
    if (!searchState.catalog) {
        try { searchState.catalog = await api('GET', '/api/v1/query/catalog'); }
        catch (e) { searchState.catalog = { fields: Object.keys(CabinetSearch.BUILTIN_FIELDS), values: {} }; }
        const options = searchState.catalog.fields.map(f => `<option value="${esc(f)}">${esc(f)}</option>`).join('');
        document.getElementById('builderField').innerHTML = options;
        document.getElementById('builderColumns').innerHTML = options;
        document.getElementById('builderBy').innerHTML = '<option value="">(nothing)</option>' + options;
        if (!searchState.builder.conditions.length) addBuilderCondition();
    }

    renderBuilderQuery();
    document.getElementById('builderWords').focus();
}

function addBuilderCondition() {
    searchState.builder.conditions.push({ field: '', op: '=', value: '' });
    renderBuilderConditions();
}

function removeBuilderCondition(index) {
    searchState.builder.conditions.splice(index, 1);
    renderBuilderConditions();
}

function renderBuilderConditions() {
    const fields = (searchState.catalog && searchState.catalog.fields) || [];
    const ops = [['=', 'is'], ['!=', 'is not'], ['>', 'over'], ['>=', 'at least'], ['<', 'under'], ['<=', 'at most'], ['contains', 'contains'], ['starts', 'starts with'], ['exists', 'exists'], ['missing', 'is missing']];
    document.getElementById('builderConditions').innerHTML = searchState.builder.conditions.map((c, i) => {
        const values = ((searchState.catalog && searchState.catalog.values) || {})[c.field] || [];
        return `<div class="sx-builder-cond">` +
            `<select data-cond="${i}" data-part="field" aria-label="Field"><option value="">Field...</option>${fields.map(f => `<option${f === c.field ? ' selected' : ''}>${esc(f)}</option>`).join('')}</select>` +
            `<select data-cond="${i}" data-part="op" aria-label="Comparison">${ops.map(([v, l]) => `<option value="${esc(v)}"${v === c.op ? ' selected' : ''}>${esc(l)}</option>`).join('')}</select>` +
            `<input data-cond="${i}" data-part="value" aria-label="Value" list="builderValues${i}" value="${esc(c.value)}" placeholder="Value">` +
            `<datalist id="builderValues${i}">${values.map(v => `<option value="${esc(v)}">`).join('')}</datalist>` +
            `<button type="button" class="btn btn-sm" data-click="removeBuilderCondition" data-args="[${i}]" >Remove</button></div>`;
    }).join('');
    document.querySelectorAll('#builderConditions [data-cond]').forEach(el => {
        const update = () => {
            const c = searchState.builder.conditions[+el.dataset.cond];
            const changedField = el.dataset.part === 'field' && c.field !== el.value;
            c[el.dataset.part] = el.value;
            if (changedField) renderBuilderConditions(); else renderBuilderQuery();
        };
        el.addEventListener('input', update);
        el.addEventListener('change', update);
    });
    renderBuilderQuery();
}

function builderSpec() {
    const show = document.getElementById('builderShow').value;
    const field = document.getElementById('builderField').value;
    const by = document.getElementById('builderBy').value;
    document.getElementById('builderFn').hidden = !(show === 'measure' || show === 'overtime');
    document.getElementById('builderField').hidden = !(show === 'measure' || show === 'overtime' || show === 'top' || show === 'rare');
    document.getElementById('builderColumns').hidden = show !== 'table';
    return {
        words: document.getElementById('builderWords').value,
        time: document.getElementById('builderTime').value,
        conditions: searchState.builder.conditions,
        show,
        field,
        measure: show === 'measure' || (show === 'overtime' && field) ? { fn: document.getElementById('builderFn').value, field } : null,
        by: by ? [by] : [],
        columns: [...document.getElementById('builderColumns').selectedOptions].map(o => o.value),
        limit: document.getElementById('builderLimit').value,
    };
}

function renderBuilderQuery() {
    if (document.getElementById('queryBuilder').hidden) return;
    document.getElementById('builderQuery').innerHTML = CabinetSearch.highlightQuery(CabinetSearch.buildQuery(builderSpec()));
}

function useBuilderQuery() {
    const query = CabinetSearch.buildQuery(builderSpec());
    setQuery(query);
    executeQuery(query);
}

function runExample(query) {
    setQuery(query);
    executeQuery();
}

/* ===== Events ===== */

const SEVERITY_CHIPS = ['chip-muted', 'chip-muted', 'chip-muted', 'chip-warn', 'chip-danger', 'chip-danger', 'chip-danger'];

/** A row's time, short enough for its column: "Nov 13, 9:14:02 AM" unless Settings ask for another form. */
function rowTime(ts) {
    const format = localStorage.getItem('cabinet_ts_format') || 'local';
    if (format !== 'local') return fmtTime(ts);
    const ms = CabinetSearch.parseTime(ts);
    if (Number.isNaN(ms)) return '-';
    return new Date(ms).toLocaleString(undefined, { month: 'short', day: 'numeric', hour: 'numeric', minute: '2-digit', second: '2-digit' });
}

function severityName(value) {
    const level = CabinetSearch.severityLevel(value);
    return Number.isNaN(level) ? String(value ?? '') : (CabinetSearch.SEVERITIES[level] || String(value));
}

function rowMessage(row) {
    const message = row.Message ?? row.message;
    return typeof message === 'string' && message.trim() !== '' ? message : (row.RawLine ?? row._raw ?? '');
}

function renderEvents() {
    const list = document.getElementById('eventList');
    const empty = document.getElementById('searchEmpty');
    if (!searchState.results) { list.innerHTML = ''; empty.hidden = false; return; }
    empty.hidden = true;
    if (!searchState.isEvents) {
        list.innerHTML = '<div class="sx-empty">This search returns a table, not events. Choose Ranked, Over time or Table.</div>';
        return;
    }

    const rows = searchState.rows;
    if (rows.length === 0) {
        list.innerHTML = `<div class="sx-empty">No events match. Try a longer time range${searchState.index ? ', all indexes,' : ''} or fewer filters.</div>`;
        return;
    }

    const start = searchState.page * searchState.pageSize;
    const page = rows.slice(start, start + searchState.pageSize);
    const extra = searchState.selectedFields.filter(f => f !== 'Host' && f !== 'Severity');
    list.innerHTML = page.map((row, offset) => {
        const i = start + offset;
        const selected = i === searchState.selected;
        const level = CabinetSearch.severityLevel(row.Severity);
        const sev = Number.isNaN(level) ? '' : `<span class="chip ${SEVERITY_CHIPS[Math.min(6, Math.max(0, level))]}">${esc(severityName(row.Severity))}</span>`;
        const chips = extra
            .filter(f => row[f] !== undefined && row[f] !== null && row[f] !== '')
            .map(f => `<span class="sx-chip-field">${esc(f)}=<b>${esc(CabinetSearch.displayValue(row[f]))}</b></span>`).join('');
        return `<div class="sx-row${selected ? ' selected' : ''}" id="sx-ev-${i}" role="option" tabindex="${selected ? 0 : -1}" aria-selected="${selected}" data-click="selectEvent" data-args="[${i}]">` +
            `<span class="sx-row-time">${esc(rowTime(row.Timestamp ?? row._time))}</span>` +
            `<span class="sx-row-sev">${sev}</span>` +
            `<span class="sx-row-host" title="${esc(row.Host ?? '')}">${esc(row.Host ?? '')}</span>` +
            `<span class="sx-row-msg">${CabinetSearch.highlight(rowMessage(row), searchState.terms)}${chips ? ` <span class="sx-row-chips">${chips}</span>` : ''}</span></div>`;
    }).join('');
}

/** Selects an event: moss in the list, its fields in the inspector. */
function selectEvent(index, keepFocus) {
    if (!searchState.rows[index]) return;
    searchState.selected = index;
    document.querySelectorAll('#eventList .sx-row').forEach(el => {
        const on = el.id === `sx-ev-${index}`;
        el.classList.toggle('selected', on);
        el.setAttribute('aria-selected', String(on));
        el.tabIndex = on ? 0 : -1;
        if (on && !keepFocus) el.focus({ preventScroll: true });
    });
    renderInspector();
}

function onEventListKey(e) {
    if (e.key !== 'ArrowDown' && e.key !== 'ArrowUp') return;
    const start = searchState.page * searchState.pageSize;
    const end = Math.min(searchState.rows.length, start + searchState.pageSize) - 1;
    const next = Math.min(end, Math.max(start, searchState.selected + (e.key === 'ArrowDown' ? 1 : -1)));
    if (next === searchState.selected) return;
    e.preventDefault();
    selectEvent(next);
    const el = document.getElementById(`sx-ev-${next}`);
    if (el) el.scrollIntoView({ block: 'nearest' });
}

/** The fields worth listing for an event: its own fields first, then the built-in ones. */
function inspectorFields(row) {
    const skip = new Set(['Id', 'RawLine', 'Timestamp', 'Message', 'LogLevel', 'Tags', 'LineNumber', 'ParserName']);
    const builtin = ['Host', 'SourceType', 'Severity', 'Index', 'SourceFile'];
    const own = Object.keys(row).filter(k => !k.startsWith('_') && !skip.has(k) && !builtin.includes(k)).sort((a, b) => a.localeCompare(b));
    return [...own, ...builtin.filter(k => row[k] !== undefined && row[k] !== null && row[k] !== '')];
}

function renderInspector() {
    const body = document.getElementById('inspectorBody');
    const i = searchState.selected;
    const row = searchState.rows[i];
    if (!row) { body.innerHTML = '<p class="sx-side-note">Choose an event to see its fields.</p>'; return; }
    const fields = inspectorFields(row).map(k => {
        const value = k === 'Severity' ? severityName(row[k]) : CabinetSearch.displayValue(row[k]);
        const canFilter = CabinetSearch.fieldName(k) !== null && typeof row[k] !== 'object';
        return `<div class="sx-kv"><span class="sx-kv-key" title="${esc(k)}">${esc(k)}</span><span class="sx-kv-value" title="${esc(value ?? '')}">${esc(value ?? '')}</span>` +
            `<span class="sx-mini-group">${canFilter ? `<button type="button" class="sx-mini" aria-label="Only events where ${esc(k)} is ${esc(value ?? '')}" data-click="filterOnEvent" data-args="${fieldArgs(i, k, false)}">Only</button><button type="button" class="sx-mini" aria-label="Hide events where ${esc(k)} is ${esc(value ?? '')}" data-click="filterOnEvent" data-args="${fieldArgs(i, k, true)}">Hide</button>` : ''}</span></div>`;
    }).join('');
    body.innerHTML = `<div class="sx-inspector-when">${esc(rowTime(row.Timestamp ?? row._time))}</div>` +
        `<pre class="sx-inspector-raw">${esc(row.RawLine ?? row._raw ?? '')}</pre>` +
        `<div class="sx-kv-list">${fields}</div>` +
        `<div class="sx-inspector-actions"><button type="button" class="btn btn-sm" data-click="showAddToCase" data-args="[${i}]">Add to case&hellip;</button>` +
        `<button type="button" class="btn btn-sm" data-click="showEventContext" data-args="[${i}]">Events around</button></div>` +
        `<div class="sx-inspector-more"><button type="button" class="btn-link" data-click="copyEvent" data-args="[${i}]">Copy event</button>` +
        `<button type="button" class="btn-link" data-click="openEventDetail" data-args="[${i}]">Every field&hellip;</button></div>`;
}

function openEventDetail(i) {
    const row = searchState.rows[i];
    if (row && typeof showEventDetail === 'function') showEventDetail(row);
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

/* ===== Table ===== */

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
    document.getElementById('pageInfo').textContent = count === 0 ? '' : `${fmt(first)}–${fmt(last)} of ${fmt(count)}`;
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
    if (searchState.page < pages - 1) { searchState.page++; renderResultsView(); document.getElementById('resultsCard').scrollIntoView({ block: 'start' }); }
}

function prevPage() {
    if (searchState.page > 0) { searchState.page--; renderResultsView(); document.getElementById('resultsCard').scrollIntoView({ block: 'start' }); }
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
    document.getElementById('queryHistory').innerHTML = history.length
        ? '<span class="sx-recent-label">Recent</span>' + history.slice(0, 5).map(q =>
            `<button type="button" title="${esc(q)}" data-click="runExample" data-args="${esc(JSON.stringify([q]))}">${esc(q)}</button>`).join('')
        : '';
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
    if (!menu.hidden) {
        closeIndexPicker();
        closeTimePicker();
        menu.querySelector('button').focus();
    }
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
    const wrap = document.getElementById('eventList').classList.toggle('wrap');
    localStorage.setItem('cabinet_wrap', String(wrap));
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

/* ===== Add to dashboard, Alert me when, Add to case ===== */

/** The query to keep: the BQL that ran, so it means the same thing later. */
function queryToKeep() {
    return searchState.query || document.getElementById('queryInput').value.trim();
}

function questionTitle() {
    const typed = (searchState.ranQuery || document.getElementById('queryInput').value).trim();
    return typed.length > 80 ? typed.slice(0, 77) + '…' : typed;
}

const VIEW_PANELS = { ranked: 'BarChart', timeseries: 'LineChart', table: 'Table', events: 'Table', json: 'Table' };

async function showAddToDashboard() {
    const query = queryToKeep();
    if (!query) { toast('Ask a question first', 'info'); return; }
    document.getElementById('dashPanelQuery').value = query;
    document.getElementById('dashPanelTitle').value = questionTitle();
    const single = searchState.results && CabinetCharts.shape(searchState.rows).kind === 'stat';
    document.getElementById('dashPanelType').value = single ? 'SingleValue' : (VIEW_PANELS[searchState.view] || 'Table');
    document.getElementById('dashPanelResult').innerHTML = '';
    const select = document.getElementById('dashPanelTarget');
    select.innerHTML = '<option value="">Loading dashboards…</option>';
    openModal('addPanelModal');
    try {
        const data = await api('GET', '/api/v1/dashboards');
        const dashboards = Array.isArray(data) ? data : (data.data || []);
        select.innerHTML = dashboards.map(d => `<option value="${esc(d.id)}">${esc(d.title || d.name || 'Untitled')}</option>`).join('') +
            '<option value="__new">A new dashboard…</option>';
        select.value = dashboards.length ? dashboards[0].id : '__new';
    } catch (e) {
        select.innerHTML = '<option value="__new">A new dashboard…</option>';
        document.getElementById('dashPanelResult').innerHTML = errorHtml(`Dashboards couldn't be listed: ${e.message}`, 'Try again', 'showAddToDashboard');
    }
    dashPanelTargetChanged();
}

function dashPanelTargetChanged() {
    document.getElementById('dashPanelNewGroup').hidden = document.getElementById('dashPanelTarget').value !== '__new';
}

async function confirmAddToDashboard() {
    const target = document.getElementById('dashPanelTarget').value;
    const panel = {
        title: document.getElementById('dashPanelTitle').value.trim() || 'Untitled panel',
        query: document.getElementById('dashPanelQuery').value.trim(),
        type: document.getElementById('dashPanelType').value,
        width: Number(document.getElementById('dashPanelWidth').value) || 6,
        height: 4,
        column: 0,
    };
    const result = document.getElementById('dashPanelResult');
    if (!panel.query) { result.innerHTML = errorHtml('The panel needs a query.', '', ''); return; }
    try {
        if (target === '__new') {
            const title = document.getElementById('dashPanelNewTitle').value.trim();
            if (!title) { result.innerHTML = errorHtml('Give the new dashboard a title.', '', ''); return; }
            await api('POST', '/api/v1/dashboards', { title, panels: [{ ...panel, row: 0 }] });
        } else {
            const d = await api('GET', '/api/v1/dashboards/' + encodeURIComponent(target));
            const panels = (d.panels || []).map(p => ({ id: p.id, title: p.title, query: p.query, type: p.type, row: p.row, column: p.column, width: p.width, height: p.height }));
            const row = panels.reduce((max, p) => Math.max(max, (p.row || 0) + 1), 0);
            panels.push({ ...panel, row });
            await api('PUT', '/api/v1/dashboards/' + encodeURIComponent(target), { panels });
        }
        closeModal('addPanelModal');
        toast('Added to the dashboard', 'success');
    } catch (e) {
        result.innerHTML = errorHtml(`It wasn't added: ${e.message}`, 'Try again', 'confirmAddToDashboard');
    }
}

/** Without its own time, so the schedule's period decides what each run looks at. */
function withoutTime(query) {
    const stages = CabinetSearch.splitPipeline(query);
    stages[0] = stages[0].replace(/\bfrom\s+last\s+\d+\s*[smhdw]\b/gi, ' ').replace(/\b(earliest|latest)\s*=\s*("[^"]*"|\S+)/gi, ' ').replace(/\s+/g, ' ').trim();
    if (/^(find|search)$/i.test(stages[0]) || stages[0] === '') stages[0] = 'find *';
    return stages.filter((s, i) => i === 0 || s !== '').join(' | ');
}

function showAlertWhen() {
    const query = queryToKeep();
    if (!query) { toast('Ask a question first', 'info'); return; }
    document.getElementById('alertWhenName').value = questionTitle();
    document.getElementById('alertWhenQuery').value = withoutTime(query);
    document.getElementById('alertWhenCondition').value = 'when_count_gt';
    document.getElementById('alertWhenThreshold').value = '0';
    document.getElementById('alertWhenEvery').value = '5';
    document.getElementById('alertWhenResult').innerHTML = '';
    alertWhenConditionChanged();
    openModal('alertWhenModal');
}

function alertWhenConditionChanged() {
    const condition = document.getElementById('alertWhenCondition').value;
    document.getElementById('alertWhenThresholdGroup').hidden = condition === 'no_results';
}

const ALERT_PERIODS = { 5: ['*/5 * * * *', '-5m'], 15: ['*/15 * * * *', '-15m'], 60: ['0 * * * *', '-1h'], 1440: ['0 0 * * *', '-24h'] };

async function confirmAlertWhen() {
    const [cron, range] = ALERT_PERIODS[document.getElementById('alertWhenEvery').value] || ALERT_PERIODS[5];
    const body = {
        name: document.getElementById('alertWhenName').value.trim(),
        query: document.getElementById('alertWhenQuery').value.trim(),
        cronExpression: cron,
        timeRange: range,
        enabled: true,
        alertAction: {
            condition: document.getElementById('alertWhenCondition').value,
            threshold: parseInt(document.getElementById('alertWhenThreshold').value, 10) || 0,
            severity: 3,
        },
    };
    const result = document.getElementById('alertWhenResult');
    if (!body.name || !body.query) { result.innerHTML = errorHtml('Give the alert a name and a query.', '', ''); return; }
    try {
        await api('POST', '/api/v1/scheduled-searches', body);
        closeModal('alertWhenModal');
        toast('Alert set up. It runs as a scheduled search; change it under Scheduled reports.', 'success');
    } catch (e) {
        result.innerHTML = errorHtml(`The alert wasn't set up: ${e.message}`, 'Try again', 'confirmAlertWhen');
    }
}

let caseEventIndex = -1;

async function showAddToCase(i) {
    const row = searchState.rows[i];
    if (!row) return;
    caseEventIndex = i;
    document.getElementById('caseNoteText').value = '';
    document.getElementById('caseNewTitle').value = `${severityName(row.Severity)} on ${row.Host || 'an unknown host'}`;
    document.getElementById('caseAddResult').innerHTML = '';
    const select = document.getElementById('caseTarget');
    select.innerHTML = '<option value="">Loading cases…</option>';
    openModal('addToCaseModal');
    try {
        const cases = await api('GET', '/api/v1/cases');
        const open = (Array.isArray(cases) ? cases : []).filter(c => c.status !== 'Closed');
        select.innerHTML = open.map(c => `<option value="${esc(c.id)}">${esc(c.title)}</option>`).join('') + '<option value="__new">A new case…</option>';
        select.value = open.length ? open[0].id : '__new';
    } catch (e) {
        select.innerHTML = '<option value="__new">A new case…</option>';
        document.getElementById('caseAddResult').innerHTML = errorHtml(`Cases couldn't be listed: ${e.message}`, 'Try again', 'showAddToCase', [i]);
    }
    caseTargetChanged();
}

function caseTargetChanged() {
    document.getElementById('caseNewGroup').hidden = document.getElementById('caseTarget').value !== '__new';
}

async function confirmAddToCase() {
    const row = searchState.rows[caseEventIndex];
    const result = document.getElementById('caseAddResult');
    if (!row || !row.Id) { result.innerHTML = errorHtml('This event has no ID to add.', '', ''); return; }
    const target = document.getElementById('caseTarget').value;
    const note = document.getElementById('caseNoteText').value.trim();
    try {
        if (target === '__new') {
            const title = document.getElementById('caseNewTitle').value.trim();
            if (!title) { result.innerHTML = errorHtml('Give the case a title.', '', ''); return; }
            const created = await api('POST', '/api/v1/cases', { title, eventIds: [row.Id] });
            if (note || searchState.query) await api('POST', `/api/v1/cases/${encodeURIComponent(created.id)}/items`, { note: note || null, search: searchState.query || null, eventIds: [] });
        } else {
            await api('POST', `/api/v1/cases/${encodeURIComponent(target)}/items`, { note: note || null, search: searchState.query || null, eventIds: [row.Id] });
        }
        closeModal('addToCaseModal');
        toast('Added to the case', 'success');
    } catch (e) {
        result.innerHTML = errorHtml(`It wasn't added: ${e.message}`, 'Try again', 'confirmAddToCase');
    }
}
