// Strim app: a playlists home and a group picker, driven by the backend (no local M3U parsing).
// Routes: #/ (playlists, or the editor for guests), #/new, #/p/<playlist id>.

// ---------- Configuration ----------

const API_BASE = (() => {
  const fromQuery = new URLSearchParams(window.location.search).get('api') || '';
  let fromStorage = '';
  try {
    if (fromQuery) localStorage.setItem('strim.apiBase', fromQuery);
    fromStorage = localStorage.getItem('strim.apiBase') || '';
  } catch {
    // Storage can be unavailable (private mode); the default still works.
  }
  return (fromQuery || window.STRIM_API_BASE || fromStorage || defaultApiBase()).replace(/\/+$/, '');
})();

// A frontend served by a local static server (another localhost port) talks to the API on :5000.
function defaultApiBase() {
  const { protocol, hostname, port } = window.location;
  const loopback = ['localhost', '127.0.0.1', '[::1]', '::1'].includes(hostname);
  if (protocol === 'http:' && loopback && port && !['5000', '5161', '8080'].includes(port)) {
    return `http://${hostname.includes(':') && !hostname.startsWith('[') ? `[${hostname}]` : hostname}:5000/api`;
  }
  return '/api';
}

// Must match MaxDisabledGroupsCount in api/Program.cs.
const MAX_HIDDEN_GROUPS = 10000;
const CONTENT_TYPES = ['Live', 'Movies', 'Series'];

// ---------- State ----------

const state = {
  ready: false,
  user: null,
  providers: [],
  playlists: [],
  shareBase: '',
  editor: null,
};

const app = document.getElementById('app');
const accountButton = document.getElementById('account-button');
const accountDialog = document.getElementById('account-dialog');
const accountTitle = document.getElementById('account-title');
const accountBody = document.getElementById('account-body');
const menuDialog = document.getElementById('menu-dialog');
const menuTitle = document.getElementById('menu-title');
const menuBody = document.getElementById('menu-body');
const toasts = document.getElementById('toasts');

// ---------- DOM helpers ----------

function h(tag, props, ...children) {
  const el = document.createElement(tag);
  for (const [key, value] of Object.entries(props || {})) {
    if (value == null || value === false) continue;
    if (key === 'class') el.className = value;
    else if (key === 'text') el.textContent = value;
    else if (key === 'dataset') Object.assign(el.dataset, value);
    else if (key.startsWith('on') && typeof value === 'function') el.addEventListener(key.slice(2), value);
    else if (key in el && typeof value !== 'string') el[key] = value;
    else if (key === 'value') el.value = value;
    else el.setAttribute(key, value === true ? '' : value);
  }
  for (const child of children.flat()) {
    if (child == null || child === false) continue;
    el.append(child instanceof Node ? child : document.createTextNode(String(child)));
  }
  return el;
}

const ICONS = {
  back: '<path d="M15 18l-6-6 6-6"/>',
  more: '<circle cx="5" cy="12" r="1.4" fill="currentColor"/><circle cx="12" cy="12" r="1.4" fill="currentColor"/><circle cx="19" cy="12" r="1.4" fill="currentColor"/>',
  copy: '<rect x="9" y="9" width="11" height="11" rx="2"/><path d="M5 15V5a2 2 0 0 1 2-2h8"/>',
  download: '<path d="M12 4v11M7 10l5 5 5-5M5 20h14"/>',
  list: '<path d="M9 6h11M9 12h11M9 18h11M4.5 6h.01M4.5 12h.01M4.5 18h.01"/>',
  refresh: '<path d="M20 11a8 8 0 1 0-2.3 5.7M20 4v7h-7"/>',
  trash: '<path d="M4 7h16M9 7V4h6v3M6 7l1 13h10l1-13"/>',
  link: '<path d="M10 14a4 4 0 0 0 5.7 0l3-3a4 4 0 0 0-5.7-5.7l-1 1M14 10a4 4 0 0 0-5.7 0l-3 3a4 4 0 0 0 5.7 5.7l1-1"/>',
  clock: '<circle cx="12" cy="12" r="8"/><path d="M12 8v4l3 2"/>',
  plus: '<path d="M12 5v14M5 12h14"/>',
  check: '<path d="M5 12.5l4.5 4.5L19 7.5"/>',
};

function icon(name) {
  const tpl = document.createElement('template');
  tpl.innerHTML = `<svg viewBox="0 0 24 24" aria-hidden="true">${ICONS[name]}</svg>`;
  return tpl.content.firstChild;
}

const fmt = (n) => Number(n || 0).toLocaleString();
const plural = (n, word, many = `${word}s`) => `${fmt(n)} ${n === 1 ? word : many}`;

function relativeTime(value) {
  const date = new Date(value);
  const seconds = Math.round((Date.now() - date.getTime()) / 1000);
  if (!Number.isFinite(seconds)) return '';
  if (seconds < 60) return 'just now';
  if (seconds < 3600) return `${Math.floor(seconds / 60)} min ago`;
  if (seconds < 86400) return `${Math.floor(seconds / 3600)} h ago`;
  if (seconds < 86400 * 30) return `${Math.floor(seconds / 86400)} d ago`;
  return date.toLocaleDateString();
}

function sourceHost(url) {
  try {
    return new URL(url).host.replace(/^www\./, '');
  } catch {
    return '';
  }
}

function fileName(name) {
  const slug = String(name || 'playlist').trim().replace(/[^\w.-]+/g, '-').replace(/^-+|-+$/g, '');
  return `${slug || 'playlist'}.m3u`;
}

// ---------- Toasts ----------

function toast(message, { error = false, duration = 3200 } = {}) {
  const el = h('div', { class: `toast${error ? ' error' : ''}`, text: message });
  toasts.append(el);
  while (toasts.children.length > 3) toasts.firstChild.remove();
  setTimeout(() => {
    el.classList.add('leaving');
    setTimeout(() => el.remove(), 220);
  }, error ? duration + 2000 : duration);
}

async function copyText(text, label = 'Link copied') {
  try {
    await navigator.clipboard.writeText(text);
  } catch {
    // Older mobile browsers: fall back to a temporary selection.
    const area = h('textarea', { value: text, readonly: true, class: 'sr-only' });
    document.body.append(area);
    area.select();
    const ok = document.execCommand('copy');
    area.remove();
    if (!ok) {
      toast('Copy failed. Long-press the link to copy it.', { error: true });
      return;
    }
  }
  toast(label);
}

// ---------- API ----------

let csrfToken = null;

async function getCsrfToken() {
  if (csrfToken) return csrfToken;
  try {
    const res = await fetch(`${API_BASE}/auth/csrf-token`, { credentials: 'include' });
    if (res.ok) csrfToken = (await res.json()).token;
  } catch (err) {
    console.warn('Failed to fetch CSRF token', err);
  }
  return csrfToken;
}

