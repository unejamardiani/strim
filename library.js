// Strim Library: a read-only view of a saved playlist at #/p/<playlist id>/library.
// main.js calls window.StrimLibrary.mount() from its router and passes in everything this file uses.
// Nothing here changes the playlist or what the player link serves.

(() => {
  // ctx.controller is aborted by main.js when the route leaves the Library, so a pending load stops waiting.
  // The tabs (#418): each one a link to its own route, its content a panel that stays in the DOM while hidden.
  const TABS = [
    { name: 'search', label: 'Search', icon: 'search' },
    { name: 'groups', label: 'Groups', icon: 'grid' },
    { name: 'marks', label: 'Marks', icon: 'check' },
  ];
  // The Library on screen: its ctx, tab links, panels and open tab, for show().
  let view = null;

  function mount(ctx) {
    const { root, playlist, h, icon, controller } = ctx;
    const base = isViewer(ctx) ? `#/shared/${playlist.id}` : `#/p/${playlist.id}/library`;
    const nav = h('nav', { class: 'lib-tabs', 'aria-label': 'Library' },
      TABS.map((t) => h('a', { href: `${base}/${t.name}`, dataset: { tab: t.name } }, icon(t.icon), t.label)));
    const panels = Object.fromEntries(TABS.map((t) => [t.name, h('section', { class: 'lib-panel', dataset: { tab: t.name }, hidden: true })]));
    // query: the search address's "q=…&type=…" (#421), groupQuery the Groups address's "type=…" (#422), applied once
    // the list is loaded.
    const onGroups = String(ctx.sub || '').startsWith('groups');
    view = { ctx, nav, panels, base, tab: null, byKeyboard: false, query: onGroups ? '' : ctx.query || '', search: null, groupQuery: onGroups ? ctx.query || '' : '', groupType: null };
    // A link activated by keyboard clicks with detail 0; show() then moves the focus into the tab.
    nav.addEventListener('click', (e) => { view.byKeyboard = e.detail === 0; });
    // A viewer (#383) gets the list's cover as the head and the tabs under it; no ⋯ menu, nothing would be in it.
    if (isViewer(ctx)) ctx.cover = viewerCover(ctx);
    const head = ctx.cover ? [ctx.cover.el, h('div', { class: 'lib-tabs-row' }, nav)] : [ownerHead(ctx, nav)];
    // The marks (#299) do not hang on the playlist, so they show even when it has no source or fails to load.
    panels.marks.append(myMarks(ctx));
    root.replaceChildren(...head, panels.search, panels.groups, panels.marks);
    show(ctx.sub, ctx.query);
    if (!isViewer(ctx) && !playlist.sourceUrl) {
      ctx.listState = 'none';
      inPanels(ctx, () => h('div', { class: 'notice' }, 'This playlist has no source URL. Add one in the editor to see what it holds.'));
      return;
    }
    load(ctx, controller);
  }

  // Opens a tab of the Library on screen; main.js calls it for a tab change instead of mounting again.
  function show(sub, query) {
    if (!view) return;
    const name = String(sub || '').split('/')[0];
    const tab = TABS.some((t) => t.name === name) ? name : 'search';
    if (tab === 'search' && query) {
      view.query = query;
      view.search?.(query);
    }
    for (const a of view.nav.children) {
      if (a.dataset.tab === tab) a.setAttribute('aria-current', 'page');
      else a.removeAttribute('aria-current');
    }
    // The Groups address (#423): groups?type=… is the list, groups/<encoded key>?q=…&sort=… an open group.
    if (tab === 'groups') {
      view.groupKey = groupKeyOf(sub);
      view.groupQuery = query || '';
      view.groupRoute?.(view.groupQuery);
    }
    for (const [key, panel] of Object.entries(view.panels)) panel.hidden = key !== tab;
    if (view.tab === tab) return;
    const first = view.tab !== null;
    view.tab = tab;
    window.scrollTo(0, 0);
    if (!first || !view.byKeyboard) return;
    view.byKeyboard = false;
    const target = view.panels[tab].querySelector('input, h2, h3');
    if (target && target.tagName !== 'INPUT') target.tabIndex = -1;
    target?.focus();
  }

  // Search and Groups both wait on the list, so both show its loading card, error or notice.
  function inPanels(ctx, make) {
    for (const panel of [view.panels.search, view.panels.groups]) panel.replaceChildren(make());
  }

  // The owner's head (#418): back, the name on one line, the tabs (a segmented control from 768px) and ⋯.
  function ownerHead(ctx, nav) {
    const { h, icon, playlist } = ctx;
    const name = playlist.name || 'Untitled';
    return h('header', { class: 'lib-head' },
      h('a', { class: 'lib-ib is-quiet', href: '#/', 'aria-label': 'Back to playlists' }, icon('back')),
      h('div', { class: 'lib-head-text' },
        h('p', { class: 'lib-kicker', text: 'Library' }),
        h('h1', { class: 'lib-name', text: name, title: name })),
      nav,
      h('button', { class: 'lib-ib', type: 'button', 'aria-label': 'More', 'aria-haspopup': 'dialog', onclick: () => libraryMenu(ctx) }, icon('more')));
  }

  // The ⋯ menu: what the list holds, the way to the editor and "Include switched-off groups".
  function libraryMenu(ctx) {
    const { h, icon, fmt, plural, playlist } = ctx;
    const list = ctx.list;
    let facts = { none: 'This playlist has no source URL', failed: 'The list could not be loaded' }[ctx.listState] || 'Loading the list';
    if (list) {
      const on = list.groups.filter((g) => !list.disabled.has(g.key));
      const entries = on.reduce((sum, g) => sum + g.count, 0);
      facts = `${fmt(on.length)} of ${plural(list.groups.length, 'group')} on · ${fmt(entries)} of ${plural(list.totalChannels, 'entry', 'entries')} in your list`;
    }
    const toggle = h('input', {
      class: 'toggle', type: 'checkbox', role: 'switch', checked: includeOff,
      onchange: (e) => { includeOff = e.target.checked; ctx.onIncludeOff?.(); },
    });
    ctx.openMenu(playlist.name || 'Untitled', [
      h('p', { class: 'lib-menu-facts' }, facts,
        h('small', { text: 'The Library only reads your list. To change what the player link serves, edit the groups.' })),
      h('a', { class: 'menu-item', href: `#/p/${playlist.id}` },
        h('span', { class: 'mi-icon' }, icon('list')), h('span', { class: 'grow', text: 'Edit groups' })),
      h('label', { class: 'menu-item' }, h('span', { class: 'grow', text: 'Include switched-off groups' }), toggle),
    ]);
  }

  async function load(ctx, own) {
    const { h } = ctx;
    ctx.listState = 'loading';
    inPanels(ctx, () => h('div', { class: 'card loading-card' },
      h('span', { class: 'spinner', 'aria-hidden': 'true' }),
      h('div', { class: 'grow', text: isViewer(ctx) ? 'Loading the list' : 'Loading the playlist from your provider' })));
    let result;
    try {
      result = await ctx.loadGroups(own.signal);
    } catch (err) {
      if (own.signal.aborted || !ctx.isCurrent()) return;
      if (accessGone(ctx, err)) return;
      ctx.listState = 'failed';
      // A viewer gets the server's neutral message, never the provider's.
      inPanels(ctx, () => h('div', { class: 'notice error' },
        isViewer(ctx) ? h('p', null, h('b', { text: 'The list cannot be loaded right now' }), ' ', err.message || '') : h('p', { text: ctx.errorMessage(err) }),
        h('div', { class: 'row-actions' },
          h('button', { class: 'btn btn-sm', type: 'button', onclick: () => load(ctx, own) }, 'Try again'))));
      return;
    }
    if (own.signal.aborted || !ctx.isCurrent()) return;
    if (isViewer(ctx)) ctx.cover.fill(result);
    ctx.list = result;
    const browse = browseSection(ctx, result);
    view.panels.search.replaceChildren(searchSection(ctx, result, browse.refresh, browse.reload));
    view.panels.groups.replaceChildren(browse.el);
  }

  // The type control's choices and the content types each one sends; All sends none.
  const TYPES = [
    { value: 'all', label: 'All', types: [] },
    { value: 'films', label: 'Films', types: ['Movies'] },
    { value: 'series', label: 'Series', types: ['Series'] },
    { value: 'channels', label: 'Channels', types: ['Live'] },
  ];
  const TYPE_LABELS = { Live: 'Channel', Movies: 'Film', Series: 'Series' };

  // The viewer's Library (#383): the same view on someone else's list. Its copy says "this list", never "your list".
  const isViewer = (ctx) => ctx.role === 'viewer';
  const listWord = (ctx) => (isViewer(ctx) ? 'this list' : 'your list');

  // Kept for this page session only: a reload starts again with the groups that are on.
  let includeOff = false;
  // The mark filters (#297, #298), for this page session only like the toggle.
  let hideSeen = false;
  let likedOnly = false;

  // The same search as the editor's Everything, read-only: by default only the groups that are on.
  // onToggle tells the browse view that the shared toggle changed.
  // onMarkFilter tells the browse view that a mark filter changed.
  function searchSection(ctx, list, onToggle, onMarkFilter) {
    const { h } = ctx;
    // The editor's language filter (#266) on its own host: the choice lives for this view only, like the editor's.
    const host = { searchLang: [], searchLanguages: [], onLangChoice: () => run() };
    const filter = ctx.languageFilter(host);
    const state = { query: '', type: 'all', timer: 0, seq: 0, host, filter, observer: null };
    const results = { head: h('div', { class: 'search-head-status' }), body: h('div') };
    const run = () => runSearch(ctx, list, state, results);
    const schedule = () => { clearTimeout(state.timer); state.timer = setTimeout(run, 300); };

    const clear = h('button', {
      class: 'lib-clear', type: 'button', 'aria-label': 'Clear search', hidden: true,
      onclick: () => { input.value = ''; typed(); run(); input.focus(); },
    }, ctx.icon('close'));
    const typed = () => { state.query = input.value.trim(); clear.hidden = !input.value; };
    const input = h('input', {
      class: 'input input-search', type: 'search', placeholder: 'Search films, series and channels',
      'aria-label': `Search ${listWord(ctx)}`, autocomplete: 'off', spellcheck: 'false', enterkeyhint: 'search',
      oninput: () => { typed(); schedule(); },
      onkeydown: (e) => { if (e.key === 'Enter') e.target.blur(); },
    });
    // One chip row (#419): the types are a single choice, Unseen and Liked toggles; it scrolls sideways, never wraps.
    // A type chip carries its count of the last answer (#421).
    const pick = (option) => {
      state.type = option.value;
      for (const b of types) b.setAttribute('aria-pressed', String(b.dataset.value === option.value));
    };
    const types = TYPES.map((option) => h('button', {
      class: 'lib-chip', type: 'button', 'aria-pressed': String(option.value === state.type), dataset: { value: option.value },
      onclick: () => { pick(option); run(); },
    }, option.label, h('span', { class: 'n' })));
    state.types = types;
    // "Show all" under a section picks its type.
    state.pick = (value) => { pick(TYPES.find((t) => t.value === value)); run(); };
    // The address's query (#421): fills the field and the type, then searches; the same query again does nothing.
    view.search = (raw) => {
      const p = new URLSearchParams(raw);
      const type = TYPES.find((t) => t.value === p.get('type')) || TYPES[0];
      input.value = (p.get('q') || '').trim();
      if (state.seq && input.value === state.query && type.value === state.type) return;
      typed();
      pick(type);
      run();
    };
    // "Include switched-off groups" in the ⋯ menu (#418) runs the search and refreshes the groups; a viewer has no
    // menu and nothing to include.
    ctx.onIncludeOff = () => {
      run();
      onToggle();
    };
    const markFilter = (label, get, set) => {
      const b = h('button', {
        class: 'lib-chip is-toggle', type: 'button', 'aria-pressed': String(get()),
        onclick: () => {
          set(!get());
          b.setAttribute('aria-pressed', String(get()));
          run();
          onMarkFilter();
        },
      }, label);
      return b;
    };
    const seenFilter = markFilter('Unseen', () => hideSeen, (v) => { hideSeen = v; });
    const likedFilter = markFilter('Liked', () => likedOnly, (v) => { likedOnly = v; });
    // An open group's chips (#423) are the same filters: these follow and the search runs again.
    view.marksChanged = () => {
      seenFilter.setAttribute('aria-pressed', String(hideSeen));
      likedFilter.setAttribute('aria-pressed', String(likedOnly));
      run();
    };

    ctx.controller.signal.addEventListener('abort', () => { clearTimeout(state.timer); state.observer?.disconnect(); }, { once: true });
    // On a phone the labels that do not fit wait behind "+N", as in the editor; a new width fits them again.
    window.addEventListener('resize', () => ctx.fitSearchLanguages(results.body), { signal: ctx.controller.signal });
    const el = h('section', { class: 'library-search' },
      h('div', { class: 'lib-sticky' },
        h('div', { class: 'lib-search' }, input, clear),
        h('div', { class: 'lib-chips', role: 'group', 'aria-label': 'Filters' },
          types, h('span', { class: 'lib-chip-sep', 'aria-hidden': 'true' }), seenFilter, likedFilter)),
      h('div', { class: 'search-head' }, results.head, filter.bar),
      filter.panel,
      h('div', { 'aria-label': 'Search results', role: 'region' }, results.body));
    view.search(view.query);
    return el;
  }

  // The search's address (#421): #/p/<id>/library/search?q=…&type=…, replaced while typing, never a new entry.
  function keepAddress(state) {
    const p = new URLSearchParams();
    if (state.query) p.set('q', state.query);
    if (state.type !== 'all') p.set('type', state.type);
    const hash = `${view.base}/search${String(p) ? `?${p}` : ''}`;
    view.nav.querySelector('[data-tab="search"]').href = hash;
    if (view.tab === 'search' && location.hash !== hash) history.replaceState(history.state, '', hash);
  }

  // The chips' counts from the answer's counts; All is their sum, and without a query no counts.
  const COUNT_OF = { films: 'movies', series: 'series', channels: 'live' };
  function paintCounts(ctx, state, counts) {
    for (const b of state.types) {
      const keys = b.dataset.value === 'all' ? Object.values(COUNT_OF) : [COUNT_OF[b.dataset.value]];
      b.lastElementChild.textContent = counts ? ctx.fmt(keys.reduce((sum, k) => sum + (counts[k] || 0), 0)) : '';
    }
  }

  // All shows up to three sections of 4 rows (#421), each with its count and, above 4, "Show all".
  const SECTIONS = [
    { type: 'Movies', value: 'films', name: 'Films', count: 'movies', n: (ctx, n) => ctx.plural(n, 'film'), more: (ctx, n) => `Show all ${ctx.fmt(n)} films` },
    { type: 'Series', value: 'series', name: 'Series', count: 'series', n: (ctx, n) => ctx.plural(n, 'series', 'series'), more: (ctx, n) => `Show all ${ctx.fmt(n)} series` },
    { type: 'Live', value: 'channels', name: 'Channels', count: 'live', n: (ctx, n) => ctx.plural(n, 'channel'), more: (ctx, n) => `Show all ${ctx.fmt(n)} channels` },
  ];

  function sections(ctx, state, found, rowOf) {
    const { h } = ctx;
    const counts = found.counts || {};
    const section = (s) => {
      const n = counts[s.count] || 0;
      if (n <= 0) return null;
      return h('section', { class: 'lib-sec', dataset: { type: s.type } },
        h('div', { class: 'lib-sec-head' }, h('h2', { text: s.name }), h('span', { class: 'n', text: s.n(ctx, n) })),
        h('div', { class: 'lib-list' },
          h('ul', { class: 'search-list' }, (found.titles || []).filter((t) => t.contentType === s.type).slice(0, 4).map(rowOf)),
          n > 4 ? h('button', { class: 'lib-more', type: 'button', onclick: () => state.pick(s.value) }, s.more(ctx, n)) : null));
    };
    // From 1100px Films is the left column, Series and Channels stack in the right one.
    const [films, series, channels] = SECTIONS.map(section);
    return h('div', { class: 'lib-sections' }, h('div', null, films), h('div', null, series, channels));
  }

  // The search (#421) asks the titles route: one row per title, a series as one row. All asks 4 of each type for the
  // sections; a single type pages by 30.
  async function runSearch(ctx, list, state, results) {
    const { h, plural, fmt } = ctx;
    clearTimeout(state.timer);
    const seq = ++state.seq;
    state.observer?.disconnect();
    keepAddress(state);
    // The status line under the chips, beside the language chip.
    const status = (...nodes) => results.head.replaceChildren(h('p', { class: 'search-status lib-status', role: 'status' }, ...nodes));
    const hint = ctx.searchHint(state.query);
    if (hint) {
      paintCounts(ctx, state, null);
      status(hint);
      results.body.replaceChildren();
      state.filter.render();
      return;
    }
    const type = TYPES.find((t) => t.value === state.type);
    const lang = state.host.searchLang.join(',');
    const params = { cacheKey: list.cacheKey, includeDisabled: includeOff, q: state.query };
    if (type.types.length) Object.assign(params, { types: type.types.join(','), offset: 0, limit: 30 });
    else params.perType = 4;
    if (lang) params.lang = lang;
    if (hideSeen) params.hideSeen = true;
    if (likedOnly) params.liked = true;
    // The sections of the previous answer stay until the new one arrives.
    status('Searching…');
    let found;
    try {
      found = await ctx.titles(params, ctx.controller.signal);
    } catch (err) {
      if (seq !== state.seq || ctx.controller.signal.aborted || accessGone(ctx, err)) return;
      results.head.replaceChildren();
      results.body.replaceChildren(h('div', { class: 'notice error', role: 'alert', text: err.message || 'Search failed.' }));
      return;
    }
    if (seq !== state.seq || !ctx.isCurrent()) return;
    if (Array.isArray(found.languages)) {
      state.host.searchLanguages = found.languages.filter((l) => l && typeof l.value === 'string' && l.value);
    }
    paintCounts(ctx, state, found.counts || {});
    const titles = found.titles || [];
    const total = Number.isInteger(found.total) ? found.total : titles.length;
    const hidden = !isViewer(ctx) && found.hiddenCount > 0 ? found.hiddenCount : 0;
    const seen = hideSeen && found.seenHiddenCount > 0 ? found.seenHiddenCount : 0;
    const left = [hidden ? `${fmt(hidden)} more in switched-off groups` : '',
      seen ? `${plural(seen, 'seen title')} hidden` : ''].filter(Boolean).join(' · ');
    let text = 'Nothing matches your search.';
    if (total) text = [plural(total, 'title'), left].filter(Boolean).join(' · ');
    else if (left) text = `Nothing in ${listWord(ctx)}. ${left}.`;
    else if (likedOnly) text = 'Nothing you liked matches your search.';
    else if (lang) text = 'Nothing matches your search in the chosen languages.';
    if (titles.some((t) => t.languageSource === 'title')) text += ' · Labels from the title';
    // "Show them" turns "Include switched-off groups" on, like the ⋯ menu.
    const showThem = hidden ? h('button', { type: 'button', onclick: () => { includeOff = true; ctx.onIncludeOff(); } }, 'Show them') : null;
    status(text, showThem ? ' ' : null, showThem);
    const rowOf = (t) => hit(ctx, t, list.cacheKey);
    let nodes = [];
    if (!type.types.length && total) nodes = [sections(ctx, state, found, rowOf)];
    else if (titles.length) nodes = [pages(ctx, state, params, found, rowOf, seq)];
    results.body.replaceChildren(...nodes);
    state.filter.render();
    ctx.fitSearchLanguages(results.body);
  }

  // A single type (#421): 30 titles a page, the next one once the sentinel comes within 600px of the screen.
  // An open group (#423) pages the same way and passes end, its foot once everything is shown.
  function pages(ctx, state, params, first, rowOf, seq, end) {
    const { h } = ctx;
    const ul = h('ul', { class: 'search-list' }, first.titles.map(rowOf));
    const foot = h('div');
    const sentinel = h('div', { 'aria-hidden': 'true' });
    const watch = (page) => {
      foot.replaceChildren();
      if (page.hasMore !== true || !page.titles?.length) return end && foot.append(end());
      foot.append(sentinel);
      state.observer = new IntersectionObserver((seen) => { if (seen.some((e) => e.isIntersecting)) more(); }, { rootMargin: '600px' });
      state.observer.observe(sentinel);
    };
    // A failed page shows the error and Try again below the rows already shown, which stay.
    const more = async () => {
      state.observer?.disconnect();
      let page;
      try {
        page = await ctx.titles({ ...params, offset: ul.children.length, limit: 30 }, ctx.controller.signal);
      } catch (err) {
        if (seq !== state.seq || ctx.controller.signal.aborted || accessGone(ctx, err)) return;
        foot.replaceChildren(h('div', { class: 'notice error', role: 'alert' },
          h('p', { text: err.message || 'The titles could not be loaded.' }),
          h('button', { class: 'btn btn-sm', type: 'button', onclick: () => more() }, 'Try again')));
        return;
      }
      if (seq !== state.seq || !ctx.isCurrent()) return;
      ul.append(...(page.titles || []).map(rowOf));
      ctx.fitSearchLanguages(ul);
      watch(page);
    };
    watch(first);
    return h('div', { class: 'lib-list' }, ul, foot);
  }

  // Marks (#298): seen and thumbs on a film or series, stored per user by the marks API that main.js hands in; they
  // never touch the playlist. The state is kept by mark key, so every variant of a title in the view shows the same.
  const MARK_KINDS = { Movies: 'film', Series: 'series' };
  const marks = new Map();

  // Channels and names without a title have no mark key and get no controls.
  function markControls(ctx, entry) {
    const kind = MARK_KINDS[entry.contentType];
    if (!entry.markKey || !kind) return null;
    const { h, icon } = ctx;
    const key = entry.markKey;
    marks.set(key, { seen: entry.seen === true, rating: entry.rating || 0, watchlist: entry.watchlist === true });
    // Tapping an active control again undoes it.
    const control = (label, name, next) => h('button', {
      class: 'icon-btn library-mark', type: 'button', 'aria-label': label, title: label, 'aria-pressed': 'false',
      onclick: () => setMark(ctx, entry.name, kind, key, next(marks.get(key))),
    }, icon(name));
    // Rows use the film card's capsule (#383) in both Libraries.
    const el = h('span', { class: 'library-marks film-marks', role: 'group', 'aria-label': 'Your marks', dataset: { key } },
      control('Seen', 'check', toggleSeen),
      control('Thumbs up', 'up', (m) => ({ ...m, rating: m.rating === 1 ? 0 : 1 })),
      control('Thumbs down', 'down', (m) => ({ ...m, rating: m.rating === -1 ? 0 : -1 })));
    paintMarks(el);
    return el;
  }

  // Seen wins (#420): a title marked seen leaves the watchlist at once, before the request; the server does the same.
  const toggleSeen = (m) => (m.seen ? { ...m, seen: false } : { ...m, seen: true, watchlist: false });

  function paintMarks(el) {
    const m = marks.get(el.dataset.key);
    const [seen, up, down] = el.children;
    seen.setAttribute('aria-pressed', String(m.seen));
    up.setAttribute('aria-pressed', String(m.rating === 1));
    down.setAttribute('aria-pressed', String(m.rating === -1));
    // The Watchlist button sits beside the capsule, on the card and in the row.
    const watch = el.parentElement?.querySelector(':scope > .lib-watch');
    if (watch) paintWatch(watch, m);
    // The row's phone controls (#419) and its dimmed title follow the capsule.
    const row = el.closest('.lib-row');
    if (!row) return;
    row.classList.toggle('is-seen', m.seen);
    const quick = row.querySelector('.lib-quick');
    const [like, seenButton] = quick.children;
    seenButton.setAttribute('aria-pressed', String(m.seen));
    like.dataset.rating = String(m.rating);
    like.children[0].toggleAttribute('hidden', m.rating !== 1);
    like.children[1].toggleAttribute('hidden', m.rating !== -1);
    quick.setAttribute('aria-label', { 1: 'Your marks, liked', '-1': 'Your marks, disliked' }[m.rating] || 'Your marks');
  }

  // A row's marks (#419): below 768px the liked glyph and one Seen button, from 768px the capsule. Thumbs are set in
  // the card; the Seen button toggles seen with the same call as the capsule.
  function rowMarks(ctx, entry) {
    const capsule = markControls(ctx, entry);
    if (!capsule) return null;
    const { h, icon } = ctx;
    const key = entry.markKey;
    const seen = h('button', {
      class: 'lib-seen', type: 'button', 'aria-label': 'Seen', 'aria-pressed': 'false',
      onclick: () => setMark(ctx, entry.name, MARK_KINDS[entry.contentType], key, toggleSeen(marks.get(key))),
    }, icon('check'));
    const like = h('span', { class: 'lib-like', 'aria-hidden': 'true' }, icon('up'), icon('down'));
    return [h('span', { class: 'lib-quick', role: 'group' }, like, seen), watchButton(ctx, entry), capsule];
  }

  // The Watchlist button (#420): after the card's capsule and, from 768px, before a row's capsule. Tapping it again
  // takes the title off the list.
  function watchButton(ctx, entry) {
    const key = entry.markKey;
    const el = ctx.h('button', {
      class: 'lib-watch', type: 'button', dataset: { key },
      onclick: () => setMark(ctx, entry.name, MARK_KINDS[entry.contentType], key, { ...marks.get(key), watchlist: !marks.get(key).watchlist }),
    }, ctx.icon('bookmark'));
    paintWatch(el, marks.get(key));
    return el;
  }

  function paintWatch(el, m) {
    const label = m.watchlist === true ? 'On your watchlist' : 'Add to watchlist';
    el.setAttribute('aria-pressed', String(m.watchlist === true));
    el.setAttribute('aria-label', label);
    el.title = label;
  }

  // Shown at once on every row of the title; a failed request puts the mark back and says so.
  async function setMark(ctx, name, kind, key, next) {
    const before = marks.get(key);
    const repaint = () => {
      for (const el of ctx.root.querySelectorAll('.library-marks')) if (el.dataset.key === key) paintMarks(el);
    };
    marks.set(key, next);
    repaint();
    try {
      const saved = await ctx.setMark({ name, kind, seen: next.seen, rating: next.rating, watchlist: next.watchlist });
      // The server's watchlist (#414) is taken over, unless a later tap already replaced this change.
      if (saved && marks.get(key) === next && saved.watchlist !== next.watchlist) {
        marks.set(key, { ...next, watchlist: saved.watchlist === true });
        repaint();
      }
      // A card opened from a similar film (#368) uses a local key; the rows of the title use the one the API returns.
      const real = saved?.key || keyAlias.get(key);
      if (real && real !== key) {
        keyAlias.set(key, real);
        syncRows(ctx, real, marks.get(key));
      }
    } catch (err) {
      // A later tap on the same title already replaced this change.
      if (marks.get(key) !== next) return;
      marks.set(key, before);
      repaint();
      ctx.toast(`The mark was not changed. ${err.message || ''}`.trim(), { error: true });
    }
  }

  // Browse (#287): the analysed groups by type, then in playlist order. An open group (#423) has its own address,
  // #/…/groups/<encoded key>, and pages its titles by 30 from the titles route as its end comes near.
  const BROWSE_ORDER = ['Movies', 'Series', 'Live'];
  // The Groups tab's chips (#422), one per type with groups, and the word a group's count takes.
  const GROUP_TYPES = [
    { value: 'films', type: 'Movies', label: 'Films', one: 'film', many: 'films' },
    { value: 'series', type: 'Series', label: 'Series', one: 'episode', many: 'episodes' },
    { value: 'channels', type: 'Live', label: 'Channels', one: 'channel', many: 'channels' },
  ];
  // The open group's orders (#423), cycled by the sort chip.
  const SORTS = [{ value: 'playlist', label: 'Playlist order' }, { value: 'title', label: 'A to Z' }, { value: 'year', label: 'Newest year' }];
  // A group's filter (#423) needs 2 letters or digits; below that the whole group shows.
  const filterOf = (raw) => ((raw.trim().match(/[\p{L}\p{N}]/gu) || []).length >= 2 ? raw.trim() : '');
  // The group key of a Groups address, decoded; null for the group list.
  function groupKeyOf(sub) {
    const raw = String(sub || '').split('/').slice(1).join('/');
    try { return raw ? decodeURIComponent(raw) : null; } catch { return raw; }
  }
  // The chosen type is kept for the page session, like the toggles.
  let groupType = null;

  // "+N new" (#422): the group counts this device saw last, per playlist, { "<groupKey>": count }. Never sent to the
  // server. null when nothing is stored, storage is unavailable or the value is not valid: then nothing is new.
  const countsKey = (ctx) => `strim.library.counts.${ctx.playlist.id}`;
  function readCounts(ctx) {
    try {
      const stored = JSON.parse(localStorage.getItem(countsKey(ctx)));
      return stored && typeof stored === 'object' && !Array.isArray(stored) ? stored : null;
    } catch {
      return null;
    }
  }

  // Groups that no longer exist are dropped when the object is written.
  function writeCounts(ctx, list, counts) {
    const kept = {};
    for (const g of list.groups) if (Number.isFinite(counts[g.key])) kept[g.key] = counts[g.key];
    try {
      localStorage.setItem(countsKey(ctx), JSON.stringify(kept));
    } catch {
      // No storage on this device: no badges.
    }
  }

  // The first load on a device stores every group's count and marks nothing new.
  function seenCounts(ctx, list) {
    const stored = readCounts(ctx);
    if (stored) return stored;
    const counts = Object.fromEntries(list.groups.map((g) => [g.key, g.count]));
    writeCounts(ctx, list, counts);
    return counts;
  }

  // A group grew since this device last looked: the difference; no stored value, a lower or the same count: 0.
  const newSince = (seen, g) => (Number.isFinite(seen[g.key]) && g.count > seen[g.key] ? g.count - seen[g.key] : 0);

  function browseGroups(list, withOff) {
    const shown = list.groups.filter((g) => withOff || !list.disabled.has(g.key));
    const sorted = [...shown].sort((a, b) => BROWSE_ORDER.indexOf(a.type) - BROWSE_ORDER.indexOf(b.type) || a.index - b.index);
    return { sorted, hidden: list.groups.length - shown.length };
  }

  function browseSection(ctx, list) {
    const { h, icon, fmt, plural } = ctx;
    const state = { open: null, seen: seenCounts(ctx, list) };
    const groupsEl = h('div', { class: 'library-groups' });
    // From 1100px the group list (its own scroll) and the open group are two columns; below, the group replaces it.
    const side = h('div', { class: 'lib-groups-side' }, h('h2', { class: 'library-title', text: 'Browse groups' }), groupsEl);
    const openEl = h('div', { class: 'lib-group-open', hidden: true });
    const el = h('section', { class: 'library-browse lib-groups-cols' }, side, openEl);
    const status = (text) => h('p', { class: 'search-status', role: 'status', text });
    const groupHash = (key, p) => `${view.base}/groups/${encodeURIComponent(key)}${String(p) ? `?${p}` : ''}`;
    const markParams = () => new URLSearchParams([...(hideSeen ? [['unseen', '1']] : []), ...(likedOnly ? [['liked', '1']] : [])]);

    // A group row (#422), the same for owner and viewer: the whole row is the button that opens the group at its
    // address, a new history entry, so Back returns to the list.
    const groupRow = (g, t) => {
      const fresh = newSince(state.seen, g);
      return h('button', { class: 'lib-row is-group', type: 'button', dataset: { key: g.key, type: g.type }, onclick: () => { location.hash = groupHash(g.key, markParams()); } },
        h('span', { class: 'lib-dot', 'aria-hidden': 'true', dataset: { type: g.type } }),
        h('span', { class: 'lib-text' },
          h('span', { class: 'lib-title', text: g.title || 'Untitled group' }),
          h('span', { class: 'lib-meta', text: plural(g.count, t.one, t.many) })),
        list.disabled.has(g.key) ? h('span', { class: 'lib-off', text: 'Off' }) : null,
        fresh > 0 ? h('span', { class: 'lib-new', text: `+${fmt(fresh)} new` }) : null,
        h('span', { class: 'lib-chev', 'aria-hidden': 'true' }, icon('chevron')));
    };

    // The chips (#422): one per type that has groups, with its number of groups; the list shows the chosen type's
    // groups in playlist order.
    const renderGroups = () => {
      const { sorted, hidden } = browseGroups(list, includeOff);
      const nodes = [];
      if (hidden > 0) nodes.push(status(`${plural(hidden, 'switched-off group')} hidden`));
      const types = GROUP_TYPES.map((t) => ({ ...t, groups: sorted.filter((g) => g.type === t.type) })).filter((t) => t.groups.length);
      if (!types.length) {
        if (!isViewer(ctx)) nodes.push(status('No group is on. Include switched-off groups to browse them.'));
        return groupsEl.replaceChildren(...nodes);
      }
      const chosen = types.find((t) => t.value === groupType) || types[0];
      groupType = chosen.value;
      keepGroupsAddress();
      const chips = types.map((t) => h('button', {
        class: 'lib-chip', type: 'button', 'aria-pressed': String(t === chosen), dataset: { value: t.value },
        onclick: () => {
          groupType = t.value;
          renderGroups();
          groupsEl.querySelector(`.lib-chip[data-value="${t.value}"]`)?.focus();
        },
      }, t.label, h('span', { class: 'n', text: fmt(t.groups.length) })));
      nodes.unshift(h('div', { class: 'lib-chips', role: 'group', 'aria-label': 'Group type' }, chips));
      nodes.push(h('div', { class: 'lib-list', dataset: { type: chosen.type } }, chosen.groups.map((g) => groupRow(g, chosen))));
      groupsEl.replaceChildren(...nodes);
      markOpenGroup();
    };

    // The Groups address (#422): #/…/groups?type=…, replaced, never a new entry; an open group keeps its own.
    const keepGroupsAddress = () => {
      const hash = `${view.base}/groups?type=${groupType}`;
      view.nav.querySelector('[data-tab="groups"]').href = hash;
      if (view.tab === 'groups' && !state.open && location.hash !== hash) history.replaceState(history.state, '', hash);
    };
    // show() hands every Groups address here (#423): a key that is shown opens its group, none the list; a key that
    // is not in the list (or not shown) falls back to the list with a toast.
    view.groupRoute = (raw) => {
      const key = view.groupKey;
      const g = key === null ? null : browseGroups(list, includeOff).sorted.find((x) => x.key === key);
      // Back on the tab with the open group's own address: it stays as it is.
      if (g && state.open?.group === g && location.hash === state.open.hash) return;
      if (g) return open(g, new URLSearchParams(raw));
      const was = close();
      const type = new URLSearchParams(raw).get('type');
      if (GROUP_TYPES.some((t) => t.value === type)) groupType = type;
      if (key !== null) {
        view.groupKey = null;
        ctx.toast('That group is not in this list any more.', { error: true });
      }
      renderGroups();
      focusRow(was);
    };

    // From 1100px the open group's row stays visible beside its entries, tinted.
    const markOpenGroup = () => {
      for (const b of groupsEl.querySelectorAll('.is-group')) b.classList.toggle('is-open', b.dataset.key === state.open?.group.key);
    };

    // Back to the group list; its pending page or filter is dropped. close() returns the key of the group that was
    // open, and the caller focuses its row with focusRow() once the list is drawn again (a redraw drops the focus).
    const stop = (o) => { o.seq += 1; o.observer?.disconnect(); clearTimeout(o.timer); };
    ctx.controller.signal.addEventListener('abort', () => { if (state.open) stop(state.open); }, { once: true });
    const close = () => {
      const o = state.open;
      if (!o) return null;
      stop(o);
      state.open = null;
      el.classList.remove('has-open');
      openEl.hidden = true;
      openEl.replaceChildren();
      markOpenGroup();
      return o.group.key;
    };
    const focusRow = (key) => {
      for (const b of groupsEl.querySelectorAll('.is-group')) if (b.dataset.key === key) b.focus();
    };

    // Opening a group stores its current count on this device; its badge goes the next time the list is drawn. The
    // address's query sets the filter, the order and the shared mark chips.
    const open = (g, p) => {
      if (state.open) stop(state.open);
      if (state.seen[g.key] !== g.count) {
        state.seen = { ...state.seen, [g.key]: g.count };
        writeCounts(ctx, list, state.seen);
      }
      const unseen = p.get('unseen') === '1';
      const liked = p.get('liked') === '1';
      if (unseen !== hideSeen || liked !== likedOnly) {
        hideSeen = unseen;
        likedOnly = liked;
        view.marksChanged?.();
      }
      const t = GROUP_TYPES.find((x) => x.type === g.type) || GROUP_TYPES[2];
      const title = g.title || 'Untitled group';
      const o = { group: g, q: filterOf(p.get('q') || ''), sort: SORTS.find((s) => s.value === p.get('sort')) || SORTS[0], seq: 0, observer: null, timer: 0 };
      o.count = h('p');
      o.body = h('div', { role: 'region', 'aria-label': `Entries of ${title}` });
      o.paintCount = (seenHidden) => o.count.replaceChildren(...[plural(g.count, t.one, t.many),
        hideSeen && seenHidden > 0 ? ` · ${fmt(seenHidden)} seen hidden` : '',
        list.disabled.has(g.key) ? h('span', { class: 'lib-off', text: 'Off' }) : null].filter(Boolean));
      // The filter (minimum 2 letters or digits, below that the whole group) waits 300ms after the last key.
      const filter = () => {
        clear.hidden = !input.value;
        const q = filterOf(input.value);
        clearTimeout(o.timer);
        if (q !== o.q) o.timer = setTimeout(() => { o.q = q; loadGroup(o); }, 300);
      };
      const input = h('input', {
        class: 'input input-search', type: 'search', placeholder: `Filter ${title}`, 'aria-label': `Filter ${title}`,
        value: p.get('q') || '', autocomplete: 'off', spellcheck: 'false', enterkeyhint: 'search',
        oninput: filter, onkeydown: (e) => { if (e.key === 'Enter') e.target.blur(); },
      });
      const clear = h('button', {
        class: 'lib-clear', type: 'button', 'aria-label': 'Clear filter', hidden: true,
        onclick: () => { input.value = ''; filter(); input.focus(); },
      }, icon('close'));
      // Unseen and Liked are the Search tab's chips (shared hideSeen / likedOnly); sort cycles the three orders.
      const mark = (label, get, set) => h('button', {
        class: 'lib-chip is-toggle', type: 'button',
        onclick: () => { set(!get()); view.marksChanged?.(); loadGroup(o); },
      }, label);
      const seenChip = mark('Unseen', () => hideSeen, (v) => { hideSeen = v; });
      const likedChip = mark('Liked', () => likedOnly, (v) => { likedOnly = v; });
      const sortChip = h('button', {
        class: 'lib-chip', type: 'button',
        onclick: () => { o.sort = SORTS[(SORTS.indexOf(o.sort) + 1) % SORTS.length]; loadGroup(o); },
      });
      o.paintChips = () => {
        seenChip.setAttribute('aria-pressed', String(hideSeen));
        likedChip.setAttribute('aria-pressed', String(likedOnly));
        sortChip.replaceChildren(icon('sort'), o.sort.label);
        sortChip.setAttribute('aria-label', `Sort: ${o.sort.label}`);
      };
      state.open = o;
      groupType = t.value;
      renderGroups();
      openEl.replaceChildren(
        h('div', { class: 'lib-sticky' },
          h('div', { class: 'lib-ghead' },
            h('a', { class: 'lib-ib', href: `${view.base}/groups?type=${groupType}`, 'aria-label': 'All groups' }, icon('back')),
            h('div', { class: 'lib-ghead-text' }, h('h2', { text: title, title }), o.count)),
          h('div', { class: 'lib-search lib-gfilter' }, input, clear),
          h('div', { class: 'lib-chips', role: 'group', 'aria-label': 'Filters' }, seenChip, likedChip, sortChip)),
        o.body);
      el.classList.add('has-open');
      openEl.hidden = false;
      window.scrollTo(0, 0);
      clear.hidden = !input.value;
      o.paintCount(0);
      o.body.replaceChildren(status('Loading entries…'));
      loadGroup(o);
    };

    // A series row carries its seasons, a film in more than one entry its versions; nothing else has a meta line.
    const entryRow = (e) => {
      const meta = e.contentType === 'Series' ? h('b', { text: seasonsLine(ctx, e) })
        : e.contentType === 'Movies' && e.entries > 1 ? plural(e.entries, 'version') : null;
      return row(ctx, e, e.group, list.cacheKey, meta, null);
    };

    // The first page of the open group by 30 titles; a change of filter, chip or order starts here again. The rows
    // shown stay until the answer arrives.
    const loadGroup = async (o) => {
      if (o !== state.open) return;
      const seq = ++o.seq;
      o.observer?.disconnect();
      o.paintChips();
      const params = { cacheKey: list.cacheKey, includeDisabled: includeOff, group: o.group.key, sort: o.sort.value, offset: 0, limit: 30 };
      if (o.q) params.q = o.q;
      if (hideSeen) params.hideSeen = true;
      if (likedOnly) params.liked = true;
      const p = markParams();
      if (o.q) p.set('q', o.q);
      if (o.sort.value !== 'playlist') p.set('sort', o.sort.value);
      o.hash = groupHash(o.group.key, p);
      view.nav.querySelector('[data-tab="groups"]').href = o.hash;
      if (view.tab === 'groups' && location.hash !== o.hash) history.replaceState(history.state, '', o.hash);
      let found;
      try {
        found = await ctx.titles(params, ctx.controller.signal);
      } catch (err) {
        if (seq !== o.seq || ctx.controller.signal.aborted || accessGone(ctx, err)) return;
        o.body.replaceChildren(h('div', { class: 'notice error', role: 'alert' },
          h('p', { text: err.message || 'The entries could not be loaded.' }),
          h('button', { class: 'btn btn-sm', type: 'button', onclick: () => loadGroup(o) }, 'Try again')));
        return;
      }
      if (seq !== o.seq || !ctx.isCurrent()) return;
      o.paintCount(found.seenHiddenCount);
      const titles = found.titles || [];
      const total = Number.isInteger(found.total) ? found.total : titles.length;
      const empty = o.q || hideSeen || likedOnly ? 'Nothing in this group matches.' : 'No entries in this group.';
      const foot = () => h('p', { class: 'lib-foot', role: 'status', text: total ? `All ${fmt(total)} shown` : empty });
      o.body.replaceChildren(total ? pages(ctx, o, params, { ...found, titles }, entryRow, seq, foot) : h('div', { class: 'lib-list' }, foot()));
    };

    // The shared toggle: an open group stays open unless it is off and switched-off groups are left out again.
    const refresh = () => {
      const was = state.open && !includeOff && list.disabled.has(state.open.group.key) ? close() : null;
      renderGroups();
      focusRow(was);
    };
    // A mark filter loads the open group again from its first page.
    const reload = () => { if (state.open) loadGroup(state.open); };
    view.groupRoute(view.groupQuery);
    return { el, refresh, reload };
  }

  // A result is one title (#421): its meta line, then the editor's language labels (#265); 'Off' marks a title of a
  // group that is off.
  function hit(ctx, m, cacheKey) {
    const { h } = ctx;
    const where = [isViewer(ctx) ? groupLabel(ctx, m.group) : null, ...titleMeta(ctx, m)];
    return row(ctx, m, m.group, cacheKey, [h('span', { class: 'lib-where' }, where), ctx.searchLanguages(m)],
      m.enabled === false && !isViewer(ctx) ? h('span', { class: 'search-badge off', text: 'Off' }) : null);
  }

  // A series: bold seasons and episodes, then the group; a film in more than one entry: its versions; a channel: the
  // group. A viewer's group is the label in front.
  function titleMeta(ctx, t) {
    const { h, plural } = ctx;
    const versions = t.contentType === 'Movies' && t.entries > 1;
    const parts = [];
    if (t.contentType === 'Series') parts.push(h('b', { text: seasonsLine(ctx, t) }));
    if (versions) parts.push(t.groups > 1 ? `${plural(t.entries, 'version')} in ${plural(t.groups, 'group')}` : plural(t.entries, 'version'));
    if (!isViewer(ctx) && !(versions && t.groups > 1)) parts.push(t.group);
    return parts.flatMap((p, i) => (i ? [' · ', p] : [p]));
  }

  // "S1–S3 · 21 episodes", "S1 · 6 episodes", or "21 episodes" without season numbers.
  function seasonsLine(ctx, t) {
    const episodes = ctx.plural(t.episodes || t.entries || 0, 'episode');
    if (t.seasonFirst == null) return episodes;
    const last = t.seasonLast ?? t.seasonFirst;
    return `${last === t.seasonFirst ? `S${t.seasonFirst}` : `S${t.seasonFirst}–S${last}`} · ${episodes}`;
  }

  // The row (#419) of a search result or a group entry: type marker, title and year, the meta line (none for a
  // group's entries), the Off badge, the viewer's group chip, the marks of a film or series (#298) and, when the row
  // opens a card, the chevron.
  function row(ctx, e, group, cacheKey, meta, badge) {
    const { h } = ctx;
    const type = TYPE_LABELS[e.contentType] ? e.contentType : 'Live';
    const li = h('li', { class: 'lib-row', dataset: { type } },
      h('span', { class: 'lib-dot', 'aria-hidden': 'true', dataset: { type } }),
      h('span', { class: 'lib-text' }, filmName(ctx, e, group, cacheKey), h('span', { class: 'lib-meta' }, meta)),
      badge,
      groupChip(ctx, e.group || group),
      rowMarks(ctx, e),
      filmChevron(ctx, e));
    const capsule = li.querySelector('.library-marks');
    if (capsule) paintMarks(capsule);
    return li;
  }

  // mount() runs only after this file has run to its end, so My marks below may follow the export.
  window.StrimLibrary = { mount, show };

  // My marks (#299): every film and series the user marked, newest change first, including titles that are in no
  // playlist any more. A row is plain text, never a link to an entry. Remove asks nothing and offers Undo, which sets
  // the same mark again with the set call; main.js sends both with the CSRF token.
  const MARK_FILTERS = [
    { value: 'all', label: 'All', params: {} },
    { value: 'seen', label: 'Seen', params: { seen: true } },
    { value: 'liked', label: 'Liked', params: { rating: 1 } },
    { value: 'disliked', label: 'Disliked', params: { rating: -1 } },
  ];
  const MARKS_PAGE = 50;

  function myMarks(ctx) {
    const { h, fmt, plural } = ctx;
    const view = { filter: 'all', seq: 0, shown: new Set(), total: 0, done: false, minPage: 1 };
    const status = (text) => h('p', { class: 'search-status', role: 'status', text });
    const count = h('p', { class: 'search-hit-meta' });
    const list = h('ol', { class: 'search-list' });
    const foot = h('div', { class: 'library-foot' });
    const pills = h('div', { class: 'pills', role: 'group', 'aria-label': 'Show marks' });
    for (const option of MARK_FILTERS) {
      pills.append(h('button', {
        type: 'button', 'aria-pressed': String(option.value === view.filter), dataset: { value: option.value },
        onclick: () => {
          view.filter = option.value;
          for (const b of pills.children) b.setAttribute('aria-pressed', String(b.dataset.value === option.value));
          start();
        },
      }, option.label));
    }

    const paint = () => {
      count.textContent = plural(view.total, 'mark');
      if (!view.done) foot.replaceChildren(h('button', { class: 'btn btn-sm', type: 'button', onclick: loadPage }, 'More'));
      else if (view.total > 0) foot.replaceChildren(status(`All ${fmt(view.total)} shown`));
      else if (view.filter === 'all') foot.replaceChildren(status('No marks yet. Mark a film or series as seen, or give it a thumb, in the search or a group.'));
      else foot.replaceChildren(status('No marks of this kind.'));
    };

    // The next page starts after the marks shown now; marks a page repeats after a remove or an undo are skipped.
    // A mark set in the search or a group moves to the top and shifts every position, so a page can add nothing
    // new: the list then moves on to the next page at once instead of asking for the same page again.
    const loadPage = async () => {
      const seq = view.seq;
      foot.replaceChildren(status(view.shown.size ? 'Loading more…' : 'Loading your marks…'));
      let page;
      let requested;
      try {
        const filter = MARK_FILTERS.find((f) => f.value === view.filter).params;
        requested = Math.max(Math.floor(view.shown.size / MARKS_PAGE) + 1, view.minPage);
        page = await ctx.listMarks({ ...filter, page: requested, pageSize: MARKS_PAGE }, ctx.controller.signal);
      } catch (err) {
        if (seq !== view.seq || ctx.controller.signal.aborted) return;
        foot.replaceChildren(h('div', { class: 'notice error', role: 'alert' },
          h('p', { text: err.message || 'Your marks could not be loaded.' }),
          h('button', { class: 'btn btn-sm', type: 'button', onclick: loadPage }, 'Try again')));
        return;
      }
      if (seq !== view.seq || !ctx.isCurrent()) return;
      const items = page.items || [];
      let added = 0;
      for (const m of items) {
        if (view.shown.has(m.key)) continue;
        view.shown.add(m.key);
        list.append(row(m));
        added += 1;
      }
      view.total = page.total;
      view.done = items.length < MARKS_PAGE || view.shown.size >= page.total;
      if (!view.done && added === 0) {
        view.minPage = requested + 1;
        loadPage();
        return;
      }
      paint();
    };

    const row = (m) => {
      const meta = [m.year, m.kind === 'series' ? 'Series' : 'Film', m.seen ? 'Seen' : '',
        { 1: 'Liked', '-1': 'Disliked' }[m.rating] || ''].filter(Boolean).join(' · ');
      const li = h('li', { class: 'library-hit' },
        h('span', { class: 'search-hit-text' },
          h('span', { class: 'search-hit-name', text: m.title }),
          h('span', { class: 'search-hit-meta', text: meta })),
        h('button', { class: 'btn btn-sm', type: 'button', 'aria-label': `Remove the mark on ${m.title}`, onclick: () => remove(m, li) }, 'Remove'));
      return li;
    };

    // The row goes at once and comes back on a failure; it stays hidden in place so Undo puts it back there.
    const remove = async (m, li) => {
      li.hidden = true;
      try {
        await ctx.removeMark(m.key);
      } catch (err) {
        // 404: the mark is already gone, for example removed on another device.
        if (err.status !== 404) {
          li.hidden = false;
          ctx.toast(`The mark was not removed. ${err.message || ''}`.trim(), { error: true });
          return;
        }
      }
      syncRows(ctx, m.key, { seen: false, rating: 0, watchlist: false });
      if (!li.isConnected) return;
      view.shown.delete(m.key);
      view.total -= 1;
      paint();
      ctx.toast(`Mark on ${m.title} removed`, { action: { label: 'Undo', onClick: () => undo(m, li) } });
    };

    const undo = async (m, li) => {
      if (!li.hidden || li.dataset.restoring) return;
      li.dataset.restoring = 'true';
      let mark;
      try {
        mark = await ctx.setMark({ name: m.year ? `${m.title} (${m.year})` : m.title, kind: m.kind, seen: m.seen, rating: m.rating, watchlist: m.watchlist });
      } catch (err) {
        delete li.dataset.restoring;
        ctx.toast(`The mark was not restored. ${err.message || ''}`.trim(), { error: true });
        return;
      }
      syncRows(ctx, mark.key, { seen: mark.seen, rating: mark.rating, watchlist: mark.watchlist === true });
      if (!li.isConnected || view.shown.has(mark.key)) return;
      li.replaceWith(row(mark));
      view.shown.add(mark.key);
      view.total += 1;
      paint();
    };

    const start = () => {
      view.seq += 1;
      view.shown = new Set();
      view.total = 0;
      view.done = false;
      view.minPage = 1;
      count.textContent = '';
      list.replaceChildren();
      loadPage();
    };
    start();
    return h('section', { class: 'library-mylist', 'aria-label': 'My marks' },
      h('h2', { class: 'library-title', text: 'My marks' }),
      h('p', { class: 'field-hint', text: 'Every film and series you marked, newest change first, also titles no longer in a playlist.' }),
      pills,
      h('div', { class: 'card' }, count, list, foot));
  }

  // A remove or an undo in My marks shows on the rows of the same title in the search and the open group.
  function syncRows(ctx, key, mark) {
    if (!marks.has(key)) return;
    marks.set(key, mark);
    for (const el of ctx.root.querySelectorAll('.library-marks')) if (el.dataset.key === key) paintMarks(el);
  }

  // The viewer's head (#383): the list's cover as on its "Shared with you" card, filled from library/open.
  function viewerCover(ctx) {
    const { h, icon, playlist } = ctx;
    const name = h('h1', { class: 'cover-name' });
    const by = h('p', { class: 'cover-by' });
    const el = h('header', { class: `cover library-cover cover-${ctx.pick(playlist.id, 6) + 1}` },
      h('div', { class: 'cover-top' },
        h('a', { href: '#/' }, icon('back'), 'My lists'),
        h('span', { class: 'cover-chip' }, icon('book'), 'Read-only')),
      h('div', null, name, by));
    const fill = (list) => {
      const owner = list.ownerName || 'Someone';
      name.textContent = list.name || 'Untitled';
      el.dataset.initials = name.textContent.split(/\s+/).filter(Boolean).slice(0, 2).map((w) => w.charAt(0)).join('').toUpperCase();
      const dot = () => h('i', { 'aria-hidden': 'true' });
      by.replaceChildren(
        h('span', { class: `share-avatar is-sm${['', ' t-live', ' t-movies'][ctx.pick(`${playlist.id}:tone`, 3)]}`, 'aria-hidden': 'true', text: owner.charAt(0) }),
        h('span', null, 'Shared by ', h('b', { text: owner })), dot(),
        h('span', { text: ctx.plural(list.groups.length, 'group') }), dot(),
        h('span', { text: ctx.plural(list.totalChannels, 'entry', 'entries') }));
    };
    return { el, fill };
  }

  // Any 404 from the playlist's Library routes means the share is gone: one message, then back to the lists.
  function accessGone(ctx, err) {
    if (!isViewer(ctx) || err?.status !== 404) return false;
    const { h } = ctx;
    ctx.controller.abort();
    ctx.root.replaceChildren(h('section', { class: 'card library-gone', role: 'alert' },
      h('h2', { text: 'This list is no longer shared with you' }),
      h('p', { text: 'Its owner stopped sharing it, or it was removed from your lists. Your marks stay.' }),
      h('a', { class: 'btn btn-primary', href: '#/' }, 'Back to my lists')));
    return true;
  }

  // The group on every item of a viewer's Library: a chip before the marks, on a phone in the meta line.
  const groupChip = (ctx, group) => (isViewer(ctx) && group
    ? ctx.h('span', { class: 'group-chip', title: group }, ctx.icon('folder'), ctx.h('span', { text: group })) : null);
  const groupLabel = (ctx, group) => ctx.h('span', { class: 'group-label' }, ctx.icon('folder'), ctx.h('span', { text: group }));

  // Film card (#367): a film row's name opens poster and details from TMDB in a <dialog>, a bottom sheet on a phone
  // and a side pane from 1100px. Only when /api/config reports TMDB on; it reads only and never changes the playlist.
  const card = { el: null, film: null, button: null, seq: 0, pending: null, answer: null, stack: [] };
  const keyAlias = new Map();
  const answers = new Map();
  // The mark key that first used an answer's marks (#368).
  const answerMarkKey = new WeakMap();
  const wide = window.matchMedia('(min-width: 1100px)');
  const opensCard = (ctx, e) => ctx.tmdbEnabled === true && e.contentType === 'Movies' && !!e.markKey;
  // A series row opens the same card with the series answer (#390); words and calls follow the entry's type.
  const opensSeriesCard = (ctx, e) => ctx.tmdbEnabled === true && e.contentType === 'Series' && !!e.markKey;
  const opens = (ctx, e) => opensCard(ctx, e) || opensSeriesCard(ctx, e);
  const word = (film) => (film.type === 'Series' ? 'series' : 'film');
  const subject = (answer) => answer?.film || answer?.series || null;
  const FILM_NOTES = {
    notFound: 'TMDB has no film with this title. Your marks still work.',
    notApplicable: 'There is no film card for this entry.',
    unavailable: 'TMDB is not answering right now.',
    failed: 'The film details could not be loaded.',
  };
  const notes = (film) => (film.type === 'Series'
    ? Object.fromEntries(Object.entries(FILM_NOTES).map(([k, v]) => [k, v.replace('film', 'series')])) : FILM_NOTES);
  const seriesYears = (s) => (!s.firstYear ? 'Series'
    : s.state === 'returning' && !s.lastYear ? `Series · since ${s.firstYear}`
      : s.lastYear && s.lastYear !== s.firstYear ? `Series · ${s.firstYear}–${s.lastYear}` : `Series · ${s.firstYear}`);
  const STATES = { returning: 'Returning', ended: 'Ended', cancelled: 'Cancelled', upcoming: 'Upcoming' };
  const seriesFacts = (s) => [STATES[s.state],
    s.seasonCount ? `${s.seasonCount} ${s.seasonCount === 1 ? 'season' : 'seasons'}` : '',
    s.episodeCount ? `${s.episodeCount} ${s.episodeCount === 1 ? 'episode' : 'episodes'}` : '', s.network].filter(Boolean).join(' · ');

  // The title line (#419): the name without a trailing "(YYYY)", then the year; the full name stays the button's name.
  function filmName(ctx, e, group, cacheKey) {
    // A row built from a title (#421) has its title and year; its name is the first entry's.
    const collapsed = typeof e.title === 'string' && e.title !== '';
    const year = collapsed ? [null, e.title, e.year ? String(e.year) : null] : /^(.+?)\s*\((\d{4})\)$/.exec(e.name.trim());
    const title = year ? [year[1], year[2] ? ctx.h('span', { class: 'y', text: year[2] }) : null] : [e.name];
    if (!opens(ctx, e)) return ctx.h('span', { class: 'lib-title' }, title);
    // collapsed: the card shows no "Opened from" line, since the name is only one of the series' episodes.
    const film = { name: e.name, group: group || '', cacheKey, markKey: e.markKey, off: e.enabled === false, collapsed, type: opensSeriesCard(ctx, e) ? 'Series' : 'Movies' };
    const button = ctx.h('button', {
      class: 'lib-title library-film-open', type: 'button', 'aria-haspopup': 'dialog', 'aria-expanded': 'false', 'aria-label': e.name,
      dataset: { name: film.name, group: film.group }, onclick: () => openFilm(ctx, film, button),
    }, title);
    return button;
  }

  const filmChevron = (ctx, e) => (opens(ctx, e) ? ctx.h('span', { class: 'lib-chev', 'aria-hidden': 'true' }, ctx.icon('chevron')) : null);

  function markOpen(ctx, button) {
    for (const b of ctx.root.querySelectorAll('.library-film-open')) {
      b.setAttribute('aria-expanded', String(b === button));
      b.closest('.lib-row')?.classList.toggle('is-open', b === button);
    }
  }

  const showCard = () => (wide.matches ? card.el.show() : card.el.showModal());

  // The pane does not block the page: another film row swaps the content in place. Back closes the card.
  function openFilm(ctx, film, button) {
    if (!card.el?.isConnected) card.el = filmDialog(ctx);
    card.film = film;
    card.button = button;
    card.stack = [];
    markOpen(ctx, button);
    if (!card.el.open) {
      showCard();
      history.pushState({ filmCard: true }, '');
    }
    loadFilm(ctx);
  }

  function filmDialog(ctx) {
    const { h, icon } = ctx;
    const { signal } = ctx.controller;
    // The head floats over the stage: the left slot is Back from a similar film (#368), the right one Close.
    const head = h('div', { class: 'film-card-head' }, h('span'),
      h('button', { class: 'icon-btn film-glass', type: 'button', 'aria-label': 'Close', onclick: () => el.close() }, icon('close')));
    const el = h('dialog', { class: isViewer(ctx) ? 'sheet film-card is-viewer' : 'sheet film-card', 'aria-labelledby': 'film-card-title' }, head, h('div', { class: 'film-scroll' }));
    el.addEventListener('click', (e) => { if (e.target === el) el.close(); });
    el.addEventListener('close', () => filmClosed(ctx));
    // A new mode closes and reopens the card with the same content; the close handler sees it open again.
    const reopen = () => { el.close(); showCard(); el.querySelector('#film-card-title')?.focus(); };
    wide.addEventListener('change', () => { if (el.open) reopen(); }, { signal });
    window.addEventListener('popstate', () => { if (el.open) el.close(); }, { signal });
    document.addEventListener('keydown', (e) => {
      const here = el.contains(document.activeElement) || document.activeElement === card.button;
      if (e.key === 'Escape' && el.open && !el.matches(':modal') && here) el.close();
    }, { signal });
    signal.addEventListener('abort', () => { card.pending?.abort(); el.close(); }, { once: true });
    ctx.root.append(el);
    return el;
  }

  // Closing never touches the page scroll; the focus goes back to the row, found again if the list was redrawn.
  function filmClosed(ctx) {
    if (card.el.open || ctx.controller.signal.aborted) return;
    card.pending?.abort();
    card.seq += 1;
    if (history.state?.filmCard) history.back();
    markOpen(ctx, null);
    const { button } = card;
    const film = card.stack[0]?.film || card.film;
    const again = [...ctx.root.querySelectorAll('.library-film-open')].find((b) => b.dataset.name === film.name && b.dataset.group === film.group);
    (button?.isConnected ? button : again || ctx.root.querySelector('.input-search'))?.focus({ preventScroll: true });
  }

  // One request per opened card; a newer one aborts it. Matched answers are kept for the page session.
  async function loadFilm(ctx) {
    const { film } = card;
    const seq = ++card.seq;
    card.pending?.abort();
    const key = JSON.stringify([film.name, film.group, includeOff, hideSeen, film.type]);
    let answer = answers.get(key);
    if (!answer) {
      renderFilm(ctx, null);
      const own = (card.pending = new AbortController());
      try {
        const params = { name: film.name, group: film.group, type: 'Movies', cacheKey: film.cacheKey, includeDisabled: includeOff, hideSeen };
        if (film.type === 'Series') answer = await ctx.seriesCard({ ...params, type: 'Series' }, own.signal);
        else answer = await ctx.filmCard(params, own.signal);
      } catch (err) {
        if (seq === card.seq && !accessGone(ctx, err)) renderFilm(ctx, { status: 'failed' });
        return;
      }
      if (seq !== card.seq || !ctx.isCurrent()) return;
      if (answer.status === 'matched') answers.set(key, answer);
    }
    renderFilm(ctx, answer);
  }

  // answer null is the loading state.
  function renderFilm(ctx, answer) {
    const { h, icon } = ctx;
    const entry = card.film;
    const scroll = card.el.querySelector('.film-scroll');
    const f = answer?.status === 'matched' ? subject(answer) : null;
    const series = entry.type === 'Series';
    const choose = answer?.status === 'choose' && answer.candidates?.length > 0;
    const title = h('h2', { class: choose ? 'film-state-title' : 'film-title', id: 'film-card-title', tabindex: '-1',
      text: choose ? `Which ${word(entry)} is it?` : f?.title || entry.name });
    card.answer = answer;
    const marksEl = cardMarks(ctx, entry, answer);
    const body = h('div', { class: 'film-body' });
    let top;
    if (!answer) {
      top = h('div', { class: 'film-load' }, h('div', { class: 'film-poster skel', 'aria-hidden': 'true' }), title,
        h('span', { class: 'skel skel-line', 'aria-hidden': 'true' }), h('div', { class: 'film-actions' }, marksEl),
        h('span', { class: 'sr-only', role: 'status', text: `Loading ${word(entry)} details` }));
    } else if (choose) {
      // Picking a candidate stores the match (#368) and loads the card again.
      top = h('div', { class: 'film-choose' }, title,
        h('p', { class: 'film-choose-text', text: `More than one ${word(entry)} fits "${entry.name}". Pick yours and Strim remembers it.` }),
        h('ul', { class: 'film-grid' }, answer.candidates.map((c) => h('li', null, h('button', { class: 'film-tile', type: 'button', onclick: () => setMatch(ctx, c.id) },
          poster(ctx, c.posterUrl, '', 'film-tile-poster', entry.type),
          h('span', { class: 'film-tile-title', text: c.title || entry.name }),
          h('span', { class: 'film-tile-meta', text: c.year ? String(c.year) : 'Year unknown' }))))),
        h('div', { class: 'film-actions' }, marksEl));
    } else {
      const rating = f?.rating > 0 ? f.rating.toFixed(1) : '';
      const meta = f ? (f.genres || []).slice(0, 3).join(' · ') : entry.group;
      const facts = f && series ? seriesFacts(f) : '';
      top = h('div', { class: 'film-stage' }, filmBackdrop(ctx, f?.posterUrl), poster(ctx, f?.posterUrl, `Poster of ${title.textContent}`, 'film-poster', entry.type),
        f && series ? h('p', { class: 'film-kicker', dataset: { type: 'Series' } }, h('span', { class: 'dot', 'aria-hidden': 'true' }), seriesYears(f))
          : f ? h('p', { class: 'film-kicker', dataset: { type: 'Movies' } }, h('span', { class: 'dot', 'aria-hidden': 'true' }), f.year ? `Film · ${f.year}` : 'Film') : null,
        title, meta ? h('p', { class: 'film-meta', text: meta }) : null, facts ? h('p', { class: 'series-facts', text: facts }) : null,
        h('div', { class: 'film-actions' }, rating ? [
          h('span', { class: 'film-rating', role: 'img', 'aria-label': `TMDB rating ${rating} out of 10` }, icon('star'), h('b', { text: rating }), h('span', { text: 'TMDB' })),
          h('span', { class: 'film-sep', 'aria-hidden': 'true' })] : null, marksEl));
      // A viewer's card names the group first (#383): where the title is in this list, read-only.
      if (isViewer(ctx)) {
        body.append(h('div', { class: 'film-where' },
          h('span', { class: 'film-where-icon', 'aria-hidden': 'true' }, icon('folder')),
          h('span', null, h('small', { text: 'In group' }), h('b', { text: entry.group })),
          h('span', { class: 'share-tag', text: 'Read-only' })));
      }
      if (f?.overview) body.append(h('p', { class: 'film-overview is-clamped', text: f.overview }));
      if (series && hasInList(f)) body.append(seriesInList(ctx, f, card.stack.length === 0 && !entry.collapsed ? answer.context : null));
      if (f?.similar?.length) body.append(similarFilms(ctx, f));
      if (f) {
        body.append(h('div', { class: 'film-source' },
          h('p', null, `In ${listWord(ctx)} as`, h('b', { text: entry.name }), entry.group, entry.off ? h('span', { class: 'search-badge off', text: 'Off' }) : null),
          h('button', { class: 'film-unmatch', type: 'button', onclick: () => setMatch(ctx, null) }, `Not this ${word(entry)}`)));
      }
      if (!f) {
        const failed = answer.status === 'failed';
        const retry = failed || answer.status === 'unavailable';
        body.append(h('div', { class: failed ? 'notice error' : 'notice', role: failed ? 'alert' : null },
          h('p', { text: notes(entry)[answer.status === 'choose' ? 'notFound' : answer.status] || notes(entry).failed }),
          retry ? h('button', { class: 'btn btn-sm', type: 'button', onclick: () => loadFilm(ctx) }, 'Try again') : null));
      }
    }
    if (answer?.tmdbData) body.append(filmCredit(ctx, entry));
    const prev = card.stack.at(-1);
    card.el.querySelector('.film-card-head').firstElementChild.replaceWith(prev
      ? h('button', { class: 'icon-btn film-glass', type: 'button', 'aria-label': `Back to ${prev.title}`, onclick: () => filmBack(ctx) }, icon('back'))
      : h('span'));
    card.el.setAttribute('aria-busy', String(!answer));
    scroll.replaceChildren(top, body);
    scroll.scrollTop = 0;
    const overview = body.querySelector('.film-overview.is-clamped');
    if (overview && overview.scrollHeight > overview.clientHeight) {
      const more = h('button', { class: 'film-more', type: 'button', 'aria-expanded': 'false', text: 'More', onclick: () => {
        const open = !overview.classList.toggle('is-clamped');
        more.textContent = open ? 'Less' : 'More';
        more.setAttribute('aria-expanded', String(open));
      } });
      overview.after(more);
    }
    title.focus();
  }

  // Design A (#367): a blurred copy of the same poster washes the stage and fades into the card. Same URL as the <img>,
  // so no extra request or host; set through CSSOM, which the CSP allows. It fades in once the image has loaded and
  // goes away if the image fails, so a broken poster never leaves a coloured smear.
  function filmBackdrop(ctx, url) {
    if (!url) return null;
    const el = ctx.h('div', { class: 'film-backdrop', 'aria-hidden': 'true' });
    el.style.setProperty('--film-art', `url(${JSON.stringify(url)})`);
    const probe = new Image();
    probe.referrerPolicy = 'no-referrer';
    probe.addEventListener('load', () => el.classList.add('is-loaded'), { once: true });
    probe.addEventListener('error', () => el.remove(), { once: true });
    probe.src = url;
    return el;
  }

  // The poster as given by the API, lazy; none or a failed image keeps the same box with the Movies diamond.
  function poster(ctx, url, alt, cls, type) {
    // A series box (#390) carries the Series square instead of the diamond.
    const empty = () => ctx.h('div', { class: `${cls} film-poster-empty${type === 'Series' ? ' is-series' : ''}`, 'aria-hidden': 'true' });
    if (!url) return empty();
    const img = ctx.h('img', { class: cls, alt, width: 342, height: 513, loading: 'lazy', decoding: 'async', referrerpolicy: 'no-referrer', src: url });
    img.addEventListener('error', () => img.replaceWith(empty()), { once: true });
    return img;
  }

  // The #298 controls as an icon capsule; they share the state by mark key, so the rows repaint with them. In a modal card
  // a failure shows inside the card for 5 s, because the toasts lie under the dialog's top layer.
  function cardMarks(ctx, entry, answer) {
    const key = entry.markKey;
    const now = marks.get(key) || { seen: false, rating: 0, watchlist: false };
    // An answer's marks count once: a kept answer reopened later must not undo a mark set since. Reopened under
    // another key (its row, then a similar tile), it takes the live state of the key that first used it.
    const owner = answerMarkKey.get(answer);
    const given = !answer?.marks || owner === key ? now
      : (owner && marks.get(owner)) || { seen: answer.marks.seen === true, rating: answer.marks.rating || 0, watchlist: answer.marks.watchlist === true };
    if (answer?.marks && !owner) answerMarkKey.set(answer, key);
    const cardCtx = { ...ctx, toast: cardToast(ctx) };
    const markEntry = { name: entry.name, contentType: entry.type === 'Series' ? 'Series' : 'Movies', markKey: entry.markKey, ...given };
    const el = markControls(cardCtx, markEntry);
    el.classList.add('film-marks');
    if (given.seen !== now.seen || given.rating !== now.rating || given.watchlist !== now.watchlist) syncRows(ctx, entry.markKey, given);
    // Capsule, thin line, Watchlist button (#420), in every state that shows the capsule.
    return [el, ctx.h('span', { class: 'film-sep', 'aria-hidden': 'true' }), watchButton(cardCtx, markEntry)];
  }

  const cardToast = (ctx) => (text, options) => {
    if (!card.el.matches(':modal')) return ctx.toast(text, options);
    const note = ctx.h('div', { class: 'notice error', role: 'alert', text });
    card.el.querySelector('.film-body')?.prepend(note);
    setTimeout(() => note.remove(), 5000);
  };

  // In your list (#391): the series' seasons as pills, one open at a time with its episode grid. The context marks the
  // season and episode of the row the card was opened from; a similar series opened in place has none.
  const hasInList = (f) => !!f?.inList && (f.inList.episodes > 0 || f.inList.unnumbered > 0);
  const seasonLabel = (n) => (n === 0 ? 'Specials' : `S${n}`);

  function seriesInList(ctx, f, context) {
    const { h } = ctx;
    const list = f.inList;
    const here = context?.season != null ? context : null;
    const panel = h('div', { class: 'series-panel' });
    const line = here ? h('p', { class: 'series-context' }, 'Opened from ',
      h('b', { text: [seasonLabel(here.season), here.episode != null ? `E${here.episode}` : ''].filter(Boolean).join(' · ') })) : null;
    const pills = list.seasons.map((s) => {
      const pill = h('button', {
        class: s.count > 0 ? 'series-season' : 'series-season is-absent', type: 'button', 'aria-expanded': 'false',
        onclick: () => openSeason(pill, s),
      }, here?.season === s.season ? h('span', { class: 'series-here', 'aria-hidden': 'true' }) : null,
      h('b', { text: seasonLabel(s.season) }), h('span', { text: s.total != null ? `${s.count}/${s.total}` : String(s.count) }));
      return pill;
    });
    // A pill opens its episodes below and closes the one open before; tapping the open pill closes it.
    function openSeason(pill, s) {
      const open = pill.getAttribute('aria-expanded') !== 'true';
      for (const p of pills) p.setAttribute('aria-expanded', String(open && p === pill));
      panel.replaceChildren(...(open ? seasonEpisodes(ctx, s, here?.season === s.season ? here.episode : null) : []));
      if (line) line.hidden = open;
    }
    const total = f.episodeCount > 0 ? `${list.episodes} of ${f.episodeCount} episodes` : `${list.episodes} ${list.episodes === 1 ? 'episode' : 'episodes'}`;
    return h('section', { class: 'film-section series-section' },
      h('div', { class: 'film-section-head' },
        h('h3', { class: 'film-section-title', text: `In ${listWord(ctx)}` }),
        h('span', { class: 'film-section-count', text: total })),
      pills.length ? h('div', { class: 'series-rail', role: 'group', 'aria-label': 'Seasons' }, pills) : null,
      panel, line,
      list.unnumbered > 0 ? h('p', { class: 'series-note', text: `${list.unnumbered} ${list.unnumbered === 1 ? 'entry' : 'entries'} without an episode number` }) : null);
  }

  // Every number up to TMDB's total plus those the list has beyond it; at most 200, the rest as "and N more".
  function seasonEpisodes(ctx, s, episode) {
    const { h } = ctx;
    const have = new Set(s.episodes);
    const all = [...new Set([...Array.from({ length: Math.min(s.total || 0, 200) }, (_, i) => i + 1), ...s.episodes])].sort((a, b) => a - b);
    const shown = all.slice(0, 200);
    const more = Math.max(all.length + s.count - s.episodes.length, s.total || 0) - shown.length;
    const items = shown.map((n) => h('li', {
      class: ['series-episode', have.has(n) ? '' : 'is-absent', n === episode ? 'is-here' : ''].filter(Boolean).join(' '),
      text: String(n), 'aria-label': `Episode ${n}${have.has(n) ? '' : `, not in ${listWord(ctx)}`}`,
    }));
    if (more > 0) items.push(h('li', { class: 'series-more', text: `and ${more} more` }));
    return [h('ol', { class: 'series-episodes', 'aria-label': `${seasonLabel(s.season)} episodes` }, items),
      h('p', { class: 'series-legend' }, h('span', { class: 'series-key' }), `In ${listWord(ctx)}`, h('span', { class: 'series-key is-absent' }), `Not in ${listWord(ctx)}`)];
  }

  // Similar films (#368) in the API's order, those in the list first; only those open a card.
  function similarFilms(ctx, f) {
    const { h, icon } = ctx;
    const label = card.film.type === 'Series' ? 'Similar series' : 'Similar films';
    const rail = h('div', { class: 'film-rail', role: 'group', 'aria-label': label, tabindex: '0' }, f.similar.map((s, i) => similarTile(ctx, s, i)));
    const step = (dir) => rail.scrollBy({ left: dir * rail.clientWidth });
    return h('section', { class: 'film-section' },
      h('div', { class: 'film-section-head' },
        h('h3', { class: 'film-section-title', text: label }),
        h('span', { class: 'film-section-count', text: `${f.similar.filter((s) => s.inList).length} in ${listWord(ctx)}` }),
        h('span', { class: 'film-rail-nav' },
          h('button', { class: 'icon-btn', type: 'button', 'aria-label': 'Previous', onclick: () => step(-1) }, icon('back')),
          h('button', { class: 'icon-btn', type: 'button', 'aria-label': 'Next', onclick: () => step(1) }, icon('chevron')))),
      rail);
  }

  function similarTile(ctx, s, i) {
    const { h, icon } = ctx;
    const off = s.inList && s.enabled === false;
    const art = h('span', { class: 'film-tile-art' }, poster(ctx, s.posterUrl, '', 'film-tile-poster', card.film.type),
      off ? h('span', { class: 'film-tile-flag', 'aria-hidden': 'true', text: 'Off' }) : null,
      s.seen ? h('span', { class: 'film-tile-seen', 'aria-hidden': s.inList ? 'true' : null }, icon('check'), s.inList ? null : h('span', { class: 'sr-only', text: 'Seen' })) : null);
    const title = h('span', { class: 'film-tile-title', text: s.title });
    if (!s.inList) {
      return h('div', { class: 'film-tile is-absent' }, art, title,
        h('span', { class: 'film-tile-meta', text: [s.year, `Not in ${listWord(ctx)}`].filter(Boolean).join(' · ') }));
    }
    const label = [s.title, s.year, `in ${s.group}`, off ? 'group off' : '', s.seen ? 'seen' : ''].filter(Boolean).join(', ');
    // A viewer's tile carries the group label instead of the on/off dot (#383).
    return h('button', { class: 'film-tile', type: 'button', 'aria-label': label, dataset: { i: String(i) }, onclick: () => openSimilar(ctx, s, i) }, art, title,
      h('span', { class: 'film-tile-meta' }, isViewer(ctx) ? groupLabel(ctx, s.group)
        : [h('span', { class: off ? 'film-on is-off' : 'film-on', 'aria-hidden': 'true' }), h('span', { text: s.group })]));
  }

  // The card in place: the current one goes on a stack with its scroll positions; device Back still closes all.
  function openSimilar(ctx, s, i) {
    const scroll = card.el.querySelector('.film-scroll');
    card.stack.push({ film: card.film, answer: card.answer, title: subject(card.answer).title || card.film.name, similar: s, tile: i,
      top: scroll.scrollTop, rail: scroll.querySelector('.film-rail')?.scrollLeft || 0 });
    card.film = { name: s.entry, group: s.group, cacheKey: card.film.cacheKey, markKey: `similar:${s.entry}`, off: s.enabled === false, type: card.film.type };
    // The local key starts from the tile, so Back from a card still loading or failed keeps the tile's Seen.
    if (!marks.has(card.film.markKey)) marks.set(card.film.markKey, { seen: s.seen === true, rating: 0 });
    loadFilm(ctx);
  }

  function filmBack(ctx) {
    const prev = card.stack.pop();
    prev.similar.seen = marks.get(card.film.markKey)?.seen ?? prev.similar.seen;
    card.seq += 1;
    card.pending?.abort();
    card.film = prev.film;
    renderFilm(ctx, prev.answer);
    const scroll = card.el.querySelector('.film-scroll');
    scroll.scrollTop = prev.top;
    const rail = scroll.querySelector('.film-rail');
    if (rail) rail.scrollLeft = prev.rail;
    rail?.querySelector(`[data-i="${prev.tile}"]`)?.focus({ preventScroll: true });
  }

  // Choose a candidate, or clear the match with a null id (#368); a failure brings the previous card back.
  async function setMatch(ctx, tmdbId) {
    const { film } = card;
    const before = card.answer;
    const seq = ++card.seq;
    renderFilm(ctx, null);
    try {
      if (film.type === 'Series') await ctx.setSeriesMatch({ name: film.name, tmdbId });
      else await ctx.setFilmMatch({ name: film.name, tmdbId });
    } catch (err) {
      if (seq !== card.seq) return;
      renderFilm(ctx, before);
      cardToast(ctx)(`The ${word(film)} was not changed. ${err.message || ''}`.trim(), { error: true });
      return;
    }
    if (seq !== card.seq) return;
    // Every episode row of a series shares its match (#392), so a series match drops every kept series answer.
    const stale = (k) => (film.type === 'Series' ? k[4] === 'Series' : k[0] === film.name);
    for (const k of [...answers.keys()]) if (stale(JSON.parse(k))) answers.delete(k);
    await loadFilm(ctx);
    // An automatic match cannot be cleared: the card then comes back with the same film.
    if (tmdbId === null && card.film === film && subject(card.answer) && subject(card.answer).id === subject(before)?.id) {
      card.el.querySelector('.film-body')?.prepend(ctx.h('div', { class: 'notice', text: `Strim finds only this ${word(film)} for the title.` }));
    }
  }

  // TMDB's logo and notice (#315) with the credit line (#316), only when the card carries TMDB data.
  function filmCredit(ctx, entry) {
    const { h } = ctx;
    const link = (...kids) => h('a', { href: 'https://www.themoviedb.org', target: '_blank', rel: 'noopener noreferrer' }, ...kids);
    const logo = h('img', { src: '/tmdb-logo.svg', alt: 'TMDB', class: 'tmdb-logo' });
    const logoLink = link(logo);
    logo.addEventListener('error', () => { logoLink.hidden = true; }, { once: true });
    return h('div', { class: 'film-credit' },
      h('p', { class: 'film-credit-line' }, logoLink, h('span', null, entry.type === 'Series' ? 'Series data and posters: ' : 'Film data and posters: ', link('TMDB'))),
      h('p', { text: 'This application uses TMDB and the TMDB APIs but is not endorsed, certified, or otherwise approved by TMDB.' }));
  }
})();
