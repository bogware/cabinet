/*
 * Live Tail (Platinum 3g): events as they arrive, newest at the bottom. Pause is the default
 * button and Space toggles it. "Only show" takes plain English, read through /query/understand
 * into BQL: its words go to the server's stream filter, and its simple field conditions are
 * checked here as each event arrives (CabinetSearch.liveFilter / matchesLive). The last 5,000
 * matching lines are kept, as a ring. Everything from a log goes through highlightHtml or esc.
 */
window.EXTRA_PAGES = window.EXTRA_PAGES || {};

const TAIL_KEEP = 5000;
const TAIL_SPARK_SECONDS = 20;
const TAIL_LEVELS = ['TRACE', 'DEBUG', 'INFO', 'WARN', 'ERROR', 'CRIT', 'FATAL'];

const tail = {
    controller: null,
    paused: false,
    lines: [],
    seq: 0,
    rendered: 0,
    filter: { words: '', conditions: [], unsupported: null, query: '', explanation: '' },
    perSecond: new Map(),
    shownPerSecond: new Map(),
    timer: null,
    ended: null,
};

window.EXTRA_PAGES.livetail = { title: 'Live Tail', load: () => openLiveTail(), leave: () => leaveLiveTail() };

function openLiveTail() {
    loadTailIndexes();
    const hints = document.getElementById('tailHints');
    if (hints && /Mac|iPhone|iPad/.test(navigator.platform || '')) hints.innerHTML = hints.innerHTML.replace('Ctrl+F', '&#8984;F');
    if (!tail.timer) tail.timer = setInterval(renderTailRate, 1000);
    renderTailRate();
    renderTailCount();
    if (!tail.controller) startTail();
}

function leaveLiveTail() {
    stopTail();
    if (tail.timer) { clearInterval(tail.timer); tail.timer = null; }
}

async function loadTailIndexes() {
    const select = document.getElementById('tailIndex');
    try {
        const data = await api('GET', '/api/v1/indexes');
        const indexes = (Array.isArray(data) ? data : (data.data || data.indexes || []))
            .map(i => (typeof i === 'string' ? i : (i.name || i.index))).filter(Boolean);
        const current = select.value || 'main';
        const names = indexes.length ? indexes : ['main'];
        select.innerHTML = names.map(n => `<option value="${esc(n)}">${esc(n)}</option>`).join('');
        select.value = names.includes(current) ? current : names[0];
    } catch (e) {
        // "main" stays, which is where events go unless routed elsewhere
        select.title = 'Indexes could not be listed: ' + e.message;
    }
}

document.addEventListener('change', event => {
    if (event.target && event.target.id === 'tailIndex' && tail.controller) { stopTail(); startTail(); }
});

/* ===== The stream ===== */

function startTail() {
    stopTail();
    const index = document.getElementById('tailIndex').value || 'main';
    let url = `/api/v1/logs/stream/live?index=${encodeURIComponent(index)}&pollIntervalMs=1000`;
    if (tail.filter.words) url += `&filter=${encodeURIComponent(tail.filter.words)}`;
    const controller = new AbortController();
    tail.controller = controller;
    tail.ended = null;
    setTailPaused(false);
    renderTailButtons();
    renderTailEnded();

    (async () => {
        try {
            const res = await fetch(url, { headers: { ...authHeaders(), Accept: 'text/event-stream' }, signal: controller.signal });
            if (!res.ok || !res.body) {
                let message = `the server answered ${res.status}`;
                try { const body = await res.json(); message = body.detail || body.title || message; } catch (e) { console.warn('No detail with the error', e); }
                throw new Error(message);
            }
            const reader = res.body.getReader();
            const decoder = new TextDecoder();
            let buffer = '';
            for (;;) {
                const { value, done } = await reader.read();
                if (done) break;
                buffer += decoder.decode(value, { stream: true });
                const lines = buffer.split('\n');
                buffer = lines.pop();
                for (const line of lines) {
                    if (!line.startsWith('data: ')) continue;
                    try { receiveTailEvent(JSON.parse(line.slice(6))); }
                    catch (parseError) { console.warn('Skipped a live tail line', parseError); }
                }
            }
            if (tail.controller === controller) tail.ended = 'The stream closed, as streams do after a while.';
        } catch (e) {
            if (e.name !== 'AbortError' && tail.controller === controller) tail.ended = `The stream stopped: ${e.message}.`;
        }
        if (tail.controller === controller) {
            tail.controller = null;
            renderTailButtons();
            renderTailEnded();
        }
    })();
}

function stopTail() {
    if (!tail.controller) return;
    const controller = tail.controller;
    tail.controller = null;
    controller.abort();
    renderTailButtons();
}

/** Start or stop the stream. */
function toggleTail() {
    if (tail.controller) stopTail(); else startTail();
    renderTailEnded();
}

function renderTailButtons() {
    const running = !!tail.controller;
    const start = document.getElementById('tailStartBtn');
    if (start) start.textContent = running ? 'Stop' : 'Start';
    const pause = document.getElementById('tailPauseBtn');
    if (pause) pause.disabled = !running && !tail.paused;
}

