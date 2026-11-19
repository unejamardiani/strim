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
    root.replaceChildren(head, body);
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
    if (matches.length) nodes.push(h('ul', { class: 'search-list' }, matches.map((m) => hit(ctx, m))));
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
      await ctx.setMark({ name, kind, seen: next.seen, rating: next.rating });
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
      view.list.append(...names.map((e) => h('li', { class: 'library-hit' }, h('span', { class: 'search-hit-name', text: e.name }), markControls(ctx, e))));
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
  function hit(ctx, m) {
    const { h } = ctx;
    const type = TYPE_LABELS[m.contentType] ? m.contentType : 'Live';
    return h('li', { class: 'library-hit', dataset: { type } },
      h('span', { class: 'dot', 'aria-hidden': 'true' }),
      h('span', { class: 'search-hit-text' },
        h('span', { class: 'search-hit-line' },
          h('span', { class: 'search-hit-name', text: m.name }),
          ctx.searchLanguages(m)),
        h('span', { class: 'search-hit-meta', text: `${m.group} · ${TYPE_LABELS[type]}` })),
      m.enabled === false ? h('span', { class: 'search-badge off', text: 'Off' }) : null,
      markControls(ctx, m));
  }

  window.StrimLibrary = { mount };
})();
