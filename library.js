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
      h('p', { class: 'field-hint', text: 'The Library only reads your list. To change what the player link serves, edit the groups.' })));
  }

  window.StrimLibrary = { mount };
})();
