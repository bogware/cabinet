/* ===== State ===== */
// In this tab only, unless someone chose to be remembered on this device. Anything in localStorage
// outlives the session and any script on the page can read it.
let API_KEY = sessionStorage.getItem('cabinet_api_key') || localStorage.getItem('cabinet_api_key') || '';
let currentPage = 'search';

function showLogin(message) {
    document.getElementById('loginError').textContent = message || '';
    openModal('loginModal');
}

function storeApiKey(key, remember) {
    API_KEY = key;
    sessionStorage.setItem('cabinet_api_key', key);
    if (remember) localStorage.setItem('cabinet_api_key', key);
    else localStorage.removeItem('cabinet_api_key');
}

document.getElementById('loginForm').addEventListener('submit', event => {
    event.preventDefault();
    const key = document.getElementById('loginKey').value.trim();
    if (!key) return;
    storeApiKey(key, document.getElementById('loginRemember').checked);
    window.location.reload();
});

/* ===== Handlers: what to call is data in the page, never code ===== */
function runHandler(element, name) {
    const fn = window[name];
    if (typeof fn !== 'function') return;
    const args = JSON.parse(element.dataset.args || '[]').map(a => (a === '$this' ? element : a));
    fn(...args);
}
document.addEventListener('click', event => {
    const element = event.target.closest('[data-click]');
    if (element) runHandler(element, element.dataset.click);
});
document.addEventListener('change', event => {
    const element = event.target.closest('[data-change]');
    if (element) runHandler(element, element.dataset.change);
});

/* ===== Keyboard ===== */
let lastFocusBeforeModal = null;
document.addEventListener('keydown', event => {
    const openModals = [...document.querySelectorAll('.modal-bg.show')];
    const modal = openModals[openModals.length - 1];
    if (modal) {
        if (event.key === 'Escape' && modal.id !== 'loginModal') {
            closeModal(modal.id);
            event.preventDefault();
            return;
        }
        if (event.key === 'Tab') {
            const focusable = [...modal.querySelectorAll('a[href],button:not([disabled]),input:not([type=hidden]),select,textarea,[tabindex="0"]')].filter(e => e.offsetParent !== null);
            if (focusable.length > 0) {
                const first = focusable[0];
                const last = focusable[focusable.length - 1];
                if (event.shiftKey && document.activeElement === first) { last.focus(); event.preventDefault(); }
                else if (!event.shiftKey && document.activeElement === last) { first.focus(); event.preventDefault(); }
            }
        }
        return;
    }
    // Things that act like buttons without being buttons
    if ((event.key === 'Enter' || event.key === ' ') && event.target.matches('[role="tab"], tr.clickable, [data-click]:not(button)')) {
        event.preventDefault();
        event.target.click();
    }
    if (event.key === 'Escape' && document.getElementById('sidebar').classList.contains('open')) toggleSidebar();
});

function toggleSidebar() {
    const sidebar = document.getElementById('sidebar');
    const open = sidebar.classList.toggle('open');
    document.getElementById('menuButton').setAttribute('aria-expanded', String(open));
}

function initAccessibility() {
    document.querySelectorAll('.modal').forEach((modal, i) => {
        const heading = modal.querySelector('h3');
        modal.setAttribute('role', 'dialog');
        modal.setAttribute('aria-modal', 'true');
        if (heading) {
            heading.id = heading.id || `dialog-title-${i}`;
            modal.setAttribute('aria-labelledby', heading.id);
        }
    });
    // Tie each label to the control beside it
    document.querySelectorAll('.form-group').forEach(group => {
        const label = group.querySelector('label');
        const control = group.querySelector('input:not([type=hidden]):not([type=checkbox]),select,textarea');
        if (label && control && !label.htmlFor) {
            control.id = control.id || `field-${Math.random().toString(36).slice(2, 10)}`;
            label.htmlFor = control.id;
        }
    });
    document.querySelectorAll('.tabs').forEach(list => {
        list.setAttribute('role', 'tablist');
        list.querySelectorAll('.tab').forEach(tab => {
            tab.setAttribute('role', 'tab');
            tab.setAttribute('tabindex', '0');
            tab.setAttribute('aria-selected', String(tab.classList.contains('active')));
        });
    });
    document.querySelectorAll('th:not([scope])').forEach(th => th.setAttribute('scope', 'col'));
}

function markTabSelected(tab) {
    tab.parentElement.querySelectorAll('[role="tab"]').forEach(t => t.setAttribute('aria-selected', String(t === tab)));
}

/* ===== Navigation ===== */
document.querySelectorAll('.nav-item[data-page]').forEach(item => {
    item.addEventListener('click', () => navigate(item.dataset.page));
});

const VALID_PAGES = ['rules', 'search','livetail','saved','jobs','dashboards','health','ingest','indexes','alerts','schedules','extractions','knowledge','settings'];
function navigate(page) {
    if (!VALID_PAGES.includes(page)) return;
    currentPage = page;
    document.querySelectorAll('.nav-item').forEach(n => { n.classList.remove('active'); n.removeAttribute('aria-current'); });
    const navItem = document.querySelector(`.nav-item[data-page="${page}"]`);
    if (navItem) { navItem.classList.add('active'); navItem.setAttribute('aria-current', 'page'); }
    document.getElementById('sidebar').classList.remove('open');
    stopDashboardRefresh();
    document.querySelectorAll('.page').forEach(p => p.classList.remove('active'));
    const pageEl = document.getElementById('page-' + page);
    if (pageEl) pageEl.classList.add('active');
    const titles = { rules:'Detections', search:'Search', livetail:'Live Tail', saved:'Saved Queries', jobs:'Search Jobs', dashboards:'Dashboards', health:'System Health', ingest:'Ingest Logs', indexes:'Indexes', alerts:'Alerts', schedules:'Scheduled Searches', extractions:'Field Extractions', knowledge:'Knowledge Objects', settings:'Settings' };
    document.getElementById('pageTitle').textContent = titles[page] || page;
    // Update URL without reload
    // Search keeps its own state in the hash, so a search can be bookmarked
    const onSearch = window.location.hash.startsWith('#search');
    const url = page === 'search' ? (onSearch ? window.location.pathname + window.location.hash : '/') : '/#' + page;
    if (!(page === 'search' ? (onSearch || window.location.hash === '') : window.location.hash === '#' + page)) history.pushState({ page }, titles[page], url);
    // Stop live tail if leaving that page
    if (page !== 'livetail' && tailEventSource) { tailEventSource.close(); tailEventSource = null; document.getElementById('tailDot').style.display = 'none'; document.getElementById('tailStartBtn').textContent = 'Start'; }
    // Load data for page
    if (page === 'saved') loadSavedQueries();
    if (page === 'dashboards') loadDashboards();
    if (page === 'health') loadHealthDetails();
    if (page === 'indexes') loadIndexes();
    if (page === 'alerts') loadAlerts();
    if (page === 'rules') loadRules();
    if (page === 'schedules') loadSchedules();
    if (page === 'extractions') loadExtractions();
    if (page === 'knowledge') loadKnowledgeCurrent();
    if (page === 'settings') loadSettings();
    if (page === 'livetail') loadTailIndexes();
    if (page === 'jobs') loadJobs();
}

/* ===== API Helper ===== */
async function api(method, path, body) {
    const opts = { method, headers: { 'X-API-Key': API_KEY, 'Accept': 'application/json' } };
    if (body) { opts.headers['Content-Type'] = 'application/json'; opts.body = JSON.stringify(body); }
    const res = await fetch(path, opts);
    if (res.status === 401) {
        showLogin(API_KEY ? 'That API key was not accepted.' : '');
    }
    if (!res.ok) {
        let msg = `HTTP ${res.status}`;
        try { const err = await res.json(); msg = err.detail || err.title || msg; } catch {}
        throw new Error(msg);
    }
    const ct = res.headers.get('content-type') || '';
    if (ct.includes('json')) return res.json();
    return res.text();
}

/* Search lives in search.js (logic) and search-page.js (the page). */

/* ===== Saved Queries ===== */
async function loadSavedQueries() {
    const tbody = document.getElementById('savedQueriesBody');
    tbody.innerHTML = '<tr><td colspan="4" style="text-align:center;padding:24px"><span class="spinner"></span></td></tr>';
    try {
        const data = await api('GET', '/api/v1/query/saved');
        const queries = Array.isArray(data) ? data : (data.data || []);
        if (queries.length === 0) {
            tbody.innerHTML = '<tr><td colspan="4" style="text-align:center;padding:32px;color:var(--text-secondary)">No saved queries yet</td></tr>';
            return;
        }
        tbody.innerHTML = '';
        queries.forEach(q => {
            const tr = document.createElement('tr');
            tr.innerHTML = `<td style="font-weight:500">${esc(q.name)}</td>
                <td style="max-width:300px" title="${esc(q.query)}">${esc(q.query)}</td>
                <td style="color:var(--text-secondary)">${q.createdAt ? new Date(q.createdAt).toLocaleDateString() : '-'}</td>
                <td style="white-space:nowrap"><button class="btn btn-secondary btn-sm run-btn">Run</button> <button class="btn btn-danger btn-sm del-btn">Del</button></td>`;
            tr.querySelector('.run-btn').onclick = () => runSavedQuery(q.query);
            tr.querySelector('.del-btn').onclick = () => deleteSavedQuery(q.id);
            tbody.appendChild(tr);
        });
    } catch (e) {
        tbody.innerHTML = `<tr><td colspan="4" style="color:var(--error);padding:16px">${esc(e.message)}</td></tr>`;
    }
}

