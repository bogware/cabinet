/*
 * The Setup Assistant (Platinum 3b): on first run, helps someone who can configure the server get
 * logs in. It opens by itself when the server holds no events and the signed-in identity is an
 * Admin (or can manage users or indexes), never for a Viewer. "I'll do this later" remembers the
 * choice per identity in this browser; Sources has a button to open it again.
 *
 * Steps: choose a source (with the real install command), see logs arrive (polls the event count
 * and moves on by itself), ask a first question, invite your team. Handlers are named functions
 * wired through data-click, as the content security policy forbids inline script.
 */

const setupState = {
    step: 1,
    source: 'agent',
    os: 'linux',
    selfSigned: false,
    key: null,
    canMakeKeys: false,
    me: null,
    firstEvent: false,
    count: 0,
    poll: null,
    syslog: null,
    examples: [],
    sample: [],
};

const SETUP_STEPS = [
    ['Choose a source', 'Where your logs come from'],
    ['See logs arrive', 'We’ll confirm the first events'],
    ['Ask a first question', 'Starter questions from your data'],
    ['Invite your team', 'Optional'],
];

const SETUP_SOURCES = {
    agent: { name: 'Cabinet agent', tag: 'Recommended', desc: 'Files, systemd journal, Windows event logs and Kubernetes. Spools to disk, so restarts and outages lose nothing.' },
    hec: { name: 'Splunk HEC', desc: 'Already sending to Splunk? Point those senders here — nothing changes on their side.' },
    otel: { name: 'OpenTelemetry', desc: 'OTLP over HTTP. Logs, traces and metrics become searchable events.' },
    syslog: { name: 'Syslog', desc: 'Network devices and appliances. UDP, TCP or TLS.' },
};

function setupDismissKey() {
    return `cabinet_setup_later:${(setupState.me && setupState.me.clientId) || 'session'}`;
}

/** Whether to open by itself: no events yet, and someone who can configure the server. */
async function maybeShowSetup() {
    try {
        const res = await fetch('/api/v1/roles/me', { headers: { ...authHeaders(), Accept: 'application/json' } });
        if (!res.ok) return;
        setupState.me = await res.json();
        if (!canConfigure(setupState.me)) return;
        let dismissed = false;
        try { dismissed = localStorage.getItem(setupDismissKey()) === '1'; } catch (e) { console.warn('This browser would not say whether setup was put off', e); }
        if (dismissed) return;
        const stats = await api('GET', '/api/v1/logs/stats');
        if ((stats.totalEvents || 0) === 0) openSetupAssistant();
    } catch (e) {
        // Setup is a helper; the dashboard works without it
        console.warn('Could not tell whether to show setup', e);
    }
}

function canConfigure(me) {
    if (!me) return false;
    if (/^(admin|superadmin)$/i.test(me.role || '')) return true;
    return (me.additionalCapabilities || []).some(c => /^(UserManagement|IndexManagement|All)$/i.test(c));
}

/** Opens the assistant at the first step. Sources has a button for this. */
async function openSetupAssistant() {
    if (!setupState.me) {
        try {
            const res = await fetch('/api/v1/roles/me', { headers: { ...authHeaders(), Accept: 'application/json' } });
            if (res.ok) setupState.me = await res.json();
        } catch (e) { console.warn('Could not tell who is signed in', e); }
    }
    setupState.canMakeKeys = !!setupState.me && (/^(admin|superadmin)$/i.test(setupState.me.role || '') || (setupState.me.additionalCapabilities || []).some(c => /UserManagement/i.test(c)));
    setupState.step = 1;
    let root = document.getElementById('setupAssistant');
    if (!root) {
        root = document.createElement('div');
        root.id = 'setupAssistant';
        root.className = 'setup-bg';
        root.setAttribute('role', 'dialog');
        root.setAttribute('aria-modal', 'true');
        root.setAttribute('aria-labelledby', 'setupTitle');
        document.body.appendChild(root);
        root.addEventListener('keydown', onSetupKey);
    }
    root.hidden = false;
    renderSetup();
    startSetupPolling();
    loadSyslogSettings();
    const first = root.querySelector('.setup-card[aria-checked="true"]');
    if (first) first.focus();
}

function closeSetupAssistant() {
    const root = document.getElementById('setupAssistant');
    if (root) root.hidden = true;
    stopSetupPolling();
}

/** "I'll do this later": closes, and doesn't open by itself again for this identity. */
function setupLater() {
    try { localStorage.setItem(setupDismissKey(), '1'); } catch (e) { console.warn('This browser would not keep the choice', e); }
    closeSetupAssistant();
    toast('Set up a source any time from Sources.', 'info');
}

