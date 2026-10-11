# Strim Design System: Signal Paper

Every page uses this system: the editor (`app.html`, `app.css`, `main.js`) and the site (landing
`index.html`, guides, comparison, blog and error pages, styled by `site.css`). Shared fonts, tokens,
buttons and cards live in `theme.css`.

Idea: a calm, warm reading surface (paper) with one loud signal colour. Strim is used on the sofa
and on the phone, so it should feel like a friendly consumer app, not a developer tool.

### Colour

Light follows the system by default; dark is a warm charcoal, never pure black.

| Token | Light | Dark | Use |
|-------|-------|------|-----|
| `--bg` | `#f4efe6` | `#14110e` | Page (with two soft radial glows: tangerine top right, violet top left) |
| `--surface` | `#fffcf7` | `#1e1a16` | Cards, sheets |
| `--surface-2/3` | `#efe8dc` / `#e7dfd1` | `#29231d` / `#352d26` | Inputs, pill tracks, secondary buttons |
| `--ink` / `--ink-2` / `--ink-3` | `#1d1a16` / `#665d53` / `#9a9084` | `#f5eee4` / `#b5aa9c` / `#7e7468` | Text levels |
| `--accent` | `#ff5b1f` | `#ff6a2e` | Tangerine signal: primary buttons, switches on, focus, live |
| `--on-accent` | `#1d0e05` | `#1d0e05` | Text on accent (dark ink, never white) |
| `--dock-bg` | `#1d1a16` | `#2b251f` | Floating dock and toasts (inverted in light mode) |

Content types have their own colour and shape, so colour is never the only cue:

| Type | Marker | Colour | Tag |
|------|--------|--------|-----|
| Live | ● circle | tangerine (`--live`) | shown only when a title is split |
| Movies | ◆ diamond | gold (`--movies`) | always |
| Series | ■ square | violet (`--series`) | always |

Status: green `--ok` ("In use" dot, success toast), `--warn-*` (expiry, sample notice), `--danger` (delete, errors).

### Type

- Display: **Bricolage Grotesque** (variable, optical size 12–96, weights 200–800). Headings,
  wordmark, playlist names, all large numbers. Weight 800, tight tracking (−0.03 to −0.05em).
- UI: **Figtree** (variable 300–900). Body 15px/450, labels 13px/700, buttons 15px/650.
- Mono: system mono for player links only.
- Both fonts are self-hosted in `fonts/` (OFL, see the licence files), latin and latin-ext subsets, `font-display: swap`. No Google Fonts request at runtime.
- Numbers use tabular figures.

### Brand

- Wordmark: "strım" in Bricolage 800 with the dot of the i replaced by a tangerine circle, the
  "live signal". It pulses twice on load (disabled with reduced motion).
- Favicon: ink rounded square, cream "s", tangerine dot.

### Shape and depth

- Radius: 14px controls, 22px cards, 24px dock, 26–28px sheets, full pills for filters and tags.
- Light mode: borderless cards with a soft warm shadow. Dark mode: no shadows, 1px warm borders.
- Touch: controls 46px on phones (42px from 768px), group rows 56px (50px), switches 46×28.

### Components

- **Switch**: native checkbox (`role="switch"`) styled as a toggle; tangerine when on.
- **Pills**: segmented filters on a `--surface-2` track; the active pill is raised (`--pill-on`).
- **Dock**: floating inverted bar with the big channel count (Bricolage) and Download/Save. Save turns into a quiet "✓ Saved" when nothing changed.
- **Link card**: tangerine-tinted corner glow, "Player link" label with signal dot, mono URL, primary Copy.
- **Sheets**: native `<dialog>`; bottom sheet with grab handle below 600px, centred above.
- **Toasts**: inverted pills at the top, coloured status dot.

### Voice

Short, plain, second person. "Switch off the groups you never watch", "All on / All off",
"In use, 3 h ago". No exclamation marks.

---

## Layout

### Editor (`/app`: app.html + app.css)

```
┌──────────────────────────────┐
│ Top bar (60px, sticky)       │  wordmark · account pill
├──────────────────────────────┤
│ Page (max-w 760px, 18px pad) │
│  #/      playlist cards      │  name, status, player link + Copy, Edit groups, ⋯ sheet
│  #/new   source form         │  URL → Load groups (guests also see the hero)
│  #/p/id  player link card    │
│          source line         │
│          sticky filters      │  search · type pills · All/On/Off · sort · All on/off
│          group list card     │  virtualised rows: marker, name, tag, count, switch
├──────────────────────────────┤
│ Dock (floating, bottom)      │  channels on · Download · Save
└──────────────────────────────┘
```

### Site (`/`, guides, blog: site.css)

```
┌─────────────────────────────────────────┐
│ Site bar (sticky, blurred)              │  wordmark · nav pills · Open app
├─────────────────────────────────────────┤
│ Main (max-w 1080px, 20px gutter)        │
│  .page-head   eyebrow · h1 · lead       │
│  .section     .section-head + .grid     │  cards, features, steps, FAQ
│  .cta-band    inverted dock colours     │  closes most pages
│ Articles: .prose-page (700px column)    │
├─────────────────────────────────────────┤
│ Footer                                  │  wordmark · line · links
└─────────────────────────────────────────┘
```

Below 720px the nav moves to a horizontally scrollable row under the bar.

### Site components

- **Hero demo** (`.demo`): a still of the group picker drawn in HTML (rows, switches, dock), so it
  follows the theme. Use made-up group names and counts, never real provider data.
- **Feature card** (`.feature`): icon tile tinted live/movies/series, h3, one short paragraph.
- **Steps** (`.steps`): numbered ink circles. **Guide** (`.guide`): long-form numbered steps with a
  connecting line, tangerine numbers.
- **FAQ**: `<details class="card">` with a rotating plus.
- **Error pages**: big tangerine status code, one sentence, Open Strim / Home. All asset paths are
  absolute because these pages are served at any URL.

---

## File structure

```
index.html          Landing page (public)
app.html            Editor shell, served at /app
theme.css           Fonts, tokens, base, wordmark, buttons, cards (shared)
app.css             Editor components
site.css            Landing, guides, blog and error pages
main.js             Editor logic (router, playlists, group picker, sheets)
features.html, how-to-use.html, comparison.html, blog/
404.html, 500.html, 50x.html
fonts/              Self-hosted Bricolage Grotesque and Figtree (woff2, OFL)
logo-icon.svg, logo-wordmark.svg
```

## Dev notes

- No CDN and no `unsafe-inline` in any page's CSP: never add inline `<script>`, `<style>` or
  `style=""` attributes; set dynamic styles through the CSSOM (`el.style.x = …`).
- Reference CSS/JS with plain relative paths (`theme.css`, `../site.css`). The Dockerfile appends
  a content hash (`?v=…`) at build time, which makes them cacheable for a year.
