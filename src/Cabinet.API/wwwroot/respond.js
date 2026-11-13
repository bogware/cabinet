/*
 * Respond: detection rules with what they can see (ATT&CK coverage), the coverage page, Sigma
 * import, and cases as one timeline. Values from logs, rules and people go through esc();
 * handlers are named in data-click and data-change attributes.
 */

/* ===== Detection rules ===== */

let rulesById = {};
const rulesState = { rules: [], activity: {}, activityPartial: false, coverage: null, showAll: false, gap: null };
const RULES_SHOWN = 12;
const SEVERITIES = ['Info', 'Warning', 'Error', 'Critical'];

/** The ten tactics the rules page shows, in attack order, with the short names that fit a column. */
const MATRIX_TACTICS = [
    ['TA0001', 'Initial access'], ['TA0002', 'Execute'], ['TA0003', 'Persist'], ['TA0004', 'Priv. esc.'], ['TA0005', 'Evasion'],
    ['TA0006', 'Creds'], ['TA0007', 'Discover'], ['TA0008', 'Lateral'], ['TA0011', 'C2'], ['TA0010', 'Exfil'],
];

function severityChip(severity) {
    const s = String(severity || '');
    const cls = s === 'Critical' || s === 'Error' ? 'chip-danger' : s === 'Warning' ? 'chip-warn' : 'chip-muted';
    return `<span class="chip ${cls}">${esc(s || 'Unknown')}</span>`;
}

function relativeTime(ts) {
    if (!ts) return 'never';
    const d = new Date(typeof ts === 'string' && /^\d{4}-\d\d-\d\dT[\d:.]+$/.test(ts) ? ts + 'Z' : ts);
    const s = Math.round((Date.now() - d.getTime()) / 1000);
    if (s < 60) return 'just now';
    if (s < 3600) return `${Math.round(s / 60)} min ago`;
    if (s < 86400) return `${Math.round(s / 3600)} h ago`;
    if (s < 172800) return 'yesterday';
    return d.toLocaleDateString();
}

/** How often each rule fired and when last, from alert history. */
async function loadRuleActivity() {
    const activity = {};
    let partial = false;
    try {
        const res = await fetch('/api/v1/alerts?count=1000', { headers: { ...authHeaders(), Accept: 'application/json' } });
        if (!res.ok) return { activity, partial: true };
        const alerts = await res.json();
        partial = Number(res.headers.get('X-Total-Count') || alerts.length) > alerts.length;
        alerts.forEach(a => {
            if (!a.ruleId) return;
            const entry = activity[a.ruleId] || (activity[a.ruleId] = { fired: 0, last: null });
            entry.fired++;
            if (!entry.last || new Date(a.firedAt) > new Date(entry.last)) entry.last = a.firedAt;
        });
    } catch (e) {
        partial = true;
    }
    return { activity, partial };
}

async function loadRules() {
    const box = document.getElementById('rulesTable');
    box.innerHTML = `<div class="rules-empty">${loadingHtml('Loading detection rules…')}</div>`;
    document.getElementById('rulesCoverage').innerHTML = loadingHtml('Working out what your rules can see…');
    try {
        const [rules, coverage, activity] = await Promise.all([
            api('GET', '/api/v1/rules'),
            api('GET', '/api/v1/rules/coverage').catch(() => null),
            loadRuleActivity(),
        ]);
        rulesById = Object.fromEntries(rules.map(r => [r.id, r]));
        rulesState.rules = rules;
        rulesState.coverage = coverage;
        rulesState.activity = activity.activity;
        rulesState.activityPartial = activity.partial;
        renderRulesTable();
        renderRulesCoverage();
        setNavMeta('rules', `${rules.filter(r => r.enabled).length} on`);
    } catch (e) {
        box.innerHTML = `<div class="rules-empty">${errorHtml(`Detection rules couldn't be loaded: ${e.message}.`, 'Try again', 'loadRules')}</div>`;
        document.getElementById('rulesCoverage').innerHTML = '';
    }
}

function renderRulesTable() {
    const box = document.getElementById('rulesTable');
    const act = id => rulesState.activity[id] || { fired: 0, last: null };
    const sorted = rulesState.rules.slice().sort((a, b) =>
        Number(b.enabled) - Number(a.enabled) || act(b.id).fired - act(a.id).fired || String(a.name).localeCompare(String(b.name)));
    const shown = rulesState.showAll ? sorted : sorted.slice(0, RULES_SHOWN);
    if (sorted.length === 0) {
        box.innerHTML = '<div class="rules-empty">No rules yet. Choose New rule… or Import Sigma….</div>';
        return;
    }
    const rows = shown.map(rule => {
        const a = act(rule.id);
        const args = esc(JSON.stringify([rule.id]));
        return `<div class="rules-row${rule.enabled ? '' : ' rules-row-off'}" role="row">
            <span role="cell"><input type="checkbox"${rule.enabled ? ' checked' : ''} aria-label="${esc(rule.name)} is on" data-change="toggleRuleFromBox" data-args="${esc(JSON.stringify([rule.id, '$this']))}"></span>
            <button type="button" class="rules-name" role="cell" data-click="showRuleModal" data-args="${args}">
                <span class="rules-title">${esc(rule.name)}</span>
                <span class="rules-last">${a.last ? `Last fired ${esc(relativeTime(a.last))}` : 'Hasn’t fired'}${rule.technique ? ` · ${esc(rule.technique)}` : ''}</span>
            </button>
            <span role="cell">${severityChip(rule.severity)}</span>
            <span role="cell" class="rules-fired">${esc(fmt(a.fired))}</span>
        </div>`;
    }).join('');
    const on = sorted.filter(r => r.enabled).length;
    const more = sorted.length > RULES_SHOWN
        ? ` · <button type="button" class="btn-link" data-click="toggleAllRules">${rulesState.showAll ? `Show the first ${RULES_SHOWN}` : 'Show all'}</button>`
        : '';
    box.innerHTML = `<div class="rules-head" role="row"><span role="columnheader">On</span><span role="columnheader">Rule</span><span role="columnheader">Severity</span><span role="columnheader" class="rules-fired">Fired</span></div>
        <div class="rules-rows">${rows}</div>
        <div class="rules-foot">${shown.length} of ${sorted.length} shown · ${on} on${more}${rulesState.activityPartial ? '<br>Fired counts come from the most recent 1,000 alerts.' : ''}</div>`;
}

