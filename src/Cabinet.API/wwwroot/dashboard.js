/*
 * Dashboards: the list, and one dashboard as a grid of panels (2fr 1fr 1fr). Every panel says
 * what it measures: its own description, or one worked out from its query. Charts come from
 * charts.js, which stays pure; this file only fetches, lays out and wires handlers. Anything
 * from a log or a person goes through esc().
 */

const dash = {
    list: [],
    current: null,      // the dashboard on screen, as the API returns it
    editing: false,
    refresh: 0,         // seconds, 0 = off
    range: '24h',
    updatedAt: null,
    refreshTimer: null,
    tickTimer: null,
    runId: 0,           // a newer run makes an older one's results stale
};

const DASH_REFRESH = [[0, 'Off'], [30, 'Every 30 seconds'], [60, 'Every minute'], [300, 'Every 5 minutes']];
const DASH_RANGES = [
    ['15m', 'Last 15 minutes', 15 * 60, 'the 15 minutes before'],
    ['1h', 'Last hour', 3600, 'the hour before'],
    ['4h', 'Last 4 hours', 4 * 3600, 'the 4 hours before'],
    ['24h', 'Last 24 hours', 86400, 'the day before'],
    ['7d', 'Last 7 days', 7 * 86400, 'the week before'],
    ['30d', 'Last 30 days', 30 * 86400, 'the 30 days before'],
];

const PANEL_VIZ = { Table: 'table', LineChart: 'line', AreaChart: 'area', BarChart: 'bar', StackedBar: 'stacked', SingleValue: 'stat', PieChart: 'pie', Gauge: 'gauge', Heatmap: 'heatmap', Map: 'map' };
const PANEL_KINDS = [
    ['AreaChart', 'Area line'], ['LineChart', 'Lines'], ['SingleValue', 'Big number'], ['BarChart', 'Bars'],
    ['StackedBar', 'Stacked bar (shares)'], ['PieChart', 'Pie'], ['Gauge', 'Gauge'], ['Heatmap', 'Heatmap'], ['Map', 'Map'], ['Table', 'Table'],
];
const PANEL_WIDTHS = [[6, 'Half'], [3, 'Quarter'], [12, 'Full width']];

/** Grid columns a panel takes in the four-track grid (2fr 1fr 1fr = 2 + 1 + 1). */
function panelSpan(width) {
    const w = Number(width) || 6;
    return w >= 12 ? 4 : w >= 6 ? 2 : 1;
}

function dashRange(key) {
    return DASH_RANGES.find(r => r[0] === key) || DASH_RANGES[3];
}

/** "-24h" or "24h" from the API, as one of the pop-up's ranges. */
function rangeKeyFrom(value) {
    const key = String(value || '').replace(/^-/, '');
    return DASH_RANGES.some(r => r[0] === key) ? key : '24h';
}

function sortedPanels(d) {
    return (d.panels || []).slice().sort((a, b) => (a.row - b.row) || (a.column - b.column));
}

/** What the panel says it measures: its description, or its query in words plus the time range. */
function panelDescription(panel) {
    if (panel.description) return panel.description;
    const words = CabinetCharts.describeQuery(panel.query);
    return words ? `${words}, ${dashRange(dash.range)[1].toLowerCase()}` : 'No search yet: edit the panel to add one';
}

/* ===== The list ===== */

