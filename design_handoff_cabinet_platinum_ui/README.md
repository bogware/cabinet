# Handoff: Cabinet — "Platinum" UI redesign

## Overview
A redesign of Cabinet's web UI (currently `src/Cabinet.API/wwwroot/index.html`, a single-file vanilla HTML/JS app with a dark GitHub-style theme and a 27-item sidebar). The new direction revives Mac OS 8–9 "Platinum" industrial design — tactile, calm, clearly labeled — modernised: no bezels/etched lines, soft tonal grouping, one moss-green accent, every control labeled in words. Light theme is primary; a "Graphite" dark theme mirrors it.

Core principles to preserve in implementation:
1. **Every place and control says what it is for** — toolbar shows page title + a one-line purpose; no icon-only buttons.
2. **One default action per view**, rendered as the moss-green button with a soft focus ring (Search, Continue, Apply).
3. **Plain-English first, query language visible** — every search shows "Understood as" with a sentence and the generated SPL/BQL, broken into pipeline steps each annotated in words.
4. **Adaptive results** — the result view is chosen from the question shape (ranking → bars, "find" → events + inspector, live → stream) and the UI states why ("Chosen because you asked for one number per endpoint.").

## About the Design Files
The files in this bundle are **design references created in HTML** — prototypes showing intended look and behavior, not production code to copy. Recreate them in Cabinet's frontend. Today that's plain HTML/CSS/JS served from `wwwroot`; keep that stack (no build step) unless the team chooses otherwise — if so, a light component framework (e.g. Preact/Lit) is appropriate. Mock data in the files is illustrative; wire real API endpoints (see `docs/API-GUIDE.md`).

Open `Cabinet Redesign v2.dc.html` in a browser (it loads `support.js` and `Sidebar.dc.html` from the same folder). All styles are inline in the markup — read exact values from there.

## Fidelity
**High-fidelity.** Final colors, type, spacing, radii and copy. Recreate pixel-accurately. Mocks are drawn at a 1280×800 viewport; layouts should be fluid (sidebar fixed, content flexes).

## App shell (all screens except Setup)
- Row layout: **Sidebar 232px** fixed | **Content** flex 1.
- **Sidebar** (`Sidebar.dc.html`): bg `#ECEAE5`, right border 1px `#DCD9D2`.
  - Header 52px: icon 24px + "Cabinet" (15px/700) over server name (11px, `#5B5852`), padding 0 16px.
  - Groups (gap 10px, padding 6px 10px): header row = ▼ disclosure (9px) + label uppercase 11.5px/800, letter-spacing .04em, `#5B5852`. Groups should collapse/expand on click (persist per user).
  - Items: padding 5px 10px 5px 22px, radius 4px, 13px. Selected: bg `#5A7848`, white, 700. Right-aligned meta 11.5px `#5B5852`; alerts meta (e.g. "3 new") `#B8322A` 700.
  - Groups & items (replaces the old 27-item nav):
    - **Ask**: Search · Live Tail · Saved questions · AI investigator
    - **Watch**: Dashboards · Scheduled reports · System health
    - **Respond**: Alerts · Cases · Detection rules · ATT&CK coverage
    - **Data**: Sources · Ingest pipeline · Indexes & fields
    - **Admin**: Users & groups · API keys · Settings
    - Remaining legacy pages (Anomalies, Connectors, Lookups, Macros, Field extractions, Audit log, Background searches, Players) live as sub-tabs inside the nearest item or via "Show all".
  - Footer: 8px status dot `#5A7848` + "Healthy · 1,240 events/s" + user name, 12px `#5B5852`, padding 12px 16px.
- **Toolbar** (content top): height 52px, bg `#ECEAE5`, bottom border 1px `#DCD9D2`, padding 0 20px, gap 10px. Left: title 15px/700 + purpose 13px `#5B5852`. Right: pop-up menus and buttons.
- Content area: bg `#F3F2EE`, padding 18–24px.