function toggleAllRules() {
    rulesState.showAll = !rulesState.showAll;
    renderRulesTable();
}

/** The share of known techniques with a rule that's on, and each tactic's cells. */
function coverageSummary(matrix) {
    const known = new Map();
    (matrix || []).filter(t => t.id !== 'other').forEach(t => t.techniques.forEach(tech => known.set(tech.id, tech)));
    const all = [...known.values()];
    const covered = all.filter(t => t.rules.some(r => r.enabled)).length;
    return { total: all.length, covered, percent: all.length ? Math.round((covered / all.length) * 100) : 0 };
}

function techniqueState(tech) {
    return tech.rules.some(r => r.enabled) ? 'on' : tech.rules.length ? 'off' : 'none';
}

/** The tactic with the least covered that a rule already written could help, and that rule. */
function biggestGap(matrix) {
    let best = null;
    MATRIX_TACTICS.forEach(([id]) => {
        const tactic = (matrix || []).find(t => t.id === id);
        if (!tactic || !tactic.techniques.length) return;
        const covered = tactic.techniques.filter(t => techniqueState(t) === 'on').length;
        const candidate = tactic.techniques.find(t => techniqueState(t) === 'off');
        const ratio = covered / tactic.techniques.length;
        const better = !best
            || (candidate && !best.candidate)
            || (Boolean(candidate) === Boolean(best.candidate) && ratio < best.ratio);
        if (better) best = { tactic, covered, ratio, candidate };
    });
    if (!best) return null;
    if (best.candidate) {
        const rule = best.candidate.rules.find(r => !r.enabled);
        return { ...best, rule };
    }
    return { ...best, missing: best.tactic.techniques.find(t => techniqueState(t) === 'none') };
}

function renderRulesCoverage() {
    const box = document.getElementById('rulesCoverage');
    const matrix = rulesState.coverage;
    if (!matrix) {
        box.innerHTML = errorHtml('Coverage couldn’t be worked out.', 'Try again', 'loadRules');
        return;
    }
    const summary = coverageSummary(matrix);
    const columns = MATRIX_TACTICS.map(([id, short]) => {
        const tactic = matrix.find(t => t.id === id);
        if (!tactic) return '';
        const cells = tactic.techniques.map(tech => {
            const state = techniqueState(tech);
            const words = state === 'on' ? 'covered' : state === 'off' ? 'a rule exists, turned off' : 'not covered';
            return `<button type="button" class="matrix-cell matrix-${state}" aria-label="${esc(`${tech.id} ${tech.name}: ${words}`)}" title="${esc(`${tech.id} ${tech.name}: ${words}`)}" data-click="showTechnique" data-args="${esc(JSON.stringify([tech.id]))}"></button>`;
        }).join('');
        return `<div class="matrix-col"><span class="matrix-tactic" title="${esc(tactic.name)}">${esc(short)}</span>${cells}</div>`;
    }).join('');
    const gap = biggestGap(matrix);
    rulesState.gap = gap;
    let gapHtml = '';
    if (gap && gap.rule) {
        gapHtml = `<b>${esc(gap.tactic.name)}</b> — ${gap.covered} of ${gap.tactic.techniques.length} techniques covered. Turning on “${esc(gap.rule.name)}” would cover ${esc(gap.candidate.id)} ${esc(gap.candidate.name)}.
            <button type="button" class="btn-link" data-click="backtestGap">Backtest it on last 30 days</button><div id="gapResult" aria-live="polite"></div>`;
    } else if (gap && gap.missing) {
        gapHtml = `<b>${esc(gap.tactic.name)}</b> — ${gap.covered} of ${gap.tactic.techniques.length} techniques covered, and no rule is written for ${esc(gap.missing.id)} ${esc(gap.missing.name)} yet.
            <button type="button" class="btn-link" data-click="showSigmaModal">Import Sigma rules…</button>`;
    }
    box.innerHTML = `<div class="cov-headline"><span class="cov-percent">${summary.percent}%</span><span>of common attacker techniques are covered by your turned-on rules (${summary.covered} of ${summary.total}).</span></div>
        <div class="cov-legend"><span><i class="matrix-key matrix-on"></i>Covered</span><span><i class="matrix-key matrix-off"></i>Rule exists, turned off</span><span><i class="matrix-key matrix-none"></i>Not covered</span>
            <button type="button" class="btn-link" data-click="navigate" data-args='["coverage"]'>All 14 tactics</button></div>
        <div class="matrix-panel"><div class="matrix">${columns}</div><p class="matrix-detail" id="matrixDetail" aria-live="polite">Choose a square to see the technique and the rules that cover it.</p></div>
        ${gapHtml ? `<section class="group-panel cov-gap"><h2 class="group-panel-title">Biggest gap</h2><div>${gapHtml}</div></section>` : ''}`;
}

function findTechnique(id) {
    for (const tactic of rulesState.coverage || []) {
        const tech = tactic.techniques.find(t => t.id === id);
        if (tech) return tech;
    }
    return null;
}

function showTechnique(id) {
    const tech = findTechnique(id);
    const box = document.getElementById('matrixDetail');
    if (!tech || !box) return;
    box.innerHTML = `<b>${esc(tech.id)} ${esc(tech.name)}</b>: ${tech.rules.length
        ? tech.rules.map(r => `<button type="button" class="btn-link" data-click="showRuleModal" data-args="${esc(JSON.stringify([r.id]))}">${esc(r.name)}</button> (${r.enabled ? 'on' : 'off'})`).join(', ')
        : 'no rule covers it yet.'}`;
}

