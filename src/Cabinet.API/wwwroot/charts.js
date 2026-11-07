/*
 * Charts for query results, drawn as SVG with no dependencies. shape() and the *Svg functions are
 * pure, so they can be tested without a browser. render() adds them to the page with hover,
 * keyboard access, and a data table for anyone who can't use the chart.
 */
const CabinetCharts = (() => {
    const MAX_SERIES = 8; // More can't be told apart. The rest fold into "Other".
    const MAX_BARS = 20;

    const isNumber = value => typeof value === 'number' && Number.isFinite(value);

    function escapeText(value) {
        return String(value ?? '')
            .replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;')
            .replace(/"/g, '&quot;').replace(/'/g, '&#39;');
    }

    function visibleColumns(rows) {
        const names = [];
        const seen = new Set();
        rows.slice(0, 100).forEach(row => Object.keys(row).forEach(key => {
            if (!seen.has(key)) { seen.add(key); names.push(key); }
        }));
        return names;
    }

    /** Works out what a result can be drawn as, and arranges the data for it. */
    function shape(rows, viz) {
        if (!Array.isArray(rows) || rows.length === 0) return { kind: 'empty' };

        const columns = visibleColumns(rows);
        const lower = columns.map(c => c.toLowerCase());
        const isEvents = lower.includes('rawline') || lower.includes('_raw');
        const timeKey = columns.find(c => c === '_time') || (isEvents ? undefined : columns.find(c => c.toLowerCase() === 'timestamp'));
        const candidates = columns.filter(c => c !== timeKey && !c.startsWith('_'));
        const numeric = candidates.filter(c => rows.every(r => r[c] === null || r[c] === undefined || isNumber(r[c])) && rows.some(r => isNumber(r[c])));
        const labels = candidates.filter(c => !numeric.includes(c));

        // In "stats count by status" the group is a number too. Stats writes groups first.
        if (labels.length === 0 && numeric.length >= 2 && rows.length > 1) labels.push(numeric.shift());

        const table = { kind: 'table', columns: columns.filter(c => !c.startsWith('_') || c === '_time'), rows };
        if (viz === 'table') return table;
        if (isEvents && !viz) return table;
        if (numeric.length === 0) return table;

        const wantsStat = viz === 'stat' || (!viz && rows.length === 1 && numeric.length === 1 && labels.length === 0 && !timeKey);
        if (wantsStat) return { kind: 'stat', label: numeric[0], value: rows[0][numeric[0]] };

        if (timeKey && viz !== 'bar') {
            return { ...timeSeries(rows, timeKey, numeric, labels[0]), kind: viz === 'area' ? 'area' : 'line' };
        }

        if (viz === 'line' || viz === 'area') return table;

        const labelKey = labels[0] || timeKey;
        if (!labelKey) return rows.length === 1 ? { kind: 'stat', label: numeric[0], value: rows[0][numeric[0]] } : table;
        const valueKey = numeric[0];
        const sorted = rows
            .map(r => ({ label: r[labelKey] === null || r[labelKey] === undefined || r[labelKey] === '' ? '(empty)' : String(r[labelKey]), value: Number(r[valueKey]) || 0 }))
            .sort((a, b) => b.value - a.value);
        return { kind: 'bar', labelKey, valueKey, bars: sorted.slice(0, MAX_BARS), hidden: Math.max(0, sorted.length - MAX_BARS), rows };
    }

    function timeSeries(rows, timeKey, numeric, groupKey) {
        const times = [...new Set(rows.map(r => r[timeKey]))]
            .map(t => new Date(t).getTime())
            .filter(t => !Number.isNaN(t))
            .sort((a, b) => a - b);
        const position = new Map(times.map((t, i) => [t, i]));
        const valueKey = numeric[0];

        let series;
        if (groupKey) {
            const byGroup = new Map();
            rows.forEach(r => {
                const name = r[groupKey] === null || r[groupKey] === undefined || r[groupKey] === '' ? '(empty)' : String(r[groupKey]);
                if (!byGroup.has(name)) byGroup.set(name, new Array(times.length).fill(0));
                const i = position.get(new Date(r[timeKey]).getTime());
                if (i !== undefined) byGroup.get(name)[i] += Number(r[valueKey]) || 0;
            });

            // Colors follow the group, so keep the groups in the order they first appeared
            const all = [...byGroup.entries()].map(([name, values]) => ({ name, values, total: values.reduce((a, b) => a + b, 0) }));
            if (all.length > MAX_SERIES) {
                const keep = new Set([...all].sort((a, b) => b.total - a.total).slice(0, MAX_SERIES - 1).map(s => s.name));
                const other = new Array(times.length).fill(0);
                all.filter(s => !keep.has(s.name)).forEach(s => s.values.forEach((v, i) => { other[i] += v; }));
                series = all.filter(s => keep.has(s.name)).concat([{ name: 'Other', values: other }]);
            } else {
                series = all;
            }
        } else {
            series = numeric.slice(0, MAX_SERIES).map(name => {
                const values = new Array(times.length).fill(null);
                rows.forEach(r => {
                    const i = position.get(new Date(r[timeKey]).getTime());
                    if (i !== undefined && isNumber(r[name])) values[i] = r[name];
                });
                return { name, values };
            });
        }

        return { times, series: series.map(s => ({ name: s.name, values: s.values })), valueKey, rows };
    }

    /** A round maximum and evenly spaced ticks for an axis starting at zero. */
    function niceTicks(max, count = 4) {
        if (!(max > 0)) return [0, 1];
        const rough = max / count;
        const magnitude = Math.pow(10, Math.floor(Math.log10(rough)));
        const step = [1, 2, 2.5, 5, 10].map(m => m * magnitude).find(s => s >= rough) || 10 * magnitude;
        const ticks = [];
        for (let v = 0; v < max + step * 0.999; v += step) ticks.push(Math.round(v * 1e9) / 1e9);
        return ticks;
    }

    function formatNumber(value) {
        if (value === null || value === undefined) return '-';
        const abs = Math.abs(value);
        if (abs >= 1e9) return (value / 1e9).toFixed(abs >= 1e10 ? 0 : 1) + 'B';
        if (abs >= 1e6) return (value / 1e6).toFixed(abs >= 1e7 ? 0 : 1) + 'M';
        if (abs >= 1e4) return (value / 1e3).toFixed(0) + 'K';
        if (Number.isInteger(value)) return value.toLocaleString('en-US');
        return value.toLocaleString('en-US', { maximumFractionDigits: 2 });
    }

    function formatTime(ms, span) {
        const d = new Date(ms);
        const pad = n => String(n).padStart(2, '0');
        const day = `${pad(d.getMonth() + 1)}-${pad(d.getDate())}`;
        const time = `${pad(d.getHours())}:${pad(d.getMinutes())}`;
        return span > 2 * 86400000 ? day : span > 86400000 ? `${day} ${time}` : time;
    }

    /** The plot's geometry, shared by drawing and hit-testing. */
    function layout(data, width, height) {
        const margin = { top: 12, right: 16, bottom: 26, left: 52 };
        const plotWidth = Math.max(10, width - margin.left - margin.right);
        const plotHeight = Math.max(10, height - margin.top - margin.bottom);
        const stacked = data.kind === 'area';
        let max = 0;
        data.times.forEach((_, i) => {
            const values = data.series.map(s => s.values[i] || 0);
            max = Math.max(max, stacked ? values.reduce((a, b) => a + b, 0) : Math.max(...values));
        });
        const ticks = niceTicks(max);
        const top = ticks[ticks.length - 1] || 1;
        const count = data.times.length;
        const x = i => margin.left + (count <= 1 ? plotWidth / 2 : (i / (count - 1)) * plotWidth);
        const y = value => margin.top + plotHeight - (value / top) * plotHeight;
        return { margin, plotWidth, plotHeight, ticks, x, y };
    }

    /** Draws a line or stacked area chart. Everything in it is escaped. */
    function timeSvg(data, width, height, title) {
        const { margin, plotWidth, plotHeight, ticks, x, y } = layout(data, width, height);
        const span = data.times.length > 1 ? data.times[data.times.length - 1] - data.times[0] : 0;
        const parts = [];

        ticks.forEach(tick => {
            const py = y(tick);
            parts.push(`<line x1="${margin.left}" x2="${margin.left + plotWidth}" y1="${py}" y2="${py}" stroke="var(--chart-grid)" stroke-width="1"/>`);
            parts.push(`<text x="${margin.left - 8}" y="${py + 4}" text-anchor="end" class="chart-axis">${escapeText(formatNumber(tick))}</text>`);
        });

        const tickCount = Math.min(6, data.times.length);
        for (let t = 0; t < tickCount; t++) {
            const i = tickCount === 1 ? 0 : Math.round((t / (tickCount - 1)) * (data.times.length - 1));
            const anchor = t === 0 && tickCount > 1 ? 'start' : t === tickCount - 1 && tickCount > 1 ? 'end' : 'middle';
            parts.push(`<text x="${x(i)}" y="${height - 8}" text-anchor="${anchor}" class="chart-axis">${escapeText(formatTime(data.times[i], span))}</text>`);
        }

        if (data.kind === 'area') {
            const base = new Array(data.times.length).fill(0);
            data.series.forEach((s, n) => {
                const lower = base.slice();
                s.values.forEach((v, i) => { base[i] += v || 0; });
                const upperPath = base.map((v, i) => `${i === 0 ? 'M' : 'L'}${x(i).toFixed(1)},${y(v).toFixed(1)}`).join(' ');
                const lowerPath = lower.map((v, i) => `L${x(i).toFixed(1)},${y(v).toFixed(1)}`).reverse().join(' ');
                parts.push(`<path d="${upperPath} ${lowerPath} Z" fill="var(--series-${n + 1})" fill-opacity="0.85" stroke="var(--chart-surface)" stroke-width="2"/>`);
            });
        } else {
            data.series.forEach((s, n) => {
                let path = '';
                let drawing = false;
                s.values.forEach((v, i) => {
                    if (v === null || v === undefined) { drawing = false; return; }
                    path += `${drawing ? 'L' : 'M'}${x(i).toFixed(1)},${y(v).toFixed(1)} `;
                    drawing = true;
                });
                parts.push(`<path d="${path.trim()}" fill="none" stroke="var(--series-${n + 1})" stroke-width="2" stroke-linejoin="round" stroke-linecap="round"/>`);
                // One point has no line to see, so give it a marker
                if (data.times.length === 1 && s.values[0] !== null) {
                    parts.push(`<circle cx="${x(0)}" cy="${y(s.values[0])}" r="4" fill="var(--series-${n + 1})"/>`);
                }
            });
        }

        const summary = `${title || 'Chart'}: ${data.series.map(s => s.name).join(', ')} over ${data.times.length} time periods`;
        return `<svg class="chart-svg" viewBox="0 0 ${width} ${height}" width="100%" height="${height}" role="img" aria-label="${escapeText(summary)}" tabindex="0">${parts.join('')}<g class="chart-hover"></g></svg>`;
    }

    function legendHtml(series) {
        if (series.length < 2) return '';
        return `<ul class="chart-legend">${series.map((s, n) => `<li><span class="chart-swatch" style="background:var(--series-${n + 1})"></span>${escapeText(s.name)}</li>`).join('')}</ul>`;
    }

    function barsHtml(data) {
        const max = Math.max(...data.bars.map(b => b.value), 1);
        const rows = data.bars.map(bar => {
            const percent = Math.max((bar.value / max) * 100, bar.value > 0 ? 0.5 : 0);
            return `<li class="chart-bar-row"><span class="chart-bar-label" title="${escapeText(bar.label)}">${escapeText(bar.label)}</span>` +
                `<span class="chart-bar-track"><span class="chart-bar" style="width:${percent.toFixed(2)}%"></span></span>` +
                `<span class="chart-bar-value">${escapeText(formatNumber(bar.value))}</span></li>`;
        }).join('');
        const more = data.hidden > 0 ? `<p class="chart-note">Showing the top ${data.bars.length} of ${data.bars.length + data.hidden}.</p>` : '';
        return `<p class="chart-note">${escapeText(data.valueKey)} by ${escapeText(data.labelKey)}</p><ul class="chart-bars" aria-label="${escapeText(data.valueKey)} by ${escapeText(data.labelKey)}">${rows}</ul>${more}`;
    }

    function tableHtml(columns, rows, limit) {
        const shown = rows.slice(0, limit);
        const head = columns.map(c => `<th scope="col">${escapeText(c)}</th>`).join('');
        const body = shown.map(r => `<tr>${columns.map(c => `<td>${escapeText(r[c] === null || r[c] === undefined ? '' : typeof r[c] === 'object' ? JSON.stringify(r[c]) : r[c])}</td>`).join('')}</tr>`).join('');
        const more = rows.length > shown.length ? `<p class="chart-note">Showing ${shown.length} of ${rows.length} rows.</p>` : '';
        return `<div class="chart-table"><table><thead><tr>${head}</tr></thead><tbody>${body}</tbody></table></div>${more}`;
    }

    function addHover(container, data, height) {
        const svg = container.querySelector('svg.chart-svg');
        const tooltip = container.querySelector('.chart-tooltip');
        if (!svg || !tooltip) return;
        const hover = svg.querySelector('.chart-hover');
        const width = svg.viewBox.baseVal.width;
        const { margin, plotHeight, x, y } = layout(data, width, height);
        const span = data.times.length > 1 ? data.times[data.times.length - 1] - data.times[0] : 0;
        let current = -1;

        const show = i => {
            current = i;
            let running = 0;
            const lines = data.series.map((s, n) => {
                const value = s.values[i];
                running += value || 0;
                return { name: s.name, value, n, plotted: data.kind === 'area' ? running : value };
            });
            hover.innerHTML =
                `<line x1="${x(i)}" x2="${x(i)}" y1="${margin.top}" y2="${margin.top + plotHeight}" stroke="var(--chart-crosshair)" stroke-width="1"/>` +
                lines.filter(l => l.value !== null && l.value !== undefined).map(l =>
                    `<circle cx="${x(i)}" cy="${y(l.plotted)}" r="4" fill="var(--series-${l.n + 1})" stroke="var(--chart-surface)" stroke-width="2"/>`).join('');
            tooltip.innerHTML = `<div class="chart-tooltip-title">${escapeText(new Date(data.times[i]).toLocaleString())}</div>` +
                lines.map(l => `<div class="chart-tooltip-row"><span class="chart-swatch" style="background:var(--series-${l.n + 1})"></span><span>${escapeText(l.name)}</span><strong>${escapeText(formatNumber(l.value))}</strong></div>`).join('');
            tooltip.hidden = false;
            const rect = svg.getBoundingClientRect();
            const scale = rect.width / width;
            const left = x(i) * scale;
            tooltip.style.left = `${Math.min(Math.max(0, left + 12), Math.max(0, rect.width - tooltip.offsetWidth))}px`;
            tooltip.style.top = `${margin.top}px`;
        };
        const hide = () => { current = -1; hover.innerHTML = ''; tooltip.hidden = true; };

        svg.addEventListener('pointermove', event => {
            const rect = svg.getBoundingClientRect();
            const px = ((event.clientX - rect.left) / rect.width) * width;
            let nearest = 0;
            let best = Infinity;
            data.times.forEach((_, i) => {
                const distance = Math.abs(x(i) - px);
                if (distance < best) { best = distance; nearest = i; }
            });
            show(nearest);
        });
        svg.addEventListener('pointerleave', hide);
        svg.addEventListener('blur', hide);
        svg.addEventListener('keydown', event => {
            if (event.key === 'ArrowRight' || event.key === 'ArrowLeft') {
                event.preventDefault();
                const step = event.key === 'ArrowRight' ? 1 : -1;
                show(Math.min(data.times.length - 1, Math.max(0, (current < 0 ? (step > 0 ? -1 : data.times.length) : current) + step)));
            } else if (event.key === 'Escape') {
                hide();
            }
        });
        void span;
    }

    /** Draws a result into an element. viz can be line, area, bar, stat or table, or left out to choose. */
    function render(container, rows, options = {}) {
        const data = shape(rows, options.viz);
        const height = options.height || 240;
        const title = options.title || '';

        if (data.kind === 'empty') {
            container.innerHTML = '<p class="chart-note">No results to chart.</p>';
            return data;
        }

        if (data.kind === 'table') {
            container.innerHTML = options.viz === 'table' || options.tableWhenRaw
                ? tableHtml(data.columns, rows, options.tableRows || 20)
                : '<p class="chart-note">Run an aggregating query to see a chart, such as <code>find * | stats count by host</code> or <code>find * | timechart span=1h count</code>.</p>';
            return data;
        }

        if (data.kind === 'stat') {
            container.innerHTML = `<div class="chart-stat"><div class="chart-stat-value">${escapeText(formatNumber(data.value))}</div><div class="chart-stat-label">${escapeText(data.label)}</div></div>`;
            return data;
        }

        const dataTable = rows.length > 0
            ? `<details class="chart-data"><summary>Data table</summary>${tableHtml(visibleColumns(rows).filter(c => !c.startsWith('_') || c === '_time'), rows, 200)}</details>`
            : '';

        if (data.kind === 'bar') {
            container.innerHTML = barsHtml(data) + dataTable;
            return data;
        }

        const width = Math.max(320, Math.round(container.clientWidth || 640));
        container.innerHTML = `<div class="chart-plot">${timeSvg(data, width, height, title)}<div class="chart-tooltip" hidden></div></div>${legendHtml(data.series)}${dataTable}`;
        addHover(container, data, height);
        return data;
    }

    return { shape, render, timeSvg, niceTicks, formatNumber, escapeText, tableHtml, barsHtml, legendHtml };
})();