async function loadDashboards() {
    closeDashboardView();
    const el = document.getElementById('dashboardsList');
    el.innerHTML = '<div class="empty-state">' + loadingHtml('Loading dashboards…') + '</div>';
    try {
        const data = await api('GET', '/api/v1/dashboards');
        dash.list = Array.isArray(data) ? data : (data.data || []);
        if (dash.list.length === 0) {
            el.innerHTML = '<div class="empty-state"><h3>No dashboards yet</h3><p>Choose New dashboard… to make one, or Add to dashboard… under a search result.</p></div>';
            return;
        }
        el.innerHTML = dash.list.map(d => {
            const count = d.panelCount ?? (d.panels ? d.panels.length : 0);
            const args = esc(JSON.stringify([d.id]));
            return `<article class="dash-card">
                <button type="button" class="dash-card-open" data-click="openDashboard" data-args="${args}">
                    <span class="dash-card-title">${esc(d.title || d.name || 'Untitled')}</span>
                    <span class="dash-card-desc">${esc(d.description || 'No description')}</span>
                    <span class="dash-card-meta">${count} ${count === 1 ? 'panel' : 'panels'} · changed ${esc(fmtTime(d.updatedAt || d.createdAt))}</span>
                </button>
                <div class="dash-card-actions">
                    <button type="button" class="btn btn-sm" data-click="openDashboard" data-args="${args}">Open</button>
                    <button type="button" class="btn btn-sm btn-danger" data-click="deleteDashboard" data-args="${args}">Delete…</button>
                </div>
            </article>`;
        }).join('');
    } catch (e) {
        el.innerHTML = errorHtml(`Dashboards couldn't be loaded: ${e.message}.`, 'Try again', 'loadDashboards');
    }
}

function setDashboardToolbar(viewing) {
    document.getElementById('dashCrumb').hidden = !viewing;
    document.getElementById('dashListActions').hidden = viewing;
    document.getElementById('dashViewActions').hidden = !viewing;
    document.getElementById('dashboardsList').hidden = viewing;
    document.getElementById('dashboardView').hidden = !viewing;
    if (!viewing) {
        document.getElementById('dashViewTitle').textContent = 'Dashboards';
        document.getElementById('dashPurpose').textContent = 'Panels of searches that keep themselves up to date';
    }
}

function closeDashboardView() {
    stopDashboardRefresh();
    dash.current = null;
    dash.editing = false;
    setDashboardToolbar(false);
}

/** Back to the list. */
function closeDashboard() {
    loadDashboards();
}

function stopDashboardRefresh() {
    clearInterval(dash.refreshTimer);
    clearInterval(dash.tickTimer);
    dash.refreshTimer = null;
    dash.tickTimer = null;
}

/* ===== One dashboard ===== */

async function openDashboard(id) {
    setDashboardToolbar(true);
    const view = document.getElementById('dashboardView');
    view.innerHTML = loadingHtml('Opening the dashboard…');
    try {
        const d = await api('GET', '/api/v1/dashboards/' + encodeURIComponent(id));
        dash.current = d;
        dash.refresh = d.refreshIntervalSeconds || 0;
        dash.range = rangeKeyFrom(d.defaultTimeRange);
        dash.editing = false;
        renderDashboard();
        runDashboard();
        scheduleDashboardRefresh();
    } catch (e) {
        view.innerHTML = errorHtml(`This dashboard couldn't be opened: ${e.message}.`, 'Back to dashboards', 'closeDashboard');
    }
}

function renderDashboardToolbar() {
    const d = dash.current;
    document.getElementById('dashViewTitle').textContent = d.title || 'Untitled';
    document.getElementById('dashRefreshValue').textContent = (DASH_REFRESH.find(r => r[0] === dash.refresh) || [0, `Every ${dash.refresh} seconds`])[1];
    document.getElementById('dashTimeValue').textContent = dashRange(dash.range)[1];
    const edit = document.getElementById('dashEditButton');
    edit.textContent = dash.editing ? 'Done' : 'Edit layout';
    edit.classList.toggle('btn-primary', dash.editing);
    edit.setAttribute('aria-pressed', String(dash.editing));
    updateDashboardAge();
}

function updateDashboardAge() {
    const purpose = document.getElementById('dashPurpose');
    if (!dash.current) return;
    if (!dash.updatedAt) { purpose.textContent = 'Dashboard · updating…'; return; }
    const s = Math.max(0, Math.round((Date.now() - dash.updatedAt) / 1000));
    purpose.textContent = `Dashboard · updated ${s < 60 ? `${s} s` : s < 3600 ? `${Math.floor(s / 60)} min` : `${Math.floor(s / 3600)} h`} ago`;
}