async function api(path, { method = 'GET', body, signal } = {}) {
  const headers = { 'Content-Type': 'application/json' };
  const mutating = method !== 'GET' && method !== 'HEAD';
  if (mutating) {
    const token = await getCsrfToken();
    if (token) headers['X-CSRF-TOKEN'] = token;
  }

  const res = await fetch(`${API_BASE}${path}`, {
    method,
    headers,
    credentials: 'include',
    body: body ? JSON.stringify(body) : undefined,
    signal,
  });

  // The antiforgery token is bound to the session; drop it when it may have gone stale.
  if (mutating && (res.status === 400 || res.status === 403)) csrfToken = null;
  if (res.status === 204) return null;
  if (!res.ok) {
    const err = new Error(await readError(res));
    err.status = res.status;
    throw err;
  }
  return res.json();
}

async function readError(res) {
  let message = '';
  try {
    const text = await res.text();
    try {
      const data = JSON.parse(text);
      message = data.error || data.detail || data.message || data.title || '';
    } catch {
      message = text.startsWith('<') ? '' : text;
    }
  } catch {
    // ignore unreadable bodies
  }
  message = message.replace(/\s+/g, ' ').trim().slice(0, 240);
  if (message) return message;
  if (res.status === 401) return 'You are signed out.';
  if (res.status === 429) return 'Too many requests. Wait a moment and try again.';
  return `Request failed (${res.status}).`;
}

const isAbort = (err) => err?.name === 'AbortError';

function shareUrl(playlist) {
  if (!playlist?.id || !playlist.shareCode) return '';
  return `${state.shareBase}/playlists/${playlist.id}/share/${playlist.shareCode}`;
}

// ---------- Group filters ----------
// Keep in sync with PlaylistProcessor.GroupKey and the disabled-group matching on the server.

function groupKey(groupTitle, contentType) {
  return `${groupTitle} · ${contentType}`;
}

// A saved filter entry is either a group key or a plain group-title (older saves, or a title
// that is not split by content type). Map the entries onto the groups of the loaded source and
// keep the ones that match nothing, so groups a provider drops for a while stay hidden.
function restoreFilters(entries, groups) {
  const saved = new Set(entries || []);
  const disabled = new Set();
  const matched = new Set();
  for (const g of groups) {
    if (saved.has(g.key)) {
      disabled.add(g.key);
      matched.add(g.key);
    }
    if (saved.has(g.title)) {
      disabled.add(g.key);
      matched.add(g.title);
    }
  }
  const extras = [...saved].filter((entry) => !matched.has(entry));
  return { disabled, extras };
}

// ---------- Auth ----------

async function loadSession() {
  const [config, providers, me] = await Promise.allSettled([
    api('/config'),
    api('/auth/providers'),
    api('/auth/me'),
  ]);

  const configured = config.status === 'fulfilled' ? config.value?.publicShareBaseUrl : '';
  state.shareBase = String(configured || new URL(API_BASE, window.location.origin).toString()).replace(/\/+$/, '');
  state.providers = providers.status === 'fulfilled' && Array.isArray(providers.value) ? providers.value : [];
  state.user = me.status === 'fulfilled' ? me.value : null;
  if (state.user) await loadPlaylists();
}

async function loadPlaylists() {
  try {
    const data = await api('/playlists');
    state.playlists = Array.isArray(data) ? data : [];
  } catch (err) {
    state.playlists = [];
    if (err.status === 401) state.user = null;
    else toast(`Could not load your playlists. ${err.message}`, { error: true });
  }
}

function updateAccountButton() {
  accountButton.hidden = false;
  accountButton.classList.toggle('guest', !state.user);
  if (state.user) {
    const name = state.user.userName || state.user.email || 'Account';
    accountButton.replaceChildren(h('span', { class: 'avatar', text: name.trim().charAt(0) || '?' }), h('span', { class: 'name', text: name }));
    accountButton.setAttribute('aria-label', `Account: ${name}`);
  } else {
    accountButton.replaceChildren('Sign in');
    accountButton.removeAttribute('aria-label');
  }
}

function openAccount(hint) {
  accountBody.replaceChildren();
  if (state.user) {
    accountTitle.textContent = 'Account';
    accountBody.append(
      h('p', { class: 'secondary' }, 'Signed in as ', h('strong', { text: state.user.userName || state.user.email })),
      h('button', { class: 'btn btn-block', type: 'button', onclick: signOut }, 'Sign out')
    );
  } else {
    accountTitle.textContent = 'Sign in';
    const form = h('form', { class: 'stack', novalidate: true });
    const user = h('input', { class: 'input', name: 'username', type: 'text', autocomplete: 'username', autocapitalize: 'off', spellcheck: 'false', required: true });
    const pass = h('input', { class: 'input', name: 'password', type: 'password', autocomplete: 'current-password', required: true });
    const signIn = h('button', { class: 'btn btn-primary', type: 'submit' }, 'Sign in');
    const register = h('button', { class: 'btn', type: 'button' }, 'Create account');

    form.addEventListener('submit', (e) => {
      e.preventDefault();
      submitAuth('login', user, pass, signIn);
    });
    register.addEventListener('click', () => {
      pass.autocomplete = 'new-password';
      submitAuth('register', user, pass, register);
    });

    if (hint) accountBody.append(h('p', { class: 'secondary', text: hint }));
    const enabled = state.providers.map((p) => p.name).filter(Boolean);
    if (enabled.length) {
      const labels = { google: 'Google', github: 'GitHub', microsoft: 'Microsoft' };
      accountBody.append(
        h('div', { class: 'stack' },
          enabled.map((name) => h('button', {
            class: 'btn btn-block',
            type: 'button',
            onclick: () => startExternalLogin(name),
          }, `Continue with ${labels[name] || name}`))),
        h('div', { class: 'divider', text: 'or' })
      );
    }
    form.append(
      h('label', { class: 'field' }, h('span', { class: 'field-label', text: 'Username or email' }), user),
      h('label', { class: 'field' }, h('span', { class: 'field-label', text: 'Password' }), pass),
      h('div', { class: 'row-actions' }, signIn, register)
    );
    accountBody.append(form);
    setTimeout(() => user.focus(), 50);
  }
  if (!accountDialog.open) accountDialog.showModal();
}