function onSetupKey(event) {
    if (event.key === 'Escape') { event.preventDefault(); setupLater(); return; }
    if (event.key === 'Tab') {
        const root = document.getElementById('setupAssistant');
        const focusable = [...root.querySelectorAll('button:not([disabled]),input,select,textarea,[tabindex="0"]')].filter(e => e.offsetParent !== null);
        if (!focusable.length) return;
        const first = focusable[0];
        const last = focusable[focusable.length - 1];
        if (event.shiftKey && document.activeElement === first) { last.focus(); event.preventDefault(); }
        else if (!event.shiftKey && document.activeElement === last) { first.focus(); event.preventDefault(); }
    }
    if ((event.key === 'ArrowDown' || event.key === 'ArrowRight' || event.key === 'ArrowUp' || event.key === 'ArrowLeft') && event.target.classList.contains('setup-card')) {
        event.preventDefault();
        const keys = Object.keys(SETUP_SOURCES);
        const at = keys.indexOf(setupState.source);
        const next = keys[(at + (event.key === 'ArrowDown' || event.key === 'ArrowRight' ? 1 : keys.length - 1)) % keys.length];
        chooseSetupSource(next);
        const card = document.querySelector(`.setup-card[data-source="${next}"]`);
        if (card) card.focus();
    }
}

/* ===== Waiting for the first event ===== */

function startSetupPolling() {
    stopSetupPolling();
    setupState.poll = setInterval(checkFirstEvent, 3000);
    checkFirstEvent();
}

function stopSetupPolling() {
    if (setupState.poll) { clearInterval(setupState.poll); setupState.poll = null; }
}

async function checkFirstEvent() {
    try {
        const stats = await api('GET', '/api/v1/logs/stats');
        setupState.count = stats.totalEvents || 0;
        if (setupState.count > 0 && !setupState.firstEvent) {
            setupState.firstEvent = true;
            stopSetupPolling();
            await loadSetupSample();
            // The page moves on by itself once something arrives
            if (setupState.step <= 2) setupState.step = 2;
            renderSetup();
        }
    } catch (e) {
        console.warn('Could not check for events', e);
    }
}

async function loadSetupSample() {
    try {
        const result = await api('POST', '/api/v1/query/execute', { query: 'find * | head 5', exact: true });
        setupState.sample = result.results || [];
    } catch (e) {
        setupState.sample = [];
    }
    try {
        const examples = await api('GET', '/api/v1/query/examples');
        setupState.examples = Array.isArray(examples) ? examples.slice(0, 6) : [];
    } catch (e) {
        setupState.examples = [];
    }
}

async function loadSyslogSettings() {
    try {
        const data = await api('GET', '/api/v1/settings');
        const list = Array.isArray(data) ? data : (data.settings || []);
        const value = key => { const s = list.find(x => x.key === key); return s ? (s.value ?? s.effectiveValue ?? s.default) : null; };
        setupState.syslog = { enabled: String(value('Syslog:Enabled')).toLowerCase() === 'true', udp: value('Syslog:UdpPort') || 1514, tcp: value('Syslog:TcpPort') || 1514, tls: value('Syslog:TlsPort') || 0 };
    } catch (e) {
        setupState.syslog = null;
    }
    if (setupState.step === 1 && setupState.source === 'syslog') renderSetup();
}

/* ===== What to run ===== */

const KEY_PLACEHOLDER = '<an Ingest API key>';
const PIN_PLACEHOLDER = '<SHA-256 fingerprint of this server\u2019s certificate>';

