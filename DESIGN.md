# Strim Design System

## Philosophy

Strim is an M3U playlist editor — a utility, not a media site. The design treats it like a professional desktop application: compact, monochrome, no distractions. Every pixel serves a purpose. There is no hero section, no gradient, no accent color, no ornament.

The goal is to feel instantly familiar to anyone who has used a developer tool, a DAW, or a code editor. Dark, dense, informational.

---

## Palette

```
Background      #0c0c0e
Surface         #141416
Panel           #121213
Border          #252529
Border light    #1a1a1d
Text primary    #e8e8ec
Text secondary  #7a7a80
Text muted      #55555a
```

There is exactly one accent in the entire system: white (`#e8e8ec`) used for primary buttons and interactive states. No colors. No gradients. No glow.

### Why no accent color

The app processes IPTV playlists. Users stare at group lists, checkboxes, and stats. Any color would compete with the content. Monochrome keeps the hierarchy flat and the information density high. The only place white appears is on actionable controls (primary buttons, toggle active state).

---

## Typography

| Context | Family | Size | Weight |
|---------|--------|------|--------|
| Body / UI | Inter | 11–13px | 400–500 |
| Section titles | Inter | 12–14px | 600 |
| Page headings | Inter | 16–18px | 700 |
| Stat values | Inter | 16px tabular | 700 |
| Labels / badges | Inter | 8–10px | 600 uppercase |

Inter was chosen over Space Grotesk because it renders more compactly at small sizes (10–13px range), which is critical in a dense desktop-app layout. Space Grotesk has wider glyphs that create unnecessary horizontal space in tight UI components.

All font sizes are in `px`, not `rem`, because the app is not a reading surface — it's a tool interface where precise control over text size matters.

---

## Spacing & Sizing

The base unit is `4px`.

| Element | Height | Padding | Radius |
|---------|--------|---------|--------|
| Top bar / nav | 32px (h-8) | px-3 | — |
| Input rows | 32px (h-8) | px-3 | 4px |
| Buttons (primary) | 32px (h-8) | px-4 | 4px |
| Buttons (secondary) | 32px (h-8) | px-3 | 4px |
| Group list items | 38px (min-height) | px-3 | — |
| Stat cards | auto | 6px 4px | 4px |
| Modals | slide-in from right | — | 4px |

### Standard gaps

| Context | Gap |
|---------|-----|
| Panel sections | 3px (stacked bars) |
| Cards in a grid | 3px |
| Nav links | 0px (tight row) |
| Modal from edge | 8px right margin |

### Button variants

```
Primary:
  bg-[#e8e8ec] text-[#0c0c0e] h-8 px-4 rounded-[4px] font-semibold text-xs
  hover:brightness-110

Ghost / Secondary:
  border border-[#252529] text-[#e8e8ec] h-8 px-3 rounded-[4px] font-medium text-xs
  hover:bg-[#252529]

Danger:
  (same as ghost but hover:bg-red-900/20 hover:border-red-800)
```

---

## Layout

### App page (index.html)

```
┌─────────────────────────────────────────────┐
│  Top bar (32px)                             │
├──────┬──────────────────────────────────────┤
│      │                                      │
│  S   │  URL bar  [Fetch]                    │
│  I   │──────────────────────────────────────│
│  D   │  [Input] [Filter] [Output]  [ ⋮ ]   │
│  E   │──────────────────────────────────────│
│  B   │  Stat row (3 cards)                  │
│  A   │──────────────────────────────────────│
│  R   │  Group list (checkboxes)             │
│      │                                      │
│      │                                      │
├──────┴──────────────────────────────────────┤
│  Bottom bar (status text)                   │
└─────────────────────────────────────────────┘
```

- Sidebar: 210px, collapsible, contains saved playlists
- Content area: flex-fill, contains vertical stack of bars
- Bottom bar: 24px, shows file status

### Marketing pages (features, how-to, comparison, blog)

Simple linear layout:
```
┌─────────────────────────────────────┐
│  Top bar (32px nav)                 │
├─────────────────────────────────────┤
│                                     │
│  Page content (max-w-[1000px])      │
│  Panels: 4px radius, bg-[#121213]   │
│  border-[#252529]                   │
│                                     │
├─────────────────────────────────────┤
│  Footer (text-[10px])               │
└─────────────────────────────────────┘
```

---

## Component Patterns

### Cards / Panels

Marketing pages: `p-4 rounded-[4px] bg-[#121213] border border-[#252529]`

No shadow. No gradient. No hover elevation. The border is the only separation.

### Lists

```
group-list-item:
  h-[38px] px-3 border-b border-[#1a1a1d]
  hover:bg-white/[0.02]
  disabled:opacity-30
```

### Toggle switch

Custom CSS slider. Knob slides 14px on checked state. Track is `20px` tall, same border radius. Active: white track + dark knob.

### Modals

Slide in from right edge with `8px` gap. Background fades in with `0.12s`. Dialog slides `20px` over `0.15s` with elastic easing.

### Scrollbars

Custom thin (`6px`) scrollbar. Track transparent, thumb `#1a1a1d` → `#252529` on hover.

### Stat cards

```
stat-card:
  p-[6px_4px] rounded-[4px] bg-[#141416] border border-[#1a1a1d]
  stat-label: text-[8px] uppercase tracking-[0.06em] text-[#55555a] font-semibold
  stat-value: text-[16px] font-bold text-[#e8e8ec] tabular-nums
```

---

## Animations

- Modal backdrop: `fadeIn` 0.12s ease
- Modal dialog: `slideIn` 0.15s ease (translateX 20px → 0)
- Toggle switch knob: 0.15s cubic-bezier(0.34, 1.56, 0.64, 1)
- Button active: `scale(0.97)` on click
- No hover animations on cards. No page transitions. No loading spinners (skeleton or nothing).

---

## Marketing pages vs App page

Marketing pages (features, how-to-use, comparison, blog, 404) share the same top nav and footer as the app page but have a simpler content layout. They use the same palette and panel style but with wider max-width (1000px) and larger text sizes (13–14px body vs 11–12px app).

The 404 page is the only page with any "personality" — a flickering CRT effect and fake terminal output. It still follows the monochrome palette.

---

## File structure

```
index.html          — Main app page (desktop-app layout)
style.css           — App-level CSS (components, animations, scrollbars, toggles)
main.js             — App logic (1757 lines, 39 DOM IDs, ~20 class hooks)
filter-worker.js    — Background worker for playlist processing
features.html       — Features marketing page
how-to-use.html     — Usage guide marketing page
comparison.html     — Competitor comparison marketing page
blog/index.html     — Blog index with article cards
404.html            — Custom 404 error page
```

---

## Dev notes

- Tailwind CDN is used during development (requires `unsafe-inline` in CSP)
- Zone colors (`zone-input`, `zone-filter`, `zone-output`) are hardcoded to `#e8e8ec` in tailwind config — these exist only because `main.js` references them and they must resolve to a valid color
- The `.primary` and `.ghost` CSS classes in `style.css` match the button patterns above and are used by dynamically generated buttons in `main.js`
- For production, remove the Tailwind CDN script and `unsafe-inline` CSP, replace with built CSS
