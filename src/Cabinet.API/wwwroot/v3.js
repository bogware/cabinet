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
