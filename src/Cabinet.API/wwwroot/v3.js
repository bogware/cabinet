/* Pages added in Cabinet 3: connectors and the rest. Each registers itself in window.EXTRA_PAGES,
   which app.js's navigation reads. Handlers are named functions wired through data-click, because
   the content security policy forbids inline script. Everything shown from the server goes
   through esc(). */
window.EXTRA_PAGES = window.EXTRA_PAGES || {};

/* ===== Live tail: highlighting and pausing ===== */

/** Words to highlight, as typed: plain text, never a pattern, so no input can make matching slow. */
function highlightTerms(text) {
    return String(text || '').trim().split(/\s+/).filter(Boolean).slice(0, 10);
}

/** A line as HTML with the terms marked, and whether any was found. Every part is escaped. */
function highlightHtml(text, terms) {
    const escapeHtml = s => String(s).replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;').replace(/"/g, '&quot;').replace(/'/g, '&#39;');
    const line = String(text ?? '');
    if (!terms || terms.length === 0) return { html: escapeHtml(line), matched: false };
    const pattern = new RegExp(terms.map(t => t.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')).join('|'), 'gi');
    let html = '';
    let last = 0;
    let matched = false;
    let match;
    while ((match = pattern.exec(line)) !== null) {
        matched = true;
        html += escapeHtml(line.slice(last, match.index)) + '<mark>' + escapeHtml(match[0]) + '</mark>';
        last = match.index + match[0].length;
    }
    return { html: html + escapeHtml(line.slice(last)), matched };
}

let tailPaused = false;
let tailBuffer = [];
const TAIL_BUFFER_LIMIT = 5000;
const TAIL_SHOWN_LIMIT = 2000;

function setTailPaused(paused) {
    tailPaused = paused;
    const button = document.getElementById('tailPauseBtn');
    if (button) button.textContent = paused ? 'Resume' : 'Pause';
    if (!paused) {
        const waiting = tailBuffer;
        tailBuffer = [];
        waiting.forEach(evt => showTailEvent(evt, false));
    }
    updateTailCount();
}

function toggleTailPause() {
    setTailPaused(!tailPaused);
}

function updateTailCount() {
    const label = document.getElementById('tailCount');
    if (label) label.textContent = `${tailCount} events` + (tailPaused ? `, ${tailBuffer.length} waiting` : '');
}

/** Each event as it arrives: shown, or kept while paused. A match can pause the tail. */
function receiveTailEvent(evt) {
    tailCount++;
    if (tailPaused) {
        // Past the limit, the oldest waiting events go, as the page would drop them anyway
        tailBuffer.push(evt);
        if (tailBuffer.length > TAIL_BUFFER_LIMIT) tailBuffer.shift();
        updateTailCount();
        return;
    }
    showTailEvent(evt, document.getElementById('tailPauseOnMatch')?.checked);
}

function showTailEvent(evt, pauseOnMatch) {
    const output = document.getElementById('tailOutput');
    const div = document.createElement('div');
    div.style.cssText = 'padding:2px 0;border-bottom:1px solid rgba(48,54,61,0.3)';
    const ts = evt.timestamp ? fmtTime(evt.timestamp) : '';
    const sev = evt.severity != null ? `<span class="${sevClass(evt.severity)}" style="display:inline-block;width:60px">${['TRACE', 'DEBUG', 'INFO', 'WARN', 'ERROR', 'CRIT', 'FATAL'][Math.min(parseInt(evt.severity) || 0, 6)]}</span>` : '';
    const raw = evt.rawLine || evt.RawLine || JSON.stringify(evt);
    const { html, matched } = highlightHtml(raw, highlightTerms(document.getElementById('tailHighlight')?.value));
    div.innerHTML = `<span style="color:var(--text-muted);margin-right:8px">${esc(ts)}</span>${sev}<span>${html}</span>`;
    if (matched) div.style.background = 'var(--warning-bg, rgba(210,153,34,0.15))';
    output.appendChild(div);
    output.scrollTop = output.scrollHeight;
    while (output.children.length > TAIL_SHOWN_LIMIT) output.removeChild(output.firstChild);
    updateTailCount();
    if (matched && pauseOnMatch) setTailPaused(true);
}

/* ===== Investigate ===== */
window.EXTRA_PAGES.investigate = { title: 'Investigate', load: () => loadInvestigatorStatus() };

async function loadInvestigatorStatus() {
    const note = document.getElementById('investigateStatus');
    const button = document.getElementById('investigateButton');
    try {
        const status = await api('GET', '/api/v1/investigate/status');
        button.disabled = !status.enabled;
        note.textContent = status.enabled
            ? `Ask a question about your logs. ${status.model} answers it by running searches with your access, seeing values masked, and shows the searches it ran. Check its conclusions against them.`
            : 'The AI investigator is off. An administrator can turn it on in Settings, under AI investigator.';
    } catch (e) { note.textContent = e.message; }
}

async function runInvestigation() {
    const question = document.getElementById('investigateQuestion').value.trim();
    const out = document.getElementById('investigateResult');
    const button = document.getElementById('investigateButton');
    if (!question) return;
    button.disabled = true;
    out.innerHTML = '<p class="page-note"><span class="spinner"></span> Investigating. This can take a minute.</p>';
    try {
        const r = await api('POST', '/api/v1/investigate', { question });
        const steps = (r.steps || []).map(s => `<li><code>${esc(s.query)}</code> <span style="color:var(--text-secondary)">${s.error ? 'failed: ' + esc(s.error) : esc(String(s.rows)) + ' rows'}</span>
            <button type="button" class="btn btn-secondary btn-sm" data-click="openInvestigationSearch" data-args='${esc(JSON.stringify([s.query]))}'>Open</button></li>`).join('');
        out.innerHTML = `<h4 style="margin-top:16px">Answer</h4><div style="white-space:pre-wrap">${esc(r.answer)}</div>` +
            `<h4 style="margin-top:16px">Searches it ran</h4>${steps ? `<ol>${steps}</ol>` : '<p class="page-note">None.</p>'}` +
            `<p class="page-note">${esc(r.model)}</p>`;
    } catch (e) {
        out.innerHTML = `<p style="color:var(--error)">${esc(e.message)}</p>`;
    } finally {
        button.disabled = false;
    }
}

/** Opens one of the investigator's searches on the search page. */
function openInvestigationSearch(query) {
    const input = document.getElementById('searchQuery') || document.getElementById('queryInput');
    if (input) input.value = query;
    navigate('search');
}

/* ===== Players (gaming extension) ===== */
window.EXTRA_PAGES.players = { title: 'Players', load: () => loadPlayers() };

async function loadPlayers() {
    const tbody = document.getElementById('playersBody');
    if (!tbody) return;
    try {
        const rows = await api('GET', '/api/v1/moderation/players');
        tbody.innerHTML = rows.length === 0
            ? '<tr><td colspan="5" style="text-align:center;padding:32px;color:var(--text-secondary)">No player chat in the last week.</td></tr>'
            : rows.map(r => `<tr><td><button type="button" class="btn btn-secondary btn-sm" data-click="openPlayer" data-args='${esc(JSON.stringify([r.SteamId || r.steamId]))}'>${esc(r.name || r.SteamId)}</button></td>
                <td class="mono">${esc(r.SteamId || '')}</td><td>${esc(String(r.chats ?? ''))}</td><td>${esc(String(r.names ?? ''))}</td><td>${esc(r.last_seen ? fmtTime(r.last_seen) : '')}</td></tr>`).join('');
    } catch (e) {
        tbody.innerHTML = '';
        document.getElementById('playersNote').textContent = e.message.includes('404')
            ? 'Player moderation is part of the gaming extension, which is off. An administrator can turn it on with Gaming:Enabled.'
            : e.message;
    }
}

function openPlayerFromInput() {
    const value = document.getElementById('playerLookup').value.trim();
    if (value) openPlayer(value);
}

async function openPlayer(player) {
    const panel = document.getElementById('playerProfile');
    panel.hidden = false;
    panel.innerHTML = '<span class="spinner"></span>';
    try {
        const p = await api('GET', '/api/v1/moderation/players/' + encodeURIComponent(player));
        const history = Object.entries(p.adminActions || {}).map(([a, n]) => `${esc(a)} ${esc(String(n))}`).join(', ') || 'none';
        const chat = (p.recentChat || []).map(c => `<li${c.flag ? ' style="background:var(--warning-bg, rgba(210,153,34,0.15))"' : ''}><span style="color:var(--text-muted)">${esc(c.time ? fmtTime(c.time) : '')}</span> ${esc(c.text)}${c.flag ? ` <span class="badge badge-error">${esc(c.severity || 'flagged')}</span>` : ''}</li>`).join('');
        const s = p.suggestion || {};
        panel.innerHTML = `<h4>${esc(p.names?.join(', ') || p.player)} <span class="mono" style="color:var(--text-secondary)">${esc(p.steamId || '')}</span></h4>
            <p>${esc(String(p.events))} events, first seen ${esc(p.firstSeen ? fmtTime(p.firstSeen) : '?')}, last seen ${esc(p.lastSeen ? fmtTime(p.lastSeen) : '?')}.
               ${esc(String(p.chatLines))} chat lines, ${esc(String(p.toxicLines))} flagged (${esc(String(p.severeLines))} severe). Admin actions against them: ${history}.</p>
            <div class="st-card" style="padding:12px;margin:12px 0"><strong>Suggested: ${esc(s.action || 'None')}</strong> — ${esc(s.reason || '')}
                ${s.command ? `<div class="mono" style="margin-top:8px;user-select:all">${esc(s.command)}</div>` : ''}
                <p class="page-note" style="margin-top:8px">${esc(p.suggestionNote || '')}</p></div>
            <h4>Recent chat</h4>${chat ? `<ul style="list-style:none;padding:0">${chat}</ul>` : '<p class="page-note">No chat.</p>'}`;
    } catch (e) { panel.innerHTML = `<p style="color:var(--error)">${esc(e.message)}</p>`; }
}

/* ===== Ingest pipeline ===== */
window.EXTRA_PAGES.pipelines = { title: 'Ingest Pipeline', load: () => loadPipelines() };

let pipelineRules = {};
let editingPipelineId = null;

function formatBytes(bytes) {
    const units = ['B', 'KB', 'MB', 'GB', 'TB'];
    let value = Number(bytes) || 0;
    let unit = 0;
    while (value >= 1024 && unit < units.length - 1) { value /= 1024; unit++; }
    return `${value.toFixed(unit === 0 ? 0 : 1)} ${units[unit]}`;
}

async function loadPipelines() {
    const tbody = document.getElementById('pipelineBody');
    if (!tbody) return;
    try {
        const [rules, stats] = await Promise.all([api('GET', '/api/v1/pipelines/rules'), api('GET', '/api/v1/pipelines/stats')]);
        pipelineRules = Object.fromEntries(rules.map(r => [r.id, r]));
        const byId = Object.fromEntries((stats.rules || []).map(s => [s.id, s]));
        document.getElementById('pipelineSavings').textContent =
            `Rules that drop, sample, route or redact events as they arrive, before they're stored or checked by detection rules. Since this server started they've kept ${Number(stats.eventsDropped).toLocaleString()} events (${formatBytes(stats.bytesDropped)}) out of storage.`;
        tbody.innerHTML = rules.length === 0 ? '<tr><td colspan="6" style="text-align:center;padding:32px;color:var(--text-secondary)">No rules: every event is stored as it arrives.</td></tr>'
            : rules.map(r => {
                const s = byId[r.id] || {};
                const does = r.action === 'Sample' ? `Keep 1 in ${r.sampleRate}` : r.action === 'Route' ? `To ${r.targetIndex}` : r.action;
                return `<tr><td>${esc(String(r.order))}</td><td>${esc(r.name)}${r.enabled ? '' : ' <span class="badge badge-muted">Off</span>'}</td>
                    <td class="mono" style="max-width:260px;overflow:hidden;text-overflow:ellipsis">${esc([r.pattern, r.condition].filter(Boolean).join(' and ') || 'every event')}</td>
                    <td>${esc(does)}${r.stop ? ', then stop' : ''}</td><td>${esc(String(s.matched || 0))} matched${s.dropped ? `, ${esc(String(s.dropped))} dropped (${esc(formatBytes(s.bytesDropped))})` : ''}</td>
                    <td><button type="button" class="btn btn-secondary btn-sm" data-click="editPipelineRule" data-args='${esc(JSON.stringify([r.id]))}'>Edit</button>
                        <button type="button" class="btn btn-danger btn-sm" data-click="deletePipelineRule" data-args='${esc(JSON.stringify([r.id]))}'>Del</button></td></tr>`;
            }).join('');
    } catch (e) { tbody.innerHTML = `<tr><td colspan="6" style="color:var(--error);padding:16px">${esc(e.message)}</td></tr>`; }
}

function editPipelineRule(id) {
    const r = (typeof id === 'string' && pipelineRules[id]) || { order: 10, action: 'Drop', sampleRate: 10, replacement: '[REDACTED]', enabled: true };
    editingPipelineId = r.id || null;
    const set = (field, value) => { document.getElementById(field).value = value ?? ''; };
    set('plName', r.name); set('plOrder', r.order); set('plPattern', r.pattern); set('plCondition', r.condition); set('plAction', r.action);
    set('plRate', r.sampleRate); set('plTarget', r.targetIndex); set('plRedact', r.redactPattern); set('plReplacement', r.replacement);
    document.getElementById('plStop').checked = !!r.stop;
    document.getElementById('plEnabled').checked = r.enabled !== false;
    document.getElementById('pipelineTestResult').textContent = '';
    document.getElementById('pipelineEditor').hidden = false;
}

function closePipelineEditor() { document.getElementById('pipelineEditor').hidden = true; }

function pipelineRuleFromForm() {
    const value = field => document.getElementById(field).value.trim();
    return {
        id: editingPipelineId || undefined, name: value('plName'), order: Number(value('plOrder')) || 0,
        pattern: value('plPattern') || null, condition: value('plCondition') || null, action: value('plAction'),
        sampleRate: Number(value('plRate')) || 10, targetIndex: value('plTarget') || null,
        redactPattern: value('plRedact') || null, replacement: document.getElementById('plReplacement').value,
        stop: document.getElementById('plStop').checked, enabled: document.getElementById('plEnabled').checked,
    };
}

async function testPipelineRule() {
    const out = document.getElementById('pipelineTestResult');
    const lines = document.getElementById('plLines').value.split('\n').map(l => l.trimEnd()).filter(Boolean);
    if (lines.length === 0) { out.textContent = 'Paste some lines to try it on.'; return; }
    const rule = pipelineRuleFromForm();
    const others = Object.values(pipelineRules).filter(r => r.id !== editingPipelineId);
    try {
        const results = await api('POST', '/api/v1/pipelines/test', { lines, rules: [...others, { ...rule, id: rule.id || 'preview' }] });
        out.innerHTML = `<ul>${results.map(r => `<li>${r.kept ? `<span class="badge badge-success">Kept</span> in ${esc(r.index)}: <span class="mono">${esc(r.result)}</span>` : `<span class="badge badge-muted">Dropped</span> <span class="mono">${esc(r.line)}</span>`}</li>`).join('')}</ul>`;
    } catch (e) { out.innerHTML = `<p style="color:var(--error)">${esc(e.message)}</p>`; }
}

async function savePipelineRule() {
    const rule = pipelineRuleFromForm();
    const id = editingPipelineId || (window.crypto && crypto.randomUUID ? crypto.randomUUID() : String(Date.now()));
    try {
        await api('PUT', '/api/v1/pipelines/rules/' + encodeURIComponent(id), rule);
        closePipelineEditor();
        toast('Rule saved', 'success');
        loadPipelines();
    } catch (e) { document.getElementById('pipelineTestResult').innerHTML = `<p style="color:var(--error)">${esc(e.message)}</p>`; }
}

async function deletePipelineRule(id) {
    if (!confirm('Delete this ingest rule?')) return;
    try { await api('DELETE', '/api/v1/pipelines/rules/' + encodeURIComponent(id)); loadPipelines(); }
    catch (e) { toast('Failed: ' + e.message, 'error'); }
}

/* ===== Cases ===== */
window.EXTRA_PAGES.cases = { title: 'Cases', load: () => loadCases() };

async function loadCases() {
    const tbody = document.getElementById('casesBody');
    if (!tbody) return;
    const status = document.getElementById('caseStatusFilter').value;
    try {
        const cases = await api('GET', '/api/v1/cases' + (status ? '?status=' + encodeURIComponent(status) : ''));
        tbody.innerHTML = cases.length === 0
            ? '<tr><td colspan="5" style="text-align:center;padding:32px;color:var(--text-secondary)">No cases.</td></tr>'
            : cases.map(c => `<tr><td><button type="button" class="btn btn-secondary btn-sm" data-click="openCase" data-args='${esc(JSON.stringify([c.id]))}'>${esc(c.title)}</button></td>
                <td>${esc(c.status)}</td><td>${esc(c.severity)}</td><td>${esc(c.assignee || '')}</td><td>${esc(fmtTime(c.updatedAt))}</td></tr>`).join('');
    } catch (e) { tbody.innerHTML = `<tr><td colspan="5" style="color:var(--error);padding:16px">${esc(e.message)}</td></tr>`; }
}

async function newCase() {
    const title = prompt('Case title');
    if (!title) return;
    try {
        const c = await api('POST', '/api/v1/cases', { title });
        await loadCases();
        openCase(c.id);
    } catch (e) { toast('Failed: ' + e.message, 'error'); }
}

async function openCase(id) {
    const view = document.getElementById('caseView');
    view.hidden = false;
    view.innerHTML = '<span class="spinner"></span>';
    try {
        const [c, timeline] = await Promise.all([api('GET', '/api/v1/cases/' + encodeURIComponent(id)), api('GET', `/api/v1/cases/${encodeURIComponent(id)}/timeline`)]);
        const items = timeline.map(t => `<li style="padding:6px 0;border-bottom:1px solid var(--border)"><span style="color:var(--text-muted)">${esc(fmtTime(t.time))}</span>
            <span class="badge badge-muted">${esc(t.kind)}</span>${t.host ? ` <span class="mono">${esc(t.host)}</span>` : ''}
            <div style="white-space:pre-wrap;${t.kind === 'event' ? 'font-family:monospace;font-size:12px' : ''}">${esc(t.text)}</div>${t.by ? `<span style="color:var(--text-secondary);font-size:11px">${esc(t.by)}</span>` : ''}</li>`).join('');
        const statuses = ['Open', 'Investigating', 'Resolved', 'Closed'].map(s => `<option${s === c.status ? ' selected' : ''}>${s}</option>`).join('');
        view.innerHTML = `<h4>${esc(c.title)}</h4>
            <div class="st-grid-2">
                <div class="form-group"><label for="caseStatus">Status</label><select id="caseStatus">${statuses}</select></div>
                <div class="form-group"><label for="caseAssignee">Assignee</label><input type="text" id="caseAssignee" value="${esc(c.assignee || '')}"></div>
            </div>
            <div class="modal-actions" style="justify-content:flex-start"><button class="btn btn-secondary btn-sm" data-click="saveCase" data-args='${esc(JSON.stringify([c.id]))}'>Save</button></div>
            <div class="form-group"><label for="caseNote">Add a note, event IDs or a search</label><textarea id="caseNote" rows="3" placeholder="What you found"></textarea></div>
            <div class="st-grid-2">
                <div class="form-group"><input type="text" id="caseEventIds" placeholder="Event IDs, comma-separated" aria-label="Event IDs"></div>
                <div class="form-group"><input type="text" id="caseSearch" placeholder="A search to keep" aria-label="Search" class="mono"></div>
            </div>
            <div class="modal-actions" style="justify-content:flex-start"><button class="btn btn-primary btn-sm" data-click="addToCase" data-args='${esc(JSON.stringify([c.id]))}'>Add</button></div>
            <h4 style="margin-top:16px">Timeline</h4><ul style="list-style:none;padding:0">${items || '<li class="page-note">Nothing yet.</li>'}</ul>`;
    } catch (e) { view.innerHTML = `<p style="color:var(--error)">${esc(e.message)}</p>`; }
}

async function saveCase(id) {
    try {
        await api('PATCH', '/api/v1/cases/' + encodeURIComponent(id), { status: document.getElementById('caseStatus').value, assignee: document.getElementById('caseAssignee').value.trim() || null });
        loadCases();
        openCase(id);
    } catch (e) { toast('Failed: ' + e.message, 'error'); }
}

async function addToCase(id) {
    const note = document.getElementById('caseNote').value.trim();
    const search = document.getElementById('caseSearch').value.trim();
    const eventIds = document.getElementById('caseEventIds').value.split(',').map(s => s.trim()).filter(Boolean);
    if (!note && !search && eventIds.length === 0) return;
    try {
        await api('POST', `/api/v1/cases/${encodeURIComponent(id)}/items`, { note: note || null, search: search || null, eventIds });
        openCase(id);
    } catch (e) { toast('Failed: ' + e.message, 'error'); }
}

/* ===== ATT&CK coverage and Sigma import ===== */
window.EXTRA_PAGES.coverage = { title: 'ATT&CK Coverage', load: () => loadCoverage() };

async function loadCoverage() {
    const box = document.getElementById('coverageMatrix');
    if (!box) return;
    try {
        const matrix = await api('GET', '/api/v1/detection-rules/coverage');
        box.innerHTML = matrix.map(tactic => {
            const cells = tactic.techniques.map(t => {
                const on = t.rules.filter(r => r.enabled).length;
                const style = on > 0 ? 'background:var(--accent);color:var(--bg-primary)' : t.rules.length > 0 ? 'border:1px solid var(--accent)' : 'border:1px solid var(--border);color:var(--text-muted)';
                const title = t.rules.length ? t.rules.map(r => `${r.name}${r.enabled ? '' : ' (off)'}`).join('\n') : 'No rule';
                return `<div title="${esc(title)}" style="padding:4px 6px;border-radius:4px;font-size:11px;margin-bottom:4px;${style}">${esc(t.id)} ${esc(t.name)}${t.rules.length ? ` (${t.rules.length})` : ''}</div>`;
            }).join('');
            const covered = tactic.techniques.filter(t => t.rules.some(r => r.enabled)).length;
            return `<div style="min-width:160px;max-width:180px"><div style="font-weight:600;font-size:12px;margin-bottom:6px">${esc(tactic.name)} <span style="color:var(--text-secondary)">${covered}/${tactic.techniques.length}</span></div>${cells}</div>`;
        }).join('');
    } catch (e) { box.innerHTML = `<p style="color:var(--error)">${esc(e.message)}</p>`; }
}

async function importSigma(save) {
    const out = document.getElementById('sigmaResult');
    const yaml = document.getElementById('sigmaYaml').value;
    if (!yaml.trim()) return;
    out.innerHTML = '<span class="spinner"></span>';
    try {
        const res = await fetch('/api/v1/detection-rules/sigma' + (save ? '?save=true' : ''), {
            method: 'POST',
            headers: { ...authHeaders(), 'Content-Type': 'text/plain', Accept: 'application/json' },
            body: yaml,
        });
        if (!res.ok) throw new Error(`HTTP ${res.status}`);
        const results = await res.json();
        out.innerHTML = `<ul>${results.map(r => `<li><strong>${esc(r.title)}</strong>: ${r.problem ? `<span style="color:var(--error)">${esc(r.problem)}</span>`
            : `${r.saved ? 'added, turned off' : 'converts'} (${esc(r.severity || '')}${r.technique ? ', ' + esc(r.technique) : ''})<div class="mono" style="font-size:11px">${esc(r.condition || '')}</div>`}</li>`).join('')}</ul>`;
        if (save) loadCoverage();
    } catch (e) { out.innerHTML = `<p style="color:var(--error)">${esc(e.message)}</p>`; }
}

/* ===== Anomalies ===== */
window.EXTRA_PAGES.anomalies = { title: 'Anomalies', load: () => loadAnomalyRules() };

const ANOMALY_KINDS = { Spike: 'A spike in count', Silence: 'Going quiet', NewValue: 'A new value', NewPattern: 'A new kind of line' };
let anomalyRules = {};
let editingAnomalyId = null;

async function loadAnomalyRules() {
    const tbody = document.getElementById('anomalyBody');
    if (!tbody) return;
    try {
        const rules = await api('GET', '/api/v1/anomaly-rules');
        anomalyRules = Object.fromEntries(rules.map(r => [r.id, r]));
        if (rules.length === 0) {
            tbody.innerHTML = '<tr><td colspan="6" style="text-align:center;padding:32px;color:var(--text-secondary)">No anomaly rules yet.</td></tr>';
            return;
        }
        tbody.innerHTML = rules.map(r => `<tr><td style="font-weight:500">${esc(r.name)}${r.enabled ? '' : ' <span class="badge badge-muted">Off</span>'}</td>
            <td>${esc(ANOMALY_KINDS[r.kind] || r.kind)}${r.groupBy ? ' by ' + esc(r.groupBy) : ''}${r.field ? ' of ' + esc(r.field) : ''}</td>
            <td class="mono" style="max-width:280px;overflow:hidden;text-overflow:ellipsis">${esc(r.query)}</td>
            <td>${esc(String(r.windowMinutes))} min</td>
            <td>${esc(r.lastResult || 'Not yet')}</td>
            <td><button type="button" class="btn btn-secondary btn-sm" data-click="editAnomalyRule" data-args='${esc(JSON.stringify([r.id]))}'>Edit</button>
                <button type="button" class="btn btn-danger btn-sm" data-click="deleteAnomalyRule" data-args='${esc(JSON.stringify([r.id]))}'>Del</button></td></tr>`).join('');
    } catch (e) {
        tbody.innerHTML = `<tr><td colspan="6" style="color:var(--error);padding:16px">${esc(e.message)}</td></tr>`;
    }
}

function editAnomalyRule(id) {
    const r = (typeof id === 'string' && anomalyRules[id]) || { kind: 'Spike', query: 'find *', windowMinutes: 15, baselineWindows: 96, threshold: 3, minCount: 10, severity: 'Warning', enabled: true };
    editingAnomalyId = r.id || null;
    document.getElementById('anomalyEditorTitle').textContent = r.id ? 'Edit rule' : 'New rule';
    const set = (field, value) => { document.getElementById(field).value = value ?? ''; };
    set('anomalyName', r.name); set('anomalyKind', r.kind); set('anomalyQuery', r.query); set('anomalyGroup', r.groupBy);
    set('anomalyField', r.field); set('anomalyWindow', r.windowMinutes); set('anomalyBaseline', r.baselineWindows);
    set('anomalyThreshold', r.threshold); set('anomalyMinCount', r.minCount); set('anomalySeverity', r.severity);
    document.getElementById('anomalyEnabled').checked = r.enabled !== false;
    document.getElementById('anomalyTestResult').textContent = '';
    document.getElementById('anomalyEditor').hidden = false;
}

function closeAnomalyEditor() { document.getElementById('anomalyEditor').hidden = true; }

function anomalyRuleFromForm() {
    const value = field => document.getElementById(field).value.trim();
    return {
        name: value('anomalyName'), kind: value('anomalyKind'), query: value('anomalyQuery'),
        groupBy: value('anomalyGroup') || null, field: value('anomalyField') || null,
        windowMinutes: Number(value('anomalyWindow')), baselineWindows: Number(value('anomalyBaseline')),
        threshold: Number(value('anomalyThreshold')), minCount: Number(value('anomalyMinCount')),
        severity: value('anomalySeverity'), enabled: document.getElementById('anomalyEnabled').checked,
    };
}

async function testAnomalyRule() {
    const out = document.getElementById('anomalyTestResult');
    out.innerHTML = '<span class="spinner"></span>';
    try {
        const findings = await api('POST', '/api/v1/anomaly-rules/test', anomalyRuleFromForm());
        out.innerHTML = findings.length === 0 ? '<p class="page-note">Nothing unusual in the last window.</p>'
            : `<ul>${findings.map(f => `<li>${esc(f.message)}</li>`).join('')}</ul>`;
    } catch (e) { out.innerHTML = `<p style="color:var(--error)">${esc(e.message)}</p>`; }
}

async function saveAnomalyRule() {
    const id = editingAnomalyId || (window.crypto && crypto.randomUUID ? crypto.randomUUID() : String(Date.now()));
    try {
        await api('PUT', '/api/v1/anomaly-rules/' + encodeURIComponent(id), anomalyRuleFromForm());
        closeAnomalyEditor();
        toast('Rule saved', 'success');
        loadAnomalyRules();
    } catch (e) { document.getElementById('anomalyTestResult').innerHTML = `<p style="color:var(--error)">${esc(e.message)}</p>`; }
}

async function deleteAnomalyRule(id) {
    if (!confirm('Delete this anomaly rule?')) return;
    try { await api('DELETE', '/api/v1/anomaly-rules/' + encodeURIComponent(id)); loadAnomalyRules(); }
    catch (e) { toast('Failed: ' + e.message, 'error'); }
}

/* ===== Connectors ===== */
window.EXTRA_PAGES.connectors = { title: 'Connectors', load: () => loadConnectors() };

const CONNECTOR_TYPES = {
    slack: { label: 'Slack', url: 'Incoming webhook URL', secret: null },
    teams: { label: 'Microsoft Teams', url: 'Incoming webhook URL', secret: null },
    discord: { label: 'Discord', url: 'Webhook URL', secret: null },
    pagerduty: { label: 'PagerDuty', url: 'Events API URL (optional)', secret: 'Integration (routing) key' },
    opsgenie: { label: 'Opsgenie', url: 'Alerts API URL (optional; EU: https://api.eu.opsgenie.com/v2/alerts)', secret: 'API key' },
    jira: { label: 'Jira', url: 'Site URL, such as https://acme.atlassian.net', secret: 'API token', user: true, project: 'Project key' },
    servicenow: { label: 'ServiceNow', url: 'Instance URL, such as https://acme.service-now.com', secret: 'Password', user: true, project: 'Table (default incident)' },
    email: { label: 'Email', url: null, secret: null, to: true },
    webhook: { label: 'Webhook (JSON)', url: 'URL', secret: 'Bearer token (optional)' },
};

let connectorsByName = {};

async function loadConnectors() {
    const tbody = document.getElementById('connectorsBody');
    if (!tbody) return;
    tbody.innerHTML = '<tr><td colspan="6" style="text-align:center;padding:24px"><span class="spinner"></span></td></tr>';
    try {
        const list = await api('GET', '/api/v1/connectors');
        connectorsByName = Object.fromEntries(list.map(c => [c.name, c]));
        if (list.length === 0) {
            tbody.innerHTML = '<tr><td colspan="6" style="text-align:center;padding:32px;color:var(--text-secondary)">No connectors yet. Add one, then use it from a detection rule.</td></tr>';
            return;
        }
        tbody.innerHTML = '';
        list.forEach(c => {
            const tr = document.createElement('tr');
            const type = CONNECTOR_TYPES[c.type]?.label || c.type;
            const dest = c.type === 'email' ? (c.settings?.to || '') : (c.url || '(default)');
            tr.innerHTML = `<td style="font-weight:500">${esc(c.name)}</td><td>${esc(type)}</td><td class="mono" style="max-width:320px;overflow:hidden;text-overflow:ellipsis">${esc(dest)}</td>
                <td>${c.hasSecret ? 'Saved' : '—'}</td><td>${c.enabled ? 'On' : 'Off'}</td>
                <td><button type="button" class="btn btn-secondary btn-sm" data-click="testConnector" data-args='${esc(JSON.stringify([c.name]))}'>Test</button>
                    <button type="button" class="btn btn-secondary btn-sm" data-click="showConnectorModal" data-args='${esc(JSON.stringify([c.name]))}'>Edit</button>
                    <button type="button" class="btn btn-danger btn-sm" data-click="deleteConnector" data-args='${esc(JSON.stringify([c.name]))}'>Del</button></td>`;
            tbody.appendChild(tr);
        });
    } catch (e) {
        tbody.innerHTML = `<tr><td colspan="6" style="color:var(--error);padding:16px">${esc(e.message)}</td></tr>`;
    }
}

function showConnectorModal(name) {
    const c = (typeof name === 'string' && connectorsByName[name]) || { type: 'slack', enabled: true, settings: {} };
    document.getElementById('connectorModalTitle').textContent = c.name ? 'Edit Connector' : 'New Connector';
    const nameInput = document.getElementById('connectorName');
    nameInput.value = c.name || '';
    nameInput.disabled = !!c.name;
    document.getElementById('connectorType').value = c.type;
    document.getElementById('connectorUrl').value = c.url || '';
    document.getElementById('connectorSecret').value = '';
    document.getElementById('connectorUser').value = c.settings?.user || '';
    document.getElementById('connectorProject').value = c.settings?.project || c.settings?.table || '';
    document.getElementById('connectorTo').value = c.settings?.to || '';
    document.getElementById('connectorEnabled').checked = c.enabled !== false;
    document.getElementById('connectorResult').textContent = '';
    connectorTypeChanged();
    openModal('connectorModal');
}

function connectorTypeChanged() {
    const t = CONNECTOR_TYPES[document.getElementById('connectorType').value] || CONNECTOR_TYPES.webhook;
    const show = (id, on) => { document.getElementById(id).style.display = on ? '' : 'none'; };
    show('connectorUrl', !!t.url);
    show('connectorUrlLabel', !!t.url);
    document.getElementById('connectorUrlLabel').textContent = t.url || 'URL';
    show('connectorSecret', !!t.secret);
    show('connectorSecretLabel', !!t.secret);
    document.getElementById('connectorSecretLabel').textContent = t.secret || 'Secret';
    show('connectorUserGroup', !!t.user);
    show('connectorProjectGroup', !!t.project);
    document.querySelector('label[for="connectorProject"]').textContent = t.project || 'Project';
    show('connectorToGroup', !!t.to);
}

async function saveConnector() {
    const name = document.getElementById('connectorName').value.trim();
    const type = document.getElementById('connectorType').value;
    const settings = {};
    const user = document.getElementById('connectorUser').value.trim();
    const project = document.getElementById('connectorProject').value.trim();
    const to = document.getElementById('connectorTo').value.trim();
    if (user) settings.user = user;
    if (project) settings[type === 'servicenow' ? 'table' : 'project'] = project;
    if (to) settings.to = to;
    const body = {
        name, type, settings,
        url: document.getElementById('connectorUrl').value.trim() || null,
        secret: document.getElementById('connectorSecret').value || null,
        enabled: document.getElementById('connectorEnabled').checked,
    };
    try {
        await api('PUT', '/api/v1/connectors/' + encodeURIComponent(name), body);
        closeModal('connectorModal');
        toast('Connector saved', 'success');
        loadConnectors();
    } catch (e) { document.getElementById('connectorResult').textContent = e.message; }
}

async function testConnector(name) {
    try {
        const r = await api('POST', `/api/v1/connectors/${encodeURIComponent(name)}/test`);
        toast(r.ok ? `Test sent through ${name}` : `Test failed: ${r.message}`, r.ok ? 'success' : 'error');
    } catch (e) { toast('Test failed: ' + e.message, 'error'); }
}

async function deleteConnector(name) {
    if (!confirm(`Delete connector ${name}? Rules that use it will skip it.`)) return;
    try {
        await api('DELETE', '/api/v1/connectors/' + encodeURIComponent(name));
        loadConnectors();
    } catch (e) { toast('Failed: ' + e.message, 'error'); }
}

/** Names of connectors, for the rule editor's "Send to connector" action. */
async function connectorNames() {
    try { return (await api('GET', '/api/v1/connectors')).map(c => c.name); } catch { return []; }
}