async function submitAuth(mode, userInput, passInput, button) {
  const userName = userInput.value.trim();
  const password = passInput.value;
  if (!userName || !password) {
    toast('Enter a username and a password.', { error: true });
    return;
  }
  button.disabled = true;
  try {
    await api(mode === 'register' ? '/auth/register' : '/auth/login', {
      method: 'POST',
      body: { userName, password, email: userName.includes('@') ? userName : undefined },
    });
    csrfToken = null;
    state.user = await api('/auth/me');
    await loadPlaylists();
    accountDialog.close();
    toast(mode === 'register' ? 'Account created' : 'Signed in');
    afterAuthChange();
  } catch (err) {
    const messages = { 401: 'Wrong username or password.', 423: 'Account locked. Try again later.' };
    toast(messages[err.status] || err.message, { error: true });
  } finally {
    button.disabled = false;
  }
}

function startExternalLogin(provider) {
  const returnUrl = `${window.location.pathname}${window.location.search}`;
  window.location.href = `${API_BASE}/auth/external/${encodeURIComponent(provider)}?returnUrl=${encodeURIComponent(returnUrl)}`;
}

async function signOut() {
  try {
    await api('/auth/logout', { method: 'POST' });
  } catch (err) {
    console.warn('Logout failed', err);
  }
  csrfToken = null;
  state.user = null;
  state.playlists = [];
  if (state.editor?.id) closeEditor();
  accountDialog.close();
  toast('Signed out');
  afterAuthChange();
}

function afterAuthChange() {
  updateAccountButton();
  // A guest who loaded groups keeps them after signing in, so they can save right away.
  const ed = state.editor;
  if (state.user && ed && !ed.id && ed.phase === 'ready') {
    navigate('#/new', { replace: true });
  } else if (!state.user && location.hash !== '#/' && location.hash !== '') {
    navigate('#/', { replace: true });
  } else {
    route();
  }
}

// ---------- Router ----------

let currentHash = location.hash;