function runSavedQuery(query) {
    navigate('search');
    setQuery(query);
    executeQuery();
}

async function deleteSavedQuery(id) {
    if (!confirm('Delete this saved query?')) return;
    try {
        await api('DELETE', '/api/v1/query/saved/' + id);
        toast('Saved query deleted', 'success');
        loadSavedQueries();
    } catch (e) { toast('Failed: ' + e.message, 'error'); }
}

function saveCurrentQuery() {
    const q = document.getElementById('queryInput').value.trim();
    if (!q) return;
    document.getElementById('saveQueryText').value = q;
    document.getElementById('saveQueryName').value = '';
    document.getElementById('saveQueryDesc').value = '';
    document.getElementById('saveModal').classList.add('show');
    document.getElementById('saveQueryName').focus();
}

function closeSaveModal() { document.getElementById('saveModal').classList.remove('show'); }

async function confirmSaveQuery() {
    const name = document.getElementById('saveQueryName').value.trim();
    const query = document.getElementById('saveQueryText').value;
    const description = document.getElementById('saveQueryDesc').value.trim();
    if (!name) { toast('Please enter a name', 'error'); return; }
    try {
        await api('POST', '/api/v1/query/saved', { name, query, description });
        closeSaveModal();
        toast('Query saved', 'success');
    } catch (e) { toast('Failed to save: ' + e.message, 'error'); }
}

/* ===== Dashboards ===== */
async function loadDashboards() {
    const el = document.getElementById('dashboardsList');
    el.innerHTML = '<div class="empty-state"><span class="spinner"></span></div>';
    try {
        const data = await api('GET', '/api/v1/dashboards');
        const dashboards = Array.isArray(data) ? data : (data.data || []);
        if (dashboards.length === 0) {
            el.innerHTML = '<div class="empty-state"><h3>No Dashboards</h3><p>Click "+ New Dashboard" to create one</p></div>';
            return;
        }
        el.innerHTML = '';
        dashboards.forEach(d => {
            const div = document.createElement('div');
            div.className = 'health-item';
            const panelCount = d.panelCount ?? (d.panels ? d.panels.length : 0);
            div.innerHTML = `<div class="hi-header"><span class="hi-name">${esc(d.title || d.name || 'Untitled')}</span>
                <span class="hi-status healthy">${panelCount} panels</span></div>
                <div class="hi-detail">${esc(d.description || 'No description')}</div>
                <div class="hi-detail" style="margin-top:4px">Created: ${d.createdAt ? new Date(d.createdAt).toLocaleDateString() : '-'}</div>
                <div id="dash-panels-${esc(d.id)}" style="margin-top:8px"></div>
                <div style="margin-top:8px;display:flex;gap:6px">
                    <button class="btn btn-secondary btn-sm view-btn">View Panels</button>
                    <button class="btn btn-secondary btn-sm edit-btn">Edit</button>
                    <button class="btn btn-danger btn-sm del-btn">Del</button>
                </div>`;
            div.querySelector('.view-btn').onclick = () => viewDashboardPanels(d);
            div.querySelector('.edit-btn').onclick = () => editDashboard(d);
            div.querySelector('.del-btn').onclick = () => deleteDashboard(d.id);
            el.appendChild(div);
        });
    } catch (e) {
        el.innerHTML = `<div class="empty-state" style="color:var(--error)">${esc(e.message)}</div>`;
    }
}

function showDashboardModal() {
    document.getElementById('dashboardEditId').value = '';
    document.getElementById('dashboardModalTitle').textContent = 'New Dashboard';
    document.getElementById('dashTitle').value = '';
    document.getElementById('dashDescription').value = '';
    document.getElementById('dashRefresh').value = '0';
    document.getElementById('panelEditorList').innerHTML = '';
    addPanelRow();
    openModal('dashboardModal');
    document.getElementById('dashTitle').focus();
}

async function editDashboard(summary) {
    // The list only has summaries
    const d = await api('GET', '/api/v1/dashboards/' + encodeURIComponent(summary.id));
    document.getElementById('dashboardEditId').value = d.id || '';
    document.getElementById('dashboardModalTitle').textContent = 'Edit Dashboard';
    document.getElementById('dashTitle').value = d.title || d.name || '';
    document.getElementById('dashDescription').value = d.description || '';
    document.getElementById('dashRefresh').value = String(d.refreshIntervalSeconds || 0);
    document.getElementById('panelEditorList').innerHTML = '';
    (d.panels || []).slice().sort((a, b) => (a.row - b.row) || (a.column - b.column)).forEach(addPanelRow);
    openModal('dashboardModal');
}

async function saveDashboard() {
    const id = document.getElementById('dashboardEditId').value;
    const body = {
        title: document.getElementById('dashTitle').value.trim(),
        description: document.getElementById('dashDescription').value.trim(),
        refreshIntervalSeconds: Number(document.getElementById('dashRefresh').value),
        panels: readPanelRows().filter(panel => panel.query)
    };
    if (!body.title) { toast('Title is required', 'error'); return; }
    try {
        if (id) await api('PUT', '/api/v1/dashboards/' + id, body);
        else await api('POST', '/api/v1/dashboards', body);
        closeModal('dashboardModal');
        toast(id ? 'Dashboard updated' : 'Dashboard created', 'success');
        loadDashboards();
    } catch (e) { toast('Failed: ' + e.message, 'error'); }
}

async function deleteDashboard(id) {
    if (!confirm('Delete this dashboard?')) return;
    try {
        await api('DELETE', '/api/v1/dashboards/' + id);
        toast('Dashboard deleted', 'success');
        loadDashboards();
    } catch (e) { toast('Failed: ' + e.message, 'error'); }
}

let dashboardTimer = null;

function stopDashboardRefresh() {
    if (dashboardTimer) { clearInterval(dashboardTimer); dashboardTimer = null; }
}

const PANEL_VIZ = { Table: 'table', LineChart: 'line', AreaChart: 'area', BarChart: 'bar', SingleValue: 'stat' };

async function renderPanel(panel, body) {
    if (!panel.query) { body.innerHTML = '<p class="chart-note">No query defined.</p>'; return; }
    try {
        const result = await api('POST', '/api/v1/query/execute', { query: panel.query, useCache: true });
        CabinetCharts.render(body, result.results || [], { viz: PANEL_VIZ[panel.type], title: panel.title, height: 200, tableWhenRaw: true });
    } catch (e) {
        body.innerHTML = `<p class="chart-note" style="color:var(--error)">${esc(e.message)}</p>`;
    }
}

async function viewDashboardPanels(d) {
    const container = document.getElementById('dash-panels-' + d.id);
    if (!container) return;
    stopDashboardRefresh();
    if (container.innerHTML) { container.innerHTML = ''; return; } // toggle off
    d = await api('GET', '/api/v1/dashboards/' + encodeURIComponent(d.id));
    const panels = (d.panels || []).slice().sort((a, b) => (a.row - b.row) || (a.column - b.column));
    if (panels.length === 0) { container.innerHTML = '<p class="chart-note">No panels yet. Edit the dashboard to add some.</p>'; return; }

    const refresh = d.refreshIntervalSeconds || 0;
    container.innerHTML = `<div class="dash-toolbar" role="status"></div><div class="dash-grid"></div>`;
    const status = container.querySelector('.dash-toolbar');
    const grid = container.querySelector('.dash-grid');
    const bodies = panels.map(panel => {
        const section = document.createElement('section');
        section.className = 'dash-panel';
        section.style.gridColumn = `span ${Math.min(12, Math.max(1, panel.width || 6))}`;
        section.innerHTML = `<h4>${esc(panel.title || 'Untitled panel')}</h4><div class="dash-panel-body"><span class="spinner"></span></div>`;
        grid.appendChild(section);
        return section.querySelector('.dash-panel-body');
    });

    const update = async () => {
        await Promise.all(panels.map((panel, i) => renderPanel(panel, bodies[i])));
        status.textContent = `Updated ${new Date().toLocaleTimeString()}${refresh ? `, refreshing every ${refresh} seconds` : ''}`;
    };

    await update();
    if (refresh > 0) dashboardTimer = setInterval(update, refresh * 1000);
}