function renderDashboard() {
    const d = dash.current;
    const view = document.getElementById('dashboardView');
    renderDashboardToolbar();
    const panels = sortedPanels(d);
    const editBar = dash.editing
        ? `<div class="dash-editbar" role="status"><span>Editing the layout: change each panel's width, order, title and description. Changes are saved as you make them.</span>
            <button type="button" class="btn btn-sm" data-click="showDashboardModal" data-args="${esc(JSON.stringify([d.id]))}">Dashboard details…</button></div>`
        : '';
    if (panels.length === 0) {
        view.innerHTML = `${editBar}<div class="empty-state"><h3>No panels yet</h3><p>Choose Add panel… to chart a search here.</p></div>`;
        return;
    }
    view.innerHTML = `${editBar}<div class="dash-grid${dash.editing ? ' dash-grid-editing' : ''}">${panels.map((panel, i) => panelHtml(panel, i, panels.length)).join('')}</div>`;
}

function panelHtml(panel, index, count) {
    const args = esc(JSON.stringify([panel.id]));
    const editing = dash.editing
        ? `<div class="dash-panel-edit">
            <div class="segmented" role="group" aria-label="Width of ${esc(panel.title || 'panel')}">${PANEL_WIDTHS.map(([w, label]) =>
                `<button type="button" aria-pressed="${panelSpan(panel.width) === panelSpan(w)}" data-click="setPanelWidth" data-args="${esc(JSON.stringify([panel.id, w]))}">${label}</button>`).join('')}</div>
            <button type="button" class="btn btn-sm" data-click="movePanel" data-args="${esc(JSON.stringify([panel.id, -1]))}"${index === 0 ? ' disabled' : ''}>Move earlier</button>
            <button type="button" class="btn btn-sm" data-click="movePanel" data-args="${esc(JSON.stringify([panel.id, 1]))}"${index === count - 1 ? ' disabled' : ''}>Move later</button>
            <button type="button" class="btn btn-sm" data-click="showPanelModal" data-args="${args}">Edit…</button>
            <button type="button" class="btn btn-sm btn-danger" data-click="removePanel" data-args="${args}">Remove</button>
        </div>`
        : '';
    return `<section class="dash-panel dash-kind-${esc(PANEL_VIZ[panel.type] || 'table')}" style="grid-column:span ${panelSpan(panel.width)}" data-panel="${esc(panel.id)}" aria-labelledby="panel-title-${esc(panel.id)}">
        <header class="dash-panel-head">
            <h2 class="dash-panel-title" id="panel-title-${esc(panel.id)}">${esc(panel.title || 'Untitled panel')}</h2>
            <p class="dash-panel-desc">${esc(panelDescription(panel))}</p>
        </header>
        <div class="dash-panel-body">${loadingHtml('Running this panel’s search…')}</div>
        ${editing}
    </section>`;
}

function dashboardWindow() {
    const end = new Date();
    const seconds = dashRange(dash.range)[2];
    return { start: new Date(end.getTime() - seconds * 1000), end, seconds };
}

async function runPanelQuery(query, start, end) {
    const result = await api('POST', '/api/v1/query/execute', { query, useCache: true, startTime: start.toISOString(), endTime: end.toISOString() });
    return result.results || [];
}

/** Runs every panel's search and draws it. */
async function runDashboard() {
    const d = dash.current;
    if (!d) return;
    const run = ++dash.runId;
    const { start, end, seconds } = dashboardWindow();
    await Promise.all(sortedPanels(d).map(async panel => {
        const body = document.querySelector(`#dashboardView [data-panel="${CSS.escape(panel.id)}"] .dash-panel-body`);
        if (!body) return;
        if (!panel.query) { body.innerHTML = '<p class="chart-note">No search yet. Choose Edit layout, then Edit… on this panel.</p>'; return; }
        try {
            const rows = await runPanelQuery(panel.query, start, end);
            if (run !== dash.runId) return;
            if (panel.type === 'SingleValue') {
                const previous = await runPanelQuery(panel.query, new Date(start.getTime() - seconds * 1000), start).catch(() => null);
                if (run !== dash.runId) return;
                renderBigNumber(body, panel, rows, previous);
            } else {
                CabinetCharts.render(body, rows, { viz: PANEL_VIZ[panel.type], title: panel.title, height: 190, tableWhenRaw: true, tableRows: 8, barNote: false });
            }
        } catch (e) {
            if (run !== dash.runId) return;
            body.innerHTML = errorHtml(`This panel's search didn't run: ${e.message}.`, 'Edit the panel', 'showPanelModal', [panel.id]);
        }
    }));
    if (run !== dash.runId) return;
    dash.updatedAt = Date.now();
    updateDashboardAge();
}

