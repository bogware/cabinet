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
    out.innerHTML = '<p class="page-note">' + loadingHtml('Investigating. This can take a minute.') + '</p>';
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
    panel.innerHTML = loadingHtml('Loading this player…');
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

/* Cases, ATT&CK coverage and Sigma import live in respond.js. */

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
    out.innerHTML = loadingHtml('Checking the last window…');
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

/* ===== Reports ===== */
window.EXTRA_PAGES.reports = { title: 'Reports', load: () => loadReports() };

let reportsById = {};
let editingReportId = null;

async function loadReports() {
    const tbody = document.getElementById('reportsBody');
    if (!tbody) return;
    try {
        const reports = await api('GET', '/api/v1/reports');
        reportsById = Object.fromEntries(reports.map(r => [r.id, r]));
        tbody.innerHTML = reports.length === 0 ? '<tr><td colspan="5" style="text-align:center;padding:32px;color:var(--text-secondary)">No reports.</td></tr>'
            : reports.map(r => `<tr><td>${esc(r.name)}${r.enabled ? '' : ' <span class="badge badge-muted">Off</span>'}</td><td class="mono">${esc(r.schedule)}</td>
                <td>${esc(r.recipients.join(', '))}</td><td>${esc(r.lastSentAt ? fmtTime(r.lastSentAt) + ': ' + (r.lastResult || '') : 'Not yet')}</td>
                <td><button type="button" class="btn btn-secondary btn-sm" data-click="sendReport" data-args='${esc(JSON.stringify([r.id]))}'>Send now</button>
                    <button type="button" class="btn btn-secondary btn-sm" data-click="editReport" data-args='${esc(JSON.stringify([r.id]))}'>Edit</button>
                    <button type="button" class="btn btn-danger btn-sm" data-click="deleteReport" data-args='${esc(JSON.stringify([r.id]))}'>Del</button></td></tr>`).join('');
    } catch (e) { tbody.innerHTML = `<tr><td colspan="5" style="color:var(--error);padding:16px">${esc(e.message)}</td></tr>`; }
}

function editReport(id) {
    const r = (typeof id === 'string' && reportsById[id]) || { schedule: '0 8 * * 1', recipients: [], sections: [], attachPdf: true, enabled: true };
    editingReportId = r.id || null;
    document.getElementById('rpName').value = r.name || '';
    document.getElementById('rpSchedule').value = r.schedule;
    document.getElementById('rpRecipients').value = r.recipients.join(', ');
    document.getElementById('rpSections').value = r.sections.map(s => `${s.title} | ${s.query}`).join('\n');
    document.getElementById('rpPdf').checked = r.attachPdf !== false;
    document.getElementById('rpEnabled').checked = r.enabled !== false;
    document.getElementById('reportPreview').hidden = true;
    document.getElementById('reportEditor').hidden = false;
}

function closeReportEditor() { document.getElementById('reportEditor').hidden = true; }

function reportFromForm() {
    return {
        name: document.getElementById('rpName').value.trim(),
        schedule: document.getElementById('rpSchedule').value.trim(),
        recipients: document.getElementById('rpRecipients').value.split(',').map(s => s.trim()).filter(Boolean),
        sections: document.getElementById('rpSections').value.split('\n').map(l => l.trim()).filter(Boolean).map(l => {
            const at = l.indexOf('|');
            return at < 0 ? { title: l, query: l } : { title: l.slice(0, at).trim(), query: l.slice(at + 1).trim() };
        }),
        attachPdf: document.getElementById('rpPdf').checked,
        enabled: document.getElementById('rpEnabled').checked,
    };
}

async function previewReport() {
    const box = document.getElementById('reportPreview');
    box.hidden = false;
    box.innerHTML = loadingHtml('Building the preview…');
    try {
        const res = await fetch('/api/v1/reports/preview', { method: 'POST', headers: { ...authHeaders(), 'Content-Type': 'application/json', Accept: 'text/html' }, body: JSON.stringify(reportFromForm()) });
        if (!res.ok) throw new Error(`HTTP ${res.status}`);
        // The server encodes everything from the logs; the report is tables and inline styles
        box.innerHTML = await res.text();
    } catch (e) { box.innerHTML = `<p style="color:var(--danger)">${esc(e.message)}</p>`; }
}

async function saveReport() {
    const id = editingReportId || (window.crypto && crypto.randomUUID ? crypto.randomUUID() : String(Date.now()));
    try {
        await api('PUT', '/api/v1/reports/' + encodeURIComponent(id), reportFromForm());
        closeReportEditor();
        toast('Report saved', 'success');
        loadReports();
    } catch (e) { toast('Not saved: ' + e.message, 'error'); }
}

