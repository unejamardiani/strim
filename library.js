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
    body.replaceChildren(h('section', { class: 'card' },
      h('p', null,
        h('span', { class: 'num', text: fmt(on.length) }), ` of ${plural(result.groups.length, 'group')} on, `,
        h('span', { class: 'num', text: fmt(entries) }), ` of ${plural(result.totalChannels, 'entry', 'entries')} in your list`),
      h('p', { class: 'field-hint', text: 'The Library only reads your list. To change what the player link serves, edit the groups.' })),
    searchSection(ctx, result));
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

  // The same search as the editor's Everything, read-only: by default only the groups that are on.
  function searchSection(ctx, list) {
    const { h } = ctx;
    const state = { query: '', type: 'all', timer: 0, seq: 0 };
    const results = h('div', { class: 'card search-results' });
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
      },
    }, 'Include switched-off groups');

    ctx.controller.signal.addEventListener('abort', () => clearTimeout(state.timer), { once: true });
    run();
    return h('section', { class: 'library-search' },
      input,
      h('div', { class: 'library-filters' }, types, toggle),
      results);
  }

  async function runSearch(ctx, list, state, results) {
    const { h, plural } = ctx;
    clearTimeout(state.timer);
    const seq = ++state.seq;
    const status = (text) => h('p', { class: 'search-status', role: 'status', text });
    const hint = ctx.searchHint(state.query);
    if (hint) {
      results.replaceChildren(status(hint));
      return;
    }
    const hideDisabled = !includeOff;
    const contentTypes = TYPES.find((t) => t.value === state.type).types;
    const body = { cacheKey: list.cacheKey, query: state.query, disabledGroups: [...list.disabled], hideDisabled };
    if (contentTypes.length) body.contentTypes = contentTypes;
    results.replaceChildren(status('Searching…'));
    let found;
    try {
      found = await ctx.search(body, ctx.controller.signal);
    } catch (err) {
      if (seq !== state.seq || ctx.controller.signal.aborted) return;
      results.replaceChildren(h('div', { class: 'notice error', role: 'alert', text: err.message || 'Search failed.' }));
      return;
    }
    if (seq !== state.seq || !ctx.isCurrent()) return;
    const matches = found.matches || [];
    const hidden = hideDisabled && Number.isInteger(found.hiddenCount) ? found.hiddenCount : 0;
    const left = hidden > 0 ? `${plural(hidden, 'match', 'matches')} in switched-off groups` : '';
    let text = 'Nothing matches your search.';
    if (matches.length) text = [plural(matches.length, 'match', 'matches'), left].filter(Boolean).join(' · ');
    else if (left) text = `Nothing in your list. ${left}.`;
    const nodes = [status(text)];
    if (matches.length) nodes.push(h('ul', { class: 'search-list' }, matches.map((m) => hit(h, m))));
    if (found.hasMore === true) nodes.push(h('p', { class: 'search-more', text: 'More matches, refine your search.' }));
    results.replaceChildren(...nodes);
  }

  // A result is text only: name, group and type, and 'Off' for an entry of a group that is off.
  function hit(h, m) {
    const type = TYPE_LABELS[m.contentType] ? m.contentType : 'Live';
    return h('li', { class: 'library-hit', dataset: { type } },
      h('span', { class: 'dot', 'aria-hidden': 'true' }),
      h('span', { class: 'search-hit-text' },
        h('span', { class: 'search-hit-name', text: m.name }),
        h('span', { class: 'search-hit-meta', text: `${m.group} · ${TYPE_LABELS[type]}` })),
      m.enabled === false ? h('span', { class: 'search-badge off', text: 'Off' }) : null);
  }

  window.StrimLibrary = { mount };
})();
