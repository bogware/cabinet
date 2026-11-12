/* Pages added in Cabinet 3: connectors and the rest. Each registers itself in window.EXTRA_PAGES,
   which app.js's navigation reads. Handlers are named functions wired through data-click, because
   the content security policy forbids inline script. Everything shown from the server goes
   through esc(). */
window.EXTRA_PAGES = window.EXTRA_PAGES || {};

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
