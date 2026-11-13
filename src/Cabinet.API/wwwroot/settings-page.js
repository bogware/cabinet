/*
 * The Settings page: every server setting, saying what it does, its default and where it's set,
 * with changes staged until Apply. Plus storage (disk, engine, moving), API keys, updates,
 * retention, roles and this browser's preferences. Values reach the page through esc(); handlers
 * are named in data-click and data-change attributes.
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
    meta: {},
    moving: false,
};

/*
 * The list of sections, as close to the design's grouping as the catalog allows. Each holds fixed
 * parts of the page (data-in on a .st-section) and catalog groups. A catalog group not named here
 * lands in General, so a new group always shows somewhere.
 */
const SETTINGS_SECTIONS = [
    { id: 'general', label: 'General', intro: 'How searches run, how much they may use, and your preferences in this browser.', groups: ['Search', 'Search limits', 'Rate limits', 'Background work'] },
    { id: 'overview', label: 'Storage', intro: 'Where events are kept and how they’re stored.', groups: ['Storage', 'Backups'] },
    { id: 'ingest', label: 'Ingest', intro: 'What senders can send, and the other ways events arrive.', groups: ['Ingest', 'Syslog', 'OpenTelemetry', 'Gaming'] },
    { id: 'signin', label: 'Sign-in & SSO', intro: 'Single sign-on, local accounts, the addresses browsers may come from, and roles.', groups: ['Network & security', 'Accounts'] },
    { id: 'keys', label: 'API keys', intro: '', groups: [] },
    { id: 'retention', label: 'Retention & archive', intro: 'How long events and audit records are kept, and where events go before they’re deleted.', groups: ['Retention', 'Archive'] },
    { id: 'security', label: 'Detection & privacy', intro: 'Detection rules, encrypting and masking fields, and the audit record.', groups: ['Detection', 'Encryption', 'Masking', 'Audit'] },
    { id: 'ai', label: 'AI investigator', intro: 'Answering questions about the logs with a language model that runs searches with the asker’s access.', groups: ['AI investigator'] },
    { id: 'email', label: 'Email & reports', intro: 'Alert history, and sending alerts and scheduled reports by email, chat and webhooks.', groups: ['Alerts & email', 'Webhooks'] },
    { id: 'updates', label: 'Updates', intro: '', groups: ['Updates'] },
];

const STORAGE_ENGINES = [
    ['Cabinet', 'Cabinet engine', 'Fastest on one server. Recommended.'],
    ['SQLite', 'SQLite', 'Simple single file. Slower on large data.'],
    ['PostgreSQL', 'PostgreSQL', 'Needed for several Cabinet servers.'],
    ['OpenSearch', 'OpenSearch', 'Use an existing cluster.'],
    ['ClickHouse', 'ClickHouse', 'More than one machine’s disk.'],
    ['Memory', 'Memory', 'Keeps nothing across restarts. For testing.'],
];

function bytes(n) {
    if (n === null || n === undefined) return '-';
    const units = ['bytes', 'KB', 'MB', 'GB', 'TB'];
    let value = n;
    let unit = 0;
    while (value >= 1024 && unit < units.length - 1) { value /= 1024; unit++; }
    return `${value.toFixed(unit === 0 || value >= 100 ? 0 : 1)} ${units[unit]}`;
}

function compactCount(n) {
    if (!n) return '0';
    if (n >= 1e6) return `${(n / 1e6).toFixed(n >= 1e7 ? 0 : 2)} M`;
    if (n >= 1e4) return `${Math.round(n / 1e3)} K`;
    return fmt(n);
}

function settingsSlug(name) {
    return 'group:' + name;
}

/** The section a catalog group is shown in. */
function sectionOfGroup(name) {
    const section = SETTINGS_SECTIONS.find(s => s.groups.includes(name));
    return section ? section.id : 'general';
}

/* ===== Loading ===== */