function statValue(rows) {
    const data = CabinetCharts.shape(rows, 'stat');
    return data.kind === 'stat' ? Number(data.value) : null;
}

/** 48px number, its change on the previous period, and a way to the events behind it. */
function renderBigNumber(body, panel, rows, previousRows) {
    const value = statValue(rows);
    if (value === null || Number.isNaN(value)) {
        CabinetCharts.render(body, rows, { viz: 'stat', tableWhenRaw: true, tableRows: 8 });
        return;
    }
    const lower = CabinetCharts.lowerIsBetter(`${panel.title} ${panel.query}`);
    const previous = previousRows ? statValue(previousRows) : null;
    const change = previous === null ? null : CabinetCharts.compare(value, previous, lower ? true : null, dashRange(dash.range)[3]);
    const events = CabinetCharts.eventsQuery(panel.query);
    body.innerHTML = `<div class="dash-big">
        <span class="dash-big-value${lower && value > 0 ? ' dash-big-bad' : ''}">${esc(CabinetCharts.formatNumber(value))}</span>
        ${change ? `<span class="dash-big-change dash-tone-${esc(change.tone)}">${esc(change.text)}</span>` : ''}
        <span class="dash-big-more">${events ? `<button type="button" class="btn-link" data-click="showPanelEvents" data-args="${esc(JSON.stringify([events]))}">Show events</button>` : ''}</span>
    </div>`;
}

function showPanelEvents(query) {
    if (typeof runSavedQuery === 'function') runSavedQuery(query);
}

function scheduleDashboardRefresh() {
    stopDashboardRefresh();
    if (dash.refresh > 0) dash.refreshTimer = setInterval(runDashboard, dash.refresh * 1000);
    dash.tickTimer = setInterval(updateDashboardAge, 1000);
}

/** Saves part of the dashboard; people who can't change it keep their choice for this visit. */
async function saveDashboardPart(body, quiet) {
    try {
        dash.current = await api('PUT', '/api/v1/dashboards/' + encodeURIComponent(dash.current.id), body);
        return true;
    } catch (e) {
        if (!quiet) toast(`That change wasn't saved: ${e.message}`, 'error');
        return false;
    }
}

/* ===== Toolbar pop-ups ===== */

function openDashboardRefreshMenu(button) {
    showPopupMenu(button, DASH_REFRESH.map(([seconds, label]) => ({ label, checked: seconds === dash.refresh, value: seconds })), seconds => {
        dash.refresh = seconds;
        renderDashboardToolbar();
        scheduleDashboardRefresh();
        saveDashboardPart({ refreshIntervalSeconds: seconds }, true);
    });
}

function openDashboardTimeMenu(button) {
    showPopupMenu(button, DASH_RANGES.map(([key, label]) => ({ label, checked: key === dash.range, value: key })), key => {
        dash.range = key;
        renderDashboard();
        runDashboard();
        saveDashboardPart({ defaultTimeRange: '-' + key }, true);
    });
}

function refreshDashboardNow() {
    runDashboard();
}

/* ===== Edit layout ===== */

function toggleDashboardEdit() {
    dash.editing = !dash.editing;
    renderDashboard();
    runDashboard();
}

function panelsForSave(panels) {
    return panels.map((p, i) => ({
        id: p.id, title: p.title, query: p.query, description: p.description || null, type: p.type,
        width: p.width || 6, height: p.height || 4, row: i, column: 0,
    }));
}

async function savePanels(panels) {
    if (await saveDashboardPart({ panels: panelsForSave(panels) })) {
        renderDashboard();
        runDashboard();
    }
}

function setPanelWidth(id, width) {
    savePanels(sortedPanels(dash.current).map(p => (p.id === id ? { ...p, width } : p)));
}

