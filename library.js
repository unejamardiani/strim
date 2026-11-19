// Strim Library: a read-only view of a saved playlist at #/p/<playlist id>/library.
// main.js calls window.StrimLibrary.mount() from its router and passes in everything this file uses.
// Nothing here changes the playlist or what the player link serves.

(() => {
  // ctx.controller is aborted by main.js when the route leaves the Library, so a pending load stops waiting.
  function mount(ctx) {
    const { root, playlist, h, icon, controller } = ctx;
    const head = h('div', { class: 'editor-head library-head' },
      h('a', { class: 'icon-btn', href: '#/', 'aria-label': 'Back to playlists' }, icon('back')),
      h('h1', { class: 'page-title', text: playlist.name || 'Untitled' }),
      h('a', { class: 'btn btn-sm', href: `#/p/${playlist.id}` }, icon('list'), 'Edit groups'));
    const body = h('div');
    // My marks (#299) do not hang on the playlist, so they show even when it has no source or fails to load.
    root.replaceChildren(head, body, myMarks(ctx));
    if (!playlist.sourceUrl) {
      body.replaceChildren(h('div', { class: 'notice' }, 'This playlist has no source URL. Add one in the editor to see what it holds.'));
      return;
    }
    load(ctx, body, controller);
  }

  async function load(ctx, body, own) {
    const { h, fmt, plural } = ctx;
    body.replaceChildren(h('div', { class: 'card loading-card' },
      h('span', { class: 'spinner', 'aria-hidden': 'true' }),
      h('div', { class: 'grow', text: 'Loading the playlist from your provider' })));
    let result;
    try {
      result = await ctx.loadGroups(own.signal);
    } catch (err) {
      if (own.signal.aborted || !ctx.isCurrent()) return;
      body.replaceChildren(h('div', { class: 'notice error' },
        h('p', { text: ctx.errorMessage(err) }),
        h('div', { class: 'row-actions' },
          h('button', { class: 'btn btn-sm', type: 'button', onclick: () => load(ctx, body, own) }, 'Try again'))));
      return;
    }
    if (own.signal.aborted || !ctx.isCurrent()) return;
    const on = result.groups.filter((g) => !result.disabled.has(g.key));
    const entries = on.reduce((sum, g) => sum + g.count, 0);
    const browse = browseSection(ctx, result);
    body.replaceChildren(h('section', { class: 'card' },
      h('p', null,
        h('span', { class: 'num', text: fmt(on.length) }), ` of ${plural(result.groups.length, 'group')} on, `,
        h('span', { class: 'num', text: fmt(entries) }), ` of ${plural(result.totalChannels, 'entry', 'entries')} in your list`),
      h('p', { class: 'field-hint', text: 'The Library only reads your list. To change what the player link serves, edit the groups.' })),
    searchSection(ctx, result, browse.refresh, browse.reload),
    browse.el);
  }

  // The type control's choices and the content types each one sends; All sends none.
  const TYPES = [
    { value: 'all', label: 'All', types: [] },
    { value: 'films', label: 'Films', types: ['Movies'] },
    { value: 'series', label: 'Series', types: ['Series'] },
    { value: 'channels', label: 'Channels', types: ['Live'] },
  ];
  const TYPE_LABELS = { Live: 'Channel', Movies: 'Film', Series: 'Series' };

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
    const state = { query: '', type: 'all', timer: 0, seq: 0, host, filter };
    const results = { head: h('div', { class: 'search-head-status' }), body: h('div') };
    const run = () => runSearch(ctx, list, state, results);
    const schedule = () => { clearTimeout(state.timer); state.timer = setTimeout(run, 300); };

    const input = h('input', {
      class: 'input input-search', type: 'search', placeholder: 'Search films, series and channels',
      'aria-label': 'Search your list', autocomplete: 'off', spellcheck: 'false', enterkeyhint: 'search',
      oninput: () => { state.query = input.value.trim(); schedule(); },
      onkeydown: (e) => { if (e.key === 'Enter') e.target.blur(); },
    });
    const types = h('div', { class: 'pills', role: 'group', 'aria-label': 'Type' });
    for (const option of TYPES) {
      types.append(h('button', {
        type: 'button', 'aria-pressed': String(option.value === state.type), dataset: { value: option.value },
        onclick: () => {
          state.type = option.value;
          for (const b of types.children) b.setAttribute('aria-pressed', String(b.dataset.value === option.value));
          run();
        },
      }, option.label));
    }
    const toggle = h('button', {
      class: 'btn btn-sm library-toggle', type: 'button', 'aria-pressed': String(includeOff),
      onclick: () => {
        includeOff = !includeOff;
        toggle.setAttribute('aria-pressed', String(includeOff));
        run();
        onToggle();
      },
    }, 'Include switched-off groups');
    const markFilter = (label, get, set) => {
      const b = h('button', {
        class: 'btn btn-sm library-toggle', type: 'button', 'aria-pressed': String(get()),
        onclick: () => {
          set(!get());
          b.setAttribute('aria-pressed', String(get()));
          run();
          onMarkFilter();
        },
      }, label);
      return b;
    };
    const seenFilter = markFilter('Hide seen', () => hideSeen, (v) => { hideSeen = v; });
    const likedFilter = markFilter('Liked only', () => likedOnly, (v) => { likedOnly = v; });

    ctx.controller.signal.addEventListener('abort', () => clearTimeout(state.timer), { once: true });
    // On a phone the labels that do not fit wait behind "+N", as in the editor; a new width fits them again.
    window.addEventListener('resize', () => ctx.fitSearchLanguages(results.body), { signal: ctx.controller.signal });
    run();
    return h('section', { class: 'library-search' },
      input,
      h('div', { class: 'library-filters' }, types, toggle, seenFilter, likedFilter),
      h('section', { class: 'card search-results', 'aria-label': 'Search results' },
        h('div', { class: 'search-head' }, results.head, filter.bar),
        filter.panel,
        results.body));
  }

  async function runSearch(ctx, list, state, results) {
    const { h, plural } = ctx;
    clearTimeout(state.timer);
    const seq = ++state.seq;
    const status = (text) => h('p', { class: 'search-status', role: 'status', text });
    // Shows the status beside the language chip and the results below the chip's open list.
    const show = (head, ...nodes) => {
      results.head.replaceChildren(...(head ? [status(head)] : []));
      results.body.replaceChildren(...nodes);
      state.filter.render();
    };
    const hint = ctx.searchHint(state.query);
    if (hint) {
      show(hint);
      return;
    }
    const hideDisabled = !includeOff;
    const contentTypes = TYPES.find((t) => t.value === state.type).types;
    const lang = state.host.searchLang.join(',');
    const body = { cacheKey: list.cacheKey, query: state.query, disabledGroups: [...list.disabled], hideDisabled };
    if (contentTypes.length) body.contentTypes = contentTypes;
    if (lang) body.lang = lang;
    if (hideSeen) body.hideSeen = true;
    if (likedOnly) body.liked = true;
    show('Searching…');
    let found;
    try {
      found = await ctx.search(body, ctx.controller.signal);
    } catch (err) {
      if (seq !== state.seq || ctx.controller.signal.aborted) return;
      show('', h('div', { class: 'notice error', role: 'alert', text: err.message || 'Search failed.' }));
      return;
    }
    if (seq !== state.seq || !ctx.isCurrent()) return;
    if (Array.isArray(found.languages)) {
      state.host.searchLanguages = found.languages.filter((l) => l && typeof l.value === 'string' && l.value);
    }
    const matches = found.matches || [];
    const hidden = hideDisabled && Number.isInteger(found.hiddenCount) ? found.hiddenCount : 0;
    const seen = hideSeen && Number.isInteger(found.seenHiddenCount) ? found.seenHiddenCount : 0;
    const left = [hidden > 0 ? `${plural(hidden, 'match', 'matches')} in switched-off groups` : '',
      seen > 0 ? `${plural(seen, 'seen match', 'seen matches')} hidden` : ''].filter(Boolean).join(' · ');
    let text = 'Nothing matches your search.';
    if (matches.length) text = [plural(matches.length, 'match', 'matches'), left].filter(Boolean).join(' · ');
    else if (left) text = `Nothing in your list. ${left}.`;
    else if (likedOnly) text = 'Nothing you liked matches your search.';
    else if (lang) text = 'Nothing matches your search in the chosen languages.';
    if (matches.some((m) => m.languageSource === 'title')) text += ' · Labels from the title';
    const nodes = [];
    if (matches.length) nodes.push(h('ul', { class: 'search-list' }, matches.map((m) => hit(ctx, m, list.cacheKey))));
    if (found.hasMore === true) nodes.push(h('p', { class: 'search-more', text: 'More matches, refine your search.' }));
    show(text, ...nodes);
    ctx.fitSearchLanguages(results.body);
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
    marks.set(key, { seen: entry.seen === true, rating: entry.rating || 0 });
    // Tapping an active control again undoes it.
    const control = (label, name, next) => h('button', {
      class: 'icon-btn library-mark', type: 'button', 'aria-label': label, title: label, 'aria-pressed': 'false',
      onclick: () => setMark(ctx, entry.name, kind, key, next(marks.get(key))),
    }, icon(name));
    const el = h('span', { class: 'library-marks', role: 'group', 'aria-label': 'Your marks', dataset: { key } },
      control('Seen', 'check', (m) => ({ ...m, seen: !m.seen })),
      control('Thumbs up', 'up', (m) => ({ ...m, rating: m.rating === 1 ? 0 : 1 })),
      control('Thumbs down', 'down', (m) => ({ ...m, rating: m.rating === -1 ? 0 : -1 })));
    paintMarks(el);
    return el;
  }

  function paintMarks(el) {
    const m = marks.get(el.dataset.key);
    const [seen, up, down] = el.children;
    seen.setAttribute('aria-pressed', String(m.seen));
    up.setAttribute('aria-pressed', String(m.rating === 1));
    down.setAttribute('aria-pressed', String(m.rating === -1));
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
      const saved = await ctx.setMark({ name, kind, seen: next.seen, rating: next.rating });
      // A card opened from a similar film (#368) uses a local key; the rows of the title use the one the API returns.
      const real = saved?.key || keyAlias.get(key);
      if (real && real !== key) {
        keyAlias.set(key, real);
        syncRows(ctx, real, next);
      }
    } catch (err) {
      // A later tap on the same title already replaced this change.
      if (marks.get(key) !== next) return;
      marks.set(key, before);
      repaint();
      ctx.toast(`The mark was not changed. ${err.message || ''}`.trim(), { error: true });
    }
  }

  // Browse (#287): the analysed groups by type, then in playlist order. Opening one pages through its
  // entries with group-entries; the server sends at most 50 names per page.
  const BROWSE_ORDER = ['Movies', 'Series', 'Live'];
  const BROWSE_HEADINGS = { Movies: 'Films', Series: 'Series', Live: 'Channels' };
  const PAGE_SIZE = 50;

  function browseGroups(list, withOff) {
    const shown = list.groups.filter((g) => withOff || !list.disabled.has(g.key));
    const sorted = [...shown].sort((a, b) => BROWSE_ORDER.indexOf(a.type) - BROWSE_ORDER.indexOf(b.type) || a.index - b.index);
    return { sorted, hidden: list.groups.length - shown.length };
  }

  function browseSection(ctx, list) {
    const { h, icon, fmt, plural } = ctx;
    const state = { open: null };
    const groupsEl = h('div', { class: 'library-groups' });
    const entriesEl = h('div', { class: 'library-open', hidden: true });
    const el = h('section', { class: 'library-browse' }, h('h2', { class: 'library-title', text: 'Browse groups' }), groupsEl, entriesEl);
    const status = (text) => h('p', { class: 'search-status', role: 'status', text });
    const offBadge = (g) => (list.disabled.has(g.key) ? h('span', { class: 'search-badge off', text: 'Off' }) : null);

    const renderGroups = () => {
      const { sorted, hidden } = browseGroups(list, includeOff);
      const nodes = [];
      if (hidden > 0) nodes.push(status(`${plural(hidden, 'switched-off group')} hidden`));
      if (!sorted.length) nodes.push(status('No group is on. Include switched-off groups to browse them.'));
      let ul = null;
      for (const g of sorted) {
        if (!ul || ul.dataset.type !== g.type) {
          ul = h('ul', { class: 'search-list', dataset: { type: g.type } });
          nodes.push(h('div', { class: 'card library-type' }, h('h3', { class: 'field-label', text: BROWSE_HEADINGS[g.type] }), ul));
        }
        ul.append(h('li', null, h('button', { class: 'search-hit library-group', type: 'button', dataset: { key: g.key }, onclick: () => open(g) },
          h('span', { class: 'search-hit-text' },
            h('span', { class: 'search-hit-name', text: g.title || 'Untitled group' }),
            h('span', { class: 'search-hit-meta', text: plural(g.count, 'entry', 'entries') })),
          offBadge(g))));
      }
      groupsEl.replaceChildren(...nodes);
    };

    // Back to the group list, with the focus on the group that was open.
    const close = () => {
      const key = state.open?.group.key;
      state.open = null;
      entriesEl.hidden = true;
      entriesEl.replaceChildren();
      groupsEl.hidden = false;
      for (const b of groupsEl.querySelectorAll('.library-group')) if (b.dataset.key === key) b.focus();
    };

    const open = (g, scroll = true) => {
      const view = {
        group: g, offset: 0,
        count: h('p', { class: 'search-hit-meta', text: plural(g.count, 'entry', 'entries') }),
        list: h('ol', { class: 'search-list library-entries' }),
        foot: h('div', { class: 'library-foot' }),
      };
      state.open = view;
      entriesEl.replaceChildren(
        h('div', { class: 'library-entries-head' },
          h('button', { class: 'btn btn-sm', type: 'button', onclick: close }, icon('back'), 'All groups'),
          h('div', { class: 'grow' }, h('h3', { class: 'library-group-title', text: g.title || 'Untitled group' }), view.count),
          offBadge(g)),
        h('div', { class: 'card' }, view.list, view.foot));
      groupsEl.hidden = true;
      entriesEl.hidden = false;
      if (scroll) el.scrollIntoView({ block: 'start' });
      loadPage(view);
    };

    // A failed page shows its error below the entries already shown, which stay.
    const loadPage = async (view) => {
      view.foot.replaceChildren(status(view.offset ? 'Loading more…' : 'Loading entries…'));
      let page;
      try {
        const params = { cacheKey: list.cacheKey, group: view.group.key, offset: view.offset, limit: PAGE_SIZE };
        if (hideSeen) params.hideSeen = true;
        if (likedOnly) params.liked = true;
        page = await ctx.groupEntries(params, ctx.controller.signal);
      } catch (err) {
        if (view !== state.open || ctx.controller.signal.aborted) return;
        view.foot.replaceChildren(h('div', { class: 'notice error', role: 'alert' },
          h('p', { text: err.message || 'The entries could not be loaded.' }),
          h('button', { class: 'btn btn-sm', type: 'button', onclick: () => loadPage(view) }, 'Try again')));
        return;
      }
      if (view !== state.open || !ctx.isCurrent()) return;
      const names = page.entries || [];
      view.list.append(...names.map((e) => h('li', { class: 'library-hit' }, filmName(ctx, e, view.group.title, list.cacheKey), filmChevron(ctx, e), markControls(ctx, e))));
      view.offset += names.length;
      view.count.textContent = plural(page.total, 'entry', 'entries');
      const done = names.length === 0 || view.offset >= page.total;
      if (!done) view.foot.replaceChildren(h('button', { class: 'btn btn-sm', type: 'button', onclick: () => loadPage(view) }, 'More'));
      else {
        const seen = hideSeen && page.seenHiddenCount > 0 ? ` · ${plural(page.seenHiddenCount, 'seen entry', 'seen entries')} hidden` : '';
        view.foot.replaceChildren(status((page.total === 0 ? 'No entries in this group.' : `All ${fmt(page.total)} shown`) + seen));
      }
    };

    // The shared toggle: an open group stays open unless it is off and switched-off groups are left out again.
    const refresh = () => {
      renderGroups();
      if (state.open && !includeOff && list.disabled.has(state.open.group.key)) close();
    };
    // A mark filter loads the open group again from its first page.
    const reload = () => { if (state.open) open(state.open.group, false); };
    renderGroups();
    return { el, refresh, reload };
  }

  // A result is text with the editor's language labels (#265), group and type, 'Off' for an entry of a group that
  // is off, and the mark controls for a film or series (#298).
  function hit(ctx, m, cacheKey) {
    const { h } = ctx;
    const type = TYPE_LABELS[m.contentType] ? m.contentType : 'Live';
    return h('li', { class: 'library-hit', dataset: { type } },
      h('span', { class: 'dot', 'aria-hidden': 'true' }),
      h('span', { class: 'search-hit-text' },
        h('span', { class: 'search-hit-line' },
          filmName(ctx, m, m.group, cacheKey),
          ctx.searchLanguages(m)),
        h('span', { class: 'search-hit-meta', text: `${m.group} · ${TYPE_LABELS[type]}` })),
      m.enabled === false ? h('span', { class: 'search-badge off', text: 'Off' }) : null,
      filmChevron(ctx, m),
      markControls(ctx, m));
  }

  // mount() runs only after this file has run to its end, so My marks below may follow the export.
  window.StrimLibrary = { mount };

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
      syncRows(ctx, m.key, { seen: false, rating: 0 });
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
        mark = await ctx.setMark({ name: m.year ? `${m.title} (${m.year})` : m.title, kind: m.kind, seen: m.seen, rating: m.rating });
      } catch (err) {
        delete li.dataset.restoring;
        ctx.toast(`The mark was not restored. ${err.message || ''}`.trim(), { error: true });
        return;
      }
      syncRows(ctx, mark.key, { seen: mark.seen, rating: mark.rating });
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

  // Film card (#367): a film row's name opens poster and details from TMDB in a <dialog>, a bottom sheet on a phone
  // and a side pane from 1100px. Only when /api/config reports TMDB on; it reads only and never changes the playlist.
  const card = { el: null, film: null, button: null, seq: 0, pending: null, answer: null, stack: [] };
  const keyAlias = new Map();
  const answers = new Map();
  const wide = window.matchMedia('(min-width: 1100px)');
  const opensCard = (ctx, e) => ctx.tmdbEnabled === true && e.contentType === 'Movies' && !!e.markKey;
  const FILM_NOTES = {
    notFound: 'TMDB has no film with this title. Your marks still work.',
    notApplicable: 'There is no film card for this entry.',
    unavailable: 'TMDB is not answering right now.',
    failed: 'The film details could not be loaded.',
  };

  function filmName(ctx, e, group, cacheKey) {
    if (!opensCard(ctx, e)) return ctx.h('span', { class: 'search-hit-name', text: e.name });
    const film = { name: e.name, group: group || '', cacheKey, markKey: e.markKey, off: e.enabled === false };
    const button = ctx.h('button', {
      class: 'search-hit-name library-film-open', type: 'button', 'aria-haspopup': 'dialog', 'aria-expanded': 'false',
      dataset: { name: film.name, group: film.group }, text: e.name, onclick: () => openFilm(ctx, film, button),
    });
    return button;
  }

  const filmChevron = (ctx, e) => (opensCard(ctx, e) ? ctx.h('span', { class: 'library-film-chevron' }, ctx.icon('chevron')) : null);

  function markOpen(ctx, button) {
    for (const b of ctx.root.querySelectorAll('.library-film-open')) {
      b.setAttribute('aria-expanded', String(b === button));
      b.closest('.library-hit')?.classList.toggle('is-open', b === button);
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
    const el = h('dialog', { class: 'sheet film-card', 'aria-labelledby': 'film-card-title' }, head, h('div', { class: 'film-scroll' }));
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
    const key = JSON.stringify([film.name, film.group, includeOff, hideSeen]);
    let answer = answers.get(key);
    if (!answer) {
      renderFilm(ctx, null);
      const own = (card.pending = new AbortController());
      try {
        const params = { name: film.name, group: film.group, type: 'Movies', cacheKey: film.cacheKey, includeDisabled: includeOff, hideSeen };
        answer = await ctx.filmCard(params, own.signal);
      } catch {
        if (seq === card.seq) renderFilm(ctx, { status: 'failed' });
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
    const f = answer?.status === 'matched' ? answer.film : null;
    const choose = answer?.status === 'choose' && answer.candidates?.length > 0;
    const title = h('h2', { class: choose ? 'film-state-title' : 'film-title', id: 'film-card-title', tabindex: '-1',
      text: choose ? 'Which film is it?' : f?.title || entry.name });
    card.answer = answer;
    const marksEl = cardMarks(ctx, entry, answer);
    const body = h('div', { class: 'film-body' });
    let top;
    if (!answer) {
      top = h('div', { class: 'film-load' }, h('div', { class: 'film-poster skel', 'aria-hidden': 'true' }), title,
        h('span', { class: 'skel skel-line', 'aria-hidden': 'true' }), marksEl,
        h('span', { class: 'sr-only', role: 'status', text: 'Loading film details' }));
    } else if (choose) {
      // Picking a candidate stores the match (#368) and loads the card again.
      top = h('div', { class: 'film-choose' }, title,
        h('p', { class: 'film-choose-text', text: `More than one film fits "${entry.name}". Pick yours and Strim remembers it.` }),
        h('ul', { class: 'film-grid' }, answer.candidates.map((c) => h('li', null, h('button', { class: 'film-tile', type: 'button', onclick: () => setMatch(ctx, c.id) },
          poster(ctx, c.posterUrl, '', 'film-tile-poster'),
          h('span', { class: 'film-tile-title', text: c.title || entry.name }),
          h('span', { class: 'film-tile-meta', text: c.year ? String(c.year) : 'Year unknown' }))))),
        marksEl);
    } else {
      const rating = f?.rating > 0 ? f.rating.toFixed(1) : '';
      const meta = f ? (f.genres || []).slice(0, 3).join(' · ') : entry.group;
      top = h('div', { class: 'film-stage' }, poster(ctx, f?.posterUrl, `Poster of ${title.textContent}`, 'film-poster'),
        f ? h('p', { class: 'film-kicker', dataset: { type: 'Movies' } }, h('span', { class: 'dot', 'aria-hidden': 'true' }), f.year ? `Film · ${f.year}` : 'Film') : null,
        title, meta ? h('p', { class: 'film-meta', text: meta }) : null,
        h('div', { class: 'film-actions' }, rating ? [
          h('span', { class: 'film-rating', role: 'img', 'aria-label': `TMDB rating ${rating} out of 10` }, icon('star'), h('b', { text: rating }), h('span', { text: 'TMDB' })),
          h('span', { class: 'film-sep', 'aria-hidden': 'true' })] : null, marksEl));
      if (f?.overview) body.append(h('p', { class: 'film-overview is-clamped', text: f.overview }));
      if (f?.similar?.length) body.append(similarFilms(ctx, f));
      if (f) {
        body.append(h('div', { class: 'film-source' },
          h('p', null, 'In your list as', h('b', { text: entry.name }), entry.group, entry.off ? h('span', { class: 'search-badge off', text: 'Off' }) : null),
          h('button', { class: 'film-unmatch', type: 'button', onclick: () => setMatch(ctx, null) }, 'Not this film')));
      }
      if (!f) {
        const failed = answer.status === 'failed';
        const retry = failed || answer.status === 'unavailable';
        body.append(h('div', { class: failed ? 'notice error' : 'notice', role: failed ? 'alert' : null },
          h('p', { text: FILM_NOTES[answer.status === 'choose' ? 'notFound' : answer.status] || FILM_NOTES.failed }),
          retry ? h('button', { class: 'btn btn-sm', type: 'button', onclick: () => loadFilm(ctx) }, 'Try again') : null));
      }
    }
    if (answer?.tmdbData) body.append(filmCredit(ctx));
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

  // The poster as given by the API, lazy; none or a failed image keeps the same box with the Movies diamond.
  function poster(ctx, url, alt, cls) {
    const empty = () => ctx.h('div', { class: `${cls} film-poster-empty`, 'aria-hidden': 'true' });
    if (!url) return empty();
    const img = ctx.h('img', { class: cls, alt, width: 342, height: 513, loading: 'lazy', decoding: 'async', referrerpolicy: 'no-referrer', src: url });
    img.addEventListener('error', () => img.replaceWith(empty()), { once: true });
    return img;
  }

  // The #298 controls as an icon capsule; they share the state by mark key, so the rows repaint with them. In a modal card
  // a failure shows inside the card for 5 s, because the toasts lie under the dialog's top layer.
  function cardMarks(ctx, entry, answer) {
    const now = marks.get(entry.markKey) || { seen: false, rating: 0 };
    // An answer's marks count once: a kept answer reopened later must not undo a mark set since.
    const given = answer?.marks ? { seen: answer.marks.seen === true, rating: answer.marks.rating || 0 } : now;
    if (answer) answer.marks = null;
    const toast = cardToast(ctx);
    const el = markControls({ ...ctx, toast }, { name: entry.name, contentType: 'Movies', markKey: entry.markKey, ...given });
    el.classList.add('film-marks');
    if (given.seen !== now.seen || given.rating !== now.rating) syncRows(ctx, entry.markKey, given);
    return el;
  }

  const cardToast = (ctx) => (text, options) => {
    if (!card.el.matches(':modal')) return ctx.toast(text, options);
    const note = ctx.h('div', { class: 'notice error', role: 'alert', text });
    card.el.querySelector('.film-body')?.prepend(note);
    setTimeout(() => note.remove(), 5000);
  };

  // Similar films (#368) in the API's order, those in the list first; only those open a card.
  function similarFilms(ctx, f) {
    const { h, icon } = ctx;
    const rail = h('div', { class: 'film-rail', role: 'group', 'aria-label': 'Similar films', tabindex: '0' }, f.similar.map((s, i) => similarTile(ctx, s, i)));
    const step = (dir) => rail.scrollBy({ left: dir * rail.clientWidth });
    return h('section', { class: 'film-section' },
      h('div', { class: 'film-section-head' },
        h('h3', { class: 'film-section-title', text: 'Similar films' }),
        h('span', { class: 'film-section-count', text: `${f.similar.filter((s) => s.inList).length} in your list` }),
        h('span', { class: 'film-rail-nav' },
          h('button', { class: 'icon-btn', type: 'button', 'aria-label': 'Previous', onclick: () => step(-1) }, icon('back')),
          h('button', { class: 'icon-btn', type: 'button', 'aria-label': 'Next', onclick: () => step(1) }, icon('chevron')))),
      rail);
  }

  function similarTile(ctx, s, i) {
    const { h, icon } = ctx;
    const off = s.inList && s.enabled === false;
    const art = h('span', { class: 'film-tile-art' }, poster(ctx, s.posterUrl, '', 'film-tile-poster'),
      off ? h('span', { class: 'film-tile-flag', 'aria-hidden': 'true', text: 'Off' }) : null,
      s.seen ? h('span', { class: 'film-tile-seen', 'aria-hidden': s.inList ? 'true' : null }, icon('check'), s.inList ? null : h('span', { class: 'sr-only', text: 'Seen' })) : null);
    const title = h('span', { class: 'film-tile-title', text: s.title });
    if (!s.inList) {
      return h('div', { class: 'film-tile is-absent' }, art, title,
        h('span', { class: 'film-tile-meta', text: [s.year, 'Not in your list'].filter(Boolean).join(' · ') }));
    }
    const label = [s.title, s.year, `in ${s.group}`, off ? 'group off' : '', s.seen ? 'seen' : ''].filter(Boolean).join(', ');
    return h('button', { class: 'film-tile', type: 'button', 'aria-label': label, dataset: { i: String(i) }, onclick: () => openSimilar(ctx, s, i) }, art, title,
      h('span', { class: 'film-tile-meta' }, h('span', { class: off ? 'film-on is-off' : 'film-on', 'aria-hidden': 'true' }), h('span', { text: s.group })));
  }

  // The card in place: the current one goes on a stack with its scroll positions; device Back still closes all.
  function openSimilar(ctx, s, i) {
    const scroll = card.el.querySelector('.film-scroll');
    card.stack.push({ film: card.film, answer: card.answer, title: card.answer.film.title || card.film.name, similar: s, tile: i,
      top: scroll.scrollTop, rail: scroll.querySelector('.film-rail')?.scrollLeft || 0 });
    card.film = { name: s.entry, group: s.group, cacheKey: card.film.cacheKey, markKey: `similar:${s.entry}`, off: s.enabled === false };
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
      await ctx.setFilmMatch({ name: film.name, tmdbId });
    } catch (err) {
      if (seq !== card.seq) return;
      renderFilm(ctx, before);
      cardToast(ctx)(`The film was not changed. ${err.message || ''}`.trim(), { error: true });
      return;
    }
    if (seq !== card.seq) return;
    for (const k of [...answers.keys()]) if (JSON.parse(k)[0] === film.name) answers.delete(k);
    await loadFilm(ctx);
    // An automatic match cannot be cleared: the card then comes back with the same film.
    if (tmdbId === null && card.film === film && card.answer?.film && card.answer.film.id === before?.film?.id) {
      card.el.querySelector('.film-body')?.prepend(ctx.h('div', { class: 'notice', text: 'Strim finds only this film for the title.' }));
    }
  }

  // TMDB's logo and notice (#315) with the credit line (#316), only when the card carries TMDB data.
  function filmCredit(ctx) {
    const { h } = ctx;
    const link = (...kids) => h('a', { href: 'https://www.themoviedb.org', target: '_blank', rel: 'noopener noreferrer' }, ...kids);
    const logo = h('img', { src: '/tmdb-logo.svg', alt: 'TMDB', class: 'tmdb-logo' });
    const logoLink = link(logo);
    logo.addEventListener('error', () => { logoLink.hidden = true; }, { once: true });
    return h('div', { class: 'film-credit' },
      h('p', { class: 'film-credit-line' }, logoLink, h('span', null, 'Film data and posters: ', link('TMDB'))),
      h('p', { text: 'This application uses TMDB and the TMDB APIs but is not endorsed, certified, or otherwise approved by TMDB.' }));
  }
})();