async function sendReport(id) {
    try {
        const r = await api('POST', `/api/v1/reports/${encodeURIComponent(id)}/send`);
        toast(r.lastResult || 'Sent', r.lastResult && r.lastResult.startsWith('Not') ? 'error' : 'success');
        loadReports();
    } catch (e) { toast('Failed: ' + e.message, 'error'); }
}

async function deleteReport(id) {
    if (!confirm('Delete this report?')) return;
    try { await api('DELETE', '/api/v1/reports/' + encodeURIComponent(id)); loadReports(); }
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
    tbody.innerHTML = '<tr><td colspan="6" style="text-align:center;padding:24px">' + loadingHtml('Loading connectors…') + '</td></tr>';
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

/* ===== Users, groups and SCIM tokens ===== */
window.EXTRA_PAGES.users = { title: 'Users', load: () => loadUsers() };

let directoryUsers = [];
let directoryGroups = [];
let editingUserId = null;
let editingGroupId = null;

/** Index permissions as lines of "pattern: Capability, Capability". */
function permissionsToText(list) {
    return (list || []).map(p => `${p.indexPattern}: ${(p.capabilities || []).join(', ')}`).join('\n');
}

function textToPermissions(text) {
    return String(text || '').split('\n').map(line => line.trim()).filter(Boolean).map(line => {
        const colon = line.lastIndexOf(':');
        const pattern = (colon > 0 ? line.slice(0, colon) : line).trim();
        const caps = colon > 0 ? line.slice(colon + 1).split(',').map(c => c.trim()).filter(Boolean) : ['Search'];
        return { indexPattern: pattern, capabilities: caps };
    });
}

function showOnce(title, note, value) {
    document.getElementById('onceTitle').textContent = title;
    document.getElementById('onceNote').textContent = note;
    document.getElementById('onceValue').textContent = value;
    openModal('onceModal');
}

async function loadUsers() {
    const usersBody = document.getElementById('usersBody');
    const groupsBody = document.getElementById('groupsBody');
    if (!usersBody) return;
    document.getElementById('scimEndpoint').textContent = window.location.origin + '/scim/v2';
    usersBody.innerHTML = '<tr><td colspan="7" style="text-align:center;padding:24px">' + loadingHtml('Loading users…') + '</td></tr>';
    try {
        [directoryUsers, directoryGroups] = await Promise.all([api('GET', '/api/v1/users'), api('GET', '/api/v1/groups')]);
    } catch (e) {
        usersBody.innerHTML = `<tr><td colspan="7" style="color:var(--error);padding:16px">${esc(e.message)}</td></tr>`;
        return;
    }
    usersBody.innerHTML = directoryUsers.length === 0
        ? '<tr><td colspan="7" style="text-align:center;padding:32px;color:var(--text-secondary)">No users yet. Add a local account, or connect your identity provider through SCIM.</td></tr>'
        : directoryUsers.map(u => {
            const status = [u.active ? 'Active' : 'Deactivated', u.locked ? 'Locked' : '', u.mfaEnabled ? 'MFA' : '', u.mustChangePassword ? 'Must change password' : ''].filter(Boolean).join(', ');
            const id = esc(JSON.stringify([u.id]));
            const local = u.source === 'Local';
            return `<tr><td><strong>${esc(u.userName)}</strong>${u.displayName ? `<br><span style="color:var(--text-secondary)">${esc(u.displayName)}</span>` : ''}${u.email ? `<br><span class="mono" style="color:var(--text-secondary)">${esc(u.email)}</span>` : ''}</td>
                <td>${esc(u.source)}</td><td>${esc(u.effectiveRole || 'None')}${u.role && u.role !== u.effectiveRole ? ` (own: ${esc(u.role)})` : ''}</td>
                <td>${esc((u.groups || []).map(g => g.displayName).join(', '))}</td><td>${esc(status)}</td>
                <td>${esc(u.lastSignInAt ? fmtTime(u.lastSignInAt) : 'Never')}</td>
                <td style="white-space:nowrap"><button type="button" class="btn btn-secondary btn-sm" data-click="showUserModal" data-args='${id}'>Edit</button>
                    ${local ? `<button type="button" class="btn btn-secondary btn-sm" data-click="resetUserPassword" data-args='${id}'>Reset password</button>` : ''}
                    ${u.mfaEnabled ? `<button type="button" class="btn btn-secondary btn-sm" data-click="resetUserMfa" data-args='${id}'>Reset MFA</button>` : ''}
                    ${u.locked ? `<button type="button" class="btn btn-secondary btn-sm" data-click="unlockUser" data-args='${id}'>Unlock</button>` : ''}
                    <button type="button" class="btn btn-secondary btn-sm" data-click="signOutUser" data-args='${id}'>Sign out</button>
                    <button type="button" class="btn btn-danger btn-sm" data-click="deleteUser" data-args='${id}'>Del</button></td></tr>`;
        }).join('');
    const names = Object.fromEntries(directoryUsers.map(u => [u.id, u.userName]));
    groupsBody.innerHTML = directoryGroups.length === 0
        ? '<tr><td colspan="6" style="text-align:center;padding:24px;color:var(--text-secondary)">No groups yet.</td></tr>'
        : directoryGroups.map(g => `<tr><td><strong>${esc(g.displayName)}</strong></td><td>${esc(g.source)}</td><td>${esc(g.role || 'None')}</td>
            <td class="mono">${esc(permissionsToText(g.indexPermissions) || 'Every index')}</td>
            <td title="${esc(g.members.map(m => names[m] || m).join(', '))}">${esc(String(g.members.length))}</td>
            <td style="white-space:nowrap"><button type="button" class="btn btn-secondary btn-sm" data-click="showGroupModal" data-args='${esc(JSON.stringify([g.id]))}'>Edit</button>
                <button type="button" class="btn btn-danger btn-sm" data-click="deleteGroup" data-args='${esc(JSON.stringify([g.id]))}'>Del</button></td></tr>`).join('');
    loadScimTokens();
}

function userKindChanged() {
    const sso = document.getElementById('userKind').value === 'sso';
    document.getElementById('userPasswordGroup').style.display = sso || editingUserId ? 'none' : '';
}

function showUserModal(id) {
    const u = (typeof id === 'string' && directoryUsers.find(x => x.id === id)) || null;
    editingUserId = u ? u.id : null;
    document.getElementById('userModalTitle').textContent = u ? 'Edit User' : 'New User';
    document.getElementById('userName').value = u ? u.userName : '';
    document.getElementById('userDisplayName').value = u?.displayName || '';
    document.getElementById('userEmail').value = u?.email || '';
    document.getElementById('userKind').value = u && u.source !== 'Local' ? 'sso' : 'local';
    document.getElementById('userKindGroup').style.display = u ? 'none' : '';
    document.getElementById('userPassword').value = '';
    document.getElementById('userExternalId').value = u?.externalId || '';
    document.getElementById('userRole').value = u?.role || '';
    document.getElementById('userPermissions').value = permissionsToText(u?.indexPermissions);
    document.getElementById('userActive').checked = u ? u.active : true;
    const inGroups = new Set((u?.groups || []).map(g => g.id));
    document.getElementById('userGroups').innerHTML = directoryGroups.length === 0
        ? '<span class="page-note">No groups yet.</span>'
        : directoryGroups.map(g => `<label class="check-label"><input type="checkbox" class="user-group" value="${esc(g.id)}"${inGroups.has(g.id) ? ' checked' : ''}> ${esc(g.displayName)}</label>`).join(' ');
    document.getElementById('userResult').textContent = '';
    userKindChanged();
    openModal('userModal');
}

async function saveUser() {
    const body = {
        userName: document.getElementById('userName').value.trim(),
        displayName: document.getElementById('userDisplayName').value.trim() || null,
        email: document.getElementById('userEmail').value.trim() || null,
        externalId: document.getElementById('userExternalId').value.trim(),
        role: document.getElementById('userRole').value || null,
        indexPermissions: textToPermissions(document.getElementById('userPermissions').value),
        groups: [...document.querySelectorAll('#userGroups .user-group:checked')].map(c => c.value),
        active: document.getElementById('userActive').checked,
    };
    try {
        if (editingUserId) {
            await api('PUT', '/api/v1/users/' + encodeURIComponent(editingUserId), body);
            toast('User saved', 'success');
        } else {
            body.sso = document.getElementById('userKind').value === 'sso';
            body.password = body.sso ? null : (document.getElementById('userPassword').value || null);
            const created = await api('POST', '/api/v1/users', body);
            if (created.temporaryPassword) {
                showOnce('Temporary password', `Give this to ${created.user.userName}. They choose their own password when they first sign in. It isn't shown again.`, created.temporaryPassword);
            } else {
                toast('User added', 'success');
            }
        }
        closeModal('userModal');
        loadUsers();
    } catch (e) { document.getElementById('userResult').textContent = e.message; }
}

async function userAction(id, path, confirmText, done) {
    const u = directoryUsers.find(x => x.id === id);
    if (confirmText && !confirm(confirmText.replace('{0}', u ? u.userName : id))) return null;
    try {
        const result = await api('POST', `/api/v1/users/${encodeURIComponent(id)}/${path}`);
        if (done) toast(done, 'success');
        loadUsers();
        return result;
    } catch (e) { toast('Failed: ' + e.message, 'error'); return null; }
}

async function resetUserPassword(id) {
    const r = await userAction(id, 'reset-password', 'Give {0} a temporary password? Their sessions end and they choose a new password at their next sign-in.');
    if (r) showOnce('Temporary password', 'Give this to the user. It isn\'t shown again, and must be changed at their next sign-in.', r.password);
}

function resetUserMfa(id) { return userAction(id, 'reset-mfa', 'Remove the authenticator app of {0}? Their sessions end; they can set up a new one.', 'Authenticator removed'); }

function unlockUser(id) { return userAction(id, 'unlock', null, 'Unlocked'); }

function signOutUser(id) { return userAction(id, 'sign-out', 'Sign {0} out everywhere?', 'Signed out everywhere'); }

async function deleteUser(id) {
    const u = directoryUsers.find(x => x.id === id);
    if (!confirm(`Remove ${u ? u.userName : id}? To keep the record, deactivate them instead.`)) return;
    try { await api('DELETE', '/api/v1/users/' + encodeURIComponent(id)); loadUsers(); }
    catch (e) { toast('Failed: ' + e.message, 'error'); }
}

function showGroupModal(id) {
    const g = (typeof id === 'string' && directoryGroups.find(x => x.id === id)) || null;
    editingGroupId = g ? g.id : null;
    document.getElementById('groupModalTitle').textContent = g ? 'Edit Group' : 'New Group';
    document.getElementById('groupName').value = g ? g.displayName : '';
    document.getElementById('groupRole').value = g?.role || '';
    document.getElementById('groupPermissions').value = permissionsToText(g?.indexPermissions);
    const members = new Set(g?.members || []);
    document.getElementById('groupMembers').innerHTML = directoryUsers.length === 0
        ? '<span class="page-note">No users yet.</span>'
        : directoryUsers.map(u => `<label class="check-label" style="display:block"><input type="checkbox" class="group-member" value="${esc(u.id)}"${members.has(u.id) ? ' checked' : ''}> ${esc(u.userName)}</label>`).join('');
    document.getElementById('groupResult').textContent = '';
    openModal('groupModal');
}

async function saveGroup() {
    const body = {
        displayName: document.getElementById('groupName').value.trim(),
        role: document.getElementById('groupRole').value || null,
        indexPermissions: textToPermissions(document.getElementById('groupPermissions').value),
        members: [...document.querySelectorAll('#groupMembers .group-member:checked')].map(c => c.value),
    };
    try {
        if (editingGroupId) await api('PUT', '/api/v1/groups/' + encodeURIComponent(editingGroupId), body);
        else await api('POST', '/api/v1/groups', body);
        closeModal('groupModal');
        toast('Group saved', 'success');
        loadUsers();
    } catch (e) { document.getElementById('groupResult').textContent = e.message; }
}

async function deleteGroup(id) {
    const g = directoryGroups.find(x => x.id === id);
    if (!confirm(`Remove group ${g ? g.displayName : id}? Its members lose the role it gave them.`)) return;
    try { await api('DELETE', '/api/v1/groups/' + encodeURIComponent(id)); loadUsers(); }
    catch (e) { toast('Failed: ' + e.message, 'error'); }
}

async function loadScimTokens() {
    const body = document.getElementById('scimTokensBody');
    if (!body) return;
    try {
        const tokens = await api('GET', '/api/v1/scim-tokens');
        body.innerHTML = tokens.length === 0
            ? '<tr><td colspan="6" style="text-align:center;padding:24px;color:var(--text-secondary)">No SCIM tokens.</td></tr>'
            : tokens.map(t => `<tr><td>${esc(t.name)}</td><td class="mono">${esc(t.prefix || '')}</td><td>${esc(fmtTime(t.createdAt))}</td>
                <td>${esc(t.lastUsedAt ? fmtTime(t.lastUsedAt) : 'Never')}</td><td>${t.revoked ? 'Revoked' : 'Active'}</td>
                <td>${t.revoked ? '' : `<button type="button" class="btn btn-danger btn-sm" data-click="revokeScimToken" data-args='${esc(JSON.stringify([t.id]))}'>Revoke</button>`}</td></tr>`).join('');
    } catch (e) { body.innerHTML = `<tr><td colspan="6" style="color:var(--error);padding:16px">${esc(e.message)}</td></tr>`; }
}

async function createScimToken() {
    const name = document.getElementById('scimTokenName').value.trim() || `SCIM ${new Date().toISOString().slice(0, 10)}`;
    try {
        const t = await api('POST', '/api/v1/scim-tokens', { name });
        showOnce('SCIM token', `Paste this into your identity provider as the secret token, with ${window.location.origin}/scim/v2 as the tenant URL. It isn't shown again.`, t.token);
        loadScimTokens();
    } catch (e) { toast('Failed: ' + e.message, 'error'); }
}

async function revokeScimToken(id) {
    if (!confirm('Revoke this SCIM token? Provisioning with it stops at once.')) return;
    try { await api('DELETE', '/api/v1/scim-tokens/' + encodeURIComponent(id)); loadScimTokens(); }
    catch (e) { toast('Failed: ' + e.message, 'error'); }
}

/* ===== Signing in with a local account ===== */
let localChallenge = null;

function showLocalStep(step) {
    ['localStepPassword', 'localStepMfa', 'localStepPasswordChange', 'localStepEnroll'].forEach(id => {
        document.getElementById(id).hidden = id !== step;
    });
}

async function localPost(path, body) {
    const res = await fetch(path, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json', Accept: 'application/json', 'X-Cabinet-Request': '1' },
        body: JSON.stringify(body),
    });
    let data = {};
    try { data = await res.json(); } catch { data = {}; }
    return { ok: res.ok, status: res.status, data };
}

