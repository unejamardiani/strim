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
  Row and sheet actions use the compact controls (`.btn-xs`, `.icon-btn.is-sm`, `.btn-quiet`, `.btn-danger`,
  `.btn-danger-quiet`): 32–36px visible with a 44px touch target from `::after`, replacing the 46px rule for them.
  Primary actions are 40–44px high and never full width on desktop.

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
playlist card's Library action opens it; Edit groups in its ⋯ menu goes back to the editor.

Read-only: nothing in the Library changes the playlist or what the player link serves. It has no
switches, no bulk action, no Save and no dock; results and entries are text, not controls, except
the mark controls and, for films and series, the name, which opens the film or series card.

Film card (#367): only with TMDB on (`/api/config`), a film row's name is a button and the whole row
its tap target (hover tint, 16px chevron at every width, `.is-open` tint and 3px `--accent` inset).
Channels and, with TMDB off, every row stay text and nothing about TMDB shows. One `<dialog>`: the
app's sheet below 1100px (bottom sheet with handle below 600px, 520px centred from 600px), from 1100px
a 420px side pane beside the list that does not block the page; while it is open the page reserves
462px (490px for a viewer) on the right, so the pane never covers the list. A glass Close circle floats top right.
It loads once on open (a skeleton poster, the entry name and the marks). The stage is centred: 2:3
poster (200px, 208px in the pane; Movies-diamond box when missing), kicker "Film · year", title
(Bricolage 36px/800), up to three genres joined by " · ", then the TMDB rating and the marks capsule
(Seen, Thumbs up, Thumbs down as 36px icon buttons sharing the rows' state), then, after a second
thin line, the Watchlist button (#420): a 42px bookmark circle on its own capsule background,
"Add to watchlist" when off, "On your watchlist" (filled, `--accent-soft`) when on, in every state
that shows the capsule (loading, "choose", film and series, owner and viewer). On a 320px sheet the
row may wrap (10px row gap). The overview is clamped
to 3 lines with More/Less. "choose" lists the candidates as a 3-column grid of poster tiles; other
states and failures show the entry name, its group and a short notice. With TMDB data, TMDB's logo,
credit line and notice end it. Close, Escape, backdrop or Back close it without moving the page.

Similar films and the match (#368): below the overview a "Similar films" rail ("N in your list", from
1100px Previous/Next arrows) of 112px poster tiles in the API's order, those in the list first. An
in-list tile is a button with a green dot and its group (hollow dot and an "Off" flag when the group
is off) and opens that film's card in place, with a glass Back circle top left that returns to the
previous card and its scroll; device Back still closes the whole card. Other tiles are greyed out,
"Not in your list", and open nothing. A seen film carries a small check on its poster. "choose"
candidates are buttons: a tap stores the match and reloads the card. A matched card ends with "In
your list as" (entry, group) and "Not this film", which clears the match. A failed request brings the
previous card back with an error notice.

Series card (#390): with TMDB on, a series row's name opens the same card from `series-card`, and
another episode row swaps it in place. Everything is the film card's except: the kicker carries the
Series square, "Series · 2019–2023", "Series · since 2019" while it runs or "Series · 2019"; under the
genres a facts line (13px/550, `--ink-3`) joins state, seasons, episodes and network with " · ",
leaving out what is missing; a missing poster shows the Series square on `--series-soft`; the marks
set the series mark, so every row of the series repaints; the texts say "series" for "film"
("Similar series", "Which series is it?", "Not this series", "Series data and posters: TMDB").

In your list (#391): between overview and similar series, a film section head "In your list" with
"21 of 32 episodes" (or "21 episodes" without TMDB's total), hidden when the list holds nothing of
the series. Seasons are 38px pills on the film rail's mask ("S1" and count/total, "Specials" for
season 0); a season the list lacks is outlined in `--line-strong` with `--ink-3`. A pill opens its
episodes below (one at a time, closed at first) as a grid of 36px numbers, filled when in the list,
outlined when not, at most 200 then "and N more", with a 12px legend. Opened from an episode row,
the season pill carries a 7px Series square, the episode `--series-soft` with a 2px `--series`
ring, and while no season is open a line reads "Opened from S1 · E3". Entries without an episode
number are counted in a line of their own.

Similar series and the match (#392): the film card's rail, tiles, in-place card with glass Back,
"choose" grid and "Not this series", unchanged. An in-list similar series opens as a series card
without the "Opened from" context. A stored or cleared match holds for every episode row of the
series, so each of them loads its card again; a failed request brings the previous card back.

Marks (#298): every film and series row (search results and group entries) ends with its marks.
From 768px that is the wide form, the capsule: Seen (check), Thumbs up and Thumbs down as 32px icon
buttons (since #383, instead of `--control-h` circles) with `aria-pressed`. Below 768px
(#419) the row keeps the room for the title: a 15px liked glyph (thumb up in `--accent`, thumb down
in `--ink-3`, nothing when unrated; its state is in the group label of the Seen button) and one
round 36px Seen button (44px touch target, `aria-label="Seen"`); thumbs are set in the card. A
pressed control has the `--accent-soft` fill; tapping it again undoes it. A seen title's name is
`--ink-2` at weight 550. A mark is the user's, not the playlist's: it is stored at once and shows on
every variant of the title in the view. A failed request puts the mark back and shows an error
toast. From 768px the Watchlist button stands directly before the capsule; below 768px rows have
none (the card has it) and a watchlisted row shows no badge. Seen wins: marking a title seen takes it
off the watchlist at once on every control of the title, and the server does the same; a failed
request puts seen, rating and watchlist back. Channels and names without a title get no controls. Two toggle chips after the type chips,
"Unseen" and "Liked", filter the search and the open group for the page session; with Unseen on,
the count of seen matches left out is shown. The editor shows no marks.

Rows (#419): a search result and a group entry share one row, 56px minimum: the type marker (Live
circle, Movies diamond, Series square), the name without a trailing "(YYYY)" and the year in
`--ink-3` (the full name stays the accessible name), a meta line in `--ink-3` with the group in
`--ink-2` and the type word (search results only, then the language labels), "Off", the marks and,
for a row that opens a card, the chevron.

My marks (#299): a section below Browse groups lists every mark of the user, newest change first,
50 at a time with More, including titles no longer in any playlist. It shows even when the playlist
does not load. Pills filter it to All, Seen, Liked or Disliked. A row is plain text, never a link to
an entry: display title, then year (when known) · Film or Series · Seen · Liked or Disliked, and a
Remove button at `--control-h`. Remove asks no confirmation: the row goes at once and a toast offers
Undo, which sets the same mark again and puts the row back in its place. With no marks it says so
and how to mark a title.

My list by default: the Library shows the entries of the groups that are on. One switch in the ⋯
menu, "Include switched-off groups", adds the groups that are off and marks their groups and entries "Off". It is
kept for the page session only. With the toggle off, the count of left-out matches and the number of
hidden groups are shown.

Tabs and routes (#418): Search, Groups and Marks, each with its own address:
`#/p/<id>/library/search|groups|marks` for the owner, `#/shared/<id>/search|groups|marks` for an
invited viewer. The root and an unknown tab open Search and replace the address. Tabs are links,
the active one with `aria-current="page"`; a tab change keeps the loaded list, the typed query, an
open group and the loaded marks (the panels stay in the DOM, hidden), scrolls to the top and, from
the keyboard, moves the focus to the tab's first field or heading. Marks shows at once; Search and
Groups show the loading card or the error inside their panel.

Head (owner): back (36px, quiet), kicker "Library" and the playlist name on one line with an
ellipsis (Bricolage 800, 26px, 32px from 768px), the tabs from 768px, and ⋯ (36px, `--surface-2`).
⋯ opens the app's sheet titled with the playlist name: "25 of 38 groups on · 59,060 of 119,070
entries in your list" ("Loading the list" before), the read-only hint, Edit groups and the
"Include switched-off groups" switch. A viewer keeps the cover as the head, without ⋯, the tabs
under it.

```
┌──────────────────────────────┐
│ Head                         │  ‹ · LIBRARY / playlist name · tabs (from 768px) · ⋯
│ Search tab                   │  field · chip row (All/Films/Series/Channels | Unseen/Liked)
│                              │  results card: rows (name and year, group · type, Off, marks)
│ Groups tab: Browse groups    │  type chips with group counts, one list of rows, playlist order
│   open group                 │  All groups · title · count, entries card, More
│ Marks tab: My marks          │  filter pills · count, marks card (title, meta, Remove), More
│ Tab bar (below 768px)        │  floating, dock colours: Search · Groups · Marks
│ Film card                    │  sheet, or side pane from 1100px: poster, facts, marks, overview
└──────────────────────────────┘
```

Search: the editor's search, read-only, 300ms after typing; a result shows name, group and type.
Its filters are one chip row (#419): All, Films, Series, Channels (a single choice), a thin
separator, then the toggles Unseen and Liked, all 34px chips with `aria-pressed` (a chosen type
`--ink` on `--bg`, an active toggle `--accent-soft`). The row never wraps; it scrolls sideways
without a visible scrollbar. Below 600px the result status takes its own line and the Language
chip sits below it.
Search in sections (#421): one row per title from the titles route, a series as one row (bold
"S1–S3 · 21 episodes", then the group), a film in several entries as "2 versions". The field (with
a clear button) and the chip row stick under the top bar; the chips carry the counts of the answer
("All 17", "Films 12"). All shows up to three sections, Films, Series and Channels, each with its
count, at most 4 rows and, above 4, "Show all 12 films", which picks that type; from 1100px Films
is the left column, Series and Channels stack on the right. A single type loads 30 titles at a
time as the end of the list comes within 600px. The status line names the titles and those left
out ("7 more in switched-off groups" with "Show them", "3 seen titles hidden"). The query and type
live in the address, `#/p/<id>/library/search?q=…&type=films`, replaced while typing.
Browse: the groups by type, then in playlist order; opening one pages its entries 50 at a time with
More until all are shown.
Type chips and group rows (#422), owner and viewer alike: a chip row (single choice, the search's
chips) with "Films 11", "Series 7", "Channels 7", the number of groups of each type, only for types
that have groups; the first is chosen at the start, the choice kept for the page session and in the
address, `#/…/groups?type=films|series|channels` (replaced). 12px below, one `.lib-list` of the
chosen type's groups in playlist order. A row is one button: type marker, title (one line, ellipsis,
"Untitled group"), meta "5,412 films", "12,840 episodes" or "214 channels" (singular for 1), "Off"
(`.lib-off`, owner with switched-off groups included), "+12 new" (`.lib-new`) and the chevron.
"+N new" is per device: `localStorage` `strim.library.counts.<playlistId>` holds `{ groupKey: count }`;
a group whose count is above the stored one shows the difference. The first load on a device stores
every count and marks nothing; opening a group stores its count (the badge goes at the next draw);
groups that no longer exist are dropped when it is written. Unavailable storage or invalid JSON
shows no badge; nothing is sent to the server. From 1100px the open group's row carries `.is-open`.

Phone layout (below 1100px): one 760px column. Below 768px the tabs are a floating bar at the
bottom (62px, 24px radius, the dock's inverted colours, icon over label, the active tab tinted with
an `--accent` icon); the footer pads 104px to scroll clear of it, and an open film card's backdrop
covers it. From 768px they are a segmented control in the head (owner) or in a row under the cover
(viewer). An open group replaces the group list; All groups returns to the list with the focus on
the group that was open.

Wide Library (from 1100px, CSS only): once the groups are loaded the page widens to a 1440px
maximum (same 18px padding). While a group is open, Browse becomes two columns with a 24px gap,
`minmax(300px, 0.4fr) minmax(0, 1fr)`: left the group list, which stays shown, right the open
group's entries, sticky below the top bar and scrolling within the screen height. The head with the
segmented tabs, the search and the Browse title span the width; with no group open the list keeps
the full width.

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