function setupCommand() {
    const origin = window.location.origin;
    const https = window.location.protocol === 'https:';
    const key = setupState.key || KEY_PLACEHOLDER;
    const pin = https && setupState.selfSigned;
    const host = window.location.hostname;
    switch (setupState.source) {
        case 'agent':
            if (setupState.os === 'windows') {
                return `.\\install-agent.ps1 -ServerUrl ${origin}${pin ? ` -CertificateSha256 "${PIN_PLACEHOLDER}"` : ''} -ApiKey "${key}" -Paths "C:\\logs\\*.log"`;
            }
            if (setupState.os === 'kubernetes') {
                return `# Build the agent image (it isn't published): docker build -t cabinet-agent -f Dockerfile.agent .\n` +
                    `# Run it as a DaemonSet with /var/log mounted, and these environment variables:\n` +
                    `Agent__Output__ServerUrl=${origin}\nAgent__Output__ApiKey=${key}\n` +
                    (pin ? `Agent__Output__CertificateSha256=${PIN_PLACEHOLDER}\n` : '') +
                    `Agent__Inputs__0__Type=kubernetes`;
            }
            return `sudo ./install-agent.sh --server ${origin}${pin ? ` --pin "${PIN_PLACEHOLDER}"` : ''} --api-key "${key}" --paths "/var/log/*.log"`;
        case 'hec':
            return `curl ${origin}/services/collector/event -H "Authorization: Splunk ${key}" -d '{"event": "hello from HEC", "sourcetype": "test"}'`;
        case 'otel':
            return `OTEL_EXPORTER_OTLP_ENDPOINT=${origin}\nOTEL_EXPORTER_OTLP_PROTOCOL=http/protobuf\nOTEL_EXPORTER_OTLP_HEADERS=X-API-Key=${key}`;
        case 'syslog': {
            const s = setupState.syslog;
            const udp = s ? s.udp : 1514;
            const tcp = s ? s.tcp : 1514;
            return `# Point devices at ${host}, UDP port ${udp} or TCP port ${tcp}${s && Number(s.tls) > 0 ? `, or TLS port ${s.tls}` : ''}. With rsyslog:\n*.* @@${host}:${tcp}`;
        }
        default:
            return '';
    }
}

function setupSourceNotes() {
    const https = window.location.protocol === 'https:';
    const notes = [];
    if (setupState.source === 'agent') {
        if (setupState.os === 'linux') notes.push('Run it as root from the folder you extracted cabinet-linux-x64.tar.gz into.');
        if (setupState.os === 'windows') notes.push('Run it in PowerShell as Administrator, from the extracted Windows release.');
        if (https) notes.push(setupState.selfSigned
            ? 'Find the fingerprint in your browser’s certificate viewer (the padlock), or with: openssl s_client -connect ' + window.location.host + ' </dev/null | openssl x509 -noout -fingerprint -sha256'
            : 'Using a certificate this server made itself? Tick the box below and the command pins it.');
        else notes.push('This page was opened over http, so the agent will send in the clear and won’t update itself. Use the server’s https address if it has one.');
    }
    if (setupState.source === 'hec') notes.push(`Senders use ${window.location.origin}/services/collector/event (or /raw) with the key as "Authorization: Splunk".`);
    if (setupState.source === 'otel') notes.push('Set these on the OpenTelemetry Collector or SDK that exports your logs. Protobuf and JSON both work.');
    if (setupState.source === 'syslog') {
        const s = setupState.syslog;
        if (!s) notes.push('Syslog has to be turned on under Settings, Syslog; your key may not be able to read that setting.');
        else if (!s.enabled) notes.push('Syslog is off on this server. Turn it on under Settings, Syslog, then point devices at it.');
        notes.push('Syslog senders aren’t authenticated: restrict these ports at the network.');
    }
    if (!setupState.key && setupState.source !== 'syslog') {
        notes.push(setupState.canMakeKeys
            ? 'Make a key for this source with the button above, so the command is ready to paste.'
            : 'Ask an admin for an Ingest API key (Admin, API keys) and put it where the command says.');
    }
    return notes;
}

/* ===== Drawing ===== */

function renderSetup() {
    const root = document.getElementById('setupAssistant');
    if (!root) return;
    const step = setupState.step;
    const rail = SETUP_STEPS.map(([label, sub], i) => {
        const n = i + 1;
        const state = n === step ? 'current' : n < step ? 'done' : '';
        return `<li class="setup-step ${state}"${n === step ? ' aria-current="step"' : ''}><span class="setup-num" aria-hidden="true">${n < step ? '&#10003;' : n}</span>` +
            `<span class="setup-step-text"><span class="setup-step-label">${esc(label)}</span><span class="setup-step-sub">${esc(sub)}</span></span></li>`;
    }).join('');

    let body = '';
    let primary = 'Continue';
    if (step === 1) body = setupStepSource();
    if (step === 2) body = setupStepArrive();
    if (step === 3) body = setupStepAsk();
    if (step === 4) { body = setupStepTeam(); primary = 'Finish'; }

    root.innerHTML = `<div class="setup-window">
        <div class="setup-titlebar"><img src="icon.svg" alt="" width="18" height="18"><span id="setupTitle">Set up Cabinet</span></div>
        <div class="setup-main">
            <nav class="setup-rail" aria-label="Setup steps"><ol>${rail}</ol><span class="setup-rail-note">You can change any of this later in Admin › Settings.</span></nav>
            <div class="setup-content">
                <div class="setup-body">${body}</div>
                <div class="setup-footer">
                    <button type="button" class="btn-link" data-click="setupLater">I’ll do this later</button>
                    <span class="sx-flex"></span>
                    ${step > 1 ? '<button type="button" class="btn setup-btn" data-click="setupBack">Go Back</button>' : ''}
                    <button type="button" class="btn btn-primary setup-btn" data-click="setupNext">${primary}</button>
                </div>
            </div>
        </div>
    </div>`;
}