function addPanelRow(panel) {
    const data = panel && panel.title !== undefined ? panel : { title: '', query: '', type: 'LineChart', width: 6 };
    const row = document.createElement('div');
    row.className = 'panel-row';
    const options = [['LineChart', 'Line'], ['AreaChart', 'Stacked area'], ['BarChart', 'Bars'], ['SingleValue', 'Single value'], ['Table', 'Table']]
        .map(([value, label]) => `<option value="${value}"${data.type === value ? ' selected' : ''}>${label}</option>`).join('');
    const widths = [[12, 'Full'], [6, 'Half'], [4, 'Third'], [3, 'Quarter']]
        .map(([value, label]) => `<option value="${value}"${Number(data.width) === value ? ' selected' : ''}>${label}</option>`).join('');
    row.innerHTML = `<div class="panel-row-grid">
            <label>Title <input type="text" class="panel-title" value="${esc(data.title || '')}"></label>
            <label>Chart <select class="panel-type">${options}</select></label>
            <label>Width <select class="panel-width">${widths}</select></label>
        </div>
        <label>Query <textarea class="panel-query" rows="2" placeholder="find * | timechart span=1h count">${esc(data.query || '')}</textarea></label>
        <div class="panel-row-actions">
            <button type="button" class="btn btn-secondary btn-sm panel-preview-btn">Preview</button>
            <button type="button" class="btn btn-secondary btn-sm panel-up" aria-label="Move panel up">Up</button>
            <button type="button" class="btn btn-secondary btn-sm panel-down" aria-label="Move panel down">Down</button>
            <button type="button" class="btn btn-danger btn-sm panel-remove" aria-label="Remove panel">Remove</button>
        </div>
        <div class="panel-preview"></div>`;
    row.dataset.panelId = data.id || '';
    row.querySelector('.panel-remove').onclick = () => row.remove();
    row.querySelector('.panel-up').onclick = () => { if (row.previousElementSibling) row.parentElement.insertBefore(row, row.previousElementSibling); };
    row.querySelector('.panel-down').onclick = () => { if (row.nextElementSibling) row.parentElement.insertBefore(row.nextElementSibling, row); };
    row.querySelector('.panel-preview-btn').onclick = () => {
        const preview = row.querySelector('.panel-preview');
        preview.innerHTML = '<span class="spinner"></span>';
        renderPanel({ query: row.querySelector('.panel-query').value.trim(), type: row.querySelector('.panel-type').value, title: row.querySelector('.panel-title').value }, preview);
    };
    document.getElementById('panelEditorList').appendChild(row);
}

function readPanelRows() {
    return [...document.querySelectorAll('#panelEditorList .panel-row')].map((row, i) => {
        const panel = {
            title: row.querySelector('.panel-title').value.trim() || `Panel ${i + 1}`,
            query: row.querySelector('.panel-query').value.trim(),
            type: row.querySelector('.panel-type').value,
            width: Number(row.querySelector('.panel-width').value),
            row: i,
            column: 0,
            height: 4,
        };
        if (row.dataset.panelId) panel.id = row.dataset.panelId;
        return panel;
    });
}

/* ===== Health ===== */
async function loadHealthDetails() {
    try {
        const data = await api('GET', '/api/health/status');
        document.getElementById('sysStatus').textContent = data.status || 'Unknown';
        document.getElementById('sysStatus').className = 'value ' + (data.status === 'Healthy' ? 'success' : data.status === 'Degraded' ? 'warning' : 'error');

        if (data.services) {
            const hc = document.getElementById('healthChecks');
            hc.innerHTML = '';
            Object.entries(data.services).forEach(([name, svc]) => {
                const div = document.createElement('div');
                div.className = 'health-item';
                const status = (svc.status || 'unknown').toLowerCase();
                const cls = status === 'healthy' ? 'healthy' : status === 'degraded' ? 'degraded' : 'unhealthy';
                div.innerHTML = `<div class="hi-header"><span class="hi-name">${esc(name)}</span>
                    <span class="hi-status ${cls}">${esc(svc.status || 'unknown')}</span></div>
                    <div class="hi-detail">${esc(svc.message || '')}</div>`;
                hc.appendChild(div);
            });
        }

        if (data.features) {
            const hf = document.getElementById('healthFeatures');
            hf.innerHTML = '';
            Object.entries(data.features).forEach(([name, enabled]) => {
                const div = document.createElement('div');
                div.className = 'health-item';
                div.innerHTML = `<div class="hi-header"><span class="hi-name">${esc(name)}</span>
                    <span class="hi-status ${enabled ? 'healthy' : 'degraded'}">${enabled ? 'Enabled' : 'Disabled'}</span></div>`;
                hf.appendChild(div);
            });
        }
    } catch (e) {
        document.getElementById('sysStatus').textContent = 'Error';
        document.getElementById('sysStatus').className = 'value error';
    }

    // Version/uptime
    try {
        const ver = await api('GET', '/api/health/version');
        document.getElementById('sysUptime').textContent = ver.version || '-';
        document.getElementById('sysMem').textContent = ver.environment || '-';
        document.getElementById('sysStorage').textContent = ver.apiVersion || '-';
    } catch {}
}

/* ===== Ingest ===== */
async function ingestLogs() {
    const input = document.getElementById('ingestInput').value.trim();
    if (!input) return;
    const ingestFmt = document.getElementById('ingestFormat').value;
    const errEl = document.getElementById('ingestError');
    const okEl = document.getElementById('ingestSuccess');
    errEl.style.display = 'none'; okEl.style.display = 'none';
    document.getElementById('ingestSpinner').classList.remove('hide');

    try {
        let logs;
        if (ingestFmt === 'json') {
            logs = JSON.parse(input);
            if (!Array.isArray(logs)) logs = [logs];
            logs = logs.map(l => typeof l === 'string' ? l : JSON.stringify(l));
        } else {
            logs = input.split('\n').filter(l => l.trim());
        }
        const data = await api('POST', '/api/v1/logs/ingest', { logs });
        okEl.textContent = `Ingested ${data.totalIngested || logs.length} of ${data.totalReceived || logs.length} events`;
        okEl.style.display = 'block';
        toast(`Ingested ${data.totalIngested || logs.length} events`, 'success');
        if (data.errors && data.errors.length > 0) {
            errEl.textContent = 'Warnings: ' + data.errors.join('; ');
            errEl.style.display = 'block';
        }
    } catch (e) {
        errEl.textContent = e.message; errEl.style.display = 'block';
    } finally {
        document.getElementById('ingestSpinner').classList.add('hide');
    }
}

/* ===== Indexes ===== */
async function loadIndexes() {
    const el = document.getElementById('indexesList');
    el.innerHTML = '<div class="empty-state"><span class="spinner"></span></div>';
    try {
        const data = await api('GET', '/api/v1/indexes');
        const indexes = Array.isArray(data) ? data : (data.data || data.indexes || []);
        if (indexes.length === 0) {
            el.innerHTML = '<div class="empty-state"><h3>No Indexes</h3><p>Ingest some logs to create indexes</p></div>';
            return;
        }
        el.innerHTML = '';
        indexes.forEach(idx => {
            const div = document.createElement('div');
            div.className = 'health-item';
            const name = typeof idx === 'string' ? idx : (idx.name || idx.index || 'unknown');
            const count = typeof idx === 'object' ? (idx.eventCount || idx.count || '-') : '-';
            div.innerHTML = `<div class="hi-header"><span class="hi-name">${esc(name)}</span>
                <span class="hi-status healthy">${count === '-' ? '' : fmt(count) + ' events'}</span></div>`;
            el.appendChild(div);
        });
    } catch (e) {
        el.innerHTML = `<div class="empty-state" style="color:var(--error)">${esc(e.message)}</div>`;
    }
}

/* ===== Alerts ===== */
async function loadAlerts() {
    const tbody = document.getElementById('alertsBody');
    tbody.innerHTML = '<tr><td colspan="6" style="text-align:center;padding:24px"><span class="spinner"></span></td></tr>';
    try {
        const sev = document.getElementById('alertSevFilter').value;
        const ack = document.getElementById('alertAckFilter').value;
        let url = '/api/v1/alerts?count=100';
        if (sev) url += '&severity=' + sev;
        if (ack) url += '&acknowledged=' + ack;
        const data = await api('GET', url);
        const alerts = Array.isArray(data) ? data : (data.data || []);
        if (alerts.length === 0) {
            tbody.innerHTML = '<tr><td colspan="6" style="text-align:center;padding:32px;color:var(--text-secondary)">No alerts found</td></tr>';
            return;
        }
        tbody.innerHTML = '';
        alerts.forEach(a => {
            const tr = document.createElement('tr');
            const acked = a.acknowledged;
            tr.innerHTML = `<td>${fmtTime(a.firedAt)}</td>
                <td>${sevBadge(a.severity)}</td>
                <td style="font-weight:500">${esc(a.searchName || '-')}${a.technique ? ` <span class="badge">${esc(a.technique)}</span>` : ''}${a.groupValue ? `<div class="chart-note">${esc(a.groupValue)}</div>` : ''}</td>
                <td style="max-width:300px" title="${esc(a.message || '')}">${esc(a.message || '-')}</td>
                <td>${acked ? '<span class="badge badge-success">Ack</span>' : '<span class="badge badge-warning">Open</span>'}</td>
                <td>${!acked ? '<button class="btn btn-secondary btn-sm ack-btn">Acknowledge</button>' : ''}</td>`;
            if (!acked) { const btn = tr.querySelector('.ack-btn'); if (btn) btn.onclick = () => ackAlert(a.id); }
            tbody.appendChild(tr);
        });
    } catch (e) {
        tbody.innerHTML = `<tr><td colspan="6" style="color:var(--error);padding:16px">${esc(e.message)}</td></tr>`;
    }
}

let rulesById = {};