async function backtestRule(ruleId, out) {
    out.innerHTML = loadingHtml('Running the rule over the last 30 days of events…');
    try {
        const r = await api('POST', '/api/v1/rules/backtest', { ruleId, hours: 24 * 30 });
        const fired = r.wouldFire === 0 ? 'It wouldn’t have fired' : `It would have fired ${fmt(r.wouldFire)} ${r.wouldFire === 1 ? 'time' : 'times'}`;
        const top = (r.topGroups || []).slice(0, 3).map(g => `${esc(g.group || '(all)')} ${fmt(g.fires)}×`).join(', ');
        out.innerHTML = `<p class="backtest-line">${fired} over the last 30 days, on ${esc(fmt(r.matches))} matching events of ${esc(fmt(r.eventsScanned))} read${r.truncated ? ' (it stopped at its limit, so there may be more)' : ''}.${top ? ` Most for ${top}.` : ''}</p>`;
        return r;
    } catch (e) {
        out.innerHTML = errorHtml(`The backtest didn't run: ${e.message}.`);
        return null;
    }
}

async function backtestGap() {
    const gap = rulesState.gap;
    const out = document.getElementById('gapResult');
    if (!gap || !gap.rule || !out) return;
    const r = await backtestRule(gap.rule.id, out);
    if (r) out.insertAdjacentHTML('beforeend', `<button type="button" class="btn btn-sm" data-click="toggleRule" data-args="${esc(JSON.stringify([gap.rule.id, true]))}">Turn on “${esc(gap.rule.name)}”</button>`);
}

function toggleRuleFromBox(id, box) {
    toggleRule(id, box.checked);
}

async function toggleRule(id, enabled) {
    try {
        await api('POST', `/api/v1/rules/${encodeURIComponent(id)}/${enabled ? 'enable' : 'disable'}`);
        const rule = rulesById[id];
        toast(`${rule ? rule.name : 'Rule'} is ${enabled ? 'on' : 'off'}`, 'success');
        loadRules();
    } catch (e) { toast('Failed: ' + e.message, 'error'); loadRules(); }
}

async function deleteRule(id) {
    id = typeof id === 'string' ? id : document.getElementById('ruleEditId').value;
    const rule = rulesById[id];
    if (!confirm(`Delete the rule "${rule ? rule.name : ''}"? This can't be undone.`)) return;
    try {
        await api('DELETE', `/api/v1/rules/${encodeURIComponent(id)}`);
        closeModal('ruleModal');
        toast('Rule deleted', 'success');
        loadRules();
    } catch (e) { toast('Failed: ' + e.message, 'error'); }
}

const RULE_FIELDS = ['ruleName', 'ruleSeverity', 'rulePattern', 'ruleCondition', 'ruleExtract', 'ruleGroupBy', 'ruleThreshold', 'ruleWindow', 'ruleCooldown', 'ruleTechnique', 'ruleDescription'];

function showRuleModal(id) {
    const rule = (typeof id === 'string' && rulesById[id]) || {};
    const builtin = Boolean(rule.builtin);
    document.getElementById('ruleEditId').value = rule.id || '';
    document.getElementById('ruleModalTitle').textContent = rule.id ? (builtin ? 'Built-in rule' : 'Edit rule') : 'New rule';
    document.getElementById('ruleName').value = rule.name || '';
    document.getElementById('ruleSeverity').value = rule.severity || 'Warning';
    document.getElementById('rulePattern').value = rule.pattern || '';
    document.getElementById('ruleCondition').value = rule.condition || '';
    document.getElementById('ruleExtract').value = rule.extract || '';
    document.getElementById('ruleGroupBy').value = rule.groupBy || '';
    document.getElementById('ruleThreshold').value = rule.threshold || 1;
    document.getElementById('ruleWindow').value = rule.windowSeconds || 300;
    document.getElementById('ruleCooldown').value = rule.cooldownSeconds ?? 300;
    document.getElementById('ruleTechnique').value = rule.technique || '';
    document.getElementById('ruleDescription').value = rule.description || '';
    RULE_FIELDS.forEach(f => { document.getElementById(f).disabled = builtin; });
    renderRuleActions(rule.actions);
    document.querySelectorAll('#ruleActions input, #ruleActions select, #ruleActions textarea, #ruleActions button, #ruleAddAction').forEach(el => { el.disabled = builtin; });
    document.getElementById('ruleBuiltinNote').hidden = !builtin;
    document.getElementById('ruleSaveButton').hidden = builtin;
    document.getElementById('ruleDeleteButton').hidden = !rule.id || builtin;
    document.getElementById('ruleTestResult').textContent = '';
    document.getElementById('ruleBacktestResult').innerHTML = '';
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
        technique: text('ruleTechnique'),
        description: text('ruleDescription'),
        actions: readRuleActions(),
        enabled: (rulesById[document.getElementById('ruleEditId').value] || { enabled: true }).enabled,
    };
}

function renderRuleActions(actions) {
    document.getElementById('ruleActions').innerHTML = '';
    (actions || []).forEach(addRuleAction);
}