function parseRoute() {
  const path = location.hash.replace(/^#/, '') || '/';
  const match = /^\/p\/([0-9a-f-]{36})$/i.exec(path);
  if (match) return { name: 'playlist', id: match[1].toLowerCase() };
  if (path === '/new') return { name: 'new' };
  return { name: 'home' };
}

function navigate(hash, { replace = false } = {}) {
  if (location.hash === hash) {
    route();
    return;
  }
  if (replace) {
    history.replaceState(null, '', hash);
    currentHash = hash;
    route();
  } else {
    location.hash = hash;
  }
}

window.addEventListener('hashchange', () => {
  if (location.hash === currentHash) return;
  const target = parseRoute();
  const ed = state.editor;
  const leavingEditor = ed && !(target.name === 'playlist' && target.id === ed.id) && !(target.name === 'new' && !ed.id);
  if (leavingEditor && isDirty(ed)) {
    if (!confirm('Discard unsaved changes?')) {
      history.replaceState(null, '', currentHash || '#/');
      return;
    }
    closeEditor();
  }
  currentHash = location.hash;
  route();
});

window.addEventListener('beforeunload', (e) => {
  if (state.editor && isDirty(state.editor)) e.preventDefault();
});

function route() {
  if (!state.ready) return;
  closeSheets();
  const target = parseRoute();
  if (!state.user) {
    if (target.name !== 'home') {
      history.replaceState(null, '', '#/');
      currentHash = '#/';
    }
    showEditor(null);
    return;
  }
  if (target.name === 'home') return showHome();
  if (target.name === 'new') return showEditor(null);
  const playlist = state.playlists.find((p) => p.id === target.id);
  if (!playlist) {
    toast('That playlist does not exist any more.', { error: true });
    navigate('#/', { replace: true });
    return;
  }
  showEditor(playlist);
}

function closeSheets() {
  if (menuDialog.open) menuDialog.close();
}

for (const dialog of [accountDialog, menuDialog]) {
  dialog.addEventListener('click', (e) => {
    // Close on backdrop clicks and on the close button.
    if (e.target === dialog || e.target.closest('[data-close]')) dialog.close();
  });
}

function setActionBar(visible) {
  document.body.classList.toggle('has-dock', visible);
}

// ---------- Home ----------

function showHome() {
  clearInterval(loadingTimer);
  teardownPicker();
  setActionBar(false);
  document.title = 'Your playlists · Strim';

  const playlists = [...state.playlists].sort(
    (a, b) => new Date(b.updatedAt || b.createdAt || 0) - new Date(a.updatedAt || a.createdAt || 0)
  );

  const head = h('div', { class: 'page-head' },
    h('h1', null, 'Playlists', playlists.length > 1 ? h('span', { class: 'count num', text: fmt(playlists.length) }) : null),
    playlists.length ? h('a', { class: 'btn btn-primary btn-sm', href: '#/new' }, icon('plus'), 'New') : null
  );

  if (!playlists.length) {
    app.replaceChildren(
      head,
      h('div', { class: 'card empty' },
        h('h2', { text: 'Start with your provider’s link' }),
        h('p', { text: 'Paste the M3U link, switch off the groups you never watch and get a link your player can use.' }),
        h('a', { class: 'btn btn-primary', href: '#/new' }, icon('plus'), 'New playlist'))
    );
    return;
  }

  app.replaceChildren(head, h('div', { class: 'playlist-list' }, playlists.map(playlistCard)));
}

function playlistMeta(playlist) {
  const parts = [];
  const total = playlist.groupCount || 0;
  const kept = Math.max(0, total - (playlist.disabledGroups?.length || 0));
  parts.push(h('span', { text: `${fmt(kept)} of ${plural(total, 'group')} kept` }));
  const lastViewed = playlist.lastViewedUtc ? new Date(playlist.lastViewedUtc) : null;
  if (lastViewed && Date.now() - lastViewed.getTime() < 86400000) {
    parts.push(h('span', { class: 'on-air', text: `In use, ${relativeTime(lastViewed)}` }));
  } else if (playlist.viewCount > 0) {
    const last = lastViewed ? `, last ${relativeTime(lastViewed)}` : '';
    parts.push(h('span', { text: `Fetched ${fmt(playlist.viewCount)}×${last}` }));
  } else {
    parts.push(h('span', { text: 'Not fetched by a player yet' }));
  }
  if (playlist.expirationUtc) {
    const expires = new Date(playlist.expirationUtc);
    const days = Math.ceil((expires - Date.now()) / 86400000);
    if (days < 0) parts.push(h('span', { class: 'bad', text: 'Subscription expired' }));
    else if (days <= 14) parts.push(h('span', { class: 'warn', text: `Expires in ${plural(days, 'day')}` }));
    else parts.push(h('span', { text: `Expires ${expires.toLocaleDateString()}` }));
  }
  return h('div', { class: 'meta' }, parts);
}

function linkBox(playlist) {
  const active = playlist.isActive !== false;
  const url = shareUrl(playlist);
  if (!active) {
    return h('div', { class: 'link-box' },
      h('div', { class: 'link-url off', text: 'Link turned off. Players get an error.' }),
      h('button', { class: 'btn btn-sm', type: 'button', onclick: () => toggleLink(playlist) }, 'Turn on'));
  }
  return h('div', { class: 'link-box' },
    h('div', { class: 'link-url', title: url, text: url }),
    h('button', { class: 'btn btn-primary', type: 'button', onclick: () => copyText(url) }, icon('copy'), 'Copy'));
}

function playlistCard(playlist) {
  return h('article', { class: 'card playlist-card' },
    h('div', { class: 'playlist-card-head' },
      h('div', null,
        h('h2', { class: 'playlist-title' }, h('a', { href: `#/p/${playlist.id}`, text: playlist.name || 'Untitled' })),
        playlistMeta(playlist)),
      h('button', { class: 'icon-btn', type: 'button', 'aria-label': `Actions for ${playlist.name}`, onclick: () => openPlaylistMenu(playlist.id) }, icon('more'))),
    linkBox(playlist),
    h('div', { class: 'row-actions' },
      h('a', { class: 'btn btn-sm', href: `#/p/${playlist.id}` }, icon('list'), 'Edit groups')));
}

function menuIcon(name) {
  return h('span', { class: 'mi-icon' }, icon(name));
}

function openPlaylistMenu(id) {
  const playlist = state.playlists.find((p) => p.id === id);
  if (!playlist) return;
  const active = playlist.isActive !== false;
  const item = (iconName, label, onclick, { danger = false, sub = '', trailing = null } = {}) =>
    h('button', { class: `menu-item${danger ? ' danger' : ''}`, type: 'button', onclick },
      menuIcon(iconName),
      h('span', { class: 'grow' }, label, sub ? h('small', { text: sub }) : null),
      trailing);

  const current = parseRoute();
  const inEditor = current.name === 'playlist' && current.id === playlist.id;
  menuTitle.textContent = playlist.name || 'Playlist';
  menuBody.replaceChildren(h('div', { class: 'menu' },
    inEditor ? null : item('list', 'Edit groups', () => navigate(`#/p/${playlist.id}`)),
    active ? item('copy', 'Copy player link', () => { menuDialog.close(); copyText(shareUrl(playlist)); }) : null,
    active ? h('a', { class: 'menu-item', href: shareUrl(playlist), download: fileName(playlist.name), onclick: () => menuDialog.close() },
      menuIcon('download'), h('span', { class: 'grow', text: 'Download .m3u' })) : null,
    h('div', { class: 'menu-sep' }),
    item('clock', 'Refresh every 6 hours', () => toggleAutoRefresh(playlist), {
      sub: playlist.lastRefreshedUtc ? `Last refresh ${relativeTime(playlist.lastRefreshedUtc)}` : 'Keeps the provider list cached for players',
      trailing: h('span', { class: `switch${playlist.autoRefreshEnabled ? ' on' : ''}`, 'aria-hidden': 'true' }),
    }),
    item('link', active ? 'Turn link off' : 'Turn link on', () => toggleLink(playlist), {
      sub: active ? 'Players stop receiving the playlist' : 'Players can fetch the playlist again',
    }),
    item('refresh', 'Replace link', () => regenerateLink(playlist), { sub: 'The current link stops working' }),
    h('div', { class: 'menu-sep' }),
    item('trash', 'Delete playlist', () => deletePlaylist(playlist), { danger: true })
  ));
  if (!menuDialog.open) menuDialog.showModal();
}

function replacePlaylist(updated) {
  state.playlists = state.playlists.map((p) => (p.id === updated.id ? { ...p, ...updated } : p));
  return state.playlists.find((p) => p.id === updated.id);
}

function refreshViews(playlist) {
  if (menuDialog.open && playlist) openPlaylistMenu(playlist.id);
  const target = parseRoute();
  if (target.name === 'home') showHome();
  else if (state.editor?.id === playlist?.id) renderLinkCard();
}

async function toggleLink(playlist) {
  try {
    const result = await api(`/playlists/${playlist.id}/toggle-active`, { method: 'PATCH' });
    const updated = replacePlaylist({ id: playlist.id, isActive: result.isActive });
    toast(result.isActive ? 'Link turned on' : 'Link turned off');
    refreshViews(updated);
  } catch (err) {
    toast(err.message, { error: true });
  }
}

async function regenerateLink(playlist) {
  if (!confirm('Replace the player link? The current link stops working and you need to update your players.')) return;
  try {
    const result = await api(`/playlists/${playlist.id}/regenerate-code`, { method: 'POST' });
    const updated = replacePlaylist({ id: playlist.id, shareCode: result.shareCode });
    toast('New link created');
    refreshViews(updated);
  } catch (err) {
    toast(err.message, { error: true });
  }
}

async function toggleAutoRefresh(playlist) {
  const enabled = !playlist.autoRefreshEnabled;
  try {
    await api(`/playlists/${playlist.id}/auto-refresh`, { method: 'PATCH', body: { enabled } });
    const updated = replacePlaylist({ id: playlist.id, autoRefreshEnabled: enabled });
    refreshViews(updated);
  } catch (err) {
    toast(err.message, { error: true });
  }
}

async function deletePlaylist(playlist) {
  if (!confirm(`Delete "${playlist.name}"? Its player link stops working.`)) return;
  try {
    await api(`/playlists/${playlist.id}`, { method: 'DELETE' });
    state.playlists = state.playlists.filter((p) => p.id !== playlist.id);
    menuDialog.close();
    toast('Playlist deleted');
    if (state.editor?.id === playlist.id) closeEditor();
    if (parseRoute().name === 'home') showHome();
    else navigate('#/', { replace: true });
  } catch (err) {
    toast(err.message, { error: true });
  }
}

// ---------- Editor ----------

function newEditor(playlist) {
  return {
    id: playlist?.id || null,
    name: playlist?.name || '',
    sourceUrl: playlist?.sourceUrl || '',
    savedName: playlist?.name || '',
    savedUrl: playlist?.sourceUrl || '',
    savedFilters: playlist?.disabledGroups || [],
    phase: 'source', // source | loading | ready | error
    error: '',
    editingSource: !playlist,
    isSample: false,
    controller: null,
    startedAt: 0,
    cacheKey: null,
    totalChannels: 0,
    expirationUtc: null,
    groups: [],
    typeCounts: {},
    disabled: new Set(),
    extras: [],
    baseline: '',
    filter: { query: '', type: 'all', show: 'all', sort: 'provider' },
    view: [],
    busy: false,
  };
}

function filterSignature(ed) {
  return JSON.stringify([...ed.disabled, ...ed.extras].sort());
}

function isDirty(ed) {
  if (!ed || ed.isSample) return false;
  if (!ed.id) return Boolean(state.user) && ed.phase === 'ready';
  if (ed.name.trim() !== ed.savedName || ed.sourceUrl !== ed.savedUrl) return true;
  return ed.phase === 'ready' && filterSignature(ed) !== ed.baseline;
}

function closeEditor() {
  state.editor?.controller?.abort();
  state.editor = null;
  teardownPicker();
}

function showEditor(playlist) {
  const id = playlist?.id || null;
  if (!state.editor || state.editor.id !== id) {
    state.editor?.controller?.abort();
    state.editor = newEditor(playlist);
    if (playlist) {
      if (playlist.sourceUrl) loadGroups(state.editor, playlist.sourceUrl);
      else {
        state.editor.phase = 'error';
        state.editor.error = 'This playlist has no source URL. Add one to load its groups.';
        state.editor.editingSource = true;
      }
    }
  }
  renderEditor();
}

// Background loads finish after the user may have moved on; only render the editor on its own route.
function isVisible(ed) {
  if (state.editor !== ed) return false;
  const target = parseRoute();
  if (!state.user) return !ed.id && target.name === 'home';
  return ed.id ? target.name === 'playlist' && target.id === ed.id : target.name === 'new';
}

async function loadGroups(ed, url, { sample = false } = {}) {
  ed.controller?.abort();
  const controller = new AbortController();
  ed.controller = controller;
  ed.phase = 'loading';
  ed.error = '';
  ed.startedAt = Date.now();
  if (isVisible(ed)) renderEditor();

  try {
    let body;
    if (sample) {
      const res = await api('/playlists/sample', { signal: controller.signal });
      body = { rawText: res.text, sourceName: 'Sample playlist' };
    } else {
      body = { sourceUrl: url };
    }
    const analysis = await api('/playlist/analyze', { method: 'POST', body, signal: controller.signal });
    if (ed.controller !== controller) return;
    applyAnalysis(ed, analysis, { sample });
  } catch (err) {
    if (isAbort(err) || ed.controller !== controller) return;
    ed.phase = 'error';
    ed.error = err.status === 504
      ? 'The provider took too long to answer. Try again in a moment.'
      : err.message || 'Could not load the playlist.';
  } finally {
    if (ed.controller === controller) ed.controller = null;
  }
  if (isVisible(ed)) renderEditor();
}

function applyAnalysis(ed, analysis, { sample }) {
  const groups = (analysis.groups || []).map((g, index) => {
    const title = g.groupTitle || g.name;
    const type = CONTENT_TYPES.includes(g.contentType) ? g.contentType : 'Live';
    return { key: g.name, title, type, count: g.count || 0, index, split: false, search: '' };
  });

  const perTitle = new Map();
  for (const g of groups) perTitle.set(g.title, (perTitle.get(g.title) || 0) + 1);
  const typeCounts = {};
  for (const g of groups) {
    g.split = perTitle.get(g.title) > 1;
    g.search = `${g.title} ${g.type === 'Live' && !g.split ? '' : g.type}`.toLowerCase();
    typeCounts[g.type] = (typeCounts[g.type] || 0) + 1;
  }

  // Changing the source of a saved playlist carries the current selection over to the new list.
  const entries = ed.groups.length ? [...ed.disabled, ...ed.extras] : ed.savedFilters;
  const { disabled, extras } = restoreFilters(entries, groups);

  ed.groups = groups;
  ed.typeCounts = typeCounts;
  ed.disabled = disabled;
  ed.extras = extras;
  ed.cacheKey = analysis.cacheKey;
  ed.totalChannels = analysis.totalChannels || groups.reduce((sum, g) => sum + g.count, 0);
  ed.expirationUtc = analysis.expirationUtc || null;
  ed.isSample = sample;
  ed.phase = 'ready';
  ed.editingSource = false;
  if (!ed.id && !ed.name) ed.name = sample ? 'Sample playlist' : sourceHost(ed.sourceUrl) || 'My playlist';
  if (ed.filter.type !== 'all' && !typeCounts[ed.filter.type]) ed.filter.type = 'all';
  // A saved playlist starts clean even when old filter names were mapped onto split groups.
  if (ed.id && !ed.baseline) ed.baseline = filterSignature(ed);
}

let loadingTimer = null;

function renderEditor() {
  const ed = state.editor;
  if (!ed) return;
  clearInterval(loadingTimer);
  teardownPicker();
  const playlist = ed.id ? state.playlists.find((p) => p.id === ed.id) : null;
  document.title = `${ed.name || (ed.id ? 'Playlist' : 'New playlist')} · Strim`;

  const nodes = [];

  if (!state.user && !ed.id && ed.phase !== 'ready') {
    nodes.push(h('section', { class: 'hero' },
      h('h1', null, 'Your playlist, ', h('em', { text: 'minus the noise.' })),
      h('p', { class: 'lead', text: 'Paste your IPTV link, switch off the groups you never watch and download a clean playlist. Sign in to get a permanent link your player can use.' }),
      h('ol', { class: 'steps' },
        h('li', null, h('b', { text: '1' }), 'Paste your link'),
        h('li', null, h('b', { text: '2' }), 'Pick your groups'),
        h('li', null, h('b', { text: '3' }), 'Play it anywhere'))));
  } else {
    nodes.push(editorHead(ed, playlist));
  }

  if (playlist) nodes.push(h('div', { id: 'link-card' }, linkCard(playlist)));

  if (ed.editingSource || ed.phase === 'source') {
    nodes.push(sourceForm(ed));
  } else if (!ed.isSample) {
    nodes.push(h('div', { class: 'source-line' },
      h('span', { class: 'host' }, 'From ', h('b', { text: sourceHost(ed.sourceUrl) || 'unknown source' })),
      h('button', { class: 'btn btn-ghost btn-sm', type: 'button', onclick: () => { ed.editingSource = true; renderEditor(); } }, 'Change')));
  }

  if (ed.phase === 'loading') nodes.push(loadingCard(ed));
  if (ed.phase === 'error') {
    nodes.push(h('div', { class: 'notice error' },
      h('p', { text: ed.error }),
      ed.sourceUrl ? h('div', { class: 'row-actions' },
        h('button', { class: 'btn btn-sm', type: 'button', onclick: () => loadGroups(ed, ed.sourceUrl) }, 'Try again')) : null));
  }

  const ready = ed.phase === 'ready';
  if (ready) {
    if (ed.isSample) {
      nodes.push(h('div', { class: 'notice' }, 'This is a sample. Paste your own playlist URL to save it and get a player link.'));
    }
    nodes.push(...pickerNodes(ed));
  }

  app.replaceChildren(...nodes);
  setActionBar(ready);
  if (ready) {
    app.append(actionBar(ed));
    mountPicker(ed);
  }
}

function editorHead(ed, playlist) {
  const back = h('a', { class: 'icon-btn', href: '#/', 'aria-label': 'Back to playlists' }, icon('back'));
  if (!ed.id && ed.phase !== 'ready') {
    return h('div', { class: 'editor-head' }, back, h('h1', { class: 'page-title', text: 'New playlist' }));
  }
  const nameInput = h('input', {
    class: 'input input-name',
    type: 'text',
    value: ed.name,
    maxlength: '200',
    placeholder: 'Playlist name',
    'aria-label': 'Playlist name',
    autocomplete: 'off',
    oninput: (e) => {
      ed.name = e.target.value;
      updateActionBar();
    },
    onkeydown: (e) => {
      if (e.key === 'Enter') e.target.blur();
    },
  });
  return h('div', { class: 'editor-head' },
    state.user ? back : null,
    nameInput,
    playlist ? h('button', { class: 'icon-btn', type: 'button', 'aria-label': 'Playlist actions', onclick: () => openPlaylistMenu(playlist.id) }, icon('more')) : null);
}

function linkCard(playlist) {
  return h('section', { class: 'card link-card' },
    h('div', { class: 'field-label', text: 'Player link' }),
    linkBox(playlist),
    h('p', { class: 'field-hint', text: 'Add this link to your IPTV player. It always serves the groups you saved here.' }));
}

function renderLinkCard() {
  const slot = document.getElementById('link-card');
  const playlist = state.playlists.find((p) => p.id === state.editor?.id);
  if (slot && playlist) slot.replaceChildren(linkCard(playlist));
}

function sourceForm(ed) {
  const url = h('input', {
    class: 'input',
    name: 'url',
    type: 'url',
    inputmode: 'url',
    autocomplete: 'off',
    autocapitalize: 'off',
    spellcheck: 'false',
    placeholder: 'https://provider.example/get.php?username=…',
    value: ed.sourceUrl,
    required: true,
  });
  const form = h('form', { class: 'card stack', novalidate: true },
    h('label', { class: 'field' },
      h('span', { class: 'field-label', text: 'Playlist URL' }),
      url,
      h('span', { class: 'field-hint', text: 'The M3U or M3U8 link from your provider.' })));

  const actions = h('div', { class: 'row-actions' },
    h('button', { class: 'btn btn-primary', type: 'submit' }, ed.phase === 'ready' ? 'Reload groups' : 'Load groups'));
  if (!ed.id && ed.phase !== 'ready') {
    actions.append(h('button', {
      class: 'btn btn-ghost',
      type: 'button',
      onclick: () => {
        ed.sourceUrl = '';
        loadGroups(ed, '', { sample: true });
      },
    }, 'Try a sample'));
  }
  if (ed.phase === 'ready' || (ed.id && ed.phase !== 'error')) {
    actions.append(h('button', {
      class: 'btn btn-ghost',
      type: 'button',
      onclick: () => {
        ed.sourceUrl = ed.savedUrl || ed.sourceUrl;
        ed.editingSource = false;
        renderEditor();
      },
    }, 'Cancel'));
  }
  form.append(actions);

  form.addEventListener('submit', (e) => {
    e.preventDefault();
    const value = url.value.trim();
    let parsed;
    try {
      parsed = new URL(value);
    } catch {
      parsed = null;
    }
    if (!parsed || !/^https?:$/.test(parsed.protocol)) {
      toast('Enter a full http(s) playlist URL.', { error: true });
      url.focus();
      return;
    }
    ed.sourceUrl = value;
    ed.editingSource = false;
    loadGroups(ed, value);
  });
  if (!ed.sourceUrl) setTimeout(() => url.focus({ preventScroll: true }), 0);
  return form;
}

function loadingCard(ed) {
  const elapsed = h('span', { class: 'num' });
  const tick = () => {
    const seconds = Math.floor((Date.now() - ed.startedAt) / 1000);
    elapsed.textContent = seconds >= 2 ? ` · ${seconds} s` : '';
  };
  tick();
  loadingTimer = setInterval(tick, 1000);
  return h('div', { class: 'card loading-card' },
    h('span', { class: 'spinner', 'aria-hidden': 'true' }),
    h('div', { class: 'grow' },
      h('div', null, ed.isSample || !ed.sourceUrl ? 'Loading sample' : 'Loading groups from your provider', elapsed),
      h('div', { class: 'field-hint', text: 'Large playlists can take up to a minute.' })),
    h('button', {
      class: 'btn btn-ghost btn-sm',
      type: 'button',
      onclick: () => {
        ed.controller?.abort();
        ed.controller = null;
        ed.phase = ed.groups.length ? 'ready' : ed.id ? 'error' : 'source';
        ed.error = 'Loading was cancelled.';
        if (!ed.groups.length && !ed.id) ed.editingSource = true;
        renderEditor();
      },
    }, 'Cancel'));
}

// ---------- Group picker ----------

const picker = {
  ed: null,
  list: null,
  rows: [],
  rowHeight: 48,
  frame: 0,
  shownLabel: null,
  typeButtons: null,
  showButtons: null,
};

function pickerNodes(ed) {
  const search = h('input', {
    class: 'input input-search',
    type: 'search',
    placeholder: `Search ${plural(ed.groups.length, 'group')}`,
    'aria-label': 'Search groups',
    autocomplete: 'off',
    spellcheck: 'false',
    enterkeyhint: 'search',
    value: ed.filter.query,
    id: 'group-search',
  });
  search.addEventListener('input', () => {
    ed.filter.query = search.value;
    scheduleViewUpdate();
  });

  const sort = h('select', { class: 'input', 'aria-label': 'Sort groups' },
    h('option', { value: 'provider', text: 'Original order' }),
    h('option', { value: 'name', text: 'A–Z' }),
    h('option', { value: 'size', text: 'Largest' }));
  sort.value = ed.filter.sort;
  sort.addEventListener('change', () => {
    ed.filter.sort = sort.value;
    updateView(true);
  });

  const segmented = (options, current, onPick) => {
    const wrap = h('div', { class: 'pills', role: 'group' });
    for (const option of options) {
      wrap.append(h('button', {
        type: 'button',
        'aria-pressed': String(option.value === current),
        dataset: { value: option.value },
        onclick: () => {
          for (const b of wrap.children) b.setAttribute('aria-pressed', String(b.dataset.value === option.value));
          onPick(option.value);
        },
      },
      option.dot ? h('span', { class: 'dot', dataset: { type: option.value } }) : null,
      option.label,
      option.count != null ? h('span', { class: 'count', text: fmt(option.count) }) : null));
    }
    return wrap;
  };

  const rows = [search];

  const types = CONTENT_TYPES.filter((t) => ed.typeCounts[t]);
  if (types.length > 1) {
    rows.push(h('div', { class: 'toolbar-row' }, segmented(
      [{ value: 'all', label: 'All' }, ...types.map((t) => ({ value: t, label: t, count: ed.typeCounts[t], dot: true }))],
      ed.filter.type,
      (value) => {
        ed.filter.type = value;
        updateView(true);
      })));
  }
  const filters = h('div', { class: 'toolbar-row' });
  filters.append(segmented(
    [{ value: 'all', label: 'All' }, { value: 'kept', label: 'On' }, { value: 'hidden', label: 'Off' }],
    ed.filter.show,
    (value) => {
      ed.filter.show = value;
      updateView(true);
    }));
  filters.append(h('div', { class: 'spacer' }), sort);
  rows.push(filters);

  picker.shownLabel = h('span', { class: 'num' });
  rows.push(h('div', { class: 'summary' },
    picker.shownLabel,
    h('div', { class: 'bulk' },
      h('button', { class: 'btn btn-ghost', type: 'button', onclick: () => bulkSet(false) }, 'All on'),
      h('button', { class: 'btn btn-ghost', type: 'button', onclick: () => bulkSet(true) }, 'All off'))));

  return [
    h('section', { class: 'picker-toolbar', 'aria-label': 'Group filters' }, rows),
    h('div', { class: 'card group-list', id: 'group-list', role: 'list', 'aria-label': 'Groups' }),
  ];
}

function mountPicker(ed) {
  picker.ed = ed;
  picker.list = document.getElementById('group-list');
  picker.rows = [];
  picker.list.addEventListener('change', onRowToggle);
  measureRowHeight();
  updateView(false);
}

function teardownPicker() {
  picker.ed = null;
  picker.list = null;
  picker.rows = [];
  cancelAnimationFrame(picker.frame);
}

function measureRowHeight() {
  const value = parseFloat(getComputedStyle(document.documentElement).getPropertyValue('--row-h'));
  picker.rowHeight = Number.isFinite(value) && value > 0 ? value : 48;
}

let viewTimer = 0;
function scheduleViewUpdate() {
  clearTimeout(viewTimer);
  viewTimer = setTimeout(() => {
    viewTimer = 0;
    updateView(true);
  }, 60);
}

const collator = new Intl.Collator(undefined, { sensitivity: 'base', numeric: true });

function updateView(scrollToTop) {
  const ed = picker.ed;
  viewTimer = 0;
  if (!ed) return;
  const terms = ed.filter.query.toLowerCase().split(/\s+/).filter(Boolean);
  const { type, show, sort } = ed.filter;
  const view = [];
  for (const g of ed.groups) {
    if (type !== 'all' && g.type !== type) continue;
    if (show === 'kept' && ed.disabled.has(g.key)) continue;
    if (show === 'hidden' && !ed.disabled.has(g.key)) continue;
    if (terms.length && !terms.every((t) => g.search.includes(t))) continue;
    view.push(g);
  }
  if (sort === 'name') view.sort((a, b) => collator.compare(a.title, b.title) || a.index - b.index);
  else if (sort === 'size') view.sort((a, b) => b.count - a.count || a.index - b.index);
  ed.view = view;

  picker.list.style.height = view.length ? `${view.length * picker.rowHeight}px` : '';
  picker.shownLabel.textContent = view.length === ed.groups.length
    ? plural(view.length, 'group')
    : `${fmt(view.length)} of ${plural(ed.groups.length, 'group')} shown`;

  let empty = picker.list.querySelector('.list-empty');
  if (!view.length) {
    if (!empty) {
      empty = h('div', { class: 'list-empty' });
      picker.list.append(empty);
    }
    empty.textContent = terms.length ? 'No groups match your search.' : 'No groups here.';
  } else if (empty) {
    empty.remove();
  }

  if (scrollToTop) {
    const toolbar = document.querySelector('.picker-toolbar');
    const listTop = picker.list.getBoundingClientRect().top + window.scrollY;
    const stickyOffset = (toolbar?.offsetHeight || 0) + parseFloat(getComputedStyle(document.documentElement).getPropertyValue('--topbar-h'));
    if (window.scrollY > listTop - stickyOffset) window.scrollTo({ top: listTop - stickyOffset });
  }
  for (const row of picker.rows) row.dataset.index = '';
  paintRows();
  updateActionBar();
}

function createRow() {
  const check = h('input', { class: 'toggle', type: 'checkbox', role: 'switch' });
  const dot = h('span', { class: 'dot', 'aria-hidden': 'true' });
  const name = h('span', { class: 'group-name' });
  const badge = h('span', { class: 'tag' });
  const count = h('span', { class: 'group-count' });
  const row = h('label', { class: 'group-row', role: 'listitem' }, dot, name, badge, count, check);
  row._parts = { check, name, badge, count };
  picker.list.append(row);
  return row;
}

function paintRows() {
  const ed = picker.ed;
  if (!ed || !picker.list) return;
  const rowHeight = picker.rowHeight;
  const top = picker.list.getBoundingClientRect().top;
  const buffer = 8;
  const first = Math.max(0, Math.floor(-top / rowHeight) - buffer);
  const last = Math.min(ed.view.length, Math.ceil((window.innerHeight - top) / rowHeight) + buffer);
  const needed = Math.max(0, last - first);

  while (picker.rows.length < needed) picker.rows.push(createRow());

  for (let i = 0; i < picker.rows.length; i++) {
    const row = picker.rows[i];
    const index = first + i;
    if (i >= needed) {
      row.hidden = true;
      continue;
    }
    const g = ed.view[index];
    row.hidden = false;
    if (row.dataset.index !== String(index)) {
      row.dataset.index = String(index);
      row.style.transform = `translateY(${index * rowHeight}px)`;
      row.dataset.type = g.type;
      const { name, badge, count } = row._parts;
      name.textContent = g.title;
      name.title = g.title;
      const showBadge = g.type !== 'Live' || g.split;
      badge.hidden = !showBadge;
      if (showBadge) badge.textContent = g.type;
      count.textContent = fmt(g.count);
    }
    const off = ed.disabled.has(g.key);
    row._parts.check.checked = !off;
    row._parts.check.setAttribute('aria-label', `${g.title}${g.type !== 'Live' || g.split ? ` (${g.type})` : ''}, ${plural(g.count, 'channel')}`);
    row.classList.toggle('off', off);
  }
}

function onRowToggle(e) {
  const check = e.target;
  if (!check.classList.contains('toggle')) return;
  const ed = picker.ed;
  const row = check.closest('.group-row');
  const g = ed?.view[Number(row?.dataset.index)];
  if (!g) return;
  if (check.checked) ed.disabled.delete(g.key);
  else ed.disabled.add(g.key);
  row.classList.toggle('off', !check.checked);
  updateActionBar();
}

function bulkSet(hide) {
  const ed = picker.ed;
  if (!ed) return;
  // Apply a search that is still debounced, so the action hits the groups the user sees.
  if (viewTimer) {
    clearTimeout(viewTimer);
    updateView(false);
  }
  if (!ed.view.length) return;
  for (const g of ed.view) {
    if (hide) ed.disabled.add(g.key);
    else ed.disabled.delete(g.key);
  }
  toast(`Switched ${hide ? 'off' : 'on'} ${plural(ed.view.length, 'group')}`);
  paintRows();
  updateActionBar();
}

window.addEventListener('scroll', () => {
  document.body.classList.toggle('scrolled', window.scrollY > 4);
  if (!picker.ed) return;
  cancelAnimationFrame(picker.frame);
  picker.frame = requestAnimationFrame(paintRows);
}, { passive: true });

window.addEventListener('resize', () => {
  if (!picker.ed) return;
  const before = picker.rowHeight;
  measureRowHeight();
  if (before !== picker.rowHeight) updateView(false);
  else paintRows();
});

document.addEventListener('keydown', (e) => {
  if (e.key !== '/' || !picker.ed) return;
  const tag = document.activeElement?.tagName;
  if (tag === 'INPUT' || tag === 'TEXTAREA' || tag === 'SELECT' || document.querySelector('dialog[open]')) return;
  e.preventDefault();
  document.getElementById('group-search')?.focus();
});

// ---------- Action bar ----------

const bar = { info: null, save: null, download: null };

function actionBar(ed) {
  bar.info = h('div', { class: 'dock-info', 'aria-live': 'polite' });
  bar.download = h('button', { class: 'btn', type: 'button', 'aria-label': 'Download .m3u', onclick: () => downloadSelection(ed) },
    icon('download'), h('span', { class: 'label-long', text: 'Download' }));
  const buttons = [bar.download];

  bar.save = null;
  if (!ed.isSample) {
    bar.save = h('button', { class: 'btn btn-primary', type: 'button', onclick: () => saveEditor(ed) });
    buttons.push(bar.save);
  } else {
    bar.download.classList.add('btn-primary');
  }
  return h('div', { class: 'dock' }, bar.info, ...buttons);
}

function selectionStats(ed) {
  let channels = 0;
  let groups = 0;
  for (const g of ed.groups) {
    if (ed.disabled.has(g.key)) continue;
    channels += g.count;
    groups += 1;
  }
  return { channels, groups };
}

function updateActionBar() {
  const ed = state.editor;
  if (!ed || ed.phase !== 'ready' || !bar.info) return;
  const { channels, groups } = selectionStats(ed);
  bar.info.replaceChildren(
    h('div', { class: 'dock-value' }, fmt(channels), h('small', { text: channels === 1 ? 'channel' : 'channels' })),
    h('div', { class: 'dock-label', text: `${fmt(groups)} of ${plural(ed.groups.length, 'group')} on` }));
  bar.download.disabled = ed.busy || channels === 0;

  if (bar.save) {
    const dirty = isDirty(ed);
    const saved = Boolean(ed.id) && !dirty && ed.busy !== 'save';
    bar.save.disabled = Boolean(ed.busy) || saved || channels === 0;
    bar.save.classList.toggle('btn-primary', !saved);
    bar.save.classList.toggle('is-saved', saved);
    bar.save.replaceChildren(...(saved
      ? [icon('check'), 'Saved']
      : [ed.busy === 'save' ? 'Saving…' : state.user ? 'Save' : 'Save…']));
  }
}

async function downloadSelection(ed) {
  ed.busy = 'download';
  updateActionBar();
  try {
    const res = await api('/playlist/generate', {
      method: 'POST',
      body: { cacheKey: ed.cacheKey, sourceUrl: ed.isSample ? null : ed.sourceUrl, disabledGroups: [...ed.disabled] },
    });
    const link = h('a', { href: `${API_BASE}/playlist/output/${encodeURIComponent(res.outputKey)}`, download: fileName(ed.name), rel: 'noopener' });
    document.body.append(link);
    link.click();
    link.remove();
    toast(`Downloading ${plural(res.keptChannels ?? 0, 'channel')}`);
  } catch (err) {
    toast(err.message, { error: true });
  } finally {
    ed.busy = false;
    updateActionBar();
  }
}

async function saveEditor(ed) {
  if (!state.user) {
    openAccount('Sign in to save this playlist and get a link for your player. Your selection stays as it is.');
    return;
  }
  const name = ed.name.trim() || sourceHost(ed.sourceUrl) || 'My playlist';
  const loaded = ed.phase === 'ready';
  const disabledGroups = loaded ? [...ed.disabled, ...ed.extras] : ed.savedFilters;
  if (disabledGroups.length > MAX_HIDDEN_GROUPS) {
    toast(`You can hide at most ${fmt(MAX_HIDDEN_GROUPS)} groups.`, { error: true });
    return;
  }

  const existing = ed.id ? state.playlists.find((p) => p.id === ed.id) : null;
  const body = {
    name,
    sourceUrl: ed.sourceUrl,
    sourceName: existing?.sourceName || sourceHost(ed.sourceUrl),
    disabledGroups,
    totalChannels: loaded ? ed.totalChannels : existing?.totalChannels,
    groupCount: loaded ? ed.groups.length : existing?.groupCount,
    expirationUtc: (loaded ? ed.expirationUtc : existing?.expirationUtc) || null,
  };

  ed.busy = 'save';
  updateActionBar();
  try {
    const saved = ed.id
      ? await api(`/playlists/${ed.id}`, { method: 'PUT', body })
      : await api('/playlists', { method: 'POST', body });
    state.playlists = [saved, ...state.playlists.filter((p) => p.id !== saved.id)];
    const created = !ed.id;
    ed.id = saved.id;
    ed.name = saved.name;
    ed.savedName = saved.name;
    ed.savedUrl = saved.sourceUrl || '';
    ed.savedFilters = saved.disabledGroups || [];
    ed.baseline = filterSignature(ed);
    ed.busy = false;
    if (created) {
      toast('Saved. Copy the player link into your IPTV app.');
      navigate(`#/p/${saved.id}`, { replace: true });
      window.scrollTo({ top: 0 });
    } else {
      toast('Changes saved. Players get them on their next refresh.');
      renderLinkCard();
      updateActionBar();
    }
  } catch (err) {
    ed.busy = false;
    updateActionBar();
    toast(err.status === 401 ? 'You were signed out. Sign in and save again.' : err.message, { error: true });
  }
}

// ---------- Start ----------

accountButton.addEventListener('click', () => openAccount());

async function init() {
  try {
    await loadSession();
  } catch (err) {
    console.error('Failed to start', err);
  }
  state.ready = true;
  updateAccountButton();
  route();
}

init();