function movePanel(id, step) {
    const panels = sortedPanels(dash.current);
    const i = panels.findIndex(p => p.id === id);
    const j = i + step;
    if (i < 0 || j < 0 || j >= panels.length) return;
    [panels[i], panels[j]] = [panels[j], panels[i]];
    savePanels(panels);
}

function removePanel(id) {
    const panel = sortedPanels(dash.current).find(p => p.id === id);
    if (!panel || !confirm(`Remove the panel "${panel.title}" from this dashboard?`)) return;
    savePanels(sortedPanels(dash.current).filter(p => p.id !== id));
}

/* ===== Add or edit one panel ===== */

function showPanelModal(id) {
    const panel = (typeof id === 'string' && dash.current && sortedPanels(dash.current).find(p => p.id === id)) || null;
    document.getElementById('panelEditId').value = panel ? panel.id : '';
    document.getElementById('panelModalTitle').textContent = panel ? 'Edit panel' : 'Add panel';
    document.getElementById('panelTitle').value = panel ? panel.title : '';
    document.getElementById('panelQuery').value = panel ? panel.query : '';
    document.getElementById('panelDescription').value = panel ? (panel.description || '') : '';
    document.getElementById('panelType').innerHTML = PANEL_KINDS.map(([value, label]) => `<option value="${value}"${(panel ? panel.type : 'AreaChart') === value ? ' selected' : ''}>${label}</option>`).join('');
    document.getElementById('panelWidth').innerHTML = PANEL_WIDTHS.map(([value, label]) => `<option value="${value}"${panelSpan(panel ? panel.width : 6) === panelSpan(value) ? ' selected' : ''}>${label}</option>`).join('');
    document.getElementById('panelPreview').innerHTML = '';
    describePanelQuery();
    openModal('panelModal');
}

/** Shows what the query measures as the description's placeholder, while there's no description. */
function describePanelQuery() {
    const words = CabinetCharts.describeQuery(document.getElementById('panelQuery').value);
    document.getElementById('panelDescription').placeholder = words ? `${words}, ${dashRange(dash.range)[1].toLowerCase()}` : 'Say exactly what this panel measures';
}

function panelFromModal() {
    return {
        id: document.getElementById('panelEditId').value || undefined,
        title: document.getElementById('panelTitle').value.trim(),
        query: document.getElementById('panelQuery').value.trim(),
        description: document.getElementById('panelDescription').value.trim() || null,
        type: document.getElementById('panelType').value,
        width: Number(document.getElementById('panelWidth').value),
        height: 4,
    };
}

async function previewPanel() {
    const panel = panelFromModal();
    const box = document.getElementById('panelPreview');
    if (!panel.query) { box.innerHTML = errorHtml('Write a search first, such as find * | timechart span=1h count.'); return; }
    box.innerHTML = loadingHtml('Running the search…');
    const { start, end, seconds } = dashboardWindow();
    try {
        const rows = await runPanelQuery(panel.query, start, end);
        if (panel.type === 'SingleValue') {
            const previous = await runPanelQuery(panel.query, new Date(start.getTime() - seconds * 1000), start).catch(() => null);
            renderBigNumber(box, panel, rows, previous);
        } else {
            CabinetCharts.render(box, rows, { viz: PANEL_VIZ[panel.type], title: panel.title, height: 180, tableWhenRaw: true, tableRows: 8, barNote: false });
        }
    } catch (e) {
        box.innerHTML = errorHtml(`The search didn't run: ${e.message}.`);
    }
}

async function savePanelModal() {
    const panel = panelFromModal();
    if (!panel.query) { toast('A panel needs a search', 'error'); return; }
    panel.title = panel.title || CabinetCharts.describeQuery(panel.query) || 'Untitled panel';
    const panels = sortedPanels(dash.current);
    const i = panels.findIndex(p => p.id === panel.id);
    if (i >= 0) panels[i] = { ...panels[i], ...panel }; else panels.push(panel);
    if (await saveDashboardPart({ panels: panelsForSave(panels) })) {
        closeModal('panelModal');
        toast(i >= 0 ? 'Panel saved' : 'Panel added', 'success');
        renderDashboard();
        runDashboard();
    }
}

/* ===== New dashboard, and a dashboard's details ===== */