async function loadRules() {
    const tbody = document.getElementById('rulesBody');
    tbody.innerHTML = '<tr><td colspan="6" style="text-align:center;padding:24px"><span class="spinner"></span></td></tr>';
    try {
        const rules = await api('GET', '/api/v1/rules');
        rulesById = Object.fromEntries(rules.map(r => [r.id, r]));
        tbody.innerHTML = '';
        rules.forEach(rule => {
            const tr = document.createElement('tr');
            const firesOn = rule.threshold > 1
                ? `${fmt(rule.threshold)} matches in ${fmt(rule.windowSeconds)}s${rule.groupBy ? ' by ' + esc(rule.groupBy) : ''}`
                : 'Each match';
            tr.innerHTML = `<td><div style="font-weight:500">${esc(rule.name)}</div><div class="chart-note">${esc(rule.description || '')}</div></td>
                <td>${sevBadge(rule.severity)}</td>
                <td>${firesOn}</td>
                <td>${esc(rule.technique || '-')}</td>
                <td><button type="button" class="btn btn-secondary btn-sm toggle-btn" aria-pressed="${rule.enabled}" aria-label="${rule.enabled ? 'Turn off' : 'Turn on'} ${esc(rule.name)}">${rule.enabled ? 'On' : 'Off'}</button></td>
                <td>${rule.builtin ? '<span class="chart-note">Built in</span>' : '<button type="button" class="btn btn-secondary btn-sm edit-btn">Edit</button> <button type="button" class="btn btn-danger btn-sm del-btn">Del</button>'}</td>`;
            tr.querySelector('.toggle-btn').onclick = () => toggleRule(rule.id, !rule.enabled);
            if (!rule.builtin) {
                tr.querySelector('.edit-btn').onclick = () => showRuleModal(rule.id);
                tr.querySelector('.del-btn').onclick = () => deleteRule(rule.id);
            }
            tbody.appendChild(tr);
        });
    } catch (e) {
        tbody.innerHTML = `<tr><td colspan="6" style="color:var(--error);padding:16px">${esc(e.message)}</td></tr>`;
    }
}

async function toggleRule(id, enabled) {
    try {
        await api('POST', `/api/v1/rules/${encodeURIComponent(id)}/${enabled ? 'enable' : 'disable'}`);
        loadRules();
    } catch (e) { toast('Failed: ' + e.message, 'error'); }
}

async function deleteRule(id) {
    if (!confirm('Delete this rule?')) return;
    try {
        await api('DELETE', `/api/v1/rules/${encodeURIComponent(id)}`);
        loadRules();
    } catch (e) { toast('Failed: ' + e.message, 'error'); }
}

function showRuleModal(id) {
    const rule = (typeof id === 'string' && rulesById[id]) || {};
    document.getElementById('ruleEditId').value = rule.id || '';
    document.getElementById('ruleModalTitle').textContent = rule.id ? 'Edit Rule' : 'New Rule';
    document.getElementById('ruleName').value = rule.name || '';
    document.getElementById('ruleSeverity').value = rule.severity || 'Warning';
    document.getElementById('rulePattern').value = rule.pattern || '';
    document.getElementById('ruleCondition').value = rule.condition || '';
    document.getElementById('ruleExtract').value = rule.extract || '';
    document.getElementById('ruleGroupBy').value = rule.groupBy || '';
    document.getElementById('ruleThreshold').value = rule.threshold || 1;
    document.getElementById('ruleWindow').value = rule.windowSeconds || 300;
    document.getElementById('ruleCooldown').value = rule.cooldownSeconds ?? 300;
    document.getElementById('ruleDescription').value = rule.description || '';
    document.getElementById('ruleTestResult').textContent = '';
    openModal('ruleModal');
}

function readRuleForm() {
    const text = id => document.getElementById(id).value.trim() || null;
    return {
        name: document.getElementById('ruleName').value.trim(),
        severity: document.getElementById('ruleSeverity').value,
        pattern: text('rulePattern'),
        condition: text('ruleCondition'),
        extract: text('ruleExtract'),
        groupBy: text('ruleGroupBy'),
        threshold: Number(document.getElementById('ruleThreshold').value),
        windowSeconds: Number(document.getElementById('ruleWindow').value),
        cooldownSeconds: Number(document.getElementById('ruleCooldown').value),
        description: text('ruleDescription'),
    };
}

async function testRule() {
    const out = document.getElementById('ruleTestResult');
    const rule = readRuleForm();
    try {
        const result = await api('POST', '/api/v1/rules/test', { line: document.getElementById('ruleTestLine').value, pattern: rule.pattern, condition: rule.condition, extract: rule.extract });
        out.textContent = result.matched
            ? `Matches.${rule.groupBy ? ` It would count by ${rule.groupBy} = ${result.fields[rule.groupBy] ?? '(no value, so it would not count)'}.` : ''}`
            : 'Does not match.';
    } catch (e) { out.textContent = e.message; }
}

async function saveRule() {
    const id = document.getElementById('ruleEditId').value;
    const rule = readRuleForm();
    try {
        if (id) await api('PUT', `/api/v1/rules/${encodeURIComponent(id)}`, rule);
        else await api('POST', '/api/v1/rules', rule);
        closeModal('ruleModal');
        toast('Rule saved', 'success');
        loadRules();
    } catch (e) { toast('Failed: ' + e.message, 'error'); }
}

async function ackAlert(id) {
    try {
        await api('POST', '/api/v1/alerts/' + id + '/acknowledge');
        toast('Alert acknowledged', 'success');
        loadAlerts();
    } catch (e) { toast('Failed: ' + e.message, 'error'); }
}

/* ===== Scheduled Searches ===== */
async function loadSchedules() {
    const tbody = document.getElementById('schedulesBody');
    tbody.innerHTML = '<tr><td colspan="6" style="text-align:center;padding:24px"><span class="spinner"></span></td></tr>';
    try {
        const data = await api('GET', '/api/v1/scheduled-searches');
        const items = Array.isArray(data) ? data : (data.data || []);
        if (items.length === 0) {
            tbody.innerHTML = '<tr><td colspan="6" style="text-align:center;padding:32px;color:var(--text-secondary)">No scheduled searches</td></tr>';
            return;
        }
        tbody.innerHTML = '';
        items.forEach(s => {
            const tr = document.createElement('tr');
            const enabled = s.enabled !== false;
            tr.innerHTML = `<td style="font-weight:500">${esc(s.name)}</td>
                <td style="max-width:220px;font-family:'SF Mono','Fira Code',monospace;font-size:11px" title="${esc(s.query)}">${esc(s.query)}</td>
                <td style="font-family:monospace;font-size:11px">${esc(s.cronExpression || s.cron || '-')}</td>
                <td>${esc(s.timeRange || '-')}</td>
                <td>${enabled ? '<span class="badge badge-success">Enabled</span>' : '<span class="badge badge-muted">Disabled</span>'}</td>
                <td style="white-space:nowrap">
                    <button class="btn btn-secondary btn-sm toggle-btn" title="${enabled ? 'Disable' : 'Enable'}">${enabled ? 'Disable' : 'Enable'}</button>
                    <button class="btn btn-secondary btn-sm edit-btn">Edit</button>
                    <button class="btn btn-danger btn-sm del-btn">Del</button>
                </td>`;
            tr.querySelector('.toggle-btn').onclick = () => toggleSchedule(s.id, !enabled);
            tr.querySelector('.edit-btn').onclick = () => editSchedule(s.id);
            tr.querySelector('.del-btn').onclick = () => deleteSchedule(s.id);
            tbody.appendChild(tr);
        });
    } catch (e) {
        tbody.innerHTML = `<tr><td colspan="6" style="color:var(--error);padding:16px">${esc(e.message)}</td></tr>`;
    }
}

function showScheduleModal(edit) {
    document.getElementById('scheduleEditId').value = '';
    document.getElementById('scheduleModalTitle').textContent = 'New Scheduled Search';
    document.getElementById('schedName').value = '';
    document.getElementById('schedQuery').value = '';
    document.getElementById('schedCron').value = '*/5 * * * *';
    document.getElementById('schedRange').value = '-5m';
    document.getElementById('schedCondition').value = 'when_count_gt';
    document.getElementById('schedThreshold').value = '100';
    document.getElementById('schedEnabled').checked = true;
    openModal('scheduleModal');
}

async function editSchedule(id) {
    try {
        const s = await api('GET', '/api/v1/scheduled-searches/' + id);
        document.getElementById('scheduleEditId').value = id;
        document.getElementById('scheduleModalTitle').textContent = 'Edit Scheduled Search';
        document.getElementById('schedName').value = s.name || '';
        document.getElementById('schedQuery').value = s.query || '';
        document.getElementById('schedCron').value = s.cronExpression || s.cron || '';
        document.getElementById('schedRange').value = s.timeRange || '-5m';
        if (s.alertAction) {
            document.getElementById('schedCondition').value = s.alertAction.condition || 'when_count_gt';
            document.getElementById('schedThreshold').value = s.alertAction.threshold || 100;
        }
        document.getElementById('schedEnabled').checked = s.enabled !== false;
        openModal('scheduleModal');
    } catch (e) { toast('Failed to load: ' + e.message, 'error'); }
}

