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
| `--ink` / `--ink-2` / `--ink-3` | `#1d1a16` / `#50483f` / `#6b6258` | `#f5eee4` / `#b5aa9c` / `#a09588` | Text levels |
| `--accent` | `#ff5b1f` | `#ff6a2e` | Tangerine signal: primary buttons, switches on, live |
| `--focus` | `#c2410c` | `#ff6a2e` | Keyboard focus ring; 3:1 or more on every surface incl. the dock |
| `--on-accent` | `#1d0e05` | `#1d0e05` | Text on accent (dark ink, never white) |
| `--edge` | `#857a6d` | `#857a6d` | Boundary of fields, switches, selected segments: 3:1 or more on every surface, one value for both themes |
| `--dock-bg` | `#1d1a16` | `#2b251f` | Floating dock and toasts (inverted in light mode) |

Contrast: the text tokens (`--ink`, `--ink-2`, `--ink-3`, `--accent-ink`) are at least 4.5:1 on `--bg`,
`--surface`, `--surface-2` and `--surface-3` in both themes (WCAG 2.2 AA; light `--accent-ink` also on
`--accent-soft`). `--ink-3` is the quietest text colour; nothing lighter may carry text. Text is never dimmed
with `opacity`. Glyphs drawn into fields (search lens, select arrow) use `#857a6d`, at least 3:1 on the field
colours of both themes. A control that is told apart only by its fill needs an `--edge`. `ContrastTokenTests`
computes these ratios from `theme.css`.

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
- Quiet buttons, icon buttons and chips carry a 1px inset `--edge`; primary and ghost buttons do not.
- Touch: controls 46px on phones (42px from 768px), group rows 56px (50px), switches 46×28.
  Row and sheet actions use the compact controls (`.btn-xs`, `.icon-btn.is-sm`, `.btn-quiet`, `.btn-danger`,
  `.btn-danger-quiet`): 32–36px visible with a 44px touch target from `::after`, replacing the 46px rule for them.
  Controls drawn smaller than 44px (small buttons, pills, text links that stand alone) get the 44px target from
  `::after` on phones; a new control below 44px needs the same.
  Primary actions are 40–44px high and never full width on desktop.

### Components

- **Fields**: `.input` on `--surface-2` with a 1.5px `--edge` border; the focus mark (`--focus`) replaces it. The
  playlist name (`.input-name`) shows the edge only on hover.
- **Switch**: native checkbox (`role="switch"`) styled as a toggle. Off: `--surface-3` track with a 1.5px inset
  `--edge` ring and an `--edge` knob. On: tangerine track without ring, `--on-accent` knob. On and off differ by
  position, fill and knob.
- **Pills**: segmented filters on a `--surface-2` track; the active pill is raised (`--pill-on`) and outlined with
  a 1.5px inset `--edge`.