## Components
- **Push button**: height 28 (toolbar) / 30–32 (forms); padding 0 12–18px; border 1px `#9C988F`; radius 4px; bg `linear-gradient(#FFFFFF,#ECEAE5)`; 13–14px/700 `#1D1D1F`. Hover: lighten to `#FFFFFF→#F3F2EE`; active: `#E1DED8→#ECEAE5`.
- **Default button**: border `#43593A`; bg `linear-gradient(#6F8C5A,#57744A)`; white 700; ring `box-shadow: 0 0 0 2px <page bg>, 0 0 0 4px #BACBA9`. Return triggers it.
- **Pop-up menu** (dropdown): push-button style; content "<muted label> <value> ▼" — e.g. "Time Last 7 days ▼"; label `#5B5852`; caret 10px.
- **Segmented control**: border 1px `#9C988F`, radius 4px, segments padding 4px 12px, 12.5px, separators 1px `#9C988F`; selected = default-button gradient, white 700.
- **Ask field**: height 46–54px, bg `#fff`, border 1px `#C9C5BD`, radius 5px, padding 0 16px. Leading label "ASK" 11.5px/800, letter-spacing .06em `#5B5852`; text 16–18px; trailing hint "Return ↵" 12px.
- **Group panel** (replaces etched group boxes — do NOT draw outlines): bg `#EAE8E3`, radius 6px, title 12.5px/700 positioned top-left (12px/16px), content padding-top ~40px.
- **Content card** (results, dashboard panels, tables): bg `#fff`, border 1px `#DCD9D2`, radius 6px. Card sub-header strip bg `#FAF9F6`, bottom border `#E1DED8`.
- **Bar row**: grid `140px 1fr 70px`, gap 14px; label mono 12.5px right-aligned; track 18px `#EFEDE9` radius 4px; fill `linear-gradient(#9DB687,#5A7848)`; value 13px/700.
- **Checkbox**: 16px, border `#6B6862`, radius 4px; on = default gradient + white ✓. **Radio**: 16px circle; on = `radial-gradient(circle,#fff 0 3px,#5A7848 3.5px)`.
- **Severity/status chip**: 11.5px/700, radius 4px, padding 1px 6px (Warning: bg `#F6EBD3` fg `#7A5410`).
- **Progress (indeterminate)**: 12px tall, radius 6px, border `#6B6862`, stripes `repeating-linear-gradient(-45deg,#BACBA9 0 6px,#E6EDDD 6px 12px)`, animate the background-position.

## Screens
**3a Search** — Ask field + default "Search". "Understood as" panel: sentence with key terms bold; pipeline chips (white, border `#E1DED8`, radius 7px) each with mono code 11.5px `#43593A` over a plain-words line 12px; separated by "|". Actions: "Edit query", "Search these exact words instead", auto-correction notice ("Fixed typo: …"). Result card with view switcher (Ranked / Over time / Table / Events) + reason text. Right column 260px: "Ask next" suggestions (links `#43593A`, hover bg `#E6EDDD`), "About this result" (Rows, Took, Read, Where), buttons Save question… / Add to dashboard… / Alert me when….

**3b Setup Assistant (first run)** — centred window 940×640 on `#E6E4DF`, no outline, shadow `0 20px 50px rgba(0,0,0,.14)`, radius 12px. Title bar 40px "Set up Cabinet". Left steps rail 250px (bg `#E9E7E2`): numbered circles 26px; current step = moss fill. Steps: Choose a source · See logs arrive · Ask a first question · Invite your team (optional). Step 1: title 26px/800 "How will your logs get here?", 2×2 radio cards (Cabinet agent [Recommended] · Splunk HEC · OpenTelemetry · Syslog; selected card border 1.5px `#5A7848`, bg `#E6EDDD`). Install-command panel with OS segmented control, mono command + Copy, indeterminate progress "Waiting for the first event… this page will move on by itself." Footer: "I'll do this later" link · Go Back · **Continue**. Auto-advance when first event arrives.

**3c Search results — events** — Events view: timeline histogram 64px (amber `#E3B866→#B7791F` for warnings), rows grid `142px 72px 64px 1fr` (time, severity chip, host, mono message), selected row = moss bg, white text. Right inspector 300px: "Selected event" field list with per-field Only / Hide buttons, Add to case… / Events around; "Fields in these results" summary (field, distinct values, coverage %).

**3d Dashboard** — toolbar: "Web traffic" + "Dashboard · updated 12 s ago"; Refresh pop-up, Time pop-up, Edit layout, Add panel…. Grid `2fr 1fr 1fr`, rows `minmax(0,1fr) auto`, gap 16px. Every panel: title 14.5px/700 + description 12.5px saying exactly what's measured. Panels: Requests per hour (area line), Server errors (big number 48px/800 `#B8322A`, delta, "Show events"), Blocked connections, Slowest endpoints (bars), Status codes (stacked bar + legend), Events by source type.

**3e Detection rules & ATT&CK** — left table 380px (On, Rule + "Last fired …", Severity, Fired count); off rules at 55% opacity. Right: "41% of common attacker techniques are covered…", legend (Covered `#5A7848` / Rule exists, turned off `#BACBA9` / Not covered `#E9E7E2`), 10-column tactic matrix of 30px cells. "Biggest gap" panel with a one-click "Backtest it on last 30 days".

**3f Case** — breadcrumb "Cases › #42 …"; Export report, **Close case…**. "ADD" field (note or in-case search). Vertical timeline (2px rail `#E1DED8`, 12px dots colored by kind: Alert `#B8322A`, Search `#5A7848`, Note `#5B5852`, Action `#2F7D4F`); entries as white cards with uppercase kind label 11px/800. Right 280px: Case details (Status pop-up, Severity, Owner, Opened, Technique, Ticket), counts, "Ask the AI investigator…".