/** Moves the sign-in to whatever the server says comes next. */
async function handleLocalStep(result) {
    const error = document.getElementById('loginError');
    error.textContent = '';
    const d = result.data || {};
    if (d.challenge) localChallenge = d.challenge;
    if (result.ok && d.status === 'signed-in') {
        if (d.recoveryCodes && d.recoveryCodes.length) {
            closeModal('loginModal');
            showOnce('Recovery codes', 'Keep these somewhere safe. Each signs you in once if you lose your authenticator. They aren\'t shown again.', d.recoveryCodes.join('\n'));
            document.getElementById('onceModal').addEventListener('click', () => window.location.reload(), { once: true });
            return;
        }
        window.location.reload();
        return;
    }
    if (d.status === 'mfa-required') { showLocalStep('localStepMfa'); document.getElementById('localMfaCode').focus(); return; }
    if (d.status === 'password-change-required') {
        document.getElementById('localPasswordChangeNote').textContent = `${d.message || 'Choose a new password to continue.'} Use at least ${d.minLength || 12} characters.`;
        showLocalStep('localStepPasswordChange');
        return;
    }
    if (d.status === 'mfa-enrollment-required') {
        const r = await localPost('/auth/login/mfa/enroll', { challenge: localChallenge });
        if (r.ok) {
            localChallenge = r.data.challenge;
            document.getElementById('localEnrollSecret').value = r.data.secret;
            document.getElementById('localEnrollLink').href = r.data.otpauthUri;
            showLocalStep('localStepEnroll');
            return;
        }
        error.textContent = r.data.message || 'Sign-in failed';
        showLocalStep('localStepPassword');
        return;
    }
    error.textContent = d.message || 'Sign-in failed';
    // A challenge that can't be used again means starting over
    if (!d.challenge && result.status !== 429) { localChallenge = null; showLocalStep('localStepPassword'); }
}

