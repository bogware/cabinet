/*
 * The Settings page: every server setting, described and editable, plus storage, API keys,
 * updates, retention, roles and this browser's preferences. Values reach the page through esc();
 * handlers are named in data-click and data-change attributes.
 */

const settingsState = {
    data: null,
    storage: null,
    storageOverview: null,
    keys: [],
    edits: {},
    errors: {},
    section: 'overview',
    loaded: false,
    forbidden: false,
};

const FIXED_SECTIONS = [
    { id: 'overview', label: 'Storage' },
    { id: 'keys', label: 'API keys' },
    { id: 'updates', label: 'Install updates' },
    { id: 'retention', label: 'Retention by index' },
    { id: 'roles', label: 'Roles & access' },
    { id: 'browser', label: 'This browser' },
];

function bytes(n) {
    if (n === null || n === undefined) return '-';
    const units = ['bytes', 'KB', 'MB', 'GB', 'TB'];
    let value = n;
    let unit = 0;
    while (value >= 1024 && unit < units.length - 1) { value /= 1024; unit++; }
    return `${value.toFixed(unit === 0 || value >= 100 ? 0 : 1)} ${units[unit]}`;
}

function settingsSlug(name) {
    return 'group:' + name;
}

/* ===== Loading ===== */

async function loadServerSettings() {
    if (!settingsState.loaded) {
        document.getElementById('settingsSearch').addEventListener('input', renderSettingsVisibility);
        settingsState.loaded = true;
    }

    const [settings, storage, keys] = await Promise.allSettled([
        api('GET', '/api/v1/settings'),
        api('GET', '/api/v1/storage'),
        api('GET', '/api/v1/keys'),
    ]);

    const notice = document.getElementById('settingsAccessNotice');
    if (settings.status === 'rejected') {
        settingsState.forbidden = /403|forbidden|not allowed|capabilit/i.test(settings.reason.message);
        notice.hidden = false;
        notice.textContent = settingsState.forbidden
            ? 'Server settings, storage and API keys need an Admin key. Your preferences for this browser are below.'
            : `Settings could not be loaded: ${settings.reason.message}`;
        settingsState.data = null;
    } else {
        notice.hidden = true;
        settingsState.data = settings.value;
        settingsState.forbidden = false;
    }

    settingsState.storage = storage.status === 'fulfilled' ? storage.value.current : null;
    settingsState.storageOverview = storage.status === 'fulfilled' ? storage.value : null;
    settingsState.keys = keys.status === 'fulfilled' ? keys.value : [];
    renderSettingsNav();
    renderStorageOverview();
    renderKeys();
    renderSettingsGroups();
    renderRestartBanner();
    renderSaveBar();
    showSettingsSection(settingsState.forbidden && settingsState.section === 'overview' ? 'browser' : settingsState.section);
}

/* ===== Navigation ===== */

function renderSettingsNav() {
    const groups = settingsState.data ? settingsState.data.groups : [];
    const fixed = FIXED_SECTIONS.filter(s => !settingsState.forbidden || s.id === 'browser' || s.id === 'roles');
    const item = (id, label, count) => {
        const changed = count ? `<span class="st-nav-dot" title="${count} unsaved">${count}</span>` : '';
        return `<button type="button" class="st-nav-item" data-section="${esc(id)}" aria-current="${settingsState.section === id ? 'page' : 'false'}" data-click="showSettingsSection" data-args="${esc(JSON.stringify([id]))}">${esc(label)}${changed}</button>`;
    };
    const unsaved = name => Object.keys(settingsState.edits).filter(key => groupOf(key) === name).length;
    document.getElementById('settingsNav').innerHTML =
        fixed.map(s => item(s.id, s.label)).join('') +
        (groups.length ? '<div class="st-nav-heading">Server settings</div>' + groups.map(g => item(settingsSlug(g.name), g.name, unsaved(g.name))).join('') : '');
}

function showSettingsSection(id) {
    settingsState.section = id;
    document.getElementById('settingsSearch').value = '';
    document.querySelectorAll('#page-settings .st-section').forEach(section => {
        section.hidden = section.dataset.section !== id;
        section.querySelectorAll('.st-row').forEach(row => { row.hidden = false; });
    });
    document.querySelectorAll('#settingsNav .st-nav-item').forEach(button => button.setAttribute('aria-current', button.dataset.section === id ? 'page' : 'false'));
    document.getElementById('settingsNoMatch').hidden = true;
    const main = document.querySelector('#page-settings .st-main');
    if (main) main.scrollTop = 0;
}