**3g Live Tail (Graphite)** — Pause default button (Space toggles), "ONLY SHOW" plain-English filter, events/s sparkline. "Understood as" with generated filter. Stream: mono 12.5px rows grid `4px 110px 54px 64px 1fr` with severity color bar; newest at bottom; ERROR rows tinted `rgba(240,113,103,.08)`. Footer hints + "Keeping last 5,000 lines".

**3h Settings** — toolbar search "Find a setting". Inner list 210px (General, Storage, Ingest, Sign-in & SSO, API keys, Retention & archive, AI investigator [Off], Email & reports, Updates [3.5 available]). Storage pane: Disk (path, usage bar, Move…, Back up now), Storage engine radios with one-line explanation each + provenance line ("Default: SQLite · Set in appsettings.Production.json as Storage:Type"), "Keep events for [90] days" stepper with explanation. Footer: change count + "takes effect without a restart", Revert / **Apply**. Every setting shows: what it does, default, where it's configured.

## Interactions & Behavior
- Ask field: Enter runs; results reshape by intent; "Understood as" always visible above results; clicking a pipeline chip edits that step.
- View switcher remembers manual override per saved question.
- Events: click row → inspector; Only/Hide add filters to the query (and update "Understood as").
- Timeline histograms: drag to zoom time range.
- Sidebar groups collapse/expand (150ms height ease-out); selection is immediate.
- Default button: Enter; Esc = cancel/back in dialogs.
- Live Tail: Space pause/resume; click line to inspect; ⌘F find within stream.
- Settings: changes staged until Apply; Revert restores; show "requires restart" when applicable.
- Loading: indeterminate striped progress + plain sentence of what's happening. Errors: sentence explaining what went wrong and one suggested action (no codes alone).

## State
- `theme` (light | graphite, follow system by default), `sidebarCollapsedGroups`, `activeRoute`.
- Search: `question`, `parsed {sentence, steps[{code, words}], query}`, `timeRange`, `indexes`, `view` (auto | ranked | timeseries | table | events), `results`, `selectedEvent`, `stats {rows, tookMs, eventsRead, pushdown}`.
- Setup: `step` (1–4), `sourceType`, `os`, `firstEventReceived` (poll/stream).
- Live Tail: `paused`, `filter`, `parsedFilter`, `rate`, ring buffer (5,000).
- Settings: `saved`, `draft`, `dirtyCount`.

## Design Tokens
Light
- Page `#F3F2EE` · Chrome (sidebar/toolbar) `#ECEAE5` · Group panel `#EAE8E3` · Setup backdrop `#E6E4DF` · Surface `#FFFFFF` · Card header `#FAF9F6` · Bar track `#EFEDE9`
- Ink `#1D1D1F` · Secondary `#5B5852` · Placeholder `#6B6862` · Body-in-card `#3E3C38`
- Hairline `#DCD9D2` · Divider `#E1DED8` · Field border `#C9C5BD` · Control border `#9C988F`
- Moss accent `#5A7848` · Moss dark/text `#43593A` · Gradient `#6F8C5A → #57744A` · Light `#9DB687` · Tint `#BACBA9` · Wash `#E6EDDD`
- Danger `#B8322A` · Warning fg `#7A5410` bg `#F6EBD3` · Amber chart `#E3B866 → #B7791F` · Success `#2F7D4F`
Graphite (dark)
- Page `#232325` · Chrome `#1E1E20` · Surface `#2A2A2D` · Well `#18181A` · Hairline `#2E2E31` · Divider `#3A3936`
- Ink `#ECEAE5` · Secondary `#A9A69F` · Muted `#8A877F`
- Moss `#8FAD78 → #6A8957` (button), border `#3B4F30`, code `#B9D3A2`, chart `#A4BF8D`, ring `#4F6640`
- Error `#F07167` · Warn `#D9A441`

Typography
- UI: **Atkinson Hyperlegible Next** (400/500/700/800). Mono: **Atkinson Hyperlegible Mono** (400/500/700). Google Fonts.
- Scale: 11 / 11.5 / 12 / 12.5 / 13 / 13.5 / 14 / 14.5 / 15 / 16 / 18 / 22 / 26 / 48 px. Labels uppercase 11.5px/800 +.04–.06em.

Radius: controls 4px · fields 5px · panels/cards 6px · chips 4–7px · dialogs 12px. Avoid anything rounder on controls.
Spacing: 4 · 6 · 8 · 10 · 12 · 14 · 16 · 18 · 20 · 22 · 24 px.
Shadows: none on controls/cards (no bezels, no inset wells, no etched highlights). Only floating windows/menus: `0 20px 50px rgba(0,0,0,.14)` / menus `0 10px 30px rgba(0,0,0,.22)`.

## Assets
- `assets/icon.svg`, `assets/logo.svg` — from Cabinet repo `docs/images/`.
- No other imagery; charts are drawn in code.

## Files
- `Cabinet Redesign v2.dc.html` — all eight screens (3a–3h).
- `Sidebar.dc.html` — app-shell sidebar (props: `active`, `dark`).
- `support.js` — runtime needed only to open the HTML references.