async function loadServerSettings() {
    if (!settingsState.loaded) {
        document.getElementById('settingsSearch').addEventListener('input', renderSettingsVisibility);
        settingsState.loaded = true;
    }

    const [settings, storage, keys, updates, investigator] = await Promise.allSettled([
        api('GET', '/api/v1/settings'),
        api('GET', '/api/v1/storage'),
        api('GET', '/api/v1/keys'),
        api('GET', '/api/v1/updates'),
        api('GET', '/api/v1/investigate/status'),
    ]);

    const notice = document.getElementById('settingsAccessNotice');
    if (settings.status === 'rejected') {
        settingsState.forbidden = /403|forbidden|not allowed|capabilit/i.test(settings.reason.message);
        notice.hidden = false;
        notice.innerHTML = settingsState.forbidden
            ? 'Server settings, storage and API keys need an Admin key. Your preferences for this browser are under General.'
            : errorHtml(`Settings couldn't be loaded: ${settings.reason.message}.`, 'Try again', 'loadServerSettings');
        settingsState.data = null;
    } else {
        notice.hidden = true;
        settingsState.data = settings.value;
        settingsState.forbidden = false;
    }

    settingsState.storage = storage.status === 'fulfilled' ? storage.value.current : null;
    settingsState.storageOverview = storage.status === 'fulfilled' ? storage.value : null;
    settingsState.keys = keys.status === 'fulfilled' ? keys.value : [];
    settingsState.meta = {
        ai: investigator.status === 'fulfilled' ? (investigator.value.enabled ? 'On' : 'Off') : '',
        updates: updates.status === 'fulfilled' && updates.value.available && updates.value.latest ? `${updates.value.latest} available` : '',
    };
    renderSettingsNav();
    renderSettingsHeads();
    renderStorageOverview();
    renderKeys();
    renderSettingsGroups();
    renderRestartBanner();
    renderSaveBar();
    const allowed = settingsState.forbidden ? ['general', 'signin'] : SETTINGS_SECTIONS.map(s => s.id);
    showSettingsSection(allowed.includes(settingsState.section) ? settingsState.section : 'general');
}

/* ===== Navigation ===== */

function renderSettingsNav() {
    const sections = SETTINGS_SECTIONS.filter(s => !settingsState.forbidden || s.id === 'general' || s.id === 'signin');
    const unsaved = id => Object.keys(settingsState.edits).filter(key => sectionOfGroup(groupOf(key)) === id).length;
    document.getElementById('settingsNav').innerHTML = sections.map(s => {
        const count = unsaved(s.id);
        const meta = count ? `<span class="st-nav-dot" title="${count} not applied yet">${count}</span>`
            : settingsState.meta[s.id] ? `<span class="st-nav-meta">${esc(settingsState.meta[s.id])}</span>` : '';
        return `<button type="button" class="st-nav-item" data-section="${esc(s.id)}" aria-current="${settingsState.section === s.id ? 'page' : 'false'}" data-click="showSettingsSection" data-args="${esc(JSON.stringify([s.id]))}"><span>${esc(s.label)}</span>${meta}</button>`;
    }).join('');
}

/** Each section's title and one-line purpose. */
function renderSettingsHeads() {
    const box = document.getElementById('settingsHeads');
    box.innerHTML = SETTINGS_SECTIONS.filter(s => s.intro || !['keys', 'updates'].includes(s.id)).map(s =>
        `<section class="st-section st-head" data-in="${esc(s.id)}" hidden><h2>${esc(s.label)}</h2>${s.intro ? `<p class="st-intro">${esc(s.intro)}</p>` : ''}</section>`).join('');
}

/** Shows one section; "group:Retention" opens the section that holds that group. */
function showSettingsSection(id) {
    if (typeof id === 'string' && id.startsWith('group:')) id = sectionOfGroup(id.slice(6));
    if (id === 'browser') id = 'general';
    if (id === 'roles') id = 'signin';
    settingsState.section = id;
    document.getElementById('settingsSearch').value = '';
    document.getElementById('page-settings').classList.remove('st-searching');
    document.querySelectorAll('#page-settings .st-section').forEach(section => {
        section.hidden = section.dataset.in !== id;
        section.querySelectorAll('.st-row').forEach(row => { row.hidden = false; });
    });
    document.querySelectorAll('#settingsNav .st-nav-item').forEach(button => button.setAttribute('aria-current', button.dataset.section === id ? 'page' : 'false'));
    document.getElementById('settingsNoMatch').hidden = true;
    renderSaveBar();
    const main = document.querySelector('#page-settings .st-main');
    if (main) main.scrollTop = 0;
}