async function saveSchedule() {
    const id = document.getElementById('scheduleEditId').value;
    const body = {
        name: document.getElementById('schedName').value.trim(),
        query: document.getElementById('schedQuery').value.trim(),
        cronExpression: document.getElementById('schedCron').value.trim(),
        timeRange: document.getElementById('schedRange').value.trim(),
        enabled: document.getElementById('schedEnabled').checked,
        alertAction: {
            condition: document.getElementById('schedCondition').value,
            threshold: parseInt(document.getElementById('schedThreshold').value) || 100,
            severity: 3
        }
    };
    if (!body.name || !body.query) { toast('Name and query are required', 'error'); return; }
    try {
        if (id) await api('PUT', '/api/v1/scheduled-searches/' + id, body);
        else await api('POST', '/api/v1/scheduled-searches', body);
        closeModal('scheduleModal');
        toast(id ? 'Schedule updated' : 'Schedule created', 'success');
        loadSchedules();
    } catch (e) { toast('Failed: ' + e.message, 'error'); }
}

async function toggleSchedule(id, enable) {
    try {
        await api('POST', '/api/v1/scheduled-searches/' + id + '/' + (enable ? 'enable' : 'disable'));
        toast('Schedule ' + (enable ? 'enabled' : 'disabled'), 'success');
        loadSchedules();
    } catch (e) { toast('Failed: ' + e.message, 'error'); }
}

async function deleteSchedule(id) {
    if (!confirm('Delete this scheduled search?')) return;
    try {
        await api('DELETE', '/api/v1/scheduled-searches/' + id);
        toast('Deleted', 'success');
        loadSchedules();
    } catch (e) { toast('Failed: ' + e.message, 'error'); }
}

/* ===== Field Extractions ===== */
async function loadExtractions() {
    const tbody = document.getElementById('extractionsBody');
    tbody.innerHTML = '<tr><td colspan="4" style="text-align:center;padding:24px"><span class="spinner"></span></td></tr>';
    try {
        const data = await api('GET', '/api/v1/field-extractions');
        const items = Array.isArray(data) ? data : (data.data || []);
        if (items.length === 0) {
            tbody.innerHTML = '<tr><td colspan="4" style="text-align:center;padding:32px;color:var(--text-secondary)">No field extractions configured</td></tr>';
            return;
        }
        tbody.innerHTML = '';
        items.forEach(e => {
            const tr = document.createElement('tr');
            tr.innerHTML = `<td style="font-weight:500">${esc(e.name)}</td>
                <td style="font-family:'SF Mono','Fira Code',monospace;font-size:11px;max-width:300px" title="${esc(e.pattern)}">${esc(e.pattern)}</td>
                <td>${e.priority || 100}</td>
                <td style="white-space:nowrap">
                    <button class="btn btn-secondary btn-sm edit-btn">Edit</button>
                    <button class="btn btn-danger btn-sm del-btn">Del</button>
                </td>`;
            tr.querySelector('.edit-btn').onclick = () => editExtraction(e.id);
            tr.querySelector('.del-btn').onclick = () => deleteExtraction(e.id);
            tbody.appendChild(tr);
        });
    } catch (e) {
        tbody.innerHTML = `<tr><td colspan="4" style="color:var(--error);padding:16px">${esc(e.message)}</td></tr>`;
    }
}

function showExtractionModal() {
    document.getElementById('extractionEditId').value = '';
    document.getElementById('extractionModalTitle').textContent = 'New Field Extraction';
    document.getElementById('extName').value = '';
    document.getElementById('extPattern').value = '';
    document.getElementById('extPriority').value = '100';
    openModal('extractionModal');
}

async function editExtraction(id) {
    try {
        const e = await api('GET', '/api/v1/field-extractions/' + id);
        document.getElementById('extractionEditId').value = id;
        document.getElementById('extractionModalTitle').textContent = 'Edit Field Extraction';
        document.getElementById('extName').value = e.name || '';
        document.getElementById('extPattern').value = e.pattern || '';
        document.getElementById('extPriority').value = e.priority || 100;
        openModal('extractionModal');
    } catch (e) { toast('Failed: ' + e.message, 'error'); }
}

async function saveExtraction() {
    const id = document.getElementById('extractionEditId').value;
    const body = {
        name: document.getElementById('extName').value.trim(),
        pattern: document.getElementById('extPattern').value.trim(),
        priority: parseInt(document.getElementById('extPriority').value) || 100
    };
    if (!body.name || !body.pattern) { toast('Name and pattern are required', 'error'); return; }
    try {
        if (id) await api('PUT', '/api/v1/field-extractions/' + id, body);
        else await api('POST', '/api/v1/field-extractions', body);
        closeModal('extractionModal');
        toast(id ? 'Extraction updated' : 'Extraction created', 'success');
        loadExtractions();
    } catch (e) { toast('Failed: ' + e.message, 'error'); }
}

async function deleteExtraction(id) {
    if (!confirm('Delete this field extraction?')) return;
    try {
        await api('DELETE', '/api/v1/field-extractions/' + id);
        toast('Deleted', 'success');
        loadExtractions();
    } catch (e) { toast('Failed: ' + e.message, 'error'); }
}

/* ===== Knowledge Objects ===== */
let activeKnowledgeTab = 'lookups';

function switchKnowledgeTab(tab, el) {
    markTabSelected(el);
    activeKnowledgeTab = tab;
    document.querySelectorAll('#knowledgeTabs .tab').forEach(t => t.classList.remove('active'));
    el.classList.add('active');
    ['lookups','macros','aliases'].forEach(t => {
        const tbl = document.getElementById('knowledgeTable-' + t);
        if (tbl) tbl.classList.toggle('hide', t !== tab);
    });
    loadKnowledgeCurrent();
}

function loadKnowledgeCurrent() {
    if (activeKnowledgeTab === 'lookups') loadLookups();
    else if (activeKnowledgeTab === 'macros') loadMacros();
    else loadAliases();
}

async function loadLookups() {
    const tbody = document.getElementById('lookupsBody');
    tbody.innerHTML = '<tr><td colspan="5" style="text-align:center;padding:24px"><span class="spinner"></span></td></tr>';
    try {
        const data = await api('GET', '/api/v1/knowledge/lookups');
        const items = Array.isArray(data) ? data : (data.data || []);
        if (items.length === 0) { tbody.innerHTML = '<tr><td colspan="5" style="text-align:center;padding:24px;color:var(--text-secondary)">No lookups</td></tr>'; return; }
        tbody.innerHTML = '';
        items.forEach(l => {
            const tr = document.createElement('tr');
            tr.innerHTML = `<td style="font-weight:500">${esc(l.name)}</td>
                <td style="color:var(--text-secondary)">${esc(l.description || '-')}</td>
                <td>${(l.columns || []).length}</td><td>${(l.rows || []).length}</td>
                <td><button class="btn btn-danger btn-sm del-btn">Del</button></td>`;
            tr.querySelector('.del-btn').onclick = () => deleteKnowledgeItem('lookups', l.id);
            tbody.appendChild(tr);
        });
    } catch (e) { tbody.innerHTML = `<tr><td colspan="5" style="color:var(--error);padding:16px">${esc(e.message)}</td></tr>`; }
}

async function loadMacros() {
    const tbody = document.getElementById('macrosBody');
    tbody.innerHTML = '<tr><td colspan="4" style="text-align:center;padding:24px"><span class="spinner"></span></td></tr>';
    try {
        const data = await api('GET', '/api/v1/knowledge/macros');
        const items = Array.isArray(data) ? data : (data.data || []);
        if (items.length === 0) { tbody.innerHTML = '<tr><td colspan="4" style="text-align:center;padding:24px;color:var(--text-secondary)">No macros</td></tr>'; return; }
        tbody.innerHTML = '';
        items.forEach(m => {
            const tr = document.createElement('tr');
            tr.innerHTML = `<td style="font-weight:500">${esc(m.name)}</td>
                <td style="font-family:monospace;font-size:11px;max-width:300px" title="${esc(m.definition)}">${esc(m.definition)}</td>
                <td style="color:var(--text-secondary);font-size:11px">${esc((m.parameters || []).join(', ')) || '-'}</td>
                <td><button class="btn btn-danger btn-sm del-btn">Del</button></td>`;
            tr.querySelector('.del-btn').onclick = () => deleteKnowledgeItem('macros', m.id);
            tbody.appendChild(tr);
        });
    } catch (e) { tbody.innerHTML = `<tr><td colspan="4" style="color:var(--error);padding:16px">${esc(e.message)}</td></tr>`; }
}

async function loadAliases() {
    const tbody = document.getElementById('aliasesBody');
    tbody.innerHTML = '<tr><td colspan="4" style="text-align:center;padding:24px"><span class="spinner"></span></td></tr>';
    try {
        const data = await api('GET', '/api/v1/knowledge/aliases');
        const items = Array.isArray(data) ? data : (data.data || []);
        if (items.length === 0) { tbody.innerHTML = '<tr><td colspan="4" style="text-align:center;padding:24px;color:var(--text-secondary)">No field aliases</td></tr>'; return; }
        tbody.innerHTML = '';
        items.forEach(a => {
            const tr = document.createElement('tr');
            tr.innerHTML = `<td>${esc(a.sourceType || '*')}</td>
                <td style="font-family:monospace">${esc(a.originalField)}</td>
                <td style="font-family:monospace;color:var(--accent)">${esc(a.aliasField)}</td>
                <td><button class="btn btn-danger btn-sm del-btn">Del</button></td>`;
            tr.querySelector('.del-btn').onclick = () => deleteKnowledgeItem('aliases', a.id);
            tbody.appendChild(tr);
        });
    } catch (e) { tbody.innerHTML = `<tr><td colspan="4" style="color:var(--error);padding:16px">${esc(e.message)}</td></tr>`; }
}

