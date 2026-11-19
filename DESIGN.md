# Strim Design System: Signal Paper

Every page uses this system: the editor (`app.html`, `app.css`, `main.js`) and the site (landing
`index.html`, guides, comparison and error pages, styled by `site.css`). Shared fonts, tokens,
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

Wide editor (from 1100px, CSS only): once the groups are loaded, the editor page widens to a
1440px maximum (same 18px padding) and the picker becomes two columns with a 24px gap,
`minmax(0, 1fr) minmax(300px, 0.4fr)`. Left, the larger share (about 740px at 1100px, 985px at
1440px): sticky filters, search results and the group list, so search, filters and All on/off
stay reachable above the list. Right, the rest but at least 300px: the entry pane, a sticky area reserved for the
entries of a group (a dashed 22px outline while it is empty).

Desktop group row (from 1100px): each row is a one-line grid with fixed tracks, so the columns
line up down the list. In order: marker (10px), name (1fr but at least 260px, so every track
still fits at 1100px), type tag (64px; an unsplit Live row hides its tag and its name spans the
tag track as well, at least about 336px), sample titles
(1fr: the first entry titles of the group, comma-joined and cut off with an ellipsis), adult
mark (36px: an "18+" tag on `--surface-2` in `--ink-2`), split origin (0.6fr: "from" and the
provider's group title when Strim split that title by content type), duplicates and variants
(48px each, right-aligned 12px/650 tabular figures in `--ink-2`, such as "3 dup" and "2 var": the
group's entries in duplicate sets and in variant groups, from `groupCounts` of the last report),
count (56px, right-aligned) and switch (46px). The two report cells stay empty until the person
runs the report for the loaded playlist and for a group the report has no findings for; loading
another playlist empties them again, so they never show 0. Sample titles and origin are 13px/450
in `--ink-3`. A value that is unknown or empty leaves its cell empty, with no placeholder. Row
height stays `--row-h`. Below 1100px these five cells are not shown and the row is the flex row of marker, name, tag, count and switch. The head, player link card and
source line span both columns; the dock stays centred at 736px. Below 1100px, and on the new playlist
route, the 760px page applies unchanged and the entry pane is not shown.

Playlist card facts (from 768px): below the status line a quiet line in `--ink-3` (numbers in
`--ink-2`, tabular figures) shows the stored facts from `/api/playlists/overview`: "12 of 40 groups
on", "8,214 entries", "Changed 2 d ago", "Player fetched 3 h ago" and the source host. A fact that
is not stored is left out. Below 768px the line is hidden, so the phone card is unchanged.

Wide playlist list (from 1024px, CSS only): when the list (`#/`) has playlists, its page widens to
a 1200px maximum (same 18px padding) and the playlist cards form a grid,
`repeat(auto-fill, minmax(340px, 1fr))` with the list's 14px gap: two cards per row at 1024px,
three at 1200px and wider. Empty tracks stay reserved, so one or two playlists keep a single
column's width instead of stretching. Cards in a row share the tallest height and Edit groups sits
at the bottom. Cards keep DOM order, so reading and tab order run left to right, row by row. Below
1024px the list is the single 760px column; the empty state keeps the 760px page. Only the editor changes: the site pages keep their 1080px main column and the guides their
700px column.

### Library (`#/p/<id>/library`: library.js + library.css)

Purpose: find what a saved playlist holds, films, series and channels, without editing it. The
playlist card's Library action opens it; Edit groups in its head goes back to the editor.

Read-only: nothing in the Library changes the playlist or what the player link serves. It has no
switches, no bulk action, no Save and no dock; results and entries are text, not controls, except
the mark controls below.

Marks (#298): every film and series row (search results and group entries) ends with three round
icon buttons, Seen (check), Thumbs up and Thumbs down, `--control-h` square (46px on phones, 42px
from 768px) with `aria-pressed`. A pressed control has the `--accent-soft` fill; tapping it again
undoes it. A mark is the user's, not the playlist's: it is stored at once and shows on every variant
of the title in the view. A failed request puts the mark back and shows an error toast. Channels and
names without a title get no controls. Two more toggles next to "Include switched-off groups",
"Hide seen" and "Liked only", filter the search and the open group for the page session; with Hide
seen on, the count of seen matches left out is shown. The editor shows no marks.

My marks (#299): a section below Browse groups lists every mark of the user, newest change first,
50 at a time with More, including titles no longer in any playlist. It shows even when the playlist
does not load. Pills filter it to All, Seen, Liked or Disliked. A row is plain text, never a link to
an entry: display title, then year (when known) · Film or Series · Seen · Liked or Disliked, and a
Remove button at `--control-h`. Remove asks no confirmation: the row goes at once and a toast offers
Undo, which sets the same mark again and puts the row back in its place. With no marks it says so
and how to mark a title.

My list by default: the Library shows the entries of the groups that are on. One toggle, "Include
switched-off groups", adds the groups that are off and marks their groups and entries "Off". It is
kept for the page session only. With the toggle off, the count of left-out matches and the number of
hidden groups are shown.

```
┌──────────────────────────────┐
│ Head                         │  back · playlist name · Edit groups
│ Summary card                 │  groups on, entries in your list, read-only hint
│ Search                       │  field · type pills (All/Films/Series/Channels) · toggle
│                              │  results card: name, group · type, Off badge
│ Browse groups                │  one card per type (Films, Series, Channels), playlist order
│   open group                 │  All groups · title · count, entries card, More
│ My marks                     │  filter pills · count, marks card (title, meta, Remove), More
└──────────────────────────────┘
```

Search: the editor's search, read-only, 300ms after typing; a result shows name, group and type.
Browse: the groups by type, then in playlist order; opening one pages its entries 50 at a time with
More until all are shown.

Phone layout (below 1100px): one 760px column. An open group replaces the group list; All groups
returns to the list with the focus on the group that was open.

Wide Library (from 1100px, CSS only): once the groups are loaded the page widens to a 1440px
maximum (same 18px padding). While a group is open, Browse becomes two columns with a 24px gap,
`minmax(300px, 0.4fr) minmax(0, 1fr)`: left the group list, which stays shown, right the open
group's entries, sticky below the top bar and scrolling within the screen height. Head, summary,
search and the Browse title span the width; with no group open the list keeps the full width.

### Site (`/`, guides: site.css)

```
┌─────────────────────────────────────────┐
│ Site bar (sticky, blurred)              │  wordmark · nav pills · Open app
├─────────────────────────────────────────┤
│ Main (max-w 1080px, 20px gutter)        │
│  .page-head   eyebrow · h1 · lead       │
│  .section     .section-head + .grid     │  cards, features, steps, FAQ
│  .cta-band    inverted dock colours     │  closes most pages
│ Guide: .guide (700px column)            │
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
- **Error pages**: a small television (`.tv`, decorative, `aria-hidden`) in the dock's colours
  next to the copy (above it on phones): eyebrow with the status code, a heading with the one
  joke, one plain sentence, Open Strim / Home (plus Try again on 5xx). The screen tells the pages
  apart: 404 snow with "No signal" (inline SVG `feTurbulence`), 500 colour bars in the content
  type colours with "Please stand by", 50x a test card with the wordmark. Motion (rolling bar,
  drifting snow, a jolt, the pulsing dot) is CSS only and stops with reduced motion. All asset
  paths are absolute because these pages are served at any URL.

---

## File structure

```
index.html          Landing page (public)
app.html            Editor shell, served at /app
theme.css           Fonts, tokens, base, wordmark, buttons, cards (shared)
app.css             Editor components
site.css            Landing, guides and error pages
main.js             Editor logic (router, playlists, group picker, sheets)
features.html, how-to-use.html, comparison.html
404.html, 500.html, 50x.html
fonts/              Self-hosted Bricolage Grotesque and Figtree (woff2, OFL)
logo-icon.svg, logo-wordmark.svg
```

## Dev notes

- No CDN and no `unsafe-inline` in any page's CSP: never add inline `<script>`, `<style>` or
  `style=""` attributes; set dynamic styles through the CSSOM (`el.style.x = …`).
- Reference CSS/JS with plain relative paths (`theme.css`, `../site.css`). The Dockerfile appends
  a content hash (`?v=…`) at build time, which makes them cacheable for a year.