async function signInLocal() {
    const userName = document.getElementById('localUser').value.trim();
    const password = document.getElementById('localPassword').value;
    if (!userName || !password) return;
    const result = await localPost('/auth/login/local', { userName, password });
    document.getElementById('localPassword').value = '';
    await handleLocalStep(result);
}

async function signInLocalMfa() {
    const code = document.getElementById('localMfaCode').value.trim();
    if (!code) return;
    document.getElementById('localMfaCode').value = '';
    await handleLocalStep(await localPost('/auth/login/mfa', { challenge: localChallenge, code }));
}

async function signInLocalNewPassword() {
    const newPassword = document.getElementById('localNewPassword').value;
    if (newPassword !== document.getElementById('localNewPassword2').value) {
        document.getElementById('loginError').textContent = 'The two passwords differ.';
        return;
    }
    document.getElementById('localNewPassword').value = '';
    document.getElementById('localNewPassword2').value = '';
    await handleLocalStep(await localPost('/auth/login/password', { challenge: localChallenge, newPassword }));
}

async function signInLocalEnroll() {
    const code = document.getElementById('localEnrollCode').value.trim();
    if (!code) return;
    document.getElementById('localEnrollCode').value = '';
    await handleLocalStep(await localPost('/auth/login/mfa/enroll/confirm', { challenge: localChallenge, code }));
}