async function showDashboardModal(id) {
    let d = null;
    if (typeof id === 'string') {
        try { d = await api('GET', '/api/v1/dashboards/' + encodeURIComponent(id)); } catch (e) { toast(`Couldn't open its details: ${e.message}`, 'error'); return; }
    }
    document.getElementById('dashboardEditId').value = d ? d.id : '';
    document.getElementById('dashboardModalTitle').textContent = d ? 'Dashboard details' : 'New dashboard';
    document.getElementById('dashTitle').value = d ? (d.title || '') : '';
    document.getElementById('dashDescription').value = d ? (d.description || '') : '';
    document.getElementById('dashRefresh').value = String(d ? (d.refreshIntervalSeconds || 0) : 0);
    document.getElementById('panelEditorList').innerHTML = '';
    if (d) sortedPanels(d).forEach(addPanelRow); else addPanelRow();
    document.getElementById('dashboardDeleteButton').hidden = !d;
    openModal('dashboardModal');
    document.getElementById('dashTitle').focus();
}

async function saveDashboard() {
    const id = document.getElementById('dashboardEditId').value;
    const body = {
        title: document.getElementById('dashTitle').value.trim(),
        description: document.getElementById('dashDescription').value.trim(),
        refreshIntervalSeconds: Number(document.getElementById('dashRefresh').value),
        panels: readPanelRows().filter(panel => panel.query),
    };
    if (!body.title) { toast('Give the dashboard a title', 'error'); return; }
    try {
        const saved = id ? await api('PUT', '/api/v1/dashboards/' + encodeURIComponent(id), body) : await api('POST', '/api/v1/dashboards', body);
        closeModal('dashboardModal');
        toast(id ? 'Dashboard saved' : 'Dashboard created', 'success');
        openDashboard(saved.id || id);
    } catch (e) { toast('Saving failed: ' + e.message, 'error'); }
}

async function deleteDashboard(id) {
    id = typeof id === 'string' ? id : document.getElementById('dashboardEditId').value;
    const d = dash.list.find(x => x.id === id) || dash.current;
    if (!confirm(`Delete the dashboard "${d ? d.title : ''}"? Its panels go with it. This can't be undone.`)) return;
    try {
        await api('DELETE', '/api/v1/dashboards/' + encodeURIComponent(id));
        closeModal('dashboardModal');
        toast('Dashboard deleted', 'success');
        loadDashboards();
    } catch (e) { toast('Deleting failed: ' + e.message, 'error'); }
}

function addPanelRow(panel) {
    const data = panel && panel.title !== undefined ? panel : { title: '', query: '', type: 'AreaChart', width: 6 };
    const row = document.createElement('div');
    row.className = 'panel-row';
    const options = PANEL_KINDS.map(([value, label]) => `<option value="${value}"${data.type === value ? ' selected' : ''}>${label}</option>`).join('');
    const widths = PANEL_WIDTHS.map(([value, label]) => `<option value="${value}"${panelSpan(data.width) === panelSpan(value) ? ' selected' : ''}>${label}</option>`).join('');
    row.innerHTML = `<div class="panel-row-grid">
            <label>Title <input type="text" class="panel-title" value="${esc(data.title || '')}"></label>
            <label>Chart <select class="panel-type">${options}</select></label>
            <label>Width <select class="panel-width">${widths}</select></label>
        </div>
        <label>Search <textarea class="panel-query mono" rows="2" placeholder="find * | timechart span=1h count">${esc(data.query || '')}</textarea></label>
        <label>What it measures <input type="text" class="panel-desc" maxlength="500" value="${esc(data.description || '')}" placeholder="Worked out from the search if left empty"></label>
        <div class="panel-row-actions">
            <button type="button" class="btn btn-sm panel-preview-btn">Preview</button>
            <button type="button" class="btn btn-sm panel-up">Move up</button>
            <button type="button" class="btn btn-sm panel-down">Move down</button>
            <button type="button" class="btn btn-sm btn-danger panel-remove">Remove</button>
        </div>
        <div class="panel-preview"></div>`;
    row.dataset.panelId = data.id || '';
    row.querySelector('.panel-remove').onclick = () => row.remove();
    row.querySelector('.panel-up').onclick = () => { if (row.previousElementSibling) row.parentElement.insertBefore(row, row.previousElementSibling); };
    row.querySelector('.panel-down').onclick = () => { if (row.nextElementSibling) row.parentElement.insertBefore(row.nextElementSibling, row); };
    row.querySelector('.panel-preview-btn').onclick = async () => {
        const preview = row.querySelector('.panel-preview');
        const query = row.querySelector('.panel-query').value.trim();
        if (!query) { preview.innerHTML = errorHtml('Write a search first.'); return; }
        preview.innerHTML = loadingHtml('Running the search…');
        const { start, end } = dashboardWindow();
        try {
            CabinetCharts.render(preview, await runPanelQuery(query, start, end), { viz: PANEL_VIZ[row.querySelector('.panel-type').value], height: 160, tableWhenRaw: true, tableRows: 8, barNote: false });
        } catch (e) { preview.innerHTML = errorHtml(`The search didn't run: ${e.message}.`); }
    };
    document.getElementById('panelEditorList').appendChild(row);
}