function addRuleAction(action) {
    action = (action && typeof action === 'object' && 'type' in action) ? action : { type: 'notify' };
    const row = document.createElement('div');
    row.className = 'rule-action';
    const sevOptions = ['<option value="">(rule severity)</option>'].concat(SEVERITIES.map(s => `<option${action.severity === s ? ' selected' : ''}>${s}</option>`)).join('');
    row.innerHTML = `
        <select class="ra-type" aria-label="What to do">
            <option value="notify"${action.type === 'notify' ? ' selected' : ''}>Notify channels</option>
            <option value="event"${action.type === 'event' ? ' selected' : ''}>Write an event</option>
            <option value="webhook"${action.type === 'webhook' ? ' selected' : ''}>Call a webhook</option>
            <option value="connector"${action.type === 'connector' ? ' selected' : ''}>Send to connector</option>
        </select>
        <input class="ra-target" aria-label="Target" value="${esc(action.target || '')}">
        <select class="ra-sev" aria-label="Severity">${sevOptions}</select>
        <input class="ra-msg" aria-label="Message" placeholder="Message (optional)" value="${esc(action.message || '')}">
        <textarea class="ra-fields" aria-label="Event fields" placeholder="Event fields: field={group} per line">${esc(fieldsToText(action.fields))}</textarea>
        <button type="button" class="btn btn-danger btn-sm ra-del">Remove</button>`;
    document.getElementById('ruleActions').appendChild(row);
    const type = row.querySelector('.ra-type');
    const apply = () => {
        const t = type.value;
        row.querySelector('.ra-target').style.display = t === 'notify' ? 'none' : '';
        row.querySelector('.ra-target').placeholder = t === 'webhook' ? 'https://your-webhook-url' : t === 'connector' ? 'Connector name (see Connectors)' : 'Index for the event (default detections)';
        row.querySelector('.ra-fields').style.display = t === 'event' ? '' : 'none';
    };
    type.addEventListener('change', apply);
    row.querySelector('.ra-del').addEventListener('click', () => row.remove());
    apply();
}

function fieldsToText(fields) {
    return fields ? Object.entries(fields).map(([k, v]) => `${k}=${v}`).join('\n') : '';
}