function renderTailEnded() {
    const box = document.getElementById('tailOutput');
    const old = box.querySelector('.lt-ended');
    if (old) old.remove();
    if (tail.controller) return;
    const note = document.createElement('div');
    note.className = 'lt-ended';
    note.innerHTML = errorHtml(tail.ended || 'The stream is stopped.', tail.ended ? 'Reconnect' : 'Start', 'toggleTail');
    box.appendChild(note);
    box.scrollTop = box.scrollHeight;
}

/* ===== Arriving events ===== */

function secondNow() { return Math.floor(Date.now() / 1000); }

function bump(map) {
    const s = secondNow();
    map.set(s, (map.get(s) || 0) + 1);
    if (map.size > TAIL_SPARK_SECONDS + 5) [...map.keys()].filter(k => k < s - TAIL_SPARK_SECONDS - 2).forEach(k => map.delete(k));
}

/** Each event as it arrives: counted, checked against the filter, kept, and shown unless paused. */
function receiveTailEvent(evt) {
    bump(tail.perSecond);
    if (!CabinetSearch.matchesLive(evt, tail.filter)) return;
    bump(tail.shownPerSecond);
    tail.lines.push({ seq: ++tail.seq, evt });
    if (tail.lines.length > TAIL_KEEP) tail.lines.splice(0, tail.lines.length - TAIL_KEEP);
    if (!tail.paused) renderTailLines();
    else renderTailCount();
}

function tailMessage(evt) {
    const fields = evt.fields || evt.Fields || {};
    const message = fields.Message ?? fields.message;
    return typeof message === 'string' && message.trim() !== '' ? message : (evt.rawLine ?? evt.RawLine ?? JSON.stringify(evt));
}

function tailTime(ts) {
    const ms = CabinetSearch.parseTime(ts);
    if (Number.isNaN(ms)) return '';
    const d = new Date(ms);
    const pad = (n, w = 2) => String(n).padStart(w, '0');
    return `${pad(d.getHours())}:${pad(d.getMinutes())}:${pad(d.getSeconds())}.${pad(d.getMilliseconds(), 3)}`;
}

function tailRowHtml(line) {
    const evt = line.evt;
    const level = CabinetSearch.severityLevel(evt.severity ?? evt.Severity);
    const kind = Number.isNaN(level) ? 'info' : level >= 4 ? 'error' : level === 3 ? 'warn' : 'info';
    const { html } = highlightHtml(tailMessage(evt), highlightTerms(document.getElementById('tailHighlight').value));
    return `<div class="lt-row lt-${kind}" data-seq="${line.seq}" role="button" tabindex="-1" data-click="inspectTailLine" data-args="[${line.seq}]">` +
        `<span class="lt-bar" aria-hidden="true"></span>` +
        `<span class="lt-time">${esc(tailTime(evt.timestamp ?? evt.Timestamp))}</span>` +
        `<span class="lt-sev">${Number.isNaN(level) ? '' : TAIL_LEVELS[Math.min(6, Math.max(0, level))]}</span>` +
        `<span class="lt-host" title="${esc(evt.host ?? evt.Host ?? '')}">${esc(evt.host ?? evt.Host ?? '')}</span>` +
        `<span class="lt-msg">${html}</span></div>`;
}

/** Adds lines not yet on the page, drops ones the ring let go, and follows the bottom if it was there. */
function renderTailLines() {
    const box = document.getElementById('tailOutput');
    const atBottom = box.scrollHeight - box.scrollTop - box.clientHeight < 40;
    const fresh = tail.lines.filter(l => l.seq > tail.rendered);
    if (fresh.length) {
        const ended = box.querySelector('.lt-ended');
        const html = fresh.map(tailRowHtml).join('');
        if (ended) ended.insertAdjacentHTML('beforebegin', html); else box.insertAdjacentHTML('beforeend', html);
        tail.rendered = fresh[fresh.length - 1].seq;
        const terms = highlightTerms(document.getElementById('tailHighlight').value);
        if (document.getElementById('tailPauseOnMatch').checked && terms.length && fresh.some(l => highlightHtml(tailMessage(l.evt), terms).matched)) setTailPaused(true);
    }
    const oldest = tail.lines.length ? tail.lines[0].seq : tail.seq + 1;
    while (box.firstElementChild && box.firstElementChild.dataset.seq && Number(box.firstElementChild.dataset.seq) < oldest) box.firstElementChild.remove();
    if (atBottom) box.scrollTop = box.scrollHeight;
    renderTailCount();
}

function renderTailCount() {
    const label = document.getElementById('tailCount');
    if (!label) return;
    const waiting = tail.lines.filter(l => l.seq > tail.rendered).length;
    label.textContent = `${tail.paused && waiting ? `${fmt(waiting)} waiting · ` : ''}Keeping last ${fmt(TAIL_KEEP)} lines`;
}