/* ===== My account ===== */
async function showAccount() {
    document.getElementById('accountResult').textContent = '';
    document.getElementById('accountMfaEnroll').hidden = true;
    document.getElementById('accountMfaCode').value = '';
    try {
        const me = await api('GET', '/auth/account');
        document.getElementById('accountSummary').textContent = `${me.displayName || me.userName} (${me.userName})${me.email ? ', ' + me.email : ''}. Role: ${me.role || 'none'}.`;
        document.getElementById('accountMfaStatus').textContent = me.mfaEnabled
            ? `On. ${me.recoveryCodesLeft} recovery codes left.${me.mfaRequired ? ' Your role has to use one.' : ''}`
            : (me.mfaRequired ? 'Your role has to use one.' : 'Off. Turn it on so a stolen password isn\'t enough to sign in.');
        document.getElementById('accountMfaStart').hidden = me.mfaEnabled;
        document.getElementById('accountMfaConfirm').hidden = true;
        document.getElementById('accountMfaCodes').hidden = !me.mfaEnabled;
        document.getElementById('accountMfaRemove').hidden = !me.mfaEnabled || me.mfaRequired;
        openModal('accountModal');
    } catch (e) { toast('Failed: ' + e.message, 'error'); }
}

async function changeOwnPassword() {
    const currentPassword = document.getElementById('accountCurrentPassword').value;
    const newPassword = document.getElementById('accountNewPassword').value;
    try {
        await api('POST', '/auth/account/password', { currentPassword, newPassword });
        document.getElementById('accountCurrentPassword').value = '';
        document.getElementById('accountNewPassword').value = '';
        document.getElementById('accountResult').textContent = 'Password changed. Your other sessions have ended.';
    } catch (e) { document.getElementById('accountResult').textContent = e.message; }
}

