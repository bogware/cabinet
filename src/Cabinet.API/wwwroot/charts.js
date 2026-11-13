/*
 * Charts for query results, drawn as SVG with no dependencies. shape() and the *Svg functions are
 * pure, so they can be tested without a browser. render() adds them to the page with hover,
 * keyboard access, and a data table for anyone who can't use the chart.
 */
const CabinetCharts = (() => {
    const MAX_SERIES = 8; // More can't be told apart. The rest fold into "Other".
    const MAX_BARS = 20;

    let washCount = 0; // gradient ids, unique on the page

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

        // A map needs coordinates, such as from iplocation then stats count by lat, lon
        if (viz === 'map') {
            const latKey = columns.find(c => /^(lat|latitude)$/i.test(c));
            const lonKey = columns.find(c => /^(lon|lng|long|longitude)$/i.test(c));
            if (!latKey || !lonKey) return table;
            const valueKey = numeric.find(c => c !== latKey && c !== lonKey);
            const labelKey = labels.find(c => c !== latKey && c !== lonKey);
            const points = rows
                .map(r => ({ lat: Number(r[latKey]), lon: Number(r[lonKey]), value: valueKey ? Number(r[valueKey]) || 0 : 1, label: labelKey ? String(r[labelKey] ?? '') : '' }))
                .filter(p => Number.isFinite(p.lat) && Number.isFinite(p.lon) && Math.abs(p.lat) <= 90 && Math.abs(p.lon) <= 180);
            return points.length ? { kind: 'map', points, valueKey: valueKey || 'count', rows } : table;
        }
        if (isEvents && !viz) return table;
        if (numeric.length === 0) return table;

        if (viz === 'gauge') {
            const label = numeric[0];
            const value = Number(rows[0][label]) || 0;
            const maxKey = numeric.find(c => /^(max|limit|target)$/i.test(c));
            const max = maxKey ? Number(rows[0][maxKey]) || 0 : 0;
            return { kind: 'gauge', label, value, max: max > 0 ? max : niceTicks(Math.abs(value) || 1).slice(-1)[0] };
        }

        const wantsStat = viz === 'stat' || (!viz && rows.length === 1 && numeric.length === 1 && labels.length === 0 && !timeKey);
        if (wantsStat) return { kind: 'stat', label: numeric[0], value: rows[0][numeric[0]] };

        if (viz === 'heatmap') {
            if (!timeKey || !labels[0]) return table;
            return { ...timeSeries(rows, timeKey, numeric, labels[0]), kind: 'heatmap' };
        }

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
        if (viz === 'stacked') {
            // Each group's share of the whole; past what can be told apart folds into Other
            const positive = sorted.filter(s => s.value > 0);
            const kept = positive.slice(0, MAX_SERIES - 1);
            const rest = positive.slice(MAX_SERIES - 1).reduce((sum, s) => sum + s.value, 0);
            if (rest > 0) kept.push({ label: 'Other', value: rest });
            const total = kept.reduce((sum, s) => sum + s.value, 0);
            return kept.length ? { kind: 'stacked', labelKey, valueKey, total, parts: kept.map(p => ({ ...p, share: p.value / total })), rows } : table;
        }
        if (viz === 'pie') {
            // A pie of negative parts means nothing; slices past what can be told apart fold into Other
            const positive = sorted.filter(s => s.value > 0);
            const slices = positive.slice(0, MAX_SERIES - 1);
            const rest = positive.slice(MAX_SERIES - 1).reduce((sum, s) => sum + s.value, 0);
            if (rest > 0) slices.push({ label: 'Other', value: rest });
            return slices.length ? { kind: 'pie', labelKey, valueKey, slices, rows } : table;
        }
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

        if (data.kind === 'area' && data.series.length === 1) {
            // One measure: a line with a soft wash under it
            const values = data.series[0].values.map(v => v || 0);
            const line = values.map((v, i) => `${i === 0 ? 'M' : 'L'}${x(i).toFixed(1)},${y(v).toFixed(1)}`).join(' ');
            const floor = y(0).toFixed(1);
            const id = `chart-wash-${++washCount}`;
            parts.push(`<defs><linearGradient id="${id}" x1="0" y1="0" x2="0" y2="1"><stop offset="0" style="stop-color:var(--series-1);stop-opacity:.28"/><stop offset="1" style="stop-color:var(--series-1);stop-opacity:0"/></linearGradient></defs>`);
            parts.push(`<path d="${line} L${x(values.length - 1).toFixed(1)},${floor} L${x(0).toFixed(1)},${floor} Z" fill="url(#${id})"/>`);
            parts.push(`<path d="${line}" fill="none" stroke="var(--series-1)" stroke-width="2.2" stroke-linejoin="round" stroke-linecap="round"/>`);
        } else if (data.kind === 'area') {
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

    /** A donut: each slice's share of the total. */
    function pieSvg(data, size) {
        const total = data.slices.reduce((sum, s) => sum + s.value, 0) || 1;
        const r = size / 2 - 4;
        const inner = r * 0.58;
        const c = size / 2;
        let angle = -Math.PI / 2;
        const point = (radius, a) => `${(c + radius * Math.cos(a)).toFixed(2)},${(c + radius * Math.sin(a)).toFixed(2)}`;
        const parts = data.slices.map((s, n) => {
            const sweep = (s.value / total) * Math.PI * 2;
            const end = angle + Math.min(sweep, Math.PI * 2 - 1e-4);
            const large = sweep > Math.PI ? 1 : 0;
            const path = `M${point(r, angle)} A${r},${r} 0 ${large} 1 ${point(r, end)} L${point(inner, end)} A${inner},${inner} 0 ${large} 0 ${point(inner, angle)} Z`;
            angle += sweep;
            const share = ((s.value / total) * 100).toFixed(1);
            return `<path d="${path}" fill="var(--series-${n + 1})" stroke="var(--chart-surface)" stroke-width="2"><title>${escapeText(`${s.label}: ${formatNumber(s.value)} (${share}%)`)}</title></path>`;
        });
        const summary = `${data.valueKey} by ${data.labelKey}: ${data.slices.map(s => `${s.label} ${((s.value / total) * 100).toFixed(1)}%`).join(', ')}`;
        return `<svg class="chart-svg" viewBox="0 0 ${size} ${size}" width="${size}" height="${size}" role="img" aria-label="${escapeText(summary)}">${parts.join('')}` +
            `<text x="${c}" y="${c + 6}" text-anchor="middle" class="chart-stat-value" style="font-size:${Math.round(size / 9)}px">${escapeText(formatNumber(total))}</text></svg>`;
    }

    /** A half-circle gauge from zero to max. */
    function gaugeSvg(data, width) {
        const height = Math.round(width * 0.62);
        const r = width / 2 - 12;
        const cx = width / 2;
        const cy = height - 16;
        const fraction = Math.max(0, Math.min(1, data.max > 0 ? data.value / data.max : 0));
        const at = f => {
            const a = Math.PI * (1 - f);
            return `${(cx + r * Math.cos(a)).toFixed(2)},${(cy - r * Math.sin(a)).toFixed(2)}`;
        };
        const track = `M${at(0)} A${r},${r} 0 0 1 ${at(1)}`;
        const filled = fraction > 0 ? `<path d="M${at(0)} A${r},${r} 0 0 1 ${at(fraction)}" fill="none" stroke="var(--series-1)" stroke-width="18" stroke-linecap="butt"/>` : '';
        const summary = `${data.label}: ${formatNumber(data.value)} of ${formatNumber(data.max)}`;
        return `<svg class="chart-svg" viewBox="0 0 ${width} ${height}" width="100%" height="${height}" role="img" aria-label="${escapeText(summary)}">` +
            `<path d="${track}" fill="none" stroke="var(--chart-grid)" stroke-width="18"/>${filled}` +
            `<text x="${cx}" y="${cy - 8}" text-anchor="middle" class="chart-stat-value">${escapeText(formatNumber(data.value))}</text>` +
            `<text x="${cx - r}" y="${cy + 14}" text-anchor="middle" class="chart-axis">0</text>` +
            `<text x="${cx + r}" y="${cy + 14}" text-anchor="middle" class="chart-axis">${escapeText(formatNumber(data.max))}</text></svg>`;
    }

    /** One row per group and one column per time; darker is more. */
    function heatmapSvg(data, width) {
        const labelWidth = 120;
        const cell = Math.max(4, Math.min(28, (width - labelWidth - 8) / Math.max(1, data.times.length)));
        const rowHeight = 22;
        const height = data.series.length * rowHeight + 24;
        const max = Math.max(1, ...data.series.flatMap(s => s.values.map(v => v || 0)));
        const span = data.times.length > 1 ? data.times[data.times.length - 1] - data.times[0] : 0;
        const parts = [];
        data.series.forEach((s, row) => {
            const y = row * rowHeight;
            parts.push(`<text x="${labelWidth - 6}" y="${y + rowHeight / 2 + 4}" text-anchor="end" class="chart-axis">${escapeText(s.name.length > 18 ? s.name.slice(0, 17) + '…' : s.name)}</text>`);
            s.values.forEach((v, i) => {
                const opacity = v ? 0.12 + 0.88 * (v / max) : 0;
                parts.push(`<rect x="${(labelWidth + i * cell).toFixed(1)}" y="${y + 1}" width="${Math.max(1, cell - 1).toFixed(1)}" height="${rowHeight - 2}" fill="var(--series-1)" fill-opacity="${opacity.toFixed(3)}" stroke="var(--chart-grid)" stroke-width="0.5">` +
                    `<title>${escapeText(`${s.name}, ${new Date(data.times[i]).toLocaleString()}: ${formatNumber(v || 0)}`)}</title></rect>`);
            });
        });
        const ticks = Math.min(6, data.times.length);
        for (let t = 0; t < ticks; t++) {
            const i = ticks === 1 ? 0 : Math.round((t / (ticks - 1)) * (data.times.length - 1));
            parts.push(`<text x="${(labelWidth + i * cell + cell / 2).toFixed(1)}" y="${height - 6}" text-anchor="middle" class="chart-axis">${escapeText(formatTime(data.times[i], span))}</text>`);
        }
        const summary = `Heatmap of ${data.valueKey} for ${data.series.length} groups over ${data.times.length} time periods`;
        return `<svg class="chart-svg" viewBox="0 0 ${width} ${height}" width="100%" height="${height}" role="img" aria-label="${escapeText(summary)}">${parts.join('')}</svg>`;
    }

    /** Points by latitude and longitude, sized by value, on a 30-degree grid. */
    function mapSvg(data, width) {
        const height = Math.round(width / 2);
        const x = lon => ((lon + 180) / 360) * width;
        const y = lat => ((90 - lat) / 180) * height;
        const max = Math.max(1, ...data.points.map(p => p.value));
        const parts = [`<rect x="0" y="0" width="${width}" height="${height}" fill="var(--chart-surface)" stroke="var(--chart-grid)"/>`];
        for (let lon = -150; lon <= 150; lon += 30) parts.push(`<line x1="${x(lon)}" x2="${x(lon)}" y1="0" y2="${height}" stroke="var(--chart-grid)" stroke-width="${lon === 0 ? 1.5 : 0.5}"/>`);
        for (let lat = -60; lat <= 60; lat += 30) parts.push(`<line x1="0" x2="${width}" y1="${y(lat)}" y2="${y(lat)}" stroke="var(--chart-grid)" stroke-width="${lat === 0 ? 1.5 : 0.5}"/>`);
        [...data.points].sort((a, b) => b.value - a.value).forEach(p => {
            const radius = 3 + 14 * Math.sqrt(p.value / max);
            const name = p.label ? `${p.label}: ` : '';
            parts.push(`<circle cx="${x(p.lon).toFixed(1)}" cy="${y(p.lat).toFixed(1)}" r="${radius.toFixed(1)}" fill="var(--series-1)" fill-opacity="0.55" stroke="var(--series-1)">` +
                `<title>${escapeText(`${name}${formatNumber(p.value)} (${p.lat.toFixed(2)}, ${p.lon.toFixed(2)})`)}</title></circle>`);
        });
        const summary = `Map of ${data.points.length} locations by ${data.valueKey}`;
        return `<svg class="chart-svg" viewBox="0 0 ${width} ${height}" width="100%" height="${height}" role="img" aria-label="${escapeText(summary)}">${parts.join('')}</svg>`;
    }

    function legendHtml(series) {
        if (series.length < 2) return '';
        return `<ul class="chart-legend">${series.map((s, n) => `<li><span class="chart-swatch" style="background:var(--series-${n + 1})"></span>${escapeText(s.name)}</li>`).join('')}</ul>`;
    }

    function barsHtml(data, options = {}) {
        const max = Math.max(...data.bars.map(b => b.value), 1);
        const rows = data.bars.map(bar => {
            const percent = Math.max((bar.value / max) * 100, bar.value > 0 ? 0.5 : 0);
            return `<li class="chart-bar-row"><span class="chart-bar-label" title="${escapeText(bar.label)}">${escapeText(bar.label)}</span>` +
                `<span class="chart-bar-track"><span class="chart-bar" style="width:${percent.toFixed(2)}%"></span></span>` +
                `<span class="chart-bar-value">${escapeText(formatNumber(bar.value))}</span></li>`;
        }).join('');
        const more = data.hidden > 0 ? `<p class="chart-note">Showing the top ${data.bars.length} of ${data.bars.length + data.hidden}.</p>` : '';
        const note = options.note === false ? '' : `<p class="chart-note">${escapeText(data.valueKey)} by ${escapeText(data.labelKey)}</p>`;
        return `${note}<ul class="chart-bars" aria-label="${escapeText(data.valueKey)} by ${escapeText(data.labelKey)}">${rows}</ul>${more}`;
    }

    function tableHtml(columns, rows, limit) {
        const shown = rows.slice(0, limit);
        const head = columns.map(c => `<th scope="col">${escapeText(c)}</th>`).join('');
        const body = shown.map(r => `<tr>${columns.map(c => `<td>${escapeText(r[c] === null || r[c] === undefined ? '' : typeof r[c] === 'object' ? JSON.stringify(r[c]) : r[c])}</td>`).join('')}</tr>`).join('');
        const more = rows.length > shown.length ? `<p class="chart-note">Showing ${shown.length} of ${rows.length} rows.</p>` : '';
        return `<div class="chart-table"><table><thead><tr>${head}</tr></thead><tbody>${body}</tbody></table></div>${more}`;
    }

    /** One bar split into each group's share, and a legend with the shares. */
    function stackedHtml(data) {
        const percent = share => `${(share * 100).toFixed(share < 0.1 && share > 0 ? 1 : 0)}%`;
        const segments = data.parts.map((p, n) =>
            `<span class="chart-stack-part" style="width:${(p.share * 100).toFixed(2)}%;background:var(--series-${n + 1})" title="${escapeText(`${p.label}: ${formatNumber(p.value)} (${percent(p.share)})`)}"></span>`).join('');
        const legend = data.parts.map((p, n) =>
            `<li><span class="chart-swatch" style="background:var(--series-${n + 1})"></span><span class="chart-stack-label">${escapeText(p.label)}</span><strong>${escapeText(percent(p.share))}</strong></li>`).join('');
        const summary = `${data.valueKey} by ${data.labelKey}: ${data.parts.map(p => `${p.label} ${percent(p.share)}`).join(', ')}`;
        return `<div class="chart-stack" role="img" aria-label="${escapeText(summary)}">${segments}</div><ul class="chart-stack-legend">${legend}</ul>`;
    }

    /**
     * "Up 12% on the previous period", and whether that's good, bad or neither. lowerIsBetter
     * true makes a rise bad; null or undefined leaves every change neutral.
     */
    function compare(current, previous, lowerIsBetter, period = 'the previous period') {
        if (!isNumber(current) || !isNumber(previous)) return null;
        if (previous === 0) {
            return current === 0
                ? { text: `Same as ${period}`, direction: 'same', tone: 'neutral' }
                : { text: `Up from none in ${period}`, direction: 'up', tone: lowerIsBetter ? 'bad' : 'neutral' };
        }
        const change = (current - previous) / Math.abs(previous);
        const rounded = Math.round(Math.abs(change) * 100);
        if (rounded === 0) return { text: `Same as ${period}`, direction: 'same', tone: 'neutral' };
        const direction = change > 0 ? 'up' : 'down';
        const tone = lowerIsBetter === undefined || lowerIsBetter === null ? 'neutral'
            : (direction === 'up') === Boolean(lowerIsBetter) ? 'bad' : 'good';
        return { text: `${direction === 'up' ? 'Up' : 'Down'} ${rounded.toLocaleString('en-US')}% on ${period}`, direction, tone };
    }

    /** Whether a count is better smaller: errors, failures, denials, 5xx. */
    function lowerIsBetter(text) {
        return /\b(errors?|fail(ed|ures?|s)?|denied|denies|blocked|blocks?|critical|exceptions?|timeouts?|5\d\d|5xx|attacks?|alerts?)\b/i.test(String(text || ''));
    }

    const AGGREGATES = { dc: 'Distinct', distinct_count: 'Distinct', avg: 'Average', mean: 'Average', sum: 'Total', min: 'Lowest', max: 'Highest', median: 'Median' };
    const SPANS = { s: 'second', m: 'minute', h: 'hour', d: 'day', w: 'week' };

    function ordinal(n) {
        const last = n % 10;
        const teen = n % 100 >= 11 && n % 100 <= 13;
        return `${n}${teen ? 'th' : last === 1 ? 'st' : last === 2 ? 'nd' : last === 3 ? 'rd' : 'th'}`;
    }

    function describeAggregate(expr) {
        const fn = /^\s*(\w+)\s*(?:\(\s*([^)]*)\))?/.exec(expr || '');
        if (!fn) return 'Number of events';
        const name = fn[1].toLowerCase();
        const field = (fn[2] || '').trim();
        const pct = /^(?:perc|p)(\d{1,2})$/.exec(name);
        if (pct) return `${ordinal(Number(pct[1]))} percentile of ${field || 'the value'}`;
        if (name === 'count') return field ? `Events with ${field}` : 'Number of events';
        const word = AGGREGATES[name];
        return word ? `${word} ${field}`.trim() : `${name}(${field})`;
    }

    /**
     * What a panel's query measures, in a sentence, for a panel nobody described: "Number of
     * events per hour, matching error". Anything it doesn't recognise stays as written.
     */
    function describeQuery(query) {
        const text = String(query || '').trim();
        if (!text) return '';
        const stages = text.split(/\s*\|\s*/).filter(Boolean);
        let filter = stages.shift() || '';
        filter = filter.replace(/^(find|search)\s+/i, '').replace(/\bfrom\s+last\s+\S+/i, '').replace(/\b(earliest|latest)=\S+/gi, '').trim();
        const filters = [];
        if (filter && filter !== '*') filters.push(`matching ${filter}`);
        let measure = '';
        let first = '';
        stages.forEach(stage => {
            const [verb, ...words] = stage.split(/\s+/);
            const rest = words.join(' ');
            switch (verb.toLowerCase()) {
                case 'where':
                case 'search':
                    filters.push(`where ${rest}`);
                    break;
                case 'timechart': {
                    const span = /span=(\d+)([smhdw])/i.exec(rest);
                    const by = /\bby\s+([\w.]+)/i.exec(rest);
                    const agg = rest.replace(/span=\S+/i, '').replace(/\bby\s+[\w.]+/i, '').trim() || 'count';
                    const one = span && span[1] === '1';
                    const unit = span ? `${one ? '' : span[1] + ' '}${SPANS[span[2].toLowerCase()]}${one ? '' : 's'}` : 'period';
                    measure = `${describeAggregate(agg.split(/\s+as\s+/i)[0])} per ${unit}${by ? `, by ${by[1]}` : ''}`;
                    break;
                }
                case 'stats':
                case 'chart': {
                    const by = /\bby\s+(.+)$/i.exec(rest);
                    const aggs = rest.replace(/\bby\s+.+$/i, '').split(',').map(a => a.split(/\s+as\s+/i)[0].trim()).filter(Boolean);
                    measure = `${aggs.length ? aggs.map(describeAggregate).join(' and ') : 'Number of events'}${by ? ` by ${by[1].trim()}` : ''}`;
                    break;
                }
                case 'top':
                case 'rare': {
                    const limit = /limit=(\d+)/i.exec(rest);
                    const field = rest.replace(/limit=\S+/i, '').trim();
                    measure = `${verb.toLowerCase() === 'top' ? 'Most' : 'Least'} common ${field}${limit ? `, top ${limit[1]}` : ''}`;
                    break;
                }
                case 'head':
                case 'limit':
                    if (/^\d+$/.test(rest)) first = `, first ${rest}`;
                    break;
                default:
                    break;
            }
        });
        const sentence = [(measure || 'Events') + first].concat(filters).join(', ');
        return sentence.charAt(0).toUpperCase() + sentence.slice(1);
    }

    /** The query without its aggregation, to show the events behind a number. */
    function eventsQuery(query) {
        const stages = String(query || '').split('|');
        const at = stages.findIndex((s, i) => i > 0 && /^\s*(stats|timechart|chart|top|rare|eventstats|streamstats|sort|head|tail|limit|fields|table)\b/i.test(s));
        return (at < 0 ? stages : stages.slice(0, at)).join('|').trim();
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

    /** Draws a result into an element. viz can be line, area, bar, stacked, pie, stat, gauge, heatmap, map or table, or left out to choose. */
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
            container.innerHTML = barsHtml(data, { note: options.barNote }) + dataTable;
            return data;
        }

        if (data.kind === 'stacked') {
            container.innerHTML = stackedHtml(data) + dataTable;
            return data;
        }

        const width = Math.max(320, Math.round(container.clientWidth || 640));
        if (data.kind === 'pie') {
            const size = Math.min(height, width);
            container.innerHTML = `<div class="chart-plot" style="display:flex;justify-content:center">${pieSvg(data, size)}</div>${legendHtml(data.slices.map(s => ({ name: s.label })))}${dataTable}`;
            return data;
        }

        if (data.kind === 'gauge') {
            container.innerHTML = `<div class="chart-plot" style="max-width:${Math.max(240, height * 1.6)}px;margin:0 auto">${gaugeSvg(data, 320)}<p class="chart-note" style="text-align:center">${escapeText(data.label)}</p></div>`;
            return data;
        }

        if (data.kind === 'heatmap') {
            container.innerHTML = `<div class="chart-plot">${heatmapSvg(data, width)}</div>${dataTable}`;
            return data;
        }

        if (data.kind === 'map') {
            container.innerHTML = `<div class="chart-plot">${mapSvg(data, width)}</div>${dataTable}`;
            return data;
        }

        container.innerHTML = `<div class="chart-plot">${timeSvg(data, width, height, title)}<div class="chart-tooltip" hidden></div></div>${legendHtml(data.series)}${dataTable}`;
        addHover(container, data, height);
        return data;
    }

    return { shape, render, timeSvg, pieSvg, gaugeSvg, heatmapSvg, mapSvg, niceTicks, formatNumber, escapeText, tableHtml, barsHtml, legendHtml, stackedHtml, compare, lowerIsBetter, describeQuery, eventsQuery };
})();