function readPanelRows() {
    return [...document.querySelectorAll('#panelEditorList .panel-row')].map((row, i) => {
        const panel = {
            title: row.querySelector('.panel-title').value.trim() || CabinetCharts.describeQuery(row.querySelector('.panel-query').value) || `Panel ${i + 1}`,
            query: row.querySelector('.panel-query').value.trim(),
            description: row.querySelector('.panel-desc').value.trim() || null,
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

/* ===== Pop-up menu: a push button that opens a list; used by toolbars on several pages ===== */

let popupMenuOpen = null;

/**
 * Opens a menu under a .popup button. items: [{ label, value, checked }]. onPick gets the value.
 * Escape or a click elsewhere closes it; arrow keys move between items.
 */
function showPopupMenu(button, items, onPick) {
    closePopupMenu();
    const menu = document.createElement('div');
    menu.className = 'menu popup-menu';
    menu.setAttribute('role', 'menu');
    items.forEach((item, i) => {
        const b = document.createElement('button');
        b.type = 'button';
        b.className = 'menu-item';
        b.setAttribute('role', 'menuitemradio');
        b.setAttribute('aria-checked', String(Boolean(item.checked)));
        b.innerHTML = `<span>${esc(item.label)}</span>${item.checked ? '<span aria-hidden="true">✓</span>' : ''}`;
        b.addEventListener('click', () => { closePopupMenu(); onPick(item.value); });
        b.dataset.index = String(i);
        menu.appendChild(b);
    });
    document.body.appendChild(menu);
    const rect = button.getBoundingClientRect();
    menu.style.position = 'fixed';
    menu.style.top = `${rect.bottom + 4}px`;
    menu.style.left = `${Math.max(8, Math.min(rect.left, window.innerWidth - menu.offsetWidth - 8))}px`;
    button.setAttribute('aria-expanded', 'true');
    popupMenuOpen = { menu, button };
    const current = menu.querySelector('[aria-checked="true"]') || menu.querySelector('.menu-item');
    if (current) current.focus();
    menu.addEventListener('keydown', event => {
        const buttons = [...menu.querySelectorAll('.menu-item')];
        const at = buttons.indexOf(document.activeElement);
        if (event.key === 'ArrowDown') { event.preventDefault(); buttons[(at + 1) % buttons.length].focus(); }
        else if (event.key === 'ArrowUp') { event.preventDefault(); buttons[(at - 1 + buttons.length) % buttons.length].focus(); }
        else if (event.key === 'Escape') { event.preventDefault(); event.stopPropagation(); closePopupMenu(); button.focus(); }
        else if (event.key === 'Tab') closePopupMenu();
    });
}

function closePopupMenu() {
    if (!popupMenuOpen) return;
    popupMenuOpen.menu.remove();
    popupMenuOpen.button.setAttribute('aria-expanded', 'false');
    popupMenuOpen = null;
}

document.addEventListener('click', event => {
    if (popupMenuOpen && !event.target.closest('.popup-menu') && !popupMenuOpen.button.contains(event.target)) closePopupMenu();
});
window.addEventListener('resize', closePopupMenu);