/** Events per second over the last 20 seconds, and the share the filter lets through. */
function renderTailRate() {
    const spark = document.getElementById('tailSpark');
    if (!spark) return;
    const now = secondNow();
    const counts = [];
    for (let s = now - TAIL_SPARK_SECONDS; s < now; s++) counts.push(tail.perSecond.get(s) || 0);
    const max = Math.max(1, ...counts);
    spark.innerHTML = counts.map(c => `<span style="height:${Math.max(4, Math.round((c / max) * 100))}%"></span>`).join('');
    const recent = counts.slice(-5);
    const rate = recent.reduce((a, b) => a + b, 0) / recent.length;
    let shown = 0;
    for (let s = now - 5; s < now; s++) shown += tail.shownPerSecond.get(s) || 0;
    const received = recent.reduce((a, b) => a + b, 0);
    const share = tail.filter.conditions.length && received ? ` · ${Math.round((shown / received) * 100)}% shown` : '';
    document.getElementById('tailRate').innerHTML = `<b>${esc(rate > 0 && rate < 10 ? rate.toFixed(1) : fmt(Math.round(rate)))}</b> events/s${esc(share)}`;
}

/* ===== Pause, clear, inspect ===== */

function setTailPaused(paused) {
    tail.paused = paused;
    const button = document.getElementById('tailPauseBtn');
    if (button) {
        button.setAttribute('aria-pressed', String(paused));
        button.classList.toggle('paused', paused);
    }
    const label = document.getElementById('tailPauseLabel');
    if (label) label.textContent = paused ? 'Resume' : 'Pause';
    if (!paused) renderTailLines(); else renderTailCount();
}

function toggleTailPause() {
    setTailPaused(!tail.paused);
}

function clearTail() {
    tail.lines = [];
    tail.rendered = tail.seq;
    document.querySelectorAll('#tailOutput .lt-row').forEach(row => row.remove());
    renderTailCount();
}

function inspectTailLine(seq) {
    const line = tail.lines.find(l => l.seq === seq);
    if (line && typeof showEventDetail === 'function') showEventDetail(line.evt);
}

document.addEventListener('keydown', event => {
    if (currentPage !== 'livetail' || event.key !== ' ' || event.ctrlKey || event.metaKey || event.altKey) return;
    if (event.target.closest('input, textarea, select, button, [contenteditable], .modal-bg.show')) return;
    if (document.querySelector('.modal-bg.show, .drawer.show')) return;
    event.preventDefault();
    toggleTailPause();
});

document.addEventListener('keydown', event => {
    if (event.key === 'Enter' && event.target && event.target.id === 'tailFilter') { event.preventDefault(); applyTailFilter(); }
});

/* ===== Only show ===== */

/** Reads the plain-English filter into BQL, says how it was understood, and restarts the stream with it. */
async function applyTailFilter() {
    const text = document.getElementById('tailFilter').value.trim();
    const said = document.getElementById('tailUnderstood');
    if (!text) {
        tail.filter = { words: '', conditions: [], unsupported: null, query: '', explanation: '' };
        said.textContent = 'Showing every event as it arrives.';
        startTail();
        return;
    }
    said.innerHTML = loadingHtml('Reading the filter…');
    let understood;
    try {
        understood = await api('POST', '/api/v1/query/understand', { query: text });
    } catch (e) {
        said.innerHTML = errorHtml(`The filter couldn't be read: ${e.message}`, 'Try again', 'applyTailFilter');
        return;
    }
    if (!understood.willRun || !understood.query) {
        const message = (understood.problems || [])[0]?.message || "Couldn't tell what to show.";
        said.innerHTML = errorHtml(`Not sure what you mean: ${message}`, 'Show everything', 'clearTailFilter');
        return;
    }
    const filter = CabinetSearch.liveFilter(understood.query);
    if (filter.unsupported) {
        said.innerHTML = errorHtml(`Live Tail can match words and conditions joined by "and", but not “${filter.unsupported}”. Search can.`, 'Open in Search', 'openTailFilterInSearch');
        tail.pendingQuery = understood.query;
        return;
    }
    tail.filter = { ...filter, query: understood.query, explanation: understood.explanation || '' };
    const fixes = (understood.corrections || []).map(c => `“${esc(c.from)}” &rarr; ${esc(c.to)}`).join(', ');
    said.innerHTML = `Understood as <code>${esc(understood.query)}</code>` +
        (understood.explanation ? ` <span class="lt-said">${esc(understood.explanation)}</span>` : '') +
        (fixes ? ` <span class="lt-said">Fixed: ${fixes}</span>` : '') +
        ` <button type="button" class="btn-link" data-click="clearTailFilter">Show everything</button>`;
    clearTail();
    startTail();
}

function clearTailFilter() {
    document.getElementById('tailFilter').value = '';
    applyTailFilter();
}

function openTailFilterInSearch() {
    const query = tail.pendingQuery || tail.filter.query;
    navigate('search');
    if (query) { setQuery(query); executeQuery(query); }
}

/** Keeps the filter as a saved question, in the Search page's Save dialog. */
function saveTailAsSearch() {
    const query = tail.filter.query || 'find *';
    navigate('search');
    setQuery(query);
    saveCurrentQuery();
}