function setupHead(n, title, sub) {
    return `<div class="setup-head"><span class="setup-kicker">Step ${n} of 4</span><h2>${esc(title)}</h2><p>${esc(sub)}</p></div>`;
}

function setupWaitingHtml() {
    return setupState.firstEvent
        ? `<div class="setup-arrived" role="status"><span class="dot ok"></span>Events are arriving: ${esc(fmt(setupState.count))} so far.</div>`
        : `<div class="loading-line setup-wait" role="status"><span class="progress" aria-hidden="true"></span><span>Waiting for the first event… this page will move on by itself.</span></div>`;
}

function setupStepSource() {
    const cards = Object.entries(SETUP_SOURCES).map(([id, s]) => {
        const on = id === setupState.source;
        return `<div class="setup-card${on ? ' on' : ''}" role="radio" aria-checked="${on}" tabindex="${on ? 0 : -1}" data-source="${id}" data-click="chooseSetupSource" data-args='${esc(JSON.stringify([id]))}'>` +
            `<span class="setup-radio" aria-hidden="true"></span><span class="setup-card-text"><span class="setup-card-name">${esc(s.name)}${s.tag ? ` <span class="setup-tag">${esc(s.tag)}</span>` : ''}</span>` +
            `<span class="setup-card-desc">${esc(s.desc)}</span></span></div>`;
    }).join('');
    const os = setupState.source === 'agent'
        ? `<div class="segmented" role="group" aria-label="Operating system">${[['linux', 'Linux'], ['windows', 'Windows'], ['kubernetes', 'Kubernetes']].map(([id, label]) =>
            `<button type="button" aria-pressed="${setupState.os === id}" data-click="chooseSetupOs" data-args='${esc(JSON.stringify([id]))}'>${label}</button>`).join('')}</div>`
        : '';
    const keyButton = setupState.source !== 'syslog' && setupState.canMakeKeys && !setupState.key
        ? '<button type="button" class="btn btn-sm" data-click="makeSetupKey">Make an Ingest key for this source</button>'
        : (setupState.key ? '<span class="setup-key-note">A new Ingest key is in the command. Copy it now: it isn’t shown again.</span>' : '');
    const pin = setupState.source === 'agent' && window.location.protocol === 'https:'
        ? `<label class="check-label"><input type="checkbox" id="setupSelfSigned" data-change="toggleSetupSelfSigned"${setupState.selfSigned ? ' checked' : ''}> The server uses its own (self-signed) certificate</label>`
        : '';
    const title = { agent: 'Run this on the machine with the logs', hec: 'Point your HEC senders here', otel: 'Point your exporter here', syslog: 'Point your devices here' }[setupState.source];
    return setupHead(1, 'How will your logs get here?', 'Pick one to start. You can add more sources any time — they all end up in the same place.') +
        `<div class="setup-cards" role="radiogroup" aria-label="Source">${cards}</div>` +
        `<section class="group-panel setup-run"><h3 class="group-panel-title">${esc(title)}</h3>` +
        `<div class="setup-run-row">${os}<span class="sx-flex"></span>${keyButton}</div>` +
        `<div class="setup-command"><code id="setupCommand">${esc(setupCommand())}</code><button type="button" class="btn btn-sm" data-click="copySetupCommand">Copy</button></div>` +
        `${pin}<p class="setup-notes">${esc(setupSourceNotes().join(' '))}</p>` +
        setupWaitingHtml() + `</section>`;
}

function setupStepArrive() {
    const sample = setupState.sample.map(row => `<li><span class="setup-sample-time">${esc(fmtTime(row.Timestamp))}</span><span class="setup-sample-host">${esc(row.Host || '')}</span><code>${esc(row.RawLine || '')}</code></li>`).join('');
    return setupHead(2, setupState.firstEvent ? 'Your logs are arriving' : 'Waiting for your logs', setupState.firstEvent
        ? 'Here are the newest events Cabinet has stored. Searching works on them now.'
        : 'Once the source sends its first event, it shows here and this page moves on.') +
        `<section class="group-panel setup-run"><h3 class="group-panel-title">${setupState.firstEvent ? 'Newest events' : 'Nothing yet'}</h3>` +
        (sample ? `<ul class="setup-sample">${sample}</ul>` : '') + setupWaitingHtml() +
        (setupState.firstEvent ? '' : '<p class="setup-hint">Nothing after a few minutes? Check the source can reach this server, and that its key has the Ingest capability. Or paste a few lines on the Sources page to try Cabinet now.</p>') +
        `</section>`;
}