- **Dock**: floating inverted bar with the big channel count (Bricolage) and Download/Save. Save turns into a quiet "✓ Saved" when nothing changed.
- **Link card**: tangerine-tinted corner glow, "Player link" label with signal dot, mono URL, primary Copy.
- **Sheets**: native `<dialog>`; bottom sheet with grab handle below 600px, centred above.
- **Toasts**: inverted pills at the top, coloured status dot.
- **Icons**: 24px grid line icons built by `icon()`, class `.icon`: `currentColor` stroke 2px with round caps and joins, no fill. A holder rule (`.btn svg`, `.lib-seen svg`, …) may set the size, the stroke width or a fill; only state marks are filled (watchlist badge, pressed bookmark, rating star, the dots of ⋯).
  On state (#494): an icon that is the direct child of `[aria-current="page"]` or `[aria-pressed="true"]` fills its
  body (`.b`) with a 14% tint of its own ink and turns its signal dot (`.sig`) `--accent`, grown by a quarter around
  its centre. The dot lies clear of the body, so tangerine sits on the plain bar; it never gets a ring, a hole or an
  ink fill. The pressed bookmark stays a solid fill with its dot as a hole.
  The icons are one family, Stem and dot, built like the wordmark's "ı": stems are 2px strokes with round ends,
  and the signal is a filled dot (class `sig`, filled with `currentColor` by `.icon .sig`). Every object glyph
  carries exactly one dot (⋯ is three), while direction and operator glyphs (chevron, close, plus, check, sort)
  and the star carry none. The dot's radius is 1.5 to 2.2 on the 24 grid, and a new icon follows the rule and is
  added to `ICONS`.
  The 30 names: back, folder, more, copy, download, list, refresh, trash, link, clock, plus, check, bookmark, close,
  sort, up, down, chevron, star, home, book, people, search, grid, and six object glyphs, each with one dot:
  - `marks`: the Marks tab, a check whose stroke ends in the dot; the check is the body (`.b`, #494) and takes the tint when current (`check` stays on a seen tile, Saved and sharing).
  - `trend`: trending, a rising line with the dot ahead of it.
  - `spark`: because you liked, a four-point spark.
  - `signal`: live, on air, two broadcast arcs from the dot.
  - `tv`: channels, a screen with its antenna.
  - `eye`: the seen filter, an eye with the dot as its pupil.

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
Channels and, with TMDB off, every row stay text and nothing about TMDB shows. One modal `<dialog>`,
the app's sheet at every width (#430): bottom sheet with handle below 600px, 520px centred from 600px,
from 1100px a centred 560px dialog (588px for a viewer); from 600px it is at most `min(88dvh, 860px)` high
and scrolls inside; the Library behind keeps its full width. Backdrop, Escape, Close and Back close it, and the
focus returns to the row or tile that opened it. A glass Close circle floats top right.
It loads once on open (a skeleton poster, the entry name and the marks). The stage is centred: 2:3
poster (200px, 208px from 1100px; Movies-diamond box when missing), kicker "Film · year", title
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
"Not in your list", and open nothing. A mark set in a card opened from a tile shows on the title's
rows in Search, Groups and Marks at once, while it is still being saved (#458). A seen film carries a small check on its poster. "choose"
candidates are buttons: a tap stores the match and reloads the card. A matched card ends with "In
your list as" (entry, group) and "Not this film", which clears the match. A failed request brings the
previous card back with an error notice.

Series card (#390): with TMDB on, a series row's name opens the same card from `series-card`; the
card is modal, so no other row can be tapped while it is open. Everything is the film card's except: the kicker carries the
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
for a row that opens a card, the chevron. With TMDB on (#429) a search result of a film or series
starts with a 40×60 poster thumbnail (`.lib-thumb`, 8px radius, the poster loader of the tiles) in
place of the marker, over the type's soft colour with the marker centred, at 45% opacity when seen;
such a row (`.has-thumb`) is 76px minimum with 10px left padding. Channels and group entries keep
the marker.

My marks (#299): the Marks tab lists every mark of the user, newest change first, including titles
no longer in any playlist; a record kept only for a confirmed TMDB match is no mark and is neither
listed nor counted. It shows even when the playlist does not load. Its head (#446), sticky with the
chips in `.lib-sticky`, is the title "My marks" (`.lib-ghead`) over the count of the view: "65 marks ·
newest change first", for the Watchlist "14 titles · newest first", empty until the first page
answers. Five single-choice chips (`.lib-chips`, `aria-pressed`) pick the view: Watchlist (newest on
the watchlist first), All, Seen, Liked or Disliked; the tab opens on All and the active chip shows the
count in its `.n`. The view is kept in the address, `#/…/marks?view=watchlist|all|seen|liked|disliked`,
replaced and never a new history entry, only while the Marks panel is shown; the Marks tab link carries
it, and a link to it switches a Library already on screen. Pages of 50 load while scrolling, once a
sentinel comes within 600px of the screen. The list (`.lib-list`) follows 6px below the head; it keeps
the 760px column and from 1100px is at most 920px wide. A row (#455) is a search row (`.lib-row`,
mark key in `data-key`): type marker, display title and year, a meta line, the search rows' mark
controls (#456: `.lib-quick` below 768px, from 768px `.lib-watch` and the capsule
`.library-marks.film-marks`), and the chevron. After each page the tab asks the titles
lookup once with the page's keys (again for every loaded page when the list arrives or "Include
switched-off groups" changes; an answer for a view, setting or Library left since changes nothing).
A title in this list shows its group, for a series bold "S1–S3 · 21 episodes" · group, and its row
opens the film or series card (with TMDB on), never a link to an entry; any other row, also while the
list loads or after a failed lookup, reads "Not in this list", opens nothing (`is-static`) and keeps
its chevron's room hidden. A changed mark shows in place on every control of the title (#448), set
in the row, a card, Search or a group. A row whose mark is now empty or no longer fits the view (marked seen in
the Watchlist, for example) leaves at once and the count in the head and the chip drops by one; the
toast "Mark on <title> removed" (in the Watchlist "<title> is off your watchlist") offers Undo, which
sets the previous mark again and puts the row back in its place. At every width a change made in the
modal card offers its toast when the card closes. While the Marks tab is hidden a row leaves without a
toast. Outside the Watchlist a changed row that still fits moves to the top, and back when the save
fails; a title marked elsewhere that now fits the view without a row loads the view again (when the
tab is shown next). After a change (#457) scrolling still reaches every title of the view, whatever order the save and the pages answer in:
a page a save overlapped is shown, and once no save is on its way the list asks its pages again
from page 1 ("Loading more…" meanwhile). With no marks it says so
and how to mark a title: the Watchlist "Nothing on your watchlist yet. Add a film or series with the
bookmark in its card.", All how to mark a title, the other views "No marks of this kind.".

Marks grid (#429): with TMDB on the Marks tab shows the same views and pages as poster tiles in the
`.lib-grid` of #428 (3, 5 and 7 columns), 6px below the sticky head and chips; from 1100px the tab
is at most 1280px wide in grid view. A tile's meta is the year; a title not in this list reads "Not
in this list" and is no button. Posters are asked with the lookup's name and group, for a title not
in this list with "<title> (<year>)", its kind and no group. Marks are changed in the card; a tile
whose mark became empty or left the view goes at once with the same Undo toast. The head's view
button (`.lib-ib`, `list` icon "Show as list" / `grid` icon "Show as posters") switches to the rows
and back, kept per device in `localStorage` `strim.library.marksView` (default grid); switching
keeps the loaded marks and starts at the top. With TMDB off the tab keeps the rows without the button.

My list by default: the Library shows the entries of the groups that are on. One switch in the ⋯
menu, "Include switched-off groups", adds the groups that are off and marks their groups and entries "Off". It is
kept for the page session only. With the toggle off, the count of left-out matches and the number of
hidden groups are shown.

Tabs and routes (#418, #425): Home, Search, Groups and Marks, each with its own address:
`#/p/<id>/library/home|search|groups|marks` for the owner, `#/shared/<id>/home|search|groups|marks`
for an invited viewer. The root and an unknown tab open Home and replace the address. Tabs are links,
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

Home (#425), without posters: the owner's cover is a tinted card (`.lib-hero`, accent and series
mixed into `--surface`, no dark override) with the playlist name as the page's `h1` (Bricolage 800,
34px, two lines at most), "59,060 entries in 25 groups · read-only" (groups that are on; "Loading the
list" before) and the field "Search this list"; from 768px name and field share one row. On Home the
owner's head shows the kicker "Library · <name>" and no `h1`. A viewer keeps the cover and gets the
plain field under it. Focusing or typing in the field moves to Search with the text carried over
(`…/search?q=…`) and the focus in its field. Below it, 22px apart: Your watchlist (12 watchlisted
marks newest first, one lookup, up to 5 Marks-tab rows, the count, All to the Watchlist view; an
empty line when none), Series you follow (liked series in this list, up to 5, "N liked series", All
to Liked; left out when none), New in your groups (up to 3 groups with "+N new", largest first, from
the device's counts, read without writing; left out when nothing is new) and Browse (one tile per
type with groups that are on: marker, name, "11 groups · 17,817", opening Groups with that type).
Each section shows its own "Loading…" line and its own error with Try again; the marks sections
show also when the list failed. From 1100px the Home panel fills the page up to 1280px, never scrolls the page
sideways, and the two lists sit side by side. Marks set anywhere repaint Home's rows; a title marked seen leaves the watchlist.

Poster tiles (#427), only with TMDB on: `.lib-tile` is a 2:3 art box (`.lib-art`, radius 12px, 112px
wide, 148px from 768px), the title (13.5px, one line, ellipsis) and a meta line (12px). The art starts
as the fallback, the type's soft colour with its marker centred; a poster address fades the `<img>`
in over it (lazy, `referrerpolicy="no-referrer"`, `alt=""`, 342×513), an image error returns to the
fallback. Badges follow the shared marks: a seen check top right (the image then at 45%), a bookmark
bottom left when on the watchlist. A series tile can carry a bar "21 episodes in list" on a dark
gradient (`#fff`, the one fixed colour). A title in the list is a button opening its card (label:
title, year, group, "seen"); any other a div. One loader per Library asks the posters of tiles within
300px of the viewport, 24 at most per batch after 150ms, one batch at a time; pending ones again after
4s, three times at most; failures leave the fallback without a toast, a 404 stops it.

Home's rails (#427), with TMDB on: Your watchlist and Series you follow show up to 12 tiles in a
`.lib-rail` (meta: year or "Not in this list"; the group with the episodes bar) instead of rows, and
"New in <group>" (one line, ellipsis) shows the first 12 titles of the group with the largest "+N
new" in playlist order, "+38 since your last visit" and "Open group"; New in your groups keeps the
other groups below it. Rails scroll sideways, snap to tiles and, from 1100px, carry the similar
rail's Previous and Next (`.film-rail-nav`) in the section head. With TMDB off Home keeps its rows.

Trending this week (#478), with TMDB on: an ordinary Home section between the two lists and "New in
<group>", head "Trending this week" with the number of tiles, no link, and up to 12 tiles in a
`.lib-rail` like Your watchlist. Only titles that are in the list appear (the server matches them),
without seen and thumbs-down titles; meta and card as in "New in <group>". The poster comes with the
answer, so these tiles never ask the posters route; a title without one keeps the fallback. Asked once
the list is there, the section shows the "Loading…" line; with fewer than 3 titles, on any failure and
with TMDB off it is absent, with no notice. The answer is kept while the Library is open and asked
again when "Include switched-off groups" changes or the page reloads; a title marked seen keeps its
tile, with the check, until then.

Because you liked <title> (#479), with TMDB on: a second ordinary Home section directly under
Trending this week and before "New in <group>", with the same head, rail and tiles. The server picks
one thumbs-up title per UTC day as the seed and answers its recommendations that are in the list; the
head reads "Because you liked " and the seed's title (text, also the h2's `title`), on one line with
an ellipsis (`.lib-sec-one`) so the count and the arrows keep their place; the rail's label is the same
text. The heading is only known with the answer, so the section stays hidden while asking (no
"Loading…" line). Without a seed, with fewer than 3 titles, on any failure and with TMDB off it is
absent, with no notice. A thumb set or cleared while the Library is open does not ask again: the
heading keeps naming the seed of the answer shown until the next load of the Library. The two
sections load independently.

```
┌──────────────────────────────┐
│ Head                         │  ‹ · LIBRARY / playlist name · tabs (from 768px) · ⋯
│ Home tab                     │  tinted cover (name, facts, search field) · Your watchlist · Series you follow
│                              │  New in your groups (+N new) · Browse: Films / Series / Channels tiles
│ Search tab                   │  field · chip row (All/Films/Series/Channels | Unseen/Liked)
│                              │  results card: rows (name and year, group · type, Off, marks)
│ Groups tab: Browse groups    │  type chips with group counts, one list of rows, playlist order
│   open group                 │  All groups · title · count, entries card, More
│ Marks tab: My marks          │  head (title, count), view chips, rows (title, in this list or not, mark controls, card)
│ Tab bar (below 768px)        │  floating, dock colours: Home · Search · Groups · Marks
│ Film card                    │  sheet, centred 560px from 1100px: poster, facts, marks, overview
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
Browse: the groups by type, then in playlist order. An open group (#443) asks the titles route
(`group`, playlist order, `hideSeen` / `liked` while Unseen / Liked are on) 30 titles at a time,
the next page once a sentinel comes within 600px of the screen. Its sticky head (`.lib-sticky` with
`.lib-ghead`, under the top bar): the back button (`.lib-ib`, `aria-label="All groups"`, a link to
`…/groups?type=<type>`), the title (20px display face, one line, ellipsis, the full title in
`title`), below it "5,412 films" (the group's count with film(s), episode(s) or channel(s)), plus
" · 318 seen hidden" while Unseen is on, and "Off" (`.lib-off`). Rows are the search's title rows
without a meta line, except "S1–S3 · 21 episodes" for a series and "2 versions" for a film in
several entries. The foot (`.lib-foot`, 13px `--ink-3`, centred, a hairline above) says "All 5,412
shown" (the answer's total after the mark filters) once all are loaded; with nothing, "No entries in
this group." or, with a mark filter on, "Nothing in this group matches.". A failed later page shows
Try again below the rows, a failed first page in their place; a mark filter or a new address starts
again from the first page, and opening a group scrolls the page to the top. The grid (#428): with
TMDB on, a Films or Series group opens as poster tiles (`.lib-grid`, the Home tiles from the same
30-title pages; 3 columns on a phone, 5 from 768px, 7 from 1100px; the sentinel and the foot span
all columns). A tile's meta is the year, for a series "S1–S3 · 21 episodes"; it opens the card,
where the marks are set. The head's right end has the view button (`.lib-ib`): the `list` icon
("Show as list") in grid view, the `grid` icon ("Show as posters") in list view, the choice kept
per device in `localStorage` `strim.library.groupView` (default grid). Filter, Unseen, Liked and
sort work in both views; switching keeps the loaded titles and starts at the top. From 1100px the
grid fills the page width (up to 1280px) without the group list beside it (Back returns
to it); list view keeps the two columns. Channel groups, and every group with TMDB off, keep the
list without the button. An open group has its
own address (#442),
`#/p/<id>/library/groups/<encoded group key>` (viewer `#/shared/<id>/groups/<…>`): a row navigates
there (a history entry), a reload keeps the group open and its type chip chosen, and the browser's
Back or All groups (a navigation to `…/groups?type=…`) returns to the list with the focus on the
group's row once the list is drawn again. A group that is not in the list, or not shown, falls back
to the list with the toast "That group is not in this list any more." and the list's address
(replaced). While a group is open the list's address is not written over the group's.
Below the title in the sticky head (#444): a filter field (`.lib-search.lib-gfilter`, 10px above,
placeholder and `aria-label` "Filter " + the group title, `.lib-clear` "Clear filter") sends `q`
300ms after the last key once it has 2 letters or digits; below that the unfiltered group shows.
Then one chip row: "Unseen" and "Liked" (toggles, the Search tab's shared chips: either one changes
both tabs) and a sort chip (15px `sort` icon, not a toggle) cycling "Playlist order", "A to Z",
"Newest year" (`sort=playlist|title|year`, `aria-label="Sort: A to Z"`). A change starts again
from the first page; with a filter text the empty foot says "Nothing in this group matches.".
The group's address keeps them, replaced, never a new entry: `…/groups/<key>?q=…&sort=title|year&unseen=1&liked=1`,
defaults left out; opening a group (reload, pasted address, first visit) takes all four from it
(an unknown `sort` is playlist order, no `unseen` / `liked` switches the shared chips off), and a
group row navigates with the current `unseen` / `liked`.
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
bottom (62px, 24px radius, the dock's inverted colours, icon over label, 700 12px labels; the
active tab on a 16% `--dock-ink` pill, label weight 800, its icon in the label's ink with the on state); the footer pads 104px to scroll clear of it, and an open film card's backdrop
covers it. From 768px they are a segmented control in the head (owner) or in a row under the cover
(viewer). An open group replaces the group list at its own address (CSS hides the list while
`.has-open` is on Browse); its sticky head spans the page's full width. Back or All groups returns to
the list with the focus on the group that was open, and a group that is gone shows the list with a
toast.

Wide Library (from 1100px, CSS only): once the groups are loaded the page widens to a 1440px
maximum (same 18px padding). While a group is open, Browse becomes two columns with a 24px gap,
`minmax(300px, 0.4fr) minmax(0, 1fr)`: left the group list with its "Browse groups" title, sticky
below the top bar with its own scroll, right the open group, whose entries the page scrolls; its
head sticks 12px below the top bar and the back button is hidden. With no group open the list keeps
the full width. An open film card is a centred dialog over the page and changes none of this.

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