function readRuleActions() {
    return [...document.querySelectorAll('#ruleActions .rule-action')].map(row => {
        const type = row.querySelector('.ra-type').value;
        const action = { type };
        const target = row.querySelector('.ra-target').value.trim();
        const sev = row.querySelector('.ra-sev').value;
        const msg = row.querySelector('.ra-msg').value.trim();
        if (type !== 'notify' && target) action.target = target;
        if (sev) action.severity = sev;
        if (msg) action.message = msg;
        if (type === 'event') {
            const fields = {};
            row.querySelector('.ra-fields').value.split('\n').forEach(line => {
                const i = line.indexOf('=');
                if (i > 0) fields[line.slice(0, i).trim()] = line.slice(i + 1).trim();
            });
            if (Object.keys(fields).length) action.fields = fields;
        }
        return action;
    });
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

/** Backtests the rule as it stands in the form: saved ones by ID, changes as they are. */
async function backtestRuleForm() {
    const id = document.getElementById('ruleEditId').value;
    const rule = rulesById[id];
    const out = document.getElementById('ruleBacktestResult');
    if (rule && rule.builtin) { backtestRule(id, out); return; }
    out.innerHTML = loadingHtml('Running the rule over the last 30 days of events…');
    try {
        const r = await api('POST', '/api/v1/rules/backtest', { rule: { ...readRuleForm(), name: document.getElementById('ruleName').value.trim() || 'Backtest' }, hours: 24 * 30 });
        out.innerHTML = `<p class="backtest-line">${r.wouldFire === 0 ? 'It wouldn’t have fired' : `It would have fired ${fmt(r.wouldFire)} ${r.wouldFire === 1 ? 'time' : 'times'}`} over the last 30 days, on ${esc(fmt(r.matches))} matching events of ${esc(fmt(r.eventsScanned))} read${r.truncated ? ' (it stopped at its limit)' : ''}.</p>`;
    } catch (e) { out.innerHTML = errorHtml(`The backtest didn't run: ${e.message}.`); }
}

async function saveRule() {
    const id = document.getElementById('ruleEditId').value;
    const rule = readRuleForm();
    try {
        if (id) await api('PUT', `/api/v1/rules/${encodeURIComponent(id)}`, rule);
        else await api('POST', '/api/v1/rules', rule);
        closeModal('ruleModal');
        toast('Rule saved', 'success');
        if (currentPage === 'coverage') loadCoverage(); else loadRules();
    } catch (e) { toast('Failed: ' + e.message, 'error'); }
}

/* ===== ATT&CK coverage page ===== */
window.EXTRA_PAGES.coverage = { title: 'ATT&CK coverage', load: () => loadCoverage() };

async function loadCoverage() {
    const box = document.getElementById('coverageMatrix');
    if (!box) return;
    box.innerHTML = loadingHtml('Working out what your rules can see…');
    try {
        const matrix = await api('GET', '/api/v1/rules/coverage');
        rulesState.coverage = matrix;
        const summary = coverageSummary(matrix);
        document.getElementById('coverageSummary').innerHTML = `<span class="cov-percent">${summary.percent}%</span><span>of common attacker techniques are covered by your turned-on rules (${summary.covered} of ${summary.total}).</span>`;
        box.innerHTML = matrix.map(tactic => {
            const covered = tactic.techniques.filter(t => techniqueState(t) === 'on').length;
            const cells = tactic.techniques.map(t => {
                const state = techniqueState(t);
                const title = t.rules.length ? t.rules.map(r => `${r.name}${r.enabled ? '' : ' (off)'}`).join('\n') : 'No rule';
                return `<button type="button" class="tech-cell matrix-${state}" title="${esc(title)}" data-click="showCoverageTechnique" data-args="${esc(JSON.stringify([t.id]))}"><span class="mono">${esc(t.id)}</span> ${esc(t.name)}${t.rules.length ? ` <b>(${t.rules.length})</b>` : ''}</button>`;
            }).join('');
            return `<div class="tech-col"><div class="tech-tactic">${esc(tactic.name)} <span class="muted">${covered}/${tactic.techniques.length}</span></div>${cells}</div>`;
        }).join('');
    } catch (e) { box.innerHTML = errorHtml(`Coverage couldn't be loaded: ${e.message}.`, 'Try again', 'loadCoverage'); }
}

function showCoverageTechnique(id) {
    const tech = findTechnique(id);
    const box = document.getElementById('coverageDetail');
    if (!tech || !box) return;
    box.innerHTML = `<b>${esc(tech.id)} ${esc(tech.name)}</b>: ${tech.rules.length
        ? tech.rules.map(r => `${esc(r.name)} (${r.enabled ? 'on' : 'off'}) <button type="button" class="btn btn-sm" data-click="toggleCoverageRule" data-args="${esc(JSON.stringify([r.id, !r.enabled]))}">${r.enabled ? 'Turn off' : 'Turn on'}</button>`).join(' · ')
        : 'no rule covers it yet. Import a Sigma rule for it, or write one with New rule….'}`;
}

async function toggleCoverageRule(id, enabled) {
    try {
        await api('POST', `/api/v1/rules/${encodeURIComponent(id)}/${enabled ? 'enable' : 'disable'}`);
        await loadCoverage();
    } catch (e) { toast('Failed: ' + e.message, 'error'); }
}

/* ===== Sigma import ===== */

function showSigmaModal() {
    document.getElementById('sigmaResult').innerHTML = '';
    openModal('sigmaModal');
}

async function importSigma(save) {
    const out = document.getElementById('sigmaResult');
    const yaml = document.getElementById('sigmaYaml').value;
    if (!yaml.trim()) { out.innerHTML = errorHtml('Paste one or more Sigma rules first.'); return; }
    out.innerHTML = loadingHtml('Reading the Sigma rules…');
    try {
        const res = await fetch('/api/v1/rules/sigma' + (save ? '?save=true' : ''), {
            method: 'POST',
            headers: { ...authHeaders(), 'Content-Type': 'text/plain', Accept: 'application/json' },
            body: yaml,
        });
        if (!res.ok) {
            let message = `HTTP ${res.status}`;
            try { const body = await res.json(); message = body.detail || body.title || message; } catch (e) { /* not JSON */ }
            throw new Error(message);
        }
        const results = await res.json();
        out.innerHTML = `<ul class="sigma-results">${results.map(r => `<li><strong>${esc(r.title)}</strong>: ${r.problem ? `<span class="sigma-problem">${esc(r.problem)}</span>`
            : `${r.saved ? 'added, turned off' : 'converts'} (${esc(r.severity || '')}${r.technique ? ', ' + esc(r.technique) : ''})<div class="mono sigma-condition">${esc(r.condition || '')}</div>`}</li>`).join('')}</ul>`;
        if (save) { if (currentPage === 'coverage') loadCoverage(); else if (currentPage === 'rules') loadRules(); }
    } catch (e) { out.innerHTML = errorHtml(`The Sigma rules couldn't be read: ${e.message}.`); }
}

/* ===== Cases ===== */
window.EXTRA_PAGES.cases = { title: 'Cases', load: () => loadCases() };

const CASE_STATUSES = ['Open', 'Investigating', 'Resolved', 'Closed'];
const caseState = { current: null, timeline: [], hidden: 0, techniques: [] };

function caseLabel(c) {
    return `${c.number ? `#${c.number} ` : ''}${c.title}`;
}

function statusChip(status) {
    const cls = status === 'Open' ? 'chip-warn' : status === 'Investigating' ? 'chip-moss' : status === 'Resolved' ? 'chip-success' : 'chip-muted';
    return `<span class="chip ${cls}">${esc(status)}</span>`;
}

function setCaseToolbar(c) {
    const viewing = Boolean(c);
    document.getElementById('caseCrumb').hidden = !viewing;
    document.getElementById('caseListActions').hidden = viewing;
    document.getElementById('caseViewActions').hidden = !viewing;
    document.getElementById('caseList').hidden = viewing;
    document.getElementById('caseView').hidden = !viewing;
    document.getElementById('caseTitle').textContent = viewing ? caseLabel(c) : 'Cases';
    document.getElementById('casePurpose').hidden = viewing;
    if (viewing) document.getElementById('caseCloseButton').textContent = c.status === 'Closed' ? 'Reopen case' : 'Close case…';
}

async function loadCases() {
    caseState.current = null;
    setCaseToolbar(null);
    const tbody = document.getElementById('casesBody');
    if (!tbody) return;
    const status = document.getElementById('caseStatusFilter').value;
    tbody.innerHTML = `<tr><td colspan="6" class="cases-empty">${loadingHtml('Loading cases…')}</td></tr>`;
    try {
        const cases = await api('GET', '/api/v1/cases' + (status ? '?status=' + encodeURIComponent(status) : ''));
        tbody.innerHTML = cases.length === 0
            ? `<tr><td colspan="6" class="cases-empty">${status ? `No ${esc(status.toLowerCase())} cases.` : 'No cases yet. Choose New case…, or add events to a case from a search.'}</td></tr>`
            : cases.map(c => `<tr class="clickable" tabindex="0" data-click="openCase" data-args="${esc(JSON.stringify([c.id]))}">
                <td class="cases-num">${c.number ? `#${esc(c.number)}` : ''}</td>
                <td><span class="cases-title">${esc(c.title)}</span><span class="cases-sub">${esc(fmt((c.events || []).length))} events · ${esc(fmt((c.alertIds || []).length))} alerts · ${esc(fmt((c.notes || []).length))} notes</span></td>
                <td>${statusChip(c.status)}</td><td>${severityChip(c.severity)}</td><td>${esc(c.assignee || 'Nobody')}</td><td>${esc(relativeTime(c.updatedAt))}</td></tr>`).join('');
    } catch (e) { tbody.innerHTML = `<tr><td colspan="6">${errorHtml(`Cases couldn't be loaded: ${e.message}.`, 'Try again', 'loadCases')}</td></tr>`; }
}

function newCase() {
    document.getElementById('newCaseTitle').value = '';
    document.getElementById('newCaseDescription').value = '';
    document.getElementById('newCaseSeverity').value = 'Warning';
    openModal('newCaseModal');
}

async function createCase() {
    const title = document.getElementById('newCaseTitle').value.trim();
    if (!title) { toast('Give the case a title', 'error'); return; }
    try {
        const c = await api('POST', '/api/v1/cases', {
            title,
            severity: document.getElementById('newCaseSeverity').value,
            description: document.getElementById('newCaseDescription').value.trim() || null,
        });
        closeModal('newCaseModal');
        openCase(c.id);
    } catch (e) { toast('Failed: ' + e.message, 'error'); }
}

async function openCase(id) {
    if (currentPage !== 'cases') navigate('cases');
    const view = document.getElementById('caseView');
    setCaseToolbar({ title: 'Loading…', status: 'Open' });
    view.innerHTML = loadingHtml('Loading this case…');
    try {
        const [c, timelineResponse] = await Promise.all([
            api('GET', '/api/v1/cases/' + encodeURIComponent(id)),
            fetch(`/api/v1/cases/${encodeURIComponent(id)}/timeline`, { headers: { ...authHeaders(), Accept: 'application/json' } }),
        ]);
        if (!timelineResponse.ok) throw new Error(`The timeline didn't load (HTTP ${timelineResponse.status})`);
        caseState.current = c;
        caseState.timeline = await timelineResponse.json();
        caseState.hidden = Number(timelineResponse.headers.get('X-Hidden-Events') || 0);
        caseState.techniques = await caseTechniques(c);
        setCaseToolbar(c);
        renderCase();
    } catch (e) {
        view.innerHTML = errorHtml(`This case couldn't be opened: ${e.message}.`, 'Back to cases', 'loadCases');
    }
}

/** Techniques of the case's alerts, so the details can name them. */
async function caseTechniques(c) {
    const ids = (c.alertIds || []).slice(0, 10);
    const alerts = await Promise.all(ids.map(id => api('GET', '/api/v1/alerts/' + encodeURIComponent(id)).catch(() => null)));
    return [...new Set(alerts.filter(a => a && a.technique).map(a => a.technique))];
}

/** A ticket reference such as SEC-1183 in the case's notes or history. */
function caseTicket(c) {
    const text = [c.description || ''].concat((c.notes || []).map(n => n.text), (c.history || []).map(h => h.text)).join('\n');
    const match = /\b[A-Z][A-Z0-9]{1,9}-\d{1,7}\b/.exec(text);
    return match ? match[0] : null;
}

const TIMELINE_KINDS = { alert: 'Alert', search: 'Search', note: 'Note', change: 'Action', event: 'Event' };

function timelineTime(ts) {
    const d = new Date(typeof ts === 'string' && /^\d{4}-\d\d-\d\dT[\d:.]+$/.test(ts) ? ts + 'Z' : ts);
    const time = d.toLocaleTimeString([], { hour: 'numeric', minute: '2-digit' });
    return d.toDateString() === new Date().toDateString() ? time : `${d.toLocaleDateString([], { month: 'short', day: 'numeric' })}, ${time}`;
}

function timelineItemHtml(t) {
    const kind = TIMELINE_KINDS[t.kind] || t.kind;
    let title = '';
    let body = '';
    let actions = '';
    switch (t.kind) {
        case 'alert': {
            const at = t.text.indexOf(': ');
            title = at > 0 ? t.text.slice(0, at) : t.text;
            body = at > 0 ? t.text.slice(at + 2) : '';
            break;
        }
        case 'search':
            title = `<span class="mono">${esc(t.text)}</span>`;
            body = t.by ? `Kept by ${t.by}` : '';
            actions = `<button type="button" class="btn-link" data-click="runCaseSearch" data-args="${esc(JSON.stringify([t.text]))}">Run it here</button> · <button type="button" class="btn-link" data-click="showPanelEvents" data-args="${esc(JSON.stringify([t.text]))}">Open in Search</button>`;
            break;
        case 'note':
            title = t.by || 'Note';
            body = t.text;
            break;
        case 'change':
            title = t.text;
            body = t.by ? `By ${t.by}` : '';
            break;
        default:
            title = [t.host, t.index].filter(Boolean).join(' · ') || 'Event';
            body = `<span class="mono">${esc(t.text)}</span>`;
            break;
    }
    const titleHtml = t.kind === 'search' ? title : esc(title);
    const bodyHtml = t.kind === 'event' ? body : esc(body);
    return `<li class="tl-item tl-${esc(t.kind)}">
        <span class="tl-dot" aria-hidden="true"></span>
        <time class="tl-time" datetime="${esc(t.time)}">${esc(timelineTime(t.time))}</time>
        <div class="tl-card">
            <div class="tl-head"><span class="tl-kind">${esc(kind)}</span><span class="tl-title">${titleHtml}</span></div>
            ${body ? `<div class="tl-body">${bodyHtml}</div>` : ''}
            ${actions ? `<div class="tl-actions">${actions}</div>` : ''}
        </div>
    </li>`;
}

function renderCase() {
    const c = caseState.current;
    const view = document.getElementById('caseView');
    const id = esc(JSON.stringify([c.id]));
    const ticket = caseTicket(c);
    const items = caseState.timeline.map(timelineItemHtml).join('');
    const hidden = caseState.hidden ? `<p class="page-note">${fmt(caseState.hidden)} ${caseState.hidden === 1 ? 'event is' : 'events are'} in indexes you can't read, so ${caseState.hidden === 1 ? 'it isn’t' : 'they aren’t'} shown.</p>` : '';
    view.innerHTML = `<div class="case-layout">
        <div class="case-main">
            <div class="ask-field case-add">
                <label class="ask-label" for="caseAdd">Add</label>
                <input type="text" id="caseAdd" autocomplete="off" placeholder="Write a note, or ask a question to search inside this case…" maxlength="10000" aria-describedby="caseAddHint">
                <span class="ask-hint" aria-hidden="true">Return ↵</span>
            </div>
            <p class="case-add-hint" id="caseAddHint">A note is saved as written. A search (such as <span class="mono">find failed alice</span>) runs over this case's time, and you can keep it. Event IDs separated by commas are added as events.</p>
            <div id="caseSearchResult" aria-live="polite"></div>
            ${hidden}
            ${items ? `<ol class="timeline">${items}</ol>` : '<p class="page-note">Nothing on the timeline yet.</p>'}
        </div>
        <aside class="case-side">
            <section class="group-panel case-details">
                <h2 class="group-panel-title">Case details</h2>
                <span class="muted">Status</span><span><button type="button" class="popup popup-sm" data-click="openCaseStatusMenu" data-args='["$this"]' aria-haspopup="menu" aria-expanded="false">${esc(c.status)}</button></span>
                <span class="muted">Severity</span><span><button type="button" class="popup popup-sm case-sev case-sev-${esc(String(c.severity).toLowerCase())}" data-click="openCaseSeverityMenu" data-args='["$this"]' aria-haspopup="menu" aria-expanded="false">${esc(c.severity)}</button></span>
                <label class="muted" for="caseOwner">Owner</label><input type="text" id="caseOwner" class="case-owner" value="${esc(c.assignee || '')}" placeholder="Nobody yet" data-change="saveCaseOwner">
                <span class="muted">Opened</span><b>${esc(timelineTime(c.createdAt))}</b>
                ${caseState.techniques.length ? `<span class="muted">Technique</span><b>${esc(caseState.techniques.join(', '))}</b>` : ''}
                ${ticket ? `<span class="muted">Ticket</span><b class="case-ticket">${esc(ticket)}</b>` : ''}
            </section>
            <section class="group-panel case-counts">
                <h2 class="group-panel-title">In this case</h2>
                <div><span>Alerts</span><b>${fmt((c.alertIds || []).length)}</b></div>
                <div><span>Pinned events</span><b>${fmt((c.events || []).length)}</b></div>
                <div><span>Searches</span><b>${fmt((c.searches || []).length)}</b></div>
                <div><span>Notes</span><b>${fmt((c.notes || []).length)}</b></div>
            </section>
            <button type="button" class="btn case-ai" data-click="askInvestigatorAboutCase">Ask the AI investigator…</button>
            <button type="button" class="btn-link case-delete" data-click="deleteCase" data-args="${id}">Delete this case…</button>
        </aside>
    </div>`;
    document.getElementById('caseAdd').addEventListener('keydown', event => {
        if (event.key === 'Enter' && !event.isComposing) { event.preventDefault(); addToCase(c.id); }
    });
}

/** Whether what's typed in ADD reads like a search rather than a note. */
function looksLikeSearch(text) {
    const t = text.trim();
    if (/^(find|search|stats|timechart|top|where)\b/i.test(t) || t.includes(' | ') || t.startsWith('|')) return true;
    if (/^[\w.]+\s*(=|!=|>=|<=|>|<)\s*\S+$/.test(t)) return true;
    return /^(show|list|which|what|how many|how much|did|were|was|any|count|when)\b/i.test(t) && t.endsWith('?');
}

async function addToCase(id) {
    id = typeof id === 'string' ? id : caseState.current && caseState.current.id;
    const input = document.getElementById('caseAdd');
    const text = input ? input.value.trim() : '';
    if (!id || !text) return;
    const ids = text.split(',').map(s => s.trim()).filter(Boolean);
    try {
        if (ids.every(s => /^[0-9A-HJKMNP-TV-Z]{26}$/i.test(s))) {
            await api('POST', `/api/v1/cases/${encodeURIComponent(id)}/items`, { eventIds: ids });
            toast(`Added ${ids.length} ${ids.length === 1 ? 'event' : 'events'}`, 'success');
        } else if (looksLikeSearch(text)) {
            await runCaseSearch(text);
            return;
        } else {
            await api('POST', `/api/v1/cases/${encodeURIComponent(id)}/items`, { note: text });
            toast('Note added', 'success');
        }
        openCase(id);
    } catch (e) { toast('Failed: ' + e.message, 'error'); }
}

/** The case's time: from an hour before its first item until now. */
function caseWindow() {
    const times = caseState.timeline.map(t => new Date(t.time).getTime()).filter(n => !Number.isNaN(n));
    const first = times.length ? Math.min(...times) : Date.now() - 86400000;
    return { start: new Date(first - 3600000), end: new Date() };
}

async function runCaseSearch(query) {
    const out = document.getElementById('caseSearchResult');
    if (!out) return;
    const { start, end } = caseWindow();
    out.innerHTML = loadingHtml('Searching this case’s time…');
    try {
        const result = await api('POST', '/api/v1/query/execute', { query, startTime: start.toISOString(), endTime: end.toISOString(), limit: 50 });
        const rows = result.results || [];
        const total = typeof result.count === 'number' ? result.count : rows.length;
        const shown = result.query && result.query !== query ? `<p class="case-search-note">Understood as <span class="mono">${esc(result.query)}</span></p>` : '';
        out.innerHTML = `<div class="panel case-search">
            <div class="panel-header"><span class="title">Search in this case</span><span class="mono case-search-q">${esc(query)}</span><span style="flex:1"></span>
                <button type="button" class="btn btn-sm" data-click="keepCaseSearch" data-args="${esc(JSON.stringify([query]))}">Keep in case</button>
                <button type="button" class="btn btn-sm" data-click="showPanelEvents" data-args="${esc(JSON.stringify([query]))}">Open in Search</button>
                <button type="button" class="btn btn-sm" data-click="closeCaseSearch">Close</button></div>
            <div class="panel-body">${shown}<p class="case-search-note">${esc(fmt(total))} ${total === 1 ? 'result' : 'results'} from ${esc(timelineTime(start))} to now.</p><div id="caseSearchChart"></div></div>
        </div>`;
        CabinetCharts.render(document.getElementById('caseSearchChart'), rows, { tableWhenRaw: true, tableRows: 10, height: 160, barNote: false });
    } catch (e) {
        out.innerHTML = errorHtml(`The search didn't run: ${e.message}.`, 'Save it as a note instead', 'saveCaseNoteInstead');
    }
}

function closeCaseSearch() {
    const out = document.getElementById('caseSearchResult');
    if (out) out.innerHTML = '';
}

async function keepCaseSearch(query) {
    const c = caseState.current;
    try {
        await api('POST', `/api/v1/cases/${encodeURIComponent(c.id)}/items`, { search: query });
        toast('Search kept in the case', 'success');
        openCase(c.id);
    } catch (e) { toast('Failed: ' + e.message, 'error'); }
}

async function saveCaseNoteInstead() {
    const c = caseState.current;
    const text = document.getElementById('caseAdd').value.trim();
    if (!c || !text) return;
    try {
        await api('POST', `/api/v1/cases/${encodeURIComponent(c.id)}/items`, { note: text });
        openCase(c.id);
    } catch (e) { toast('Failed: ' + e.message, 'error'); }
}

async function patchCase(change, done) {
    const c = caseState.current;
    try {
        await api('PATCH', '/api/v1/cases/' + encodeURIComponent(c.id), change);
        if (done) toast(done, 'success');
        openCase(c.id);
    } catch (e) { toast('Failed: ' + e.message, 'error'); }
}

function openCaseStatusMenu(button) {
    const c = caseState.current;
    showPopupMenu(button, CASE_STATUSES.map(s => ({ label: s, value: s, checked: s === c.status })), status => {
        if (status !== c.status) patchCase({ status }, `Status is ${status}`);
    });
}

function openCaseSeverityMenu(button) {
    const c = caseState.current;
    showPopupMenu(button, SEVERITIES.map(s => ({ label: s, value: s, checked: s === c.severity })), severity => {
        if (severity !== c.severity) patchCase({ severity }, `Severity is ${severity}`);
    });
}

function saveCaseOwner() {
    const owner = document.getElementById('caseOwner').value.trim();
    if (owner && owner !== (caseState.current.assignee || '')) patchCase({ assignee: owner }, `Owner is ${owner}`);
}

/** Close case…, or Reopen case for a closed one. */
function closeCaseDialog() {
    const c = caseState.current;
    if (!c) return;
    if (c.status === 'Closed') { patchCase({ status: 'Open' }, 'Case reopened'); return; }
    document.getElementById('closeCaseTitle').textContent = `Close ${caseLabel(c)}`;
    document.getElementById('closeCaseNote').value = '';
    openModal('closeCaseModal');
}

async function confirmCloseCase() {
    const c = caseState.current;
    const note = document.getElementById('closeCaseNote').value.trim();
    try {
        if (note) await api('POST', `/api/v1/cases/${encodeURIComponent(c.id)}/items`, { note: `Closing: ${note}` });
        await api('PATCH', '/api/v1/cases/' + encodeURIComponent(c.id), { status: 'Closed' });
        closeModal('closeCaseModal');
        toast('Case closed', 'success');
        openCase(c.id);
    } catch (e) { toast('Failed: ' + e.message, 'error'); }
}

async function deleteCase(id) {
    const c = caseState.current;
    if (!confirm(`Delete ${caseLabel(c)}? Its notes and timeline go with it; the events and alerts stay. This can't be undone.`)) return;
    try {
        await api('DELETE', '/api/v1/cases/' + encodeURIComponent(id));
        toast('Case deleted', 'success');
        loadCases();
    } catch (e) { toast('Failed: ' + e.message, 'error'); }
}

/** A self-contained HTML report of the case, downloaded from this page. Everything is escaped. */
function exportCaseReport() {
    const c = caseState.current;
    if (!c) return;
    const rows = caseState.timeline.map(t => `<tr><td>${esc(fmtTime(t.time))}</td><td>${esc(TIMELINE_KINDS[t.kind] || t.kind)}</td><td>${esc(t.by || '')}</td><td>${esc([t.host, t.index].filter(Boolean).join(' · '))}</td><td><pre>${esc(t.text)}</pre></td></tr>`).join('');
    const html = `<!DOCTYPE html><html lang="en"><head><meta charset="utf-8"><title>${esc(caseLabel(c))}</title>
<style>body{font-family:system-ui,sans-serif;margin:32px;color:#1D1D1F}table{border-collapse:collapse;width:100%}td,th{border-bottom:1px solid #E1DED8;padding:6px 8px;text-align:left;vertical-align:top;font-size:13px}pre{margin:0;white-space:pre-wrap;font-family:ui-monospace,monospace}dt{color:#5B5852}dd{margin:0 0 6px;font-weight:700}</style></head>
<body><h1>${esc(caseLabel(c))}</h1><dl><dt>Status</dt><dd>${esc(c.status)}</dd><dt>Severity</dt><dd>${esc(c.severity)}</dd><dt>Owner</dt><dd>${esc(c.assignee || 'Nobody')}</dd><dt>Opened</dt><dd>${esc(fmtTime(c.createdAt))} by ${esc(c.createdBy)}</dd>
${c.description ? `<dt>Description</dt><dd>${esc(c.description)}</dd>` : ''}${caseState.techniques.length ? `<dt>Technique</dt><dd>${esc(caseState.techniques.join(', '))}</dd>` : ''}</dl>
<h2>Timeline</h2><table><thead><tr><th>When</th><th>Kind</th><th>By</th><th>Where</th><th>What</th></tr></thead><tbody>${rows}</tbody></table>
<p>Exported ${esc(new Date().toLocaleString())}${caseState.hidden ? `. ${esc(caseState.hidden)} events in indexes the exporter can't read are left out` : ''}.</p></body></html>`;
    const url = URL.createObjectURL(new Blob([html], { type: 'text/html' }));
    const a = document.createElement('a');
    a.href = url;
    a.download = `case-${c.number || c.id}.html`;
    document.body.appendChild(a);
    a.click();
    a.remove();
    setTimeout(() => URL.revokeObjectURL(url), 1000);
}

/** Opens the AI investigator with the case as context. */
function askInvestigatorAboutCase() {
    const c = caseState.current;
    if (!c) return;
    const lines = caseState.timeline.slice(-12).map(t => `- ${fmtTime(t.time)} ${TIMELINE_KINDS[t.kind] || t.kind}: ${String(t.text).replace(/\s+/g, ' ').slice(0, 160)}`);
    let question = `About case ${caseLabel(c)} (${c.severity}, ${c.status}):\n${lines.join('\n')}\nWhat happened, and what should I check next?`;
    if (question.length > 1990) question = question.slice(0, 1990);
    navigate('investigate');
    const box = document.getElementById('investigateQuestion');
    if (box) { box.value = question; box.focus(); }
}