function showKnowledgeModal() {
    const type = activeKnowledgeTab;
    document.getElementById('knowledgeEditId').value = '';
    document.getElementById('knowledgeType').value = type;
    document.getElementById('km-lookup-fields').classList.toggle('hide', type !== 'lookups');
    document.getElementById('km-macro-fields').classList.toggle('hide', type !== 'macros');
    document.getElementById('km-alias-fields').classList.toggle('hide', type !== 'aliases');
    const titles = { lookups: 'New Lookup', macros: 'New Macro', aliases: 'New Field Alias' };
    document.getElementById('knowledgeModalTitle').textContent = titles[type] || 'New';
    // Clear
    document.querySelectorAll('#knowledgeModal input[type=text], #knowledgeModal textarea').forEach(i => i.value = '');
    openModal('knowledgeModal');
}

async function saveKnowledge() {
    const type = document.getElementById('knowledgeType').value;
    let body, url;
    if (type === 'lookups') {
        body = { name: document.getElementById('kmLookupName').value.trim(), description: document.getElementById('kmLookupDesc').value.trim() };
        url = '/api/v1/knowledge/lookups';
        if (!body.name) { toast('Name is required', 'error'); return; }
    } else if (type === 'macros') {
        body = { name: document.getElementById('kmMacroName').value.trim(), definition: document.getElementById('kmMacroDef').value.trim(), description: document.getElementById('kmMacroDesc').value.trim() };
        url = '/api/v1/knowledge/macros';
        if (!body.name || !body.definition) { toast('Name and definition required', 'error'); return; }
    } else {
        body = { sourceType: document.getElementById('kmAliasSourceType').value.trim(), originalField: document.getElementById('kmAliasOriginal').value.trim(), aliasField: document.getElementById('kmAliasAlias').value.trim() };
        url = '/api/v1/knowledge/aliases';
        if (!body.originalField || !body.aliasField) { toast('Fields required', 'error'); return; }
    }
    try {
        await api('POST', url, body);
        closeModal('knowledgeModal');
        toast('Created successfully', 'success');
        loadKnowledgeCurrent();
    } catch (e) { toast('Failed: ' + e.message, 'error'); }
}

async function deleteKnowledgeItem(type, id) {
    if (!confirm('Delete this item?')) return;
    try {
        await api('DELETE', '/api/v1/knowledge/' + type + '/' + id);
        toast('Deleted', 'success');
        loadKnowledgeCurrent();
    } catch (e) { toast('Failed: ' + e.message, 'error'); }
}

/* ===== Settings ===== */
function loadSettings() {
    document.getElementById('settingsApiKey').value = API_KEY;
    document.getElementById('settingsServerUrl').value = window.location.origin;
    document.getElementById('settingsPageSize').value = localStorage.getItem('cabinet_page_size') || '100';
    document.getElementById('settingsTimeRange').value = localStorage.getItem('cabinet_default_range') ?? '24h';
    document.getElementById('settingsTsFormat').value = localStorage.getItem('cabinet_ts_format') || 'local';
    document.getElementById('settingsEventLimit').value = localStorage.getItem('cabinet_event_limit') || '1000';
    loadRetentionPolicies();
    loadRoles();
    loadUpdates();
}

/* ===== Updates ===== */
function renderUpdates(s) {
    const el = document.getElementById('updateStatus');
    const row = (label, value) => `<div style="display:flex;justify-content:space-between;padding:3px 0;font-size:12px"><span style="color:var(--text-secondary)">${esc(label)}</span><span>${esc(value)}</span></div>`;
    let html = row('Running', s.current) + row('Newest release', s.latest || 'not checked yet');
    if (s.available) html += `<div style="margin:6px 0;font-size:12px;color:var(--accent)">Version ${esc(s.latest)} is available.</div>`;
    if (s.state && s.state !== 'Idle') html += row('State', `${s.state}${s.message ? ': ' + s.message : ''}`);
    if (s.checkError) html += `<div style="font-size:12px;color:var(--error)">Last check failed: ${esc(s.checkError)}</div>`;
    if (!s.canApply && s.cannotApplyReason) html += `<div style="font-size:12px;color:var(--text-secondary)">${esc(s.cannotApplyReason)}</div>`;
    if (s.agents && s.agents.length) {
        html += '<div style="margin-top:8px;font-size:12px;font-weight:500">Agents</div>';
        s.agents.forEach(a => {
            const behind = s.current && a.version !== s.current;
            html += row(`${a.host} (${a.platform})`, `${a.version}${behind ? ' (updating)' : ''}`);
        });
    }
    el.innerHTML = html;
    document.getElementById('applyUpdateButton').disabled = !(s.available && s.canApply && s.state !== 'Applying');
}

async function loadUpdates() {
    try { renderUpdates(await api('GET', '/api/v1/updates')); }
    catch (e) { document.getElementById('updateStatus').innerHTML = `<div style="font-size:12px;color:var(--text-secondary)">${esc(e.message)}</div>`; }
}

async function checkForUpdates() {
    try { renderUpdates(await api('POST', '/api/v1/updates/check')); toast('Checked for updates', 'success'); }
    catch (e) { toast(e.message, 'error'); }
}

async function applyUpdate() {
    if (!confirm('Install the newest release now? Cabinet restarts on it, and goes back to this version if it does not start.')) return;
    const button = document.getElementById('applyUpdateButton');
    button.disabled = true;
    try {
        renderUpdates(await api('POST', '/api/v1/updates/apply'));
        toast('Updating. Cabinet will be back in a minute.', 'success');
        setTimeout(waitForRestart, 15000);
    } catch (e) { toast(e.message, 'error'); button.disabled = false; }
}

async function waitForRestart(tries = 0) {
    try { renderUpdates(await api('GET', '/api/v1/updates')); toast('Cabinet is back', 'success'); }
    catch (e) { if (tries < 20) setTimeout(() => waitForRestart(tries + 1), 5000); }
}

function saveApiSettings() {
    const key = document.getElementById('settingsApiKey').value.trim();
    if (key) { storeApiKey(key, localStorage.getItem('cabinet_api_key') !== null); toast('API key saved', 'success'); }
}

function saveDisplaySettings() {
    localStorage.setItem('cabinet_page_size', document.getElementById('settingsPageSize').value);
    localStorage.setItem('cabinet_default_range', document.getElementById('settingsTimeRange').value);
    localStorage.setItem('cabinet_ts_format', document.getElementById('settingsTsFormat').value);
    localStorage.setItem('cabinet_event_limit', document.getElementById('settingsEventLimit').value);
    toast('Display settings saved', 'success');
}

function toggleApiKeyVisibility() {
    const inp = document.getElementById('settingsApiKey');
    inp.type = inp.type === 'password' ? 'text' : 'password';
}

async function loadRetentionPolicies() {
    const el = document.getElementById('retentionList');
    try {
        const data = await api('GET', '/api/v1/retention');
        const items = Array.isArray(data) ? data : (data.data || []);
        if (items.length === 0) { el.innerHTML = '<div style="font-size:12px;color:var(--text-secondary);padding:4px">No retention policies</div>'; return; }
        el.innerHTML = '';
        items.forEach(p => {
            const div = document.createElement('div');
            div.style.cssText = 'display:flex;justify-content:space-between;align-items:center;padding:6px 0;border-bottom:1px solid var(--border);font-size:12px';
            div.innerHTML = `<span><strong>${esc(p.index)}</strong> — ${p.maxAgeDays}d${p.archiveBeforeDelete ? ' (archive)' : ''}</span>
                <button class="btn btn-danger btn-sm del-btn">Del</button>`;
            div.querySelector('.del-btn').onclick = () => deleteRetention(p.index);
            el.appendChild(div);
        });
    } catch (e) { el.innerHTML = `<div style="font-size:12px;color:var(--error)">${esc(e.message)}</div>`; }
}

function showRetentionModal() { openModal('retentionModal'); document.getElementById('retIndex').value = ''; }

async function saveRetention() {
    const idx = document.getElementById('retIndex').value.trim();
    if (!idx) { toast('Index name required', 'error'); return; }
    const body = { index: idx, maxAgeDays: parseInt(document.getElementById('retMaxAge').value) || 90, archiveBeforeDelete: document.getElementById('retArchive').checked };
    try {
        await api('PUT', '/api/v1/retention/' + encodeURIComponent(idx), body);
        closeModal('retentionModal');
        toast('Retention policy saved', 'success');
        loadRetentionPolicies();
    } catch (e) { toast('Failed: ' + e.message, 'error'); }
}

async function deleteRetention(idx) {
    if (!confirm('Delete retention policy for ' + idx + '?')) return;
    try {
        await api('DELETE', '/api/v1/retention/' + encodeURIComponent(idx));
        toast('Deleted', 'success');
        loadRetentionPolicies();
    } catch (e) { toast('Failed: ' + e.message, 'error'); }
}