function setupStepAsk() {
    const examples = setupState.examples.length
        ? setupState.examples
        : [{ text: 'errors by host in the last 24 hours' }, { text: 'find * | stats count by source_type' }, { text: 'events per minute over the last hour' }];
    const buttons = examples.map(e => `<button type="button" class="setup-question" data-click="setupAsk" data-args="${esc(JSON.stringify([e.text]))}">${esc(e.text)}${e.query && e.query !== e.text ? `<code>${esc(e.query)}</code>` : ''}</button>`).join('');
    return setupHead(3, 'Ask a first question', 'Ask in plain words; Cabinet shows how it understood you, in BQL, before the answer.') +
        `<section class="group-panel setup-run"><h3 class="group-panel-title">Try one of these</h3><div class="setup-questions">${buttons}</div></section>`;
}

function setupStepTeam() {
    return setupHead(4, 'Invite your team', 'Optional. People can sign in with single sign-on or local accounts; groups give them roles and the indexes they can see.') +
        `<section class="group-panel setup-run"><h3 class="group-panel-title">Users &amp; groups</h3>` +
        `<p class="setup-hint">Add people, or connect single sign-on and map its groups to roles. Viewers can search; Analysts can also save and alert; Admins can change settings.</p>` +
        `<div><button type="button" class="btn" data-click="setupOpenUsers">Open Users &amp; groups</button></div></section>`;
}

/* ===== Actions ===== */

function chooseSetupSource(id) {
    if (!SETUP_SOURCES[id]) return;
    setupState.source = id;
    renderSetup();
    const card = document.querySelector(`.setup-card[data-source="${id}"]`);
    if (card) card.focus();
}

function chooseSetupOs(os) {
    setupState.os = os;
    renderSetup();
}

function toggleSetupSelfSigned() {
    setupState.selfSigned = document.getElementById('setupSelfSigned').checked;
    renderSetup();
}

async function makeSetupKey() {
    try {
        const name = `${SETUP_SOURCES[setupState.source].name} (from setup)`;
        const created = await api('POST', '/api/v1/keys', { name, tier: 'Ingest' });
        setupState.key = created.key;
        renderSetup();
    } catch (e) {
        toast(`The key wasn't made: ${e.message}`, 'error');
    }
}

async function copySetupCommand() {
    const text = setupCommand();
    try { await navigator.clipboard.writeText(text); toast('Copied', 'success'); }
    catch (e) {
        const range = document.createRange();
        range.selectNodeContents(document.getElementById('setupCommand'));
        const selection = window.getSelection();
        selection.removeAllRanges();
        selection.addRange(range);
        toast('Press Ctrl+C to copy the selected command', 'info');
    }
}

function setupBack() {
    if (setupState.step > 1) { setupState.step--; renderSetup(); }
}

function setupNext() {
    if (setupState.step >= 4) {
        try { localStorage.setItem(setupDismissKey(), '1'); } catch (e) { console.warn('This browser would not keep the choice', e); }
        closeSetupAssistant();
        return;
    }
    setupState.step++;
    if (setupState.step === 3 && !setupState.examples.length) loadSetupSample().then(renderSetup);
    renderSetup();
}

function setupAsk(text) {
    closeSetupAssistant();
    navigate('search');
    setQuery(text);
    executeQuery();
}

function setupOpenUsers() {
    try { localStorage.setItem(setupDismissKey(), '1'); } catch (e) { console.warn('This browser would not keep the choice', e); }
    closeSetupAssistant();
    navigate('users');
}

document.addEventListener('DOMContentLoaded', () => {
    // Signed in by key or session: the dashboard's own sign-in comes first otherwise
    if (typeof API_KEY !== 'undefined' && API_KEY) maybeShowSetup();
    else fetch('/auth/providers', { headers: { Accept: 'application/json' } })
        .then(res => (res.ok ? res.json() : null))
        .then(p => { if (p && p.signedIn) maybeShowSetup(); })
        .catch(e => console.warn('Could not tell whether anyone is signed in', e));
});