/** Searching shows matching settings from every group at once. */
function renderSettingsVisibility() {
    const term = document.getElementById('settingsSearch').value.trim().toLowerCase();
    if (!term) { showSettingsSection(settingsState.section); return; }
    let matches = 0;
    document.querySelectorAll('#page-settings .st-section').forEach(section => {
        const rows = [...section.querySelectorAll('.st-row')];
        if (rows.length === 0) {
            section.hidden = !section.textContent.toLowerCase().includes(term);
            if (!section.hidden) matches++;
            return;
        }

        let any = false;
        rows.forEach(row => {
            const hit = row.dataset.search.includes(term);
            row.hidden = !hit;
            any = any || hit;
        });
        section.hidden = !any;
        if (any) matches += rows.filter(r => !r.hidden).length;
    });
    document.getElementById('settingsNoMatch').hidden = matches > 0;
}

/* ===== Storage ===== */

const STORAGE_POLL_MS = 1000;
let storagePoll = null;

function storageTypeLabel(type) {
    return type === 'SQLite' ? 'SQLite file' : type === 'PostgreSQL' ? 'PostgreSQL database' : type;
}

function renderStorageOverview() {
    const where = document.getElementById('storageWhere');
    const box = document.getElementById('storageOverview');
    const overview = settingsState.storageOverview;
    const s = settingsState.storage;
    const d = settingsState.data;
    if (!s) {
        where.innerHTML = '<div class="st-card st-muted">Storage details need an Admin key.</div>';
        box.innerHTML = '';
        document.getElementById('storageActions').hidden = true;
        return;
    }

    const type = overview ? overview.type : s.type;
    const span = s.oldestEvent ? `${esc(fmtTime(s.oldestEvent))} to ${esc(fmtTime(s.newestEvent))}` : 'no events yet';
    where.innerHTML = `<div class="st-where">
        <div class="st-where-label">Your data is stored in a ${esc(storageTypeLabel(type))}</div>
        <div class="st-where-path"><code id="storageLocation">${esc(s.location || type)}</code>
            <button type="button" class="btn btn-secondary btn-sm" data-click="copyStorageLocation">Copy</button></div>
        <div class="st-where-meta">${esc(fmt(s.eventsStored))} events &middot; ${esc(bytes(s.sizeBytes))} &middot; ${span} &middot; ${s.retentionDays > 0 ? `kept ${esc(fmt(s.retentionDays))} days` : 'kept forever'}</div>
    </div>`;

    const card = (label, value, detail) => `<div class="st-stat"><div class="st-stat-label">${esc(label)}</div><div class="st-stat-value">${value}</div>${detail ? `<div class="st-stat-detail">${detail}</div>` : ''}</div>`;
    const used = s.diskTotalBytes ? 1 - (s.diskFreeBytes / s.diskTotalBytes) : null;
    const parts = [
        card('Free disk', esc(bytes(s.diskFreeBytes)), used !== null ? `<div class="st-meter" role="meter" aria-valuenow="${Math.round(used * 100)}" aria-valuemin="0" aria-valuemax="100" aria-label="Disk used"><span style="width:${(used * 100).toFixed(1)}%"></span></div>${esc(Math.round(used * 100))}% of ${esc(bytes(s.diskTotalBytes))} used` : ''),
        card('Size per event', s.eventsStored && s.sizeBytes ? esc(bytes(s.sizeBytes / s.eventsStored)) : '-', s.eventsStored && s.sizeBytes && s.diskFreeBytes ? `Room for about ${esc(fmt(Math.floor(s.diskFreeBytes / (s.sizeBytes / s.eventsStored))))} more` : ''),
        card('Kept for', s.retentionDays > 0 ? `${esc(fmt(s.retentionDays))} days` : 'Forever', `<a href="#settings" data-click="showSettingsSection" data-args='["group:Retention"]'>Change retention</a>`),
    ];
    if (s.metadataLocation) parts.push(card('Settings, rules and history', 'Separate', `<code class="st-path">${esc(s.metadataLocation)}</code>`));
    if (d) {
        parts.push(card('Listening on', (d.listeningAddresses || []).map(a => `<code>${esc(a)}</code>`).join('<br>') || '-', 'Change with the installer (-Port, -Https)'));
        parts.push(card('Changes made on these pages', 'Settings file', `<code class="st-path">${esc(d.overridesFile)}</code>Outside the program folder, so upgrades keep it.`));
    }

    box.innerHTML = parts.join('');
    const movable = overview && overview.movableTo.includes(type);
    document.getElementById('storageActions').hidden = !movable;
    document.getElementById('moveSqliteCard').hidden = type !== 'SQLite';
    renderStorageProgress(overview ? overview.migration : null);
}