async function loadRoles() {
    const el = document.getElementById('rolesList');
    try {
        const data = await api('GET', '/api/v1/roles');
        const items = Array.isArray(data) ? data : (data.data || []);
        if (items.length === 0) { el.innerHTML = '<div style="font-size:12px;color:var(--text-secondary);padding:4px">No roles configured</div>'; return; }
        el.innerHTML = '';
        items.forEach(r => {
            const div = document.createElement('div');
            div.style.cssText = 'padding:6px 0;border-bottom:1px solid var(--border);font-size:12px';
            const role = r.role || r.systemRole || 'Unknown';
            const caps = (r.additionalCapabilities || r.capabilities || []).slice(0, 3).join(', ');
            div.innerHTML = `<div style="font-weight:500">${esc(r.apiKeyPrefix || r.apiKeyHash?.slice(0,8) || 'user')}</div>
                <div style="color:var(--text-secondary)">${esc(role)}${caps ? ' — ' + esc(caps) : ''}</div>`;
            el.appendChild(div);
        });
    } catch (e) { el.innerHTML = `<div style="font-size:12px;color:var(--text-secondary)">Roles require SuperAdmin access</div>`; }
}

/* ===== Event Detail Drawer ===== */
function showEventDetail(event) {
    const body = document.getElementById('drawerBody');
    let html = '';
    const entries = Object.entries(event).sort((a,b) => a[0].localeCompare(b[0]));
    entries.forEach(([k, v]) => {
        const val = v != null ? (typeof v === 'object' ? JSON.stringify(v, null, 2) : String(v)) : '';
        html += `<div class="field-row"><div class="field-key">${esc(k)}</div><div class="field-val">${esc(val)}</div></div>`;
    });
    if (event.RawLine || event.rawLine || event.raw) {
        html += '<div style="margin-top:16px;font-size:12px;color:var(--text-secondary)">Raw Event</div>';
        html += '<pre class="code-block" style="margin-top:6px;max-height:300px">' + esc(event.RawLine || event.rawLine || event.raw) + '</pre>';
    }
    body.innerHTML = html;
    document.getElementById('drawerBg').classList.add('show');
    document.getElementById('eventDrawer').classList.add('show');
}

function closeDrawer() {
    document.getElementById('drawerBg').classList.remove('show');
    document.getElementById('eventDrawer').classList.remove('show');
}

/* ===== Live Tail ===== */
let tailEventSource = null;
let tailCount = 0;

async function loadTailIndexes() {
    try {
        const data = await api('GET', '/api/v1/indexes');
        const indexes = Array.isArray(data) ? data : (data.data || data.indexes || []);
        const sel = document.getElementById('tailIndex');
        sel.innerHTML = '';
        if (indexes.length === 0) {
            sel.innerHTML = '<option value="main">main</option>';
        } else {
            indexes.forEach(idx => {
                const name = typeof idx === 'string' ? idx : (idx.name || idx.index || 'main');
                const opt = document.createElement('option');
                opt.value = name; opt.textContent = name;
                sel.appendChild(opt);
            });
        }
    } catch { /* keep default */ }
}

function toggleTail() {
    if (tailEventSource) {
        tailEventSource.close();
        tailEventSource = null;
        document.getElementById('tailDot').style.display = 'none';
        document.getElementById('tailStartBtn').textContent = 'Start';
        document.getElementById('tailStartBtn').classList.remove('btn-danger');
        document.getElementById('tailStartBtn').classList.add('btn-primary');
        return;
    }
    const index = document.getElementById('tailIndex').value || 'main';
    const filter = document.getElementById('tailFilter').value.trim();
    let url = `/api/v1/logs/stream/live?index=${encodeURIComponent(index)}&pollIntervalMs=1000`;
    if (filter) url += `&filter=${encodeURIComponent(filter)}`;

    const output = document.getElementById('tailOutput');
    document.getElementById('tailDot').style.display = 'inline-block';
    document.getElementById('tailStartBtn').textContent = 'Stop';
    document.getElementById('tailStartBtn').classList.remove('btn-primary');
    document.getElementById('tailStartBtn').classList.add('btn-danger');

    // SSE connection with API key via query param not supported; use fetch streaming instead
    const controller = new AbortController();
    tailEventSource = { close: () => controller.abort() };

    (async () => {
        try {
            const res = await fetch(url, {
                headers: { 'X-API-Key': API_KEY, 'Accept': 'text/event-stream' },
                signal: controller.signal
            });
            const reader = res.body.getReader();
            const decoder = new TextDecoder();
            let buffer = '';
            while (true) {
                const { value, done } = await reader.read();
                if (done) break;
                buffer += decoder.decode(value, { stream: true });
                const lines = buffer.split('\n');
                buffer = lines.pop(); // keep incomplete line
                for (const line of lines) {
                    if (line.startsWith('data: ')) {
                        try {
                            const evt = JSON.parse(line.slice(6));
                            tailCount++;
                            const div = document.createElement('div');
                            div.style.cssText = 'padding:2px 0;border-bottom:1px solid rgba(48,54,61,0.3)';
                            const ts = evt.timestamp ? fmtTime(evt.timestamp) : '';
                            const sev = evt.severity != null ? `<span class="${sevClass(evt.severity)}" style="display:inline-block;width:60px">${['TRACE','DEBUG','INFO','WARN','ERROR','CRIT','FATAL'][Math.min(parseInt(evt.severity)||0, 6)]}</span>` : '';
                            const raw = evt.rawLine || evt.RawLine || JSON.stringify(evt);
                            div.innerHTML = `<span style="color:var(--text-muted);margin-right:8px">${esc(ts)}</span>${sev}<span>${esc(raw)}</span>`;
                            output.appendChild(div);
                            // Auto-scroll to bottom
                            output.scrollTop = output.scrollHeight;
                            // Limit DOM nodes to prevent memory issues
                            while (output.children.length > 2000) output.removeChild(output.firstChild);
                            document.getElementById('tailCount').textContent = tailCount + ' events';
                        } catch {}
                    }
                }
            }
        } catch (e) {
            if (e.name !== 'AbortError') {
                const div = document.createElement('div');
                div.style.color = 'var(--error)';
                div.textContent = 'Stream ended: ' + e.message;
                output.appendChild(div);
            }
        }
        // Clean up
        document.getElementById('tailDot').style.display = 'none';
        document.getElementById('tailStartBtn').textContent = 'Start';
        document.getElementById('tailStartBtn').classList.remove('btn-danger');
        document.getElementById('tailStartBtn').classList.add('btn-primary');
        tailEventSource = null;
    })();
}

function clearTail() {
    document.getElementById('tailOutput').innerHTML = '';
    tailCount = 0;
    document.getElementById('tailCount').textContent = '0 events';
}

/* ===== Search Jobs ===== */
let jobPollTimer = null;

async function loadJobs() {
    const tbody = document.getElementById('jobsBody');
    tbody.innerHTML = '<tr><td colspan="6" style="text-align:center;padding:24px"><span class="spinner"></span></td></tr>';
    try {
        const data = await api('GET', '/api/v1/query/jobs');
        const jobs = Array.isArray(data) ? data : (data.data || data.jobs || []);
        if (jobs.length === 0) {
            tbody.innerHTML = '<tr><td colspan="6" style="text-align:center;padding:32px;color:var(--text-secondary)">No search jobs. Click "+ New Job" to create one.</td></tr>';
            return;
        }
        tbody.innerHTML = '';
        jobs.forEach(j => {
            const tr = document.createElement('tr');
            const status = (j.status || 'unknown').toLowerCase();
            const statusBadge = status === 'completed' ? '<span class="badge badge-success">Completed</span>'
                : status === 'running' ? '<span class="badge badge-info">Running</span>'
                : status === 'failed' ? '<span class="badge badge-error">Failed</span>'
                : status === 'cancelled' ? '<span class="badge badge-muted">Cancelled</span>'
                : `<span class="badge badge-muted">${esc(j.status || 'Created')}</span>`;
            const resultCount = j.resultCount != null ? fmt(j.resultCount) : '-';
            tr.innerHTML = `<td style="font-family:monospace;font-size:11px" title="${esc(j.id)}">${esc((j.id || '').slice(0, 12))}...</td>
                <td style="max-width:250px;font-family:'SF Mono','Fira Code',monospace;font-size:11px" title="${esc(j.query)}">${esc(j.query)}</td>
                <td>${statusBadge}</td>
                <td>${resultCount}</td>
                <td style="color:var(--text-secondary)">${fmtTime(j.createdAt)}</td>
                <td style="white-space:nowrap"></td>`;
            const actionsCell = tr.querySelector('td:last-child');
            if (status === 'completed') {
                const viewBtn = document.createElement('button');
                viewBtn.className = 'btn btn-secondary btn-sm';
                viewBtn.textContent = 'Results';
                viewBtn.onclick = () => viewJobResults(j.id);
                actionsCell.appendChild(viewBtn);
            }
            if (status === 'running' || status === 'created') {
                const cancelBtn = document.createElement('button');
                cancelBtn.className = 'btn btn-danger btn-sm';
                cancelBtn.textContent = 'Cancel';
                cancelBtn.style.marginLeft = '4px';
                cancelBtn.onclick = () => cancelJob(j.id);
                actionsCell.appendChild(cancelBtn);
            }
            tbody.appendChild(tr);
        });
        // Auto-refresh if any jobs are running
        if (jobs.some(j => (j.status || '').toLowerCase() === 'running' || (j.status || '').toLowerCase() === 'created')) {
            if (!jobPollTimer) jobPollTimer = setInterval(() => { if (currentPage === 'jobs') loadJobs(); }, 3000);
        } else {
            if (jobPollTimer) { clearInterval(jobPollTimer); jobPollTimer = null; }
        }
    } catch (e) {
        tbody.innerHTML = `<tr><td colspan="6" style="color:var(--error);padding:16px">${esc(e.message)}</td></tr>`;
    }
}