async function startOwnMfa() {
    try {
        const r = await api('POST', '/auth/account/mfa/enroll');
        document.getElementById('accountMfaSecret').value = r.secret;
        document.getElementById('accountMfaLink').href = r.otpauthUri;
        document.getElementById('accountMfaEnroll').hidden = false;
        document.getElementById('accountMfaConfirm').hidden = false;
        document.getElementById('accountResult').textContent = 'Add the key to your authenticator app, then type the code it shows and confirm.';
    } catch (e) { document.getElementById('accountResult').textContent = e.message; }
}

async function confirmOwnMfa() {
    try {
        const r = await api('POST', '/auth/account/mfa/confirm', { code: document.getElementById('accountMfaCode').value.trim() });
        closeModal('accountModal');
        showOnce('Recovery codes', 'Your authenticator is set up. Keep these somewhere safe: each signs you in once if you lose it. They aren\'t shown again.', r.recoveryCodes.join('\n'));
    } catch (e) { document.getElementById('accountResult').textContent = e.message; }
}

async function newOwnRecoveryCodes() {
    try {
        const r = await api('POST', '/auth/account/mfa/recovery-codes', { code: document.getElementById('accountMfaCode').value.trim() });
        closeModal('accountModal');
        showOnce('Recovery codes', 'These replace your old recovery codes. They aren\'t shown again.', r.recoveryCodes.join('\n'));
    } catch (e) { document.getElementById('accountResult').textContent = e.message; }
}

async function removeOwnMfa() {
    if (!confirm('Remove your authenticator app? Signing in will need only your password.')) return;
    try {
        await api('POST', '/auth/account/mfa/disable', { code: document.getElementById('accountMfaCode').value.trim() });
        showAccount();
    } catch (e) { document.getElementById('accountResult').textContent = e.message; }
}

async function signOutEverywhere() {
    if (!confirm('Sign out of every browser, this one included?')) return;
    try { await api('POST', '/auth/account/sign-out-everywhere'); } catch (e) { toast('Failed: ' + e.message, 'error'); return; }
    window.location.reload();
}