async function copyStorageLocation() {
    const text = document.getElementById('storageLocation').textContent;
    try { await navigator.clipboard.writeText(text); toast('Location copied', 'success'); }
    catch (e) { toast(text, 'info'); }
}

function quoteValue(value) {
    return /[;="'\s]/.test(value) ? `"${value.replace(/"/g, '""')}"` : value;
}

function postgresConnection() {
    const pasted = document.getElementById('pgConnection').value.trim();
    if (pasted) return pasted;
    const part = (key, id) => `${key}=${quoteValue(document.getElementById(id).value.trim())}`;
    return [part('Host', 'pgHost'), part('Port', 'pgPort'), part('Database', 'pgDatabase'), part('Username', 'pgUser'), part('Password', 'pgPassword'), part('SSL Mode', 'pgSsl')].join(';');
}

function sqliteConnection(path) {
    return `Data Source=${path.trim()}`;
}

function showResult(id, ok, message) {
    const box = document.getElementById(id);
    box.className = `st-result ${ok ? 'st-result-ok' : 'st-result-bad'}`;
    box.textContent = message;
}

async function testStorage(type, connectionString, resultId) {
    try {
        const result = await api('POST', '/api/v1/storage/test', { type, connectionString });
        showResult(resultId, result.ok, `${result.location ? result.location + ': ' : ''}${result.message}`);
        return result;
    } catch (e) {
        showResult(resultId, false, e.message);
        return null;
    }
}

function testSqliteTarget() {
    const path = document.getElementById('movePath').value.trim();
    if (!path) { showResult('moveResult', false, 'Enter the full path of the new file, such as D:\\Cabinet\\cabinet.db'); return; }
    return testStorage('SQLite', sqliteConnection(path), 'moveResult');
}

async function moveSqlite() {
    const path = document.getElementById('movePath').value.trim();
    if (!path) { showResult('moveResult', false, 'Enter the full path of the new file'); return; }
    if (!confirm(`Move the database to ${path}? New events wait a few seconds while it copies, then Cabinet restarts on the new file. The current file is left as it is.`)) return;
    await startMove({ type: 'SQLite', connectionString: sqliteConnection(path), copy: true }, 'moveResult');
}

async function testPostgresTarget() {
    const connectionString = postgresConnection();
    const result = await testStorage('PostgreSQL', connectionString, 'postgresResult');
    if (result && !result.ok && document.getElementById('pgCreate').checked && /doesn't exist/.test(result.message)) {
        showResult('postgresResult', true, 'Connected to the server. The database doesn\'t exist yet; it will be created when you switch.');
    }
}

async function switchToPostgres() {
    let connectionString = postgresConnection();
    const copy = document.getElementById('pgCopy').checked;
    if (document.getElementById('pgCreate').checked) {
        try {
            const created = await api('POST', '/api/v1/storage/postgres/database', { type: 'PostgreSQL', connectionString });
            if (!created.ok) { showResult('postgresResult', false, created.message); return; }
            if (copy && created.eventsStored > 0) { showResult('postgresResult', false, `That database already holds ${fmt(created.eventsStored)} events. Choose an empty one, or untick "Copy".`); return; }
        } catch (e) {
            showResult('postgresResult', false, e.message);
            return;
        }
    }

    const what = copy ? 'Copy everything into PostgreSQL and switch to it' : 'Switch to PostgreSQL without copying (it starts with what it holds)';
    if (!confirm(`${what}? Cabinet keeps running while it copies, then restarts on PostgreSQL. The SQLite file is left as it is.`)) return;
    await startMove({ type: 'PostgreSQL', connectionString, copy }, 'postgresResult');
}

async function startFresh() {
    const type = document.getElementById('freshType').value;
    const connectionString = type === 'SQLite' ? sqliteConnection(document.getElementById('freshPath').value) : postgresConnection();
    if (type === 'SQLite' && !document.getElementById('freshPath').value.trim()) { showResult('freshResult', false, 'Enter the full path of the new file'); return; }
    if (!confirm('Start Cabinet on a new, empty database? Search will show nothing until new events arrive. The current database is kept, so you can move back to it.')) return;
    await startMove({ type, connectionString, copy: false }, 'freshResult');
}

async function startMove(request, resultId) {
    try {
        const status = await api('POST', '/api/v1/storage/migrate', request);
        showResult(resultId, true, 'Started. Progress is shown above.');
        renderStorageProgress(status);
        pollStorageProgress();
        document.getElementById('storageProgress').scrollIntoView({ block: 'start', behavior: 'smooth' });
    } catch (e) {
        showResult(resultId, false, e.message);
    }
}

function pollStorageProgress() {
    clearTimeout(storagePoll);
    storagePoll = setTimeout(async () => {
        try {
            const status = await api('GET', '/api/v1/storage/migrate');
            renderStorageProgress(status);
            if (status.state === 'Running') pollStorageProgress();
            else if (status.state === 'Succeeded') waitForStorageRestart();
        } catch (e) {
            // The server is restarting on the new database
            waitForStorageRestart();
        }
    }, STORAGE_POLL_MS);
}

async function waitForStorageRestart() {
    const started = Date.now();
    await new Promise(resolve => setTimeout(resolve, 3000));
    while (Date.now() - started < 120000) {
        try {
            const response = await fetch('/health/ready', { cache: 'no-store' });
            if (response.ok) { toast('Cabinet is running on the new database', 'success'); loadServerSettings(); return; }
        } catch (e) {
            // Still restarting
        }

        await new Promise(resolve => setTimeout(resolve, 2000));
    }

    toast('Cabinet hasn\'t come back after two minutes. Check the service on the server.', 'error');
}

function renderStorageProgress(status) {
    const box = document.getElementById('storageProgress');
    if (!status || status.state === 'Idle') { box.hidden = true; return; }
    box.hidden = false;
    const running = status.state === 'Running';
    const percent = status.eventsToCopy > 0 ? Math.min(100, (status.eventsCopied / status.eventsToCopy) * 100) : (running ? 0 : 100);
    const tone = status.state === 'Failed' ? 'st-result-bad' : status.state === 'Cancelled' ? '' : 'st-result-ok';
    box.innerHTML = `
        <h3>${running ? 'Moving' : status.state === 'WaitingForRestart' ? 'Ready: restart to finish' : status.state === 'Succeeded' ? 'Done' : status.state === 'Cancelled' ? 'Cancelled' : 'Stopped'}</h3>
        <div class="st-desc">${esc(status.source || '')} &rarr; ${esc(status.target || '')}</div>
        ${status.copy ? `<div class="st-meter st-meter-big" role="progressbar" aria-valuenow="${Math.round(percent)}" aria-valuemin="0" aria-valuemax="100" aria-label="Events copied"><span style="width:${percent.toFixed(1)}%"></span></div>
        <div class="st-desc">${esc(fmt(status.eventsCopied))} of ${esc(fmt(status.eventsToCopy))} events${status.metadataCopied ? `, ${esc(fmt(status.metadataCopied))} settings, rules, keys and history records` : ''}</div>` : ''}
        <div class="st-result ${tone}">${esc(status.phase || '')}</div>
        <details class="st-advanced"${status.state === 'Failed' ? ' open' : ''}><summary>Details</summary><pre class="st-log">${esc((status.log || []).join('\n'))}</pre></details>
        <div class="st-buttons">
            ${running ? '<button type="button" class="btn btn-secondary btn-sm" data-click="cancelStorageMove">Cancel</button>' : ''}
            ${status.state === 'WaitingForRestart' && settingsState.data && settingsState.data.canRestart ? '<button type="button" class="btn btn-primary btn-sm" data-click="restartServer">Restart now</button>' : ''}
        </div>`;
    document.getElementById('storageActions').hidden = running || status.state === 'WaitingForRestart' || !(settingsState.storageOverview && settingsState.storageOverview.movableTo.includes(settingsState.storageOverview.type));
    if (running) pollStorageProgress();
}

async function cancelStorageMove() {
    try { await api('POST', '/api/v1/storage/migrate/cancel'); toast('Cancelling', 'info'); pollStorageProgress(); }
    catch (e) { toast(e.message, 'error'); }
}

/* ===== Settings groups ===== */

function groupOf(key) {
    const groups = settingsState.data ? settingsState.data.groups : [];
    const group = groups.find(g => g.settings.some(s => s.key === key));
    return group ? group.name : null;
}

function findSetting(key) {
    for (const group of settingsState.data ? settingsState.data.groups : []) {
        const setting = group.settings.find(s => s.key === key);
        if (setting) return setting;
    }
    return null;
}

function displayList(value) {
    return Array.isArray(value) ? value.join('\n') : '';
}

function displayJson(value) {
    if (value === null || value === undefined) return '';
    return typeof value === 'string' ? value : JSON.stringify(value, null, 2);
}

function describeDefault(setting) {
    const value = setting.default;
    if (setting.type === 'Secret') return '';
    if (value === null || value === undefined || value === '') return 'Default: none';
    if (setting.type === 'Bool') return `Default: ${value ? 'on' : 'off'}`;
    if (Array.isArray(value)) return value.length ? `Default: ${value.join(', ')}` : 'Default: none';
    if (typeof value === 'object') return 'Default: ' + JSON.stringify(value);
    const number = typeof value === 'number' ? fmt(value) : value;
    return `Default: ${number}${setting.unit ? ' ' + setting.unit : ''}`;
}

const SOURCE_LABELS = {
    'settings-file': ['Changed here', 'st-badge-changed'],
    appsettings: ['From the installer\'s settings', 'st-badge-file'],
    environment: ['Set by an environment variable', 'st-badge-env'],
};

function controlFor(setting) {
    const key = setting.key;
    const id = 'st-' + key.replace(/[^A-Za-z0-9]/g, '-');
    const edited = Object.prototype.hasOwnProperty.call(settingsState.edits, key);
    const value = edited ? settingsState.edits[key] : setting.value;
    const disabled = setting.editable ? '' : ' disabled';
    const args = esc(JSON.stringify([key, '$this']));
    const range = `${setting.min !== null && setting.min !== undefined ? ` min="${esc(setting.min)}"` : ''}${setting.max !== null && setting.max !== undefined ? ` max="${esc(setting.max)}"` : ''}`;

    switch (setting.type) {
        case 'Bool':
            return `<label class="st-switch"><input type="checkbox" id="${id}"${value ? ' checked' : ''}${disabled} data-change="onSettingChange" data-args="${args}"><span aria-hidden="true"></span><span class="st-switch-text">${value ? 'On' : 'Off'}</span></label>`;
        case 'Int':
        case 'Long':
        case 'Double':
            return `<span class="st-number"><input type="number" id="${id}" value="${esc(value ?? '')}"${range}${setting.type === 'Double' ? ' step="any"' : ''}${disabled} data-change="onSettingChange" data-args="${args}">${setting.unit ? `<span class="st-unit">${esc(setting.unit)}</span>` : ''}</span>`;
        case 'Enum':
            return `<select id="${id}"${disabled} data-change="onSettingChange" data-args="${args}">${(setting.options || []).map(o => `<option value="${esc(o)}"${String(o) === String(value) ? ' selected' : ''}>${esc(o)}</option>`).join('')}</select>`;
        case 'List':
            return `<textarea id="${id}" rows="${Math.min(8, Math.max(2, (value || []).length + 1))}" placeholder="One per line"${disabled} data-change="onSettingChange" data-args="${args}">${esc(displayList(value))}</textarea>`;
        case 'Json':
            return `<textarea id="${id}" class="st-json" rows="${Math.min(14, Math.max(3, displayJson(value).split('\n').length + 1))}" spellcheck="false"${disabled} data-change="onSettingChange" data-args="${args}">${esc(displayJson(value))}</textarea>`;
        case 'Secret': {
            const pendingClear = edited && settingsState.edits[key] === null;
            const state = pendingClear ? 'will be removed' : edited ? 'will be replaced' : setting.isSet ? 'is set' : 'not set';
            return `<span class="st-secret"><input type="password" id="${id}" autocomplete="new-password" placeholder="${setting.isSet ? 'Enter a new value to replace it' : 'Enter a value'}"${disabled} data-change="onSettingChange" data-args="${args}">
                <span class="st-secret-state">${esc(state)}</span>
                ${setting.isSet && setting.editable ? `<button type="button" class="btn btn-secondary btn-sm" data-click="clearSecret" data-args="${esc(JSON.stringify([key]))}">Remove</button>` : ''}</span>`;
        }
        default:
            return `<input type="text" id="${id}" value="${esc(value ?? '')}"${disabled} data-change="onSettingChange" data-args="${args}">`;
    }
}

function settingRowHtml(setting) {
    const key = setting.key;
    const id = 'st-' + key.replace(/[^A-Za-z0-9]/g, '-');
    const edited = Object.prototype.hasOwnProperty.call(settingsState.edits, key);
    const error = settingsState.errors[key];
    const badges = [];
    const source = SOURCE_LABELS[setting.source];
    if (source) badges.push(`<span class="st-badge ${source[1]}">${esc(source[0])}</span>`);
    if (edited) badges.push('<span class="st-badge st-badge-unsaved">Unsaved</span>');
    if (setting.pendingRestart) badges.push('<span class="st-badge st-badge-restart">Restart to apply</span>');
    const canReset = setting.editable && setting.source === 'settings-file';
    const search = `${setting.label} ${setting.key} ${setting.description} ${groupOf(key) || ''}`.toLowerCase();
    return `<div class="st-row${edited ? ' st-row-edited' : ''}${error ? ' st-row-error' : ''}" data-search="${esc(search)}">
        <div class="st-row-text">
            <label class="st-label" for="${id}">${esc(setting.label)}</label>
            <div class="st-desc">${esc(setting.description)}</div>
            <div class="st-meta"><code>${esc(key)}</code><span>${esc(describeDefault(setting))}</span>${setting.min !== null && setting.min !== undefined && setting.max !== null && setting.max !== undefined ? `<span>${esc(fmt(setting.min))} to ${esc(fmt(setting.max))}</span>` : ''}${badges.join('')}</div>
            ${error ? `<div class="st-error" role="alert">${esc(error)}</div>` : ''}
            ${!setting.editable && setting.source === 'environment' ? '<div class="st-desc">Change the environment variable on the server to change this.</div>' : ''}
        </div>
        <div class="st-row-control">
            ${controlFor(setting)}
            ${canReset ? `<button type="button" class="st-reset" title="Go back to the default" data-click="resetSetting" data-args="${esc(JSON.stringify([key]))}">Reset</button>` : ''}
        </div>
    </div>`;
}

function renderSettingsGroups() {
    const container = document.getElementById('settingsGroups');
    const groups = settingsState.data ? settingsState.data.groups : [];
    container.innerHTML = groups.map(group => `
        <section class="st-section" data-section="${esc(settingsSlug(group.name))}" aria-label="${esc(group.name)}" hidden>
            <h2>${esc(group.name)}</h2>
            ${group.description ? `<p class="st-intro">${esc(group.description)}</p>` : ''}
            <div class="st-rows">${group.settings.map(settingRowHtml).join('')}</div>
        </section>`).join('');
}

function rerenderSetting(key) {
    const setting = findSetting(key);
    if (!setting) return;
    const old = document.querySelector(`#settingsGroups .st-row[data-search*="${CSS.escape(key.toLowerCase())}"]`);
    const rows = [...document.querySelectorAll('#settingsGroups .st-row')];
    const row = rows.find(r => r.querySelector('code') && r.querySelector('code').textContent === key) || old;
    if (!row) { renderSettingsGroups(); return; }
    const hidden = row.hidden;
    row.outerHTML = settingRowHtml(setting);
    const fresh = [...document.querySelectorAll('#settingsGroups .st-row')].find(r => r.querySelector('code').textContent === key);
    if (fresh) fresh.hidden = hidden;
}

/* ===== Editing ===== */

function parseSettingInput(setting, element) {
    switch (setting.type) {
        case 'Bool': return element.checked;
        case 'Int':
        case 'Long': return element.value.trim() === '' ? null : Number.parseInt(element.value, 10);
        case 'Double': return element.value.trim() === '' ? null : Number.parseFloat(element.value);
        case 'List': return element.value.split('\n').map(v => v.trim()).filter(Boolean);
        case 'Json': {
            const text = element.value.trim();
            if (text === '') return null;
            try { return JSON.parse(text); }
            catch (e) { throw new Error('This isn\'t valid JSON: ' + e.message); }
        }
        default: return element.value;
    }
}

function sameValue(a, b) {
    return JSON.stringify(a ?? null) === JSON.stringify(b ?? null);
}

function onSettingChange(key, element) {
    const setting = findSetting(key);
    if (!setting) return;
    delete settingsState.errors[key];
    try {
        const value = parseSettingInput(setting, element);
        if (setting.type === 'Secret') {
            if (value === '') delete settingsState.edits[key]; else settingsState.edits[key] = value;
        } else if (sameValue(value, setting.value)) {
            delete settingsState.edits[key];
        } else {
            settingsState.edits[key] = value;
        }
    } catch (e) {
        settingsState.errors[key] = e.message;
    }

    if (setting.type === 'Bool') {
        const text = element.closest('.st-switch').querySelector('.st-switch-text');
        if (text) text.textContent = element.checked ? 'On' : 'Off';
    }

    const row = element.closest('.st-row');
    if (row) {
        row.classList.toggle('st-row-edited', Object.prototype.hasOwnProperty.call(settingsState.edits, key));
        row.classList.toggle('st-row-error', Boolean(settingsState.errors[key]));
        let error = row.querySelector('.st-error');
        if (settingsState.errors[key]) {
            if (!error) { error = document.createElement('div'); error.className = 'st-error'; error.setAttribute('role', 'alert'); row.querySelector('.st-row-text').appendChild(error); }
            error.textContent = settingsState.errors[key];
        } else if (error) {
            error.remove();
        }
    }

    renderSaveBar();
    renderSettingsNav();
}

function clearSecret(key) {
    settingsState.edits[key] = null;
    rerenderSetting(key);
    renderSaveBar();
}

function renderSaveBar() {
    const count = Object.keys(settingsState.edits).length;
    const errors = Object.keys(settingsState.errors).length;
    const bar = document.getElementById('settingsSaveBar');
    bar.hidden = count === 0 && errors === 0;
    document.getElementById('settingsSaveText').textContent = errors
        ? `${errors} ${errors === 1 ? 'setting needs' : 'settings need'} fixing`
        : `${count} unsaved ${count === 1 ? 'change' : 'changes'}`;
    document.getElementById('settingsSaveButton').disabled = errors > 0 || count === 0;
}

function discardSettings() {
    settingsState.edits = {};
    settingsState.errors = {};
    renderSettingsGroups();
    showSettingsSection(settingsState.section);
    renderSaveBar();
    renderSettingsNav();
}

async function saveSettings() {
    const button = document.getElementById('settingsSaveButton');
    button.disabled = true;
    try {
        const response = await fetch('/api/v1/settings', {
            method: 'PUT',
            headers: { 'X-API-Key': API_KEY, 'Content-Type': 'application/json', Accept: 'application/json' },
            body: JSON.stringify({ values: settingsState.edits }),
        });
        const body = await response.json().catch(() => ({}));
        if (response.status === 400 && body.errors) {
            settingsState.errors = body.errors;
            Object.keys(body.errors).forEach(rerenderSetting);
            const first = Object.keys(body.errors)[0];
            const group = groupOf(first);
            if (group) showSettingsSection(settingsSlug(group));
            renderSaveBar();
            toast('Nothing was saved: fix the highlighted settings', 'error');
            return;
        }

        if (!response.ok) throw new Error(body.detail || body.title || `HTTP ${response.status}`);
        const count = Object.keys(settingsState.edits).length;
        settingsState.data = body;
        settingsState.edits = {};
        settingsState.errors = {};
        const section = settingsState.section;
        renderSettingsGroups();
        renderSettingsNav();
        renderStorageOverview();
        renderRestartBanner();
        renderSaveBar();
        showSettingsSection(section);
        toast(`Saved ${count} ${count === 1 ? 'change' : 'changes'}${body.restartPending ? '; restart to apply' : ''}`, 'success');
    } catch (e) {
        toast('Saving failed: ' + e.message, 'error');
    } finally {
        button.disabled = Object.keys(settingsState.edits).length === 0;
    }
}

async function resetSetting(key) {
    const setting = findSetting(key);
    if (!setting || !confirm(`Set "${setting.label}" back to its default?`)) return;
    try {
        settingsState.data = await api('DELETE', '/api/v1/settings/' + encodeURIComponent(key));
        delete settingsState.edits[key];
        delete settingsState.errors[key];
        const section = settingsState.section;
        renderSettingsGroups();
        renderRestartBanner();
        renderSaveBar();
        showSettingsSection(section);
        toast(`${setting.label} is back to its default`, 'success');
    } catch (e) {
        toast('Reset failed: ' + e.message, 'error');
    }
}

/* ===== Restart ===== */

function renderRestartBanner() {
    const banner = document.getElementById('settingsRestartBanner');
    const d = settingsState.data;
    if (!d || !d.restartPending) { banner.hidden = true; return; }
    const pending = d.groups.flatMap(g => g.settings.filter(s => s.pendingRestart).map(s => s.label));
    banner.hidden = false;
    banner.innerHTML = `<div><strong>Restart Cabinet to apply ${pending.length} ${pending.length === 1 ? 'change' : 'changes'}.</strong> <span class="st-muted">${esc(pending.slice(0, 5).join(', '))}${pending.length > 5 ? ', ...' : ''}</span></div>` +
        (d.canRestart
            ? '<button type="button" class="btn btn-primary btn-sm" data-click="restartServer">Restart now</button>'
            : `<span class="st-muted">${esc(d.cannotRestartReason || 'Restart the service to apply them.')}</span>`);
}

async function restartServer() {
    if (!confirm('Restart Cabinet now? Searches in progress stop, and agents wait and resend.')) return;
    try {
        await api('POST', '/api/v1/settings/restart');
    } catch (e) {
        toast('Restart failed: ' + e.message, 'error');
        return;
    }

    toast('Restarting. This page reconnects when Cabinet is back.', 'info');
    const banner = document.getElementById('settingsRestartBanner');
    banner.innerHTML = '<span class="sx-spinner" aria-hidden="true"></span> Restarting...';
    const started = Date.now();
    await new Promise(resolve => setTimeout(resolve, 3000));
    while (Date.now() - started < 120000) {
        try {
            const response = await fetch('/health/ready', { cache: 'no-store' });
            if (response.ok) { toast('Cabinet is back', 'success'); loadServerSettings(); return; }
        } catch (e) {
            // Still restarting
        }

        await new Promise(resolve => setTimeout(resolve, 2000));
    }

    toast('Cabinet hasn\'t come back after two minutes. Check the service on the server.', 'error');
}

/* ===== API keys ===== */

function renderKeys() {
    const body = document.querySelector('#keysTable tbody');
    if (!body) return;
    if (!settingsState.keys.length) {
        body.innerHTML = `<tr><td colspan="8" class="st-muted">${settingsState.forbidden ? 'Managing keys needs an Admin key.' : 'No keys.'}</td></tr>`;
        return;
    }

    const status = k => k.revoked ? '<span class="st-badge st-badge-env">Revoked</span>'
        : k.expired ? '<span class="st-badge st-badge-restart">Expired</span>'
        : '<span class="st-badge st-badge-changed">Active</span>';
    body.innerHTML = settingsState.keys
        .slice()
        .sort((a, b) => Number(a.revoked) - Number(b.revoked) || String(a.name).localeCompare(String(b.name)))
        .map(k => `<tr${k.revoked ? ' class="st-muted"' : ''}>
            <td>${esc(k.name)}${k.source === 'settings' ? ' <span class="st-badge st-badge-file" title="Created by the installer">installer</span>' : ''}</td>
            <td>${esc(k.tier)}</td>
            <td><code>${esc(k.prefix)}...</code></td>
            <td>${k.createdAt ? esc(fmtTime(k.createdAt)) : '-'}</td>
            <td>${k.lastUsedAt ? esc(fmtTime(k.lastUsedAt)) : 'never'}</td>
            <td>${k.expiresAt ? esc(fmtTime(k.expiresAt)) : 'never'}</td>
            <td>${status(k)}</td>
            <td>${k.revoked ? '' : `<button type="button" class="btn btn-secondary btn-sm" data-click="revokeApiKey" data-args="${esc(JSON.stringify([k.id, k.name]))}">Revoke</button>`}</td>
        </tr>`).join('');
}

async function createApiKey() {
    const name = document.getElementById('newKeyName').value.trim();
    const tier = document.getElementById('newKeyTier').value;
    const expires = document.getElementById('newKeyExpires').value;
    if (!name) { toast('Give the key a name, such as the server or person using it', 'error'); return; }
    try {
        const created = await api('POST', '/api/v1/keys', { name, tier, expiresAt: expires ? new Date(expires + 'T23:59:59').toISOString() : undefined });
        const box = document.getElementById('newKeyResult');
        box.hidden = false;
        box.innerHTML = `<div><strong>Key for ${esc(created.name)} (${esc(created.tier)})</strong>. Copy it now: it won't be shown again.</div>
            <div class="st-new-key-value"><code id="newKeyValue">${esc(created.key)}</code>
            <button type="button" class="btn btn-primary btn-sm" data-click="copyNewKey">Copy</button>
            <button type="button" class="btn btn-secondary btn-sm" data-click="dismissNewKey">Done</button></div>`;
        document.getElementById('newKeyName').value = '';
        document.getElementById('newKeyExpires').value = '';
        settingsState.keys = await api('GET', '/api/v1/keys');
        renderKeys();
    } catch (e) {
        toast('Creating the key failed: ' + e.message, 'error');
    }
}

async function copyNewKey() {
    const value = document.getElementById('newKeyValue').textContent;
    try { await navigator.clipboard.writeText(value); toast('Key copied', 'success'); }
    catch (e) {
        const range = document.createRange();
        range.selectNodeContents(document.getElementById('newKeyValue'));
        window.getSelection().removeAllRanges();
        window.getSelection().addRange(range);
        toast('Selected: press Ctrl+C to copy', 'info');
    }
}

function dismissNewKey() {
    const box = document.getElementById('newKeyResult');
    box.hidden = true;
    box.innerHTML = '';
}

async function revokeApiKey(id, name) {
    if (!confirm(`Revoke the key "${name}"? Anything using it is refused from now on. This can't be undone.`)) return;
    try {
        await api('DELETE', '/api/v1/keys/' + encodeURIComponent(id));
        settingsState.keys = await api('GET', '/api/v1/keys');
        renderKeys();
        toast(`Revoked ${name}`, 'success');
    } catch (e) {
        toast('Revoking failed: ' + e.message, 'error');
    }
}