function showJobModal() {
    document.getElementById('jobQuery').value = '';
    document.getElementById('jobTimeRange').value = '-24h';
    document.getElementById('jobMaxResults').value = '10000';
    openModal('jobModal');
    document.getElementById('jobQuery').focus();
}

async function createJob() {
    const query = document.getElementById('jobQuery').value.trim();
    if (!query) { toast('Query is required', 'error'); return; }
    const body = {
        query,
        timeRange: document.getElementById('jobTimeRange').value.trim() || '-24h',
        maxResults: parseInt(document.getElementById('jobMaxResults').value) || 10000
    };
    try {
        await api('POST', '/api/v1/query/jobs', body);
        closeModal('jobModal');
        toast('Search job created', 'success');
        loadJobs();
    } catch (e) { toast('Failed: ' + e.message, 'error'); }
}

async function cancelJob(id) {
    try {
        await api('POST', '/api/v1/query/jobs/' + id + '/cancel');
        toast('Job cancelled', 'success');
        loadJobs();
    } catch (e) { toast('Failed: ' + e.message, 'error'); }
}

async function viewJobResults(id) {
    try {
        const data = await api('GET', '/api/v1/query/jobs/' + id + '/results');
        const results = data.results || data;
        if (Array.isArray(results) && results.length > 0) {
            navigate('search');
            showSearchResults(results, `Search job ${id}`);
            toast(`Loaded ${results.length} results from search job`, 'info');
        } else {
            toast('No results in this job', 'info');
        }
    } catch (e) { toast('Failed: ' + e.message, 'error'); }
}

/* ===== Theme Toggle ===== */
function toggleTheme() {
    const body = document.body;
    body.classList.toggle('light');
    const isLight = body.classList.contains('light');
    localStorage.setItem('cabinet_theme', isLight ? 'light' : 'dark');
    document.getElementById('themeBtn').textContent = isLight ? 'Dark' : 'Light';
}

// Restore theme on load
(function() {
    const saved = localStorage.getItem('cabinet_theme');
    if (saved === 'light') {
        document.body.classList.add('light');
        document.getElementById('themeBtn').textContent = 'Dark';
    }
})();

/* ===== API Key ===== */
function changeApiKey() {
    navigate('settings');
}

/* ===== Health Polling ===== */
async function checkHealth() {
    try {
        const res = await fetch('/health/live');
        const dot = document.getElementById('healthDot');
        const text = document.getElementById('healthText');
        if (res.ok) { dot.className = 'dot ok'; text.textContent = 'Connected'; }
        else { dot.className = 'dot warn'; text.textContent = 'Degraded'; }
    } catch {
        document.getElementById('healthDot').className = 'dot err';
        document.getElementById('healthText').textContent = 'Disconnected';
    }
}

let storedEventCount = null;

async function loadStats() {
    try {
        const stats = await api('GET', '/api/v1/logs/stats');
        storedEventCount = stats.totalEvents || 0;
        renderEmptyState();
    } catch (e) {
        // The count is a nicety on the empty search page; searching works without it
        storedEventCount = null;
    }
}

/* ===== Utils ===== */
function fmt(n) { return Number(n).toLocaleString(); }
function esc(s) {
    const entities = { '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' };
    return String(s ?? '').replace(/[&<>"']/g, c => entities[c]);
}
function sevClass(val) {
    const n = typeof val === 'number' ? val : parseInt(val) || 0;
    return ['sev-trace','sev-debug','sev-info','sev-warning','sev-error','sev-critical','sev-fatal'][Math.min(n, 6)];
}
function toast(msg, type='info') {
    const c = document.getElementById('toastContainer');
    const t = document.createElement('div');
    t.className = 'toast toast-' + type;
    t.textContent = msg;
    t.onclick = () => t.remove();
    c.appendChild(t);
    setTimeout(() => { t.style.animation = 'fadeOut 0.3s forwards'; setTimeout(() => t.remove(), 300); }, 4000);
}
function closeModal(id) {
    document.getElementById(id).classList.remove('show');
    if (lastFocusBeforeModal && document.contains(lastFocusBeforeModal)) lastFocusBeforeModal.focus();
    lastFocusBeforeModal = null;
}
function openModal(id) {
    const modal = document.getElementById(id);
    if (modal.classList.contains('show')) return;
    lastFocusBeforeModal = document.activeElement;
    modal.classList.add('show');
    const first = modal.querySelector('input:not([type=hidden]),select,textarea,button');
    if (first) first.focus();
}
function fmtTime(ts) {
    if (!ts) return '-';
    // The API's times are UTC; one without a zone is read as UTC, not as this browser's local time
    const d = new Date(typeof ts === 'string' && /^\d{4}-\d\d-\d\dT[\d:.]+$/.test(ts) ? ts + 'Z' : ts);
    const fmt = localStorage.getItem('cabinet_ts_format') || 'local';
    if (fmt === 'relative') {
        const diff = Date.now() - d.getTime();
        if (diff < 60000) return Math.round(diff/1000) + 's ago';
        if (diff < 3600000) return Math.round(diff/60000) + 'm ago';
        if (diff < 86400000) return Math.round(diff/3600000) + 'h ago';
        return Math.round(diff/86400000) + 'd ago';
    }
    if (fmt === 'utc') return d.toISOString();
    if (fmt === 'iso') return d.toISOString().replace('T',' ').slice(0,19);
    return d.toLocaleString();
}
function sevBadge(sev) {
    const s = String(sev || '').toLowerCase();
    const n = parseInt(sev);
    if (s === 'critical' || s === 'fatal' || n >= 5) return '<span class="badge badge-error">Critical</span>';
    if (s === 'error' || n === 4) return '<span class="badge badge-error">Error</span>';
    if (s === 'warning' || n === 3) return '<span class="badge badge-warning">Warning</span>';
    if (s === 'info' || n === 2) return '<span class="badge badge-info">Info</span>';
    return '<span class="badge badge-muted">' + esc(sev || 'Unknown') + '</span>';
}

/* ===== Init ===== */
checkHealth(); loadStats(); initSearchPage();
// P3: Reduced polling frequency for production (60s health, 120s stats)
setInterval(checkHealth, 60000);
setInterval(loadStats, 120000);

// P10: URL-based navigation — restore page from hash on load and back/forward
(function() {
    const hash = window.location.hash.replace('#', '');
    if (hash.startsWith('search')) {
        // A link to a search: the search page runs it
    } else if (hash && document.getElementById('page-' + hash)) {
        navigate(hash);
    } else {
        document.getElementById('queryInput').focus();
    }
})();
window.addEventListener('popstate', e => {
    const page = (e.state && e.state.page) || window.location.hash.replace('#', '').split('?')[0] || 'search';
    if (VALID_PAGES.includes(page)) navigate(page);
});

// P11: Keyboard shortcuts
document.addEventListener('keydown', e => {
    // Escape: close modals and drawers
    if (e.key === 'Escape') {
        closeSaveModal(); closeDrawer();
        document.querySelectorAll('.modal-bg.show').forEach(m => m.classList.remove('show'));
        hideSuggestions(); closeTimePicker(); closeSearchMenu();
        return;
    }
    // Ctrl+Enter or Cmd+Enter: execute search from anywhere
    if ((e.ctrlKey || e.metaKey) && e.key === 'Enter') {
        e.preventDefault();
        if (currentPage === 'search') executeQuery();
        return;
    }
    // Ctrl+S or Cmd+S: save current query
    if ((e.ctrlKey || e.metaKey) && e.key === 's') {
        e.preventDefault();
        if (currentPage === 'search') saveCurrentQuery();
        return;
    }
    // Ctrl+K or Cmd+K: focus search bar (command palette style)
    if ((e.ctrlKey || e.metaKey) && e.key === 'k') {
        e.preventDefault();
        navigate('search');
        document.getElementById('queryInput').focus();
        document.getElementById('queryInput').select();
        return;
    }
    // / key: focus search when not in an input
    if (e.key === '/' && !e.target.closest('input, textarea, select')) {
        e.preventDefault();
        navigate('search');
        document.getElementById('queryInput').focus();
        return;
    }
});

// Close modal on bg click
document.querySelectorAll('.modal-bg').forEach(bg => {
    bg.addEventListener('click', e => { if (e.target === bg) bg.classList.remove('show'); });
});

initAccessibility();
if (!API_KEY) showLogin();