/** Searching shows matching settings from every section at once. */
function renderSettingsVisibility() {
    const term = document.getElementById('settingsSearch').value.trim().toLowerCase();
    if (!term) { showSettingsSection(settingsState.section); return; }
    document.getElementById('page-settings').classList.add('st-searching');
    let matches = 0;
    document.querySelectorAll('#page-settings .st-section').forEach(section => {
        if (section.classList.contains('st-head')) { section.hidden = true; return; }
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
    renderSaveBar();
}

/* ===== Storage: disk, engine, how long events are kept ===== */

const STORAGE_POLL_MS = 1000;
let storagePoll = null;

function storageTypeLabel(type) {
    return type === 'SQLite' ? 'SQLite file' : type === 'PostgreSQL' ? 'PostgreSQL database' : type === 'Cabinet' ? 'Cabinet engine folder' : type;
}

function editedValue(key) {
    const setting = findSetting(key);
    if (!setting) return undefined;
    return Object.prototype.hasOwnProperty.call(settingsState.edits, key) ? settingsState.edits[key] : setting.value;
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
        document.getElementById('storageEngine').innerHTML = '';
        document.getElementById('storageKeep').innerHTML = '';
        document.getElementById('storageActions').hidden = true;
        return;
    }

    const type = overview ? overview.type : s.type;
    const movable = Boolean(overview && overview.movableTo.includes(type));
    const used = s.diskTotalBytes ? 1 - (s.diskFreeBytes / s.diskTotalBytes) : null;
    const since = s.oldestEvent ? new Date(/Z|[+-]\d\d:?\d\d$/.test(s.oldestEvent) ? s.oldestEvent : s.oldestEvent + 'Z').toLocaleDateString([], { month: 'short', day: 'numeric' }) : null;
    const size = s.sizeBytes !== null && s.sizeBytes !== undefined ? `<b>${esc(bytes(s.sizeBytes))}</b> used` : null;
    const free = s.diskFreeBytes ? `${esc(bytes(s.diskFreeBytes))} free` : null;
    const facts = [
        size && free ? `${size} of ${free}` : size || free,
        `${esc(compactCount(s.eventsStored))} events`,
        since ? `${esc(since)} – today` : 'no events yet',
    ].filter(Boolean);
    where.innerHTML = `<section class="group-panel st-disk">
        <h3 class="group-panel-title">Disk</h3>
        <div class="st-disk-line"><code class="st-disk-path" id="storageLocation">${esc(s.location || storageTypeLabel(type))}</code><span class="st-disk-facts">${facts.join(' · ')}</span></div>
        ${used !== null ? `<span class="st-meter" role="meter" aria-valuenow="${Math.round(used * 100)}" aria-valuemin="0" aria-valuemax="100" aria-label="Drive ${Math.round(used * 100)}% full"><span style="width:${(used * 100).toFixed(1)}%"></span></span>
            <span class="st-disk-note">The drive is ${esc(Math.round(used * 100))}% full (${esc(bytes(s.diskTotalBytes))} in all)${s.eventsStored && s.sizeBytes && s.diskFreeBytes ? `; room for about ${esc(compactCount(Math.floor(s.diskFreeBytes / (s.sizeBytes / s.eventsStored))))} more events at this size` : ''}.</span>` : ''}
        <div class="st-buttons">
            ${movable ? `<button type="button" class="btn" data-click="toggleStorageMove" aria-expanded="${settingsState.moving}" aria-controls="storageActions">${type === 'SQLite' ? 'Move to another drive…' : 'Move…'}</button>` : ''}
            <button type="button" class="btn" data-click="backupNow">Back up now</button>
            <button type="button" class="btn" data-click="copyStorageLocation">Copy location</button>
        </div>
        ${movable ? '' : `<p class="st-hint">Moving from ${esc(storageTypeLabel(type))} isn't done from here; ${type === 'Memory' ? 'nothing is kept to move' : 'use its own tools'}.</p>`}
    </section>`;

    const card = (label, value, detail) => `<div class="st-stat"><div class="st-stat-label">${esc(label)}</div><div class="st-stat-value">${value}</div>${detail ? `<div class="st-stat-detail">${detail}</div>` : ''}</div>`;
    const parts = [];
    if (s.metadataLocation) parts.push(card('Settings, rules and history', 'Kept in', `<code class="st-path">${esc(s.metadataLocation)}</code>`));
    if (d) {
        parts.push(card('Listening on', (d.listeningAddresses || []).map(a => `<code>${esc(a)}</code>`).join('<br>') || '-', 'Change with the installer (-Port, -Https)'));
        parts.push(card('Changes made on these pages', 'Settings file', `<code class="st-path">${esc(d.overridesFile)}</code>Outside the program folder, so upgrades keep it.`));
    }
    box.innerHTML = parts.join('');

    renderStorageEngine();
    renderStorageKeep();
    document.getElementById('storageActions').hidden = !(movable && settingsState.moving);
    document.getElementById('moveSqliteCard').hidden = type !== 'SQLite';
    document.getElementById('engineCard').hidden = type === 'Cabinet';
    renderStorageProgress(overview ? overview.migration : null);
}

function renderStorageEngine() {
    const box = document.getElementById('storageEngine');
    const setting = findSetting('Storage:Type');
    if (!setting) { box.innerHTML = ''; return; }
    const value = editedValue('Storage:Type');
    const current = String(setting.value || '').toLowerCase();
    const choices = STORAGE_ENGINES.filter(([key]) => key !== 'Memory' || current === 'memory' || String(value).toLowerCase() === 'memory');
    const staged = Object.prototype.hasOwnProperty.call(settingsState.edits, 'Storage:Type');
    box.innerHTML = `<section class="group-panel st-engine">
        <h3 class="group-panel-title">Storage engine</h3>
        <div class="st-engine-grid" role="radiogroup" aria-label="Storage engine">${choices.map(([key, label, words]) => `
            <label class="st-radio"><input type="radio" name="storageEngine" value="${esc(key)}"${String(value).toLowerCase() === key.toLowerCase() ? ' checked' : ''}${setting.editable ? '' : ' disabled'} data-change="onSettingChange" data-args="${esc(JSON.stringify(['Storage:Type', '$this']))}">
                <span><span class="st-radio-name">${esc(label)}</span><span class="st-radio-desc">${esc(words)}</span></span></label>`).join('')}
        </div>
        ${staged ? `<p class="st-engine-warn">Applying this only changes where new events go after a restart; it doesn't bring your events along. ${settingsState.storageOverview && settingsState.storageOverview.movableTo.includes(settingsState.storageOverview.type) ? '<button type="button" class="btn-link" data-click="openStorageMove">Move them instead…</button>' : ''}</p>` : ''}
        <p class="st-provenance">${provenanceHtml(setting)} · Switching with Move… pauses new events only for the switch</p>
    </section>`;
}

function renderStorageKeep() {
    const box = document.getElementById('storageKeep');
    const setting = findSetting('Storage:RetentionDays');
    if (!setting) { box.innerHTML = ''; return; }
    const value = editedValue('Storage:RetentionDays');
    box.innerHTML = `<div class="st-keep">
        <label class="st-keep-label" for="storageKeepDays">Keep events for</label>
        <span class="st-keep-control"><input type="number" id="storageKeepDays" min="${esc(setting.min ?? 0)}" max="${esc(setting.max ?? 36500)}" value="${esc(value ?? 0)}"${setting.editable ? '' : ' disabled'} data-change="onSettingChange" data-args="${esc(JSON.stringify(['Storage:RetentionDays', '$this']))}"><span>days</span></span>
        <span class="st-keep-desc">Older events are deleted${findSetting('Archive:Enabled') ? ', after being archived if archiving is on' : ''}. 0 keeps everything. Indexes with their own policy keep theirs (under Retention &amp; archive).</span>
        <span class="st-provenance">${provenanceHtml(setting)}</span>
    </div>`;
}

function toggleStorageMove() {
    settingsState.moving = !settingsState.moving;
    renderStorageOverview();
    if (settingsState.moving) document.getElementById('storageActions').scrollIntoView({ block: 'start', behavior: 'smooth' });
}

function openStorageMove() {
    settingsState.moving = true;
    renderStorageOverview();
    document.getElementById('storageActions').scrollIntoView({ block: 'start', behavior: 'smooth' });
}

async function backupNow() {
    try {
        const backup = await api('POST', '/api/v1/backups');
        toast(`Backed up: ${backup.fileName || backup.filePath || 'done'} (${bytes(backup.sizeBytes)})`, 'success');
    } catch (e) {
        toast(e.message, 'error');
    }
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

function testEngineTarget() {
    const folder = document.getElementById('engineFolder').value.trim();
    if (!folder) { showResult('engineResult', false, 'Enter the folder, such as D:\\Cabinet\\data'); return; }
    return testStorage('Cabinet', folder, 'engineResult');
}

async function switchToEngine() {
    const folder = document.getElementById('engineFolder').value.trim();
    if (!folder) { showResult('engineResult', false, 'Enter the folder'); return; }
    if (!confirm(`Copy everything into the Cabinet engine at ${folder} and switch to it? Cabinet keeps running while it copies, then restarts on the engine. The current database is left as it is, so you can switch back.`)) return;
    await startMove({ type: 'Cabinet', connectionString: folder, copy: true }, 'engineResult');
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
            ${status.state === 'WaitingForRestart' && settingsState.data && settingsState.data.canRestart ? '<button type="button" class="btn btn-sm" data-click="restartServer">Restart now</button>' : ''}
        </div>`;
    document.getElementById('storageActions').hidden = !settingsState.moving || running || status.state === 'WaitingForRestart' || !(settingsState.storageOverview && settingsState.storageOverview.movableTo.includes(settingsState.storageOverview.type));
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
    if (setting.type === 'Secret') return 'Default: not set';
    if (value === null || value === undefined || value === '') return 'Default: none';
    if (setting.type === 'Bool') return `Default: ${value ? 'on' : 'off'}`;
    if (Array.isArray(value)) return value.length ? `Default: ${value.join(', ')}` : 'Default: none';
    if (typeof value === 'object') return 'Default: ' + JSON.stringify(value);
    const number = typeof value === 'number' ? fmt(value) : value;
    return `Default: ${number}${setting.unit ? ' ' + setting.unit : ''}`;
}

/**
 * What a setting defaults to, where its value comes from, and when a change takes effect:
 * "Default: SQLite · Set in appsettings.Production.json as Storage:Type · Needs a restart".
 */
function provenanceHtml(setting) {
    const key = `<code>${esc(setting.key)}</code>`;
    const name = setting.sourceName ? `<code>${esc(setting.sourceName)}</code>` : null;
    const where = {
        default: `Not set anywhere, so the default applies (${key})`,
        appsettings: `Set in ${name || '<code>appsettings.json</code>'} as ${key}`,
        'settings-file': `Set on this page, saved in ${name || 'the settings file'} as ${key}`,
        environment: `Set by the environment variable ${name || key}, which wins over this page`,
    }[setting.source] || `Set as ${key}`;
    const when = setting.requiresRestart ? 'Needs a restart' : 'Takes effect without a restart';
    return `${esc(describeDefault(setting))} · ${where} · ${when}`;
}

function controlFor(setting) {
    const key = setting.key;
    const id = 'st-' + key.replace(/[^A-Za-z0-9]/g, '-');
    const edited = Object.prototype.hasOwnProperty.call(settingsState.edits, key);
    const reverting = edited && settingsState.edits[key] === null && setting.type !== 'Secret';
    const value = reverting ? setting.default : edited ? settingsState.edits[key] : setting.value;
    const disabled = setting.editable ? '' : ' disabled';
    const args = esc(JSON.stringify([key, '$this']));
    const range = `${setting.min !== null && setting.min !== undefined ? ` min="${esc(setting.min)}"` : ''}${setting.max !== null && setting.max !== undefined ? ` max="${esc(setting.max)}"` : ''}`;

    switch (setting.type) {
        case 'Bool':
            return `<label class="check-label st-switch"><input type="checkbox" id="${id}"${value ? ' checked' : ''}${disabled} data-change="onSettingChange" data-args="${args}"><span class="st-switch-text">${value ? 'On' : 'Off'}</span></label>`;
        case 'Int':
        case 'Long':
        case 'Double':
            return `<span class="st-number"><input type="number" id="${id}" value="${esc(value ?? '')}"${range}${setting.type === 'Double' ? ' step="any"' : ''}${disabled} data-change="onSettingChange" data-args="${args}">${setting.unit ? `<span class="st-unit">${esc(setting.unit)}</span>` : ''}</span>`;
        case 'Enum':
            return `<select id="${id}"${disabled} data-change="onSettingChange" data-args="${args}">${(setting.options || []).map(o => `<option value="${esc(o)}"${String(o).toLowerCase() === String(value).toLowerCase() ? ' selected' : ''}>${esc(o)}</option>`).join('')}</select>`;
        case 'List':
            return `<textarea id="${id}" rows="${Math.min(8, Math.max(2, (value || []).length + 1))}" placeholder="One per line"${disabled} data-change="onSettingChange" data-args="${args}">${esc(displayList(value))}</textarea>`;
        case 'Json':
            return `<textarea id="${id}" class="st-json" rows="${Math.min(14, Math.max(3, displayJson(value).split('\n').length + 1))}" spellcheck="false"${disabled} data-change="onSettingChange" data-args="${args}">${esc(displayJson(value))}</textarea>`;
        case 'Secret': {
            const pendingClear = edited && settingsState.edits[key] === null;
            const state = pendingClear ? 'will be removed when you apply' : edited ? 'will be replaced when you apply' : setting.isSet ? 'is set' : 'not set';
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
    const reverting = edited && settingsState.edits[key] === null && setting.type !== 'Secret';
    const error = settingsState.errors[key];
    const badges = [];
    if (reverting) badges.push('<span class="chip chip-warn">Goes back to the file or default when you apply</span>');
    else if (edited) badges.push('<span class="chip chip-warn">Not applied yet</span>');
    if (setting.pendingRestart) badges.push('<span class="chip chip-warn">Restart to apply</span>');
    if (setting.source === 'settings-file' && !edited) badges.push('<span class="chip chip-moss">Changed here</span>');
    const canReset = setting.editable && setting.source === 'settings-file' && !edited;
    const search = `${setting.label} ${setting.key} ${setting.description} ${groupOf(key) || ''}`.toLowerCase();
    return `<div class="st-row${edited ? ' st-row-edited' : ''}${error ? ' st-row-error' : ''}" data-key="${esc(key)}" data-search="${esc(search)}">
        <div class="st-row-text">
            <label class="st-label" for="${id}">${esc(setting.label)}</label>
            <div class="st-desc">${esc(setting.description)}</div>
            <div class="st-provenance">${provenanceHtml(setting)}${setting.min !== null && setting.min !== undefined && setting.max !== null && setting.max !== undefined ? ` · ${esc(fmt(setting.min))} to ${esc(fmt(setting.max))}` : ''}</div>
            ${badges.length ? `<div class="st-meta">${badges.join('')}</div>` : ''}
            ${error ? `<div class="st-error" role="alert">${esc(error)}</div>` : ''}
        </div>
        <div class="st-row-control">
            ${controlFor(setting)}
            ${canReset ? `<button type="button" class="btn-link st-reset" data-click="resetSetting" data-args="${esc(JSON.stringify([key]))}">Use the file or default value</button>` : ''}
            ${edited ? `<button type="button" class="btn-link st-reset" data-click="revertSetting" data-args="${esc(JSON.stringify([key]))}">Undo this change</button>` : ''}
        </div>
    </div>`;
}

function renderSettingsGroups() {
    const container = document.getElementById('settingsGroups');
    const groups = settingsState.data ? settingsState.data.groups : [];
    // In section order, so a section's groups follow one another
    const order = name => {
        const at = SETTINGS_SECTIONS.findIndex(s => s.groups.includes(name));
        return at < 0 ? 0 : at * 100 + SETTINGS_SECTIONS[at].groups.indexOf(name);
    };
    // Storage:Type is the Storage engine radios at the top of Storage, so it isn't repeated as a row
    container.innerHTML = groups.slice().sort((a, b) => order(a.name) - order(b.name)).map(group => `
        <section class="st-section st-group" data-in="${esc(sectionOfGroup(group.name))}" data-group="${esc(group.name)}" aria-label="${esc(group.name)}" hidden>
            <h3 class="st-group-title">${esc(group.name === 'Storage' ? 'More storage settings' : group.name)}</h3>
            ${group.description ? `<p class="st-group-intro">${esc(group.description)}</p>` : ''}
            <div class="st-rows">${group.settings.filter(setting => setting.key !== 'Storage:Type').map(settingRowHtml).join('')}</div>
        </section>`).join('');
}

function rerenderSetting(key) {
    const setting = findSetting(key);
    if (!setting) return;
    const row = document.querySelector(`#settingsGroups .st-row[data-key="${CSS.escape(key)}"]`);
    if (row) {
        const hidden = row.hidden;
        row.outerHTML = settingRowHtml(setting);
        const fresh = document.querySelector(`#settingsGroups .st-row[data-key="${CSS.escape(key)}"]`);
        if (fresh) fresh.hidden = hidden;
    }
    if (key === 'Storage:Type') renderStorageEngine();
    if (key === 'Storage:RetentionDays') renderStorageKeep();
}

/* ===== Editing: staged until Apply ===== */

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

    // Storage:Type and Storage:RetentionDays show in two places; keep both in step
    if (key === 'Storage:Type' || key === 'Storage:RetentionDays' || !element.closest('.st-row')) rerenderSetting(key);
    else {
        if (setting.type === 'Bool') {
            const text = element.closest('.st-switch').querySelector('.st-switch-text');
            if (text) text.textContent = element.checked ? 'On' : 'Off';
        }
        rerenderSettingKeepingFocus(key, element);
    }
    renderSaveBar();
    renderSettingsNav();
}

/** Redraws a row's badges without losing the control someone is typing in. */
function rerenderSettingKeepingFocus(key, element) {
    const row = element.closest('.st-row');
    if (!row) return;
    row.classList.toggle('st-row-edited', Object.prototype.hasOwnProperty.call(settingsState.edits, key));
    row.classList.toggle('st-row-error', Boolean(settingsState.errors[key]));
    const fresh = document.createElement('div');
    fresh.innerHTML = settingRowHtml(findSetting(key));
    const text = fresh.querySelector('.st-row-text');
    row.querySelector('.st-row-text').replaceWith(text);
    row.querySelectorAll('.st-reset').forEach(b => b.remove());
    fresh.querySelectorAll('.st-reset').forEach(b => row.querySelector('.st-row-control').appendChild(b));
}

function clearSecret(key) {
    settingsState.edits[key] = null;
    rerenderSetting(key);
    renderSaveBar();
    renderSettingsNav();
}

/** Stages going back to the appsettings value or the default, applied with the rest. */
function resetSetting(key) {
    settingsState.edits[key] = null;
    delete settingsState.errors[key];
    rerenderSetting(key);
    renderSaveBar();
    renderSettingsNav();
}

/** Drops one staged change. */
function revertSetting(key) {
    delete settingsState.edits[key];
    delete settingsState.errors[key];
    rerenderSetting(key);
    renderSaveBar();
    renderSettingsNav();
}

function renderSaveBar() {
    const bar = document.getElementById('settingsSaveBar');
    if (!bar) return;
    const keys = Object.keys(settingsState.edits);
    const count = keys.length;
    const errors = Object.keys(settingsState.errors).length;
    const searching = document.getElementById('page-settings').classList.contains('st-searching');
    const section = SETTINGS_SECTIONS.find(s => s.id === settingsState.section);
    const hasSettings = Boolean(settingsState.data) && (searching || (section && section.groups.length > 0));
    bar.hidden = !(hasSettings || count || errors);
    const restart = keys.filter(k => { const s = findSetting(k); return s && s.requiresRestart; }).length;
    let text = 'No changes';
    if (errors) text = `${errors} ${errors === 1 ? 'setting needs' : 'settings need'} fixing before anything can be applied`;
    else if (count) {
        const when = restart === 0 ? 'takes effect without a restart'
            : restart === count ? (count === 1 ? 'needs a restart' : 'need a restart')
            : `${restart} ${restart === 1 ? 'needs' : 'need'} a restart`;
        text = `${count} ${count === 1 ? 'change' : 'changes'} · ${when}`;
    }
    document.getElementById('settingsSaveText').textContent = text;
    document.getElementById('settingsSaveButton').disabled = errors > 0 || count === 0;
    document.getElementById('settingsRevertButton').disabled = count === 0 && errors === 0;
}

function discardSettings() {
    settingsState.edits = {};
    settingsState.errors = {};
    renderSettingsGroups();
    renderStorageEngine();
    renderStorageKeep();
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
            headers: { ...authHeaders(), 'Content-Type': 'application/json', Accept: 'application/json' },
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
            toast('Nothing was applied: fix the highlighted settings', 'error');
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
        showSettingsSection(section);
        toast(`Applied ${count} ${count === 1 ? 'change' : 'changes'}${body.restartPending ? '; restart Cabinet to finish' : ''}`, 'success');
    } catch (e) {
        toast('Applying failed: ' + e.message, 'error');
    } finally {
        renderSaveBar();
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
            ? '<button type="button" class="btn btn-sm" data-click="restartServer">Restart now</button>'
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
