// Strim app: a playlists home and a group picker, driven by the backend (no local M3U parsing).
// Routes: #/ (playlists, or the editor for guests), #/new, #/p/<playlist id>, #/p/<playlist id>/library.

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

// Must match Limits.MaxDisabledGroupsCount in api/Endpoints/EndpointSupport.cs.
const MAX_HIDDEN_GROUPS = 10000;
const CONTENT_TYPES = ['Live', 'Movies', 'Series'];

// ---------- State ----------

const state = {
  ready: false,
  user: null,
  providers: [],
  playlists: [],
  overview: new Map(),
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
  close: '<path d="M6 6l12 12M18 6L6 18"/>',
  book: '<path d="M5 5.5A1.5 1.5 0 0 1 6.5 4H19v13H6.5A1.5 1.5 0 0 0 5 18.5zM5 18.5A1.5 1.5 0 0 0 6.5 20H19"/>',
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

function toast(message, { error = false, duration = 3200, action = null } = {}) {
  const el = h('div', { class: `toast${error ? ' error' : ''}` }, h('span', { text: message }));
  if (action) {
    el.append(h('button', { class: 'toast-action', type: 'button', onclick: action.onClick }, action.label));
  }
  toasts.append(el);
  while (toasts.children.length > 3) toasts.firstChild.remove();
  setTimeout(() => {
    el.classList.add('leaving');
    setTimeout(() => el.remove(), 220);
  }, error ? duration + 2000 : duration);
  return el;
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

// Must match EndpointFilters.CsrfError in api/Endpoints/EndpointSupport.cs.
const CSRF_ERROR = 'Invalid or missing CSRF token';

async function api(path, { method = 'GET', body, signal } = {}, retried = false) {
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

  // The antiforgery token is bound to the session (it changes on sign-in in another tab, for
  // example). A rejected token is fetched again and the request retried once.
  if (mutating && res.status === 400 && !retried && (await isCsrfRejection(res))) {
    csrfToken = null;
    return api(path, { method, body, signal }, true);
  }
  if (res.status === 204) return null;
  if (!res.ok) {
    const err = new Error(await readError(res));
    err.status = res.status;
    throw err;
  }
  return res.json();
}

async function isCsrfRejection(res) {
  try {
    return (await res.clone().json()).error === CSRF_ERROR;
  } catch {
    return false;
  }
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
  state.overview = new Map();
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
  const match = /^\/p\/([0-9a-f-]{36})(\/library)?$/i.exec(path);
  if (match) return { name: match[2] ? 'library' : 'playlist', id: match[1].toLowerCase() };
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
  if (target.name === 'library') return showLibrary(playlist);
  showEditor(playlist);
}

function closeSheets() {
  if (menuDialog.open) menuDialog.close();
}

// A report that arrives after its sheet closed must not overwrite whatever the sheet shows next.
menuDialog.addEventListener('close', () => { reportRequest += 1; });

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
  loadOverview();
}

// Stored facts per playlist from /playlists/overview. If the call fails or the body is not a list,
// the cache is dropped and every card shows an empty facts line, so no outdated counts or dates remain.
async function loadOverview() {
  let data;
  try {
    data = await api('/playlists/overview');
  } catch {
    data = null;
  }
  state.overview = Array.isArray(data) ? new Map(data.map((item) => [item.id, item])) : new Map();
  for (const node of app.querySelectorAll('.playlist-card .facts')) {
    node.replaceWith(playlistFacts(node.dataset.id));
  }
}

// Only stored values: a null fact is left out. The source is shown by host only.
function playlistFacts(id) {
  const item = state.overview.get(id);
  const parts = [];
  const count = (value, label) => h('span', null, h('span', { class: 'num', text: fmt(value) }), label);
  if (item) {
    if (item.groupCount != null && item.groupsOn != null) parts.push(count(item.groupsOn, ` of ${plural(item.groupCount, 'group')} on`));
    if (item.totalChannels != null) parts.push(count(item.totalChannels, item.totalChannels === 1 ? ' entry' : ' entries'));
    if (item.updatedAt) parts.push(h('span', { text: `Changed ${relativeTime(item.updatedAt)}` }));
    if (item.lastViewedUtc) parts.push(h('span', { text: `Player fetched ${relativeTime(item.lastViewedUtc)}` }));
    if (item.sourceHost) parts.push(h('span', { class: 'facts-host', text: item.sourceHost }));
  }
  return h('div', { class: 'facts', dataset: { id } }, parts);
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
  const status = playlist.accountStatus?.trim();
  if (status && status.toLowerCase() !== 'active') parts.push(h('span', { class: 'warn', text: `Account ${status}` }));
  const max = playlist.accountMaxConnections;
  if (Number.isInteger(max) && max > 0) {
    const active = playlist.accountActiveConnections;
    parts.push(h('span', { text: Number.isInteger(active) ? `${fmt(active)} of ${plural(max, 'connection')}` : plural(max, 'connection') }));
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
        playlistMeta(playlist),
        playlistFacts(playlist.id)),
      h('button', { class: 'icon-btn', type: 'button', 'aria-label': `Actions for ${playlist.name}`, onclick: () => openPlaylistMenu(playlist.id) }, icon('more'))),
    linkBox(playlist),
    h('div', { class: 'row-actions' },
      h('a', { class: 'btn btn-sm', href: `#/p/${playlist.id}` }, icon('list'), 'Edit groups'),
      h('a', { class: 'btn btn-sm', href: `#/p/${playlist.id}/library` }, icon('book'), 'Library')));
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
  const inLibrary = current.name === 'library' && current.id === playlist.id;
  menuTitle.textContent = playlist.name || 'Playlist';
  menuBody.replaceChildren(h('div', { class: 'menu' },
    inEditor ? null : item('list', 'Edit groups', () => navigate(`#/p/${playlist.id}`)),
    inLibrary ? null : item('book', 'Library', () => navigate(`#/p/${playlist.id}/library`)),
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

// The group show filter (All / On / Off) is remembered per saved playlist, in this browser only (#269).
const GROUP_SHOW_VALUES = ['all', 'kept', 'hidden'];

function readGroupShow(id) {
  if (!id) return 'all';
  try {
    const value = localStorage.getItem(`strim.groupShow.${id}`);
    return GROUP_SHOW_VALUES.includes(value) ? value : 'all';
  } catch {
    return 'all';
  }
}

function writeGroupShow(id, value) {
  if (!id || !GROUP_SHOW_VALUES.includes(value)) return;
  try {
    localStorage.setItem(`strim.groupShow.${id}`, value);
  } catch {
    // Private mode or a full quota: the choice lasts for this view only.
  }
}

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
    account: null,
    groups: [],
    typeCounts: {},
    disabled: new Set(),
    extras: [],
    baseline: '',
    filter: { query: '', type: 'all', show: readGroupShow(playlist?.id), sort: 'provider' },
    searchMode: 'groups', // groups | everything
    search: null, // Everything results: { status, query, matches, hasMore, message }
    view: [],
    // Groups toggled since the view was built; they stay in it until a filter, the query or the mode changes.
    pinned: new Set(),
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
    const analysis = await fetchAnalysis(url, { sample, signal: controller.signal });
    if (ed.controller !== controller) return;
    applyAnalysis(ed, analysis, { sample });
  } catch (err) {
    if (isAbort(err) || ed.controller !== controller) return;
    ed.phase = 'error';
    ed.error = loadErrorMessage(err);
  } finally {
    if (ed.controller === controller) ed.controller = null;
  }
  if (isVisible(ed)) renderEditor();
}

// The editor and the Library load a playlist's groups through these two functions.
async function fetchAnalysis(url, { sample = false, signal } = {}) {
  let body;
  if (sample) {
    const res = await api('/playlists/sample', { signal });
    body = { rawText: res.text, sourceName: 'Sample playlist' };
  } else {
    body = { sourceUrl: url };
  }
  return api('/playlist/analyze', { method: 'POST', body, signal });
}

function loadErrorMessage(err) {
  return err.status === 504
    ? 'The provider took too long to answer. Try again in a moment.'
    : err.message || 'Could not load the playlist.';
}

function applyAnalysis(ed, analysis, { sample }) {
  const groups = (analysis.groups || []).map((g, index) => {
    const title = g.groupTitle || g.name;
    const type = CONTENT_TYPES.includes(g.contentType) ? g.contentType : 'Live';
    // Whole words of the title (S2); null only for an older response without `words`, which keeps the substring match.
    // An untitled group arrives with `words: []` and so matches no short term.
    const words = Array.isArray(g.words) ? g.words.map((w) => String(w).toLowerCase()) : null;
    // Sample titles (S1) are joined once here, so painting a row only assigns text.
    const samples = Array.isArray(g.sampleTitles) ? g.sampleTitles.filter(Boolean).join(', ') : '';
    return { key: g.name, title, type, count: g.count || 0, index, split: false, search: '', words, adult: g.isAdult === true, samples };
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

  if (bulkUndo?.ed === ed) discardBulkUndo();
  ed.groups = groups;
  ed.typeCounts = typeCounts;
  ed.disabled = disabled;
  ed.extras = extras;
  ed.cacheKey = analysis.cacheKey;
  ed.search = null;
  ed.totalChannels = analysis.totalChannels || groups.reduce((sum, g) => sum + g.count, 0);
  ed.expirationUtc = analysis.expirationUtc || null;
  // The API rejects a status over 32 characters (varchar(32)); drop only the status so the save still works.
  const status = analysis.accountStatus;
  ed.account = analysis.accountCheckedUtc ? {
    accountStatus: typeof status === 'string' && status.length <= 32 ? status : null,
    accountMaxConnections: analysis.maxConnections ?? null,
    accountActiveConnections: analysis.activeConnections ?? null,
    accountCheckedUtc: analysis.accountCheckedUtc,
  } : null;
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
    nodes.push(h('div', { class: 'report-line' },
      h('button', { class: 'btn btn-ghost', type: 'button', 'aria-haspopup': 'dialog', onclick: () => openReport(ed) }, 'Duplicates & variants')));
    // Below 1100px both wrappers are display: contents and the pane is not shown, so the narrow
    // layout is unchanged. From 1100px the pane shows the entries of the selected group.
    nodes.push(h('div', { class: 'editor-split' },
      h('div', { class: 'editor-groups' }, ...pickerNodes(ed)),
      h('aside', { class: 'entry-pane', 'aria-label': 'Group entries', 'aria-hidden': 'true' })));
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
    playlist ? h('a', { class: 'btn btn-sm', href: `#/p/${playlist.id}/library` }, icon('book'), 'Library') : null,
    playlist ? h('button', { class: 'icon-btn', type: 'button', 'aria-label': 'Playlist actions', onclick: () => openPlaylistMenu(playlist.id) }, icon('more')) : null);
}

// ---------- Library ----------

// The Library (library.js) is read-only. main.js passes in the playlist and the helpers it needs;
// the groups come from the stored disabled groups and the same analysis the editor loads.
function showLibrary(playlist) {
  clearInterval(loadingTimer);
  teardownPicker();
  setActionBar(false);
  document.title = `${playlist.name || 'Playlist'} · Library · Strim`;
  window.StrimLibrary.mount({
    root: app,
    playlist,
    h,
    icon,
    fmt,
    plural,
    isCurrent: () => {
      const target = parseRoute();
      return target.name === 'library' && target.id === playlist.id;
    },
    loadGroups: async (signal) => {
      const ed = newEditor(playlist);
      applyAnalysis(ed, await fetchAnalysis(playlist.sourceUrl, { signal }), { sample: false });
      return { groups: ed.groups, disabled: ed.disabled, totalChannels: ed.totalChannels };
    },
    errorMessage: loadErrorMessage,
  });
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
  allOnButton: null,
  allOffButton: null,
  adultSwitch: null,
  typeButtons: null,
  showButtons: null,
  results: null,
  modes: null,
  everything: null,
  highlight: null,
};

function searchLabels(ed) {
  return ed.searchMode === 'everything'
    ? { placeholder: 'Search all entries', label: 'Search all entries' }
    : { placeholder: 'Filter groups · titles: Everything', label: 'Filter groups. Find titles with Everything' };
}

// The action that takes a Groups query to the entry search. The query goes in as text, never markup.
function everythingButton(query, className = '') {
  const shown = query.trim();
  return h('button', {
    class: `btn search-everything ${className}`.trim(),
    type: 'button',
    onclick: switchToEverything,
  }, 'Search all entries for ', h('span', { class: 'search-everything-query', text: `“${shown}”` }));
}

// Acts as a press on the Everything button, then keeps focus in the field (the action disappears).
function switchToEverything() {
  picker.modes?.querySelector('[data-value="everything"]')?.click();
  document.getElementById('group-search')?.focus();
}

function updateEverythingAction(ed) {
  const slot = picker.everything;
  if (!slot) return;
  const query = ed.searchMode === 'everything' ? '' : ed.filter.query.trim();
  slot.hidden = !query;
  if (query && slot.dataset.query !== query) slot.replaceChildren(everythingButton(query));
  slot.dataset.query = query;
}

function pickerNodes(ed) {
  const labels = searchLabels(ed);
  const search = h('input', {
    class: 'input input-search',
    type: 'search',
    placeholder: labels.placeholder,
    'aria-label': labels.label,
    autocomplete: 'off',
    spellcheck: 'false',
    enterkeyhint: 'search',
    value: ed.filter.query,
    id: 'group-search',
  });
  search.addEventListener('input', () => {
    ed.filter.query = search.value;
    if (ed.searchMode === 'everything') scheduleSearch(ed);
    else {
      ed.pinned.clear();
      scheduleViewUpdate();
    }
  });

  const sort = h('select', { class: 'input', 'aria-label': 'Sort groups' },
    h('option', { value: 'provider', text: 'Original order' }),
    h('option', { value: 'name', text: 'A–Z' }),
    h('option', { value: 'size', text: 'Largest' }));
  sort.value = ed.filter.sort;
  sort.addEventListener('change', () => {
    ed.filter.sort = sort.value;
    ed.pinned.clear();
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

  const modes = segmented(
    [{ value: 'groups', label: 'Groups' }, { value: 'everything', label: 'Everything' }],
    ed.searchMode,
    (value) => {
      ed.searchMode = value;
      ed.pinned.clear();
      const next = searchLabels(ed);
      search.placeholder = next.placeholder;
      search.setAttribute('aria-label', next.label);
      picker.results.hidden = value !== 'everything';
      if (value === 'everything') runSearch(ed);
      else cancelSearch();
      updateView(true);
    });
  modes.setAttribute('aria-label', 'Search in');
  modes.classList.add('search-modes');
  picker.modes = modes;
  picker.everything = h('div', { class: 'search-everything-row', hidden: true });
  const rows = [h('div', { class: 'search-row' }, search, modes), picker.everything];

  const types = CONTENT_TYPES.filter((t) => ed.typeCounts[t]);
  picker.typeButtons = null;
  if (types.length > 1) {
    rows.push(h('div', { class: 'toolbar-row' }, picker.typeButtons = segmented(
      [{ value: 'all', label: 'All' }, ...types.map((t) => ({ value: t, label: t, count: ed.typeCounts[t], dot: true }))],
      ed.filter.type,
      (value) => {
        ed.filter.type = value;
        ed.pinned.clear();
        updateView(true);
      })));
  }
  const filters = h('div', { class: 'toolbar-row show-row' });
  filters.append(h('span', { class: 'show-label', id: 'group-show-label' },
    'Show', h('span', { class: 'show-hint', text: ' groups' })));
  filters.append(picker.showButtons = segmented(
    [{ value: 'all', label: 'All' }, { value: 'kept', label: 'On' }, { value: 'hidden', label: 'Off' }],
    ed.filter.show,
    (value) => {
      ed.filter.show = value;
      writeGroupShow(ed.isSample ? null : ed.id, value);
      ed.pinned.clear();
      updateView(true);
      // The Everything search follows the same choice: On leaves out matches in switched-off groups.
      if (ed.searchMode === 'everything') runSearch(ed);
    }));
  picker.showButtons.setAttribute('aria-labelledby', 'group-show-label');
  filters.append(h('div', { class: 'spacer' }), sort);
  rows.push(filters);

  picker.shownLabel = h('span', { class: 'num' });
  picker.allOnButton = h('button', { class: 'btn btn-ghost', type: 'button', onclick: () => bulkSet(false) }, 'All on');
  picker.allOffButton = h('button', { class: 'btn btn-ghost', type: 'button', onclick: () => bulkSet(true) }, 'All off');
  // Shown only when the analysis flagged adult groups; it edits the same per-group choices as the rows.
  picker.adultSwitch = ed.groups.some((g) => g.adult)
    ? h('input', { class: 'toggle', type: 'checkbox', role: 'switch', onchange: (e) => setAdultHidden(e.target.checked) })
    : null;
  rows.push(h('div', { class: 'summary' },
    picker.shownLabel,
    picker.adultSwitch ? h('label', { class: 'adult-switch' }, 'Hide adult groups', picker.adultSwitch) : null,
    h('div', { class: 'bulk' }, picker.allOnButton, picker.allOffButton)));

  picker.results = h('section', { class: 'card search-results', 'aria-label': 'Search results', hidden: ed.searchMode !== 'everything' });

  return [
    h('section', { class: 'picker-toolbar', 'aria-label': 'Group filters' }, rows),
    picker.results,
    h('div', { class: 'card group-list', id: 'group-list', role: 'list', 'aria-label': 'Groups' }),
  ];
}

function mountPicker(ed) {
  discardBulkUndo();
  picker.ed = ed;
  picker.list = document.getElementById('group-list');
  picker.rows = [];
  picker.list.addEventListener('change', onRowToggle);
  picker.list.addEventListener('click', onRowSelect);
  picker.list.addEventListener('keydown', onSwitchEnter);
  pane.el = document.querySelector('.entry-pane');
  measureRowHeight();
  updateView(false);
  // A re-render (new analysis, save) shows the selected group again if it still exists.
  const selected = ed.paneGroup && ed.groups.find((g) => g.key === ed.paneGroup);
  if (selected && WIDE_EDITOR.matches) openPane(ed, selected);
  else ed.paneGroup = null;
  // A new analysis clears the results, so an Everything search runs again on the new index.
  if (ed.searchMode === 'everything' && !ed.search) runSearch(ed);
  else renderSearchResults(ed);
}

function teardownPicker() {
  discardBulkUndo();
  resetPane();
  pane.el = null;
  picker.ed?.pinned.clear();
  picker.ed = null;
  picker.list = null;
  picker.rows = [];
  picker.adultSwitch = null;
  picker.results = null;
  picker.everything = null;
  picker.highlight = null;
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

// Sorted copies of a groups array, one per sort mode. Keyed by the array itself, so loading another
// playlist (which replaces ed.groups) starts a fresh cache. The index tie-break makes each order total,
// so filtering the sorted array gives the same order as sorting the filtered one.
const sortedGroups = new WeakMap();

function groupsInOrder(groups, sort) {
  if (sort !== 'name' && sort !== 'size') return groups;
  let bySort = sortedGroups.get(groups);
  if (!bySort) {
    bySort = {};
    sortedGroups.set(groups, bySort);
  }
  if (!bySort[sort]) {
    bySort[sort] = sort === 'name'
      ? groups.slice().sort((a, b) => collator.compare(a.title, b.title) || a.index - b.index)
      : groups.slice().sort((a, b) => b.count - a.count || a.index - b.index);
  }
  return bySort[sort];
}

// A term of up to three characters matches whole words only, so `es` no longer hits `Series`.
// The content type words (live, movies, series) are longer, so they keep the substring match.
function matchesTerm(g, term) {
  return term.length <= 3 && g.words ? g.words.includes(term) : g.search.includes(term);
}

function updateView(scrollToTop) {
  const ed = picker.ed;
  viewTimer = 0;
  if (!ed) return;
  // In Everything mode the field searches entries on the server, so it does not filter groups.
  const terms = ed.searchMode === 'everything' ? [] : ed.filter.query.toLowerCase().split(/\s+/).filter(Boolean);
  const { type, show, sort } = ed.filter;
  const { pinned } = ed;
  const view = [];
  for (const g of groupsInOrder(ed.groups, sort)) {
    if (type !== 'all' && g.type !== type) continue;
    // A group toggled in this view stays in it (dimmed when off) instead of vanishing under the finger.
    if (show === 'kept' && ed.disabled.has(g.key) && !pinned.has(g.key)) continue;
    if (show === 'hidden' && !ed.disabled.has(g.key) && !pinned.has(g.key)) continue;
    if (terms.length && !terms.every((t) => matchesTerm(g, t))) continue;
    view.push(g);
  }
  ed.view = view;

  picker.list.style.height = view.length ? `${view.length * picker.rowHeight}px` : '';
  picker.shownLabel.textContent = view.length === ed.groups.length
    ? plural(view.length, 'group')
    : `${fmt(view.length)} of ${plural(ed.groups.length, 'group')} shown`;
  // The visible label carries the count, so it is part of each button's accessible name.
  for (const [button, label] of [[picker.allOnButton, 'All on'], [picker.allOffButton, 'All off']]) {
    if (!button) continue;
    button.textContent = `${label} (${fmt(view.length)})`;
    button.disabled = !view.length;
  }

  let empty = picker.list.querySelector('.list-empty');
  if (!view.length) {
    if (!empty) {
      empty = h('div', { class: 'list-empty' });
      picker.list.append(empty);
    }
    // With a query, the empty state leads on to the entry search instead of ending here.
    const query = terms.length ? ed.filter.query.trim() : '';
    if (empty.dataset.query !== query || !empty.firstChild) {
      empty.dataset.query = query;
      empty.replaceChildren(query
        ? h('p', { class: 'list-empty-text', text: 'No groups match. Films, series and channels are found under Everything.' })
        : 'No groups here.');
      if (query) empty.append(everythingButton(query, 'btn-primary'));
    }
  } else if (empty) {
    empty.remove();
  }
  updateEverythingAction(ed);

  if (scrollToTop) {
    const listTop = picker.list.getBoundingClientRect().top + window.scrollY;
    const offset = stickyOffset();
    if (window.scrollY > listTop - offset) window.scrollTo({ top: listTop - offset });
  }
  for (const row of picker.rows) row.dataset.index = '';
  paintRows();
  updateActionBar();
}

// Height of the sticky top bar and filter toolbar that cover the top of the list.
function stickyOffset() {
  const toolbar = document.querySelector('.picker-toolbar');
  return (toolbar?.offsetHeight || 0) + parseFloat(getComputedStyle(document.documentElement).getPropertyValue('--topbar-h'));
}

function createRow() {
  const check = h('input', { class: 'toggle', type: 'checkbox', role: 'switch' });
  const dot = h('span', { class: 'dot', 'aria-hidden': 'true' });
  const name = h('span', { class: 'group-name' });
  const badge = h('span', { class: 'tag' });
  // Samples, adult mark and split origin show from 1100px only (app.css); without a value their cell stays empty.
  const samples = h('span', { class: 'group-samples' });
  const adult = h('span', { class: 'group-adult' });
  const origin = h('span', { class: 'group-origin' });
  const count = h('span', { class: 'group-count' });
  const row = h('label', { class: 'group-row', role: 'listitem' }, dot, name, badge, samples, adult, origin, count, check);
  row._parts = { check, name, badge, samples, adult, origin, count };
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
      const { name, badge, samples, adult, origin, count } = row._parts;
      name.textContent = g.title;
      name.title = g.title;
      const showBadge = g.type !== 'Live' || g.split;
      badge.hidden = !showBadge;
      if (showBadge) badge.textContent = g.type;
      samples.textContent = g.samples;
      samples.title = g.samples;
      adult.textContent = g.adult ? '18+' : '';
      adult.title = g.adult ? 'Adult group' : '';
      origin.textContent = g.split ? `from ${g.title}` : '';
      origin.title = g.split ? `Provider group ${g.title}, split by content type` : '';
      count.textContent = fmt(g.count);
    }
    const off = ed.disabled.has(g.key);
    row._parts.check.checked = !off;
    row._parts.check.setAttribute('aria-label', `${g.title}${g.type !== 'Live' || g.split ? ` (${g.type})` : ''}, ${plural(g.count, 'channel')}`);
    row.classList.toggle('off', off);
    row.classList.toggle('flash', g.key === picker.highlight);
    row.classList.toggle('selected', g.key === ed.paneGroup);
  }
}

function onRowToggle(e) {
  const check = e.target;
  if (!check.classList.contains('toggle')) return;
  const ed = picker.ed;
  const row = check.closest('.group-row');
  const g = ed?.view[Number(row?.dataset.index)];
  if (!g) return;
  discardBulkUndo();
  ed.pinned.add(g.key);
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
  const before = new Set(ed.disabled);
  // The visible rows, pinned ones included; each is pinned so the rows stay put after the change.
  for (const g of ed.view) {
    ed.pinned.add(g.key);
    if (hide) ed.disabled.add(g.key);
    else ed.disabled.delete(g.key);
  }
  discardBulkUndo();
  const entry = { ed, disabled: before, toast: null };
  entry.toast = toast(`Switched ${hide ? 'off' : 'on'} ${plural(ed.view.length, 'group')}`, {
    duration: 8000,
    action: { label: 'Undo', onClick: () => undoBulk(entry) },
  });
  bulkUndo = entry;
  paintRows();
  updateActionBar();
}

function setAdultHidden(hide) {
  const ed = picker.ed;
  if (!ed) return;
  discardBulkUndo();
  for (const g of ed.groups) {
    if (!g.adult) continue;
    if (hide) ed.disabled.add(g.key);
    else ed.disabled.delete(g.key);
  }
  updateView(false);
}

function syncAdultSwitch(ed) {
  if (!picker.adultSwitch || picker.ed !== ed) return;
  picker.adultSwitch.checked = ed.groups.every((g) => !g.adult || ed.disabled.has(g.key));
}

// Only the last bulk change can be undone; any later change discards it.
let bulkUndo = null;

function discardBulkUndo() {
  bulkUndo?.toast.querySelector('.toast-action')?.remove();
  bulkUndo = null;
}

function undoBulk(entry) {
  if (bulkUndo !== entry || picker.ed !== entry.ed) return;
  discardBulkUndo();
  entry.ed.disabled = entry.disabled;
  entry.toast.classList.add('leaving');
  setTimeout(() => entry.toast.remove(), 220);
  toast('Bulk change undone');
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

// ---------- Entry pane (wide editor, #217) ----------
// One page of the selected group's names at a time from /playlist/group-entries, so the browser
// never holds the whole playlist. At most ENTRY_PANE_PAGES pages stay rendered: scrolling down
// drops the top page, scrolling back up fetches it again. Names are inserted as text, never as HTML.

const WIDE_EDITOR = window.matchMedia('(min-width: 1100px)');
const ENTRY_PANE_PAGES = 3;
const pane = { el: null };

// The row is a <label>: on wide screens a click outside the switch selects the group instead of
// toggling it. The switch itself (click, Space) toggles as before; below 1100px nothing changes.
function onRowSelect(e) {
  if (!WIDE_EDITOR.matches || e.target.closest('.toggle')) return;
  const ed = picker.ed;
  const g = ed?.view[Number(e.target.closest('.group-row')?.dataset.index)];
  if (!g) return;
  e.preventDefault();
  openPane(ed, g);
}

// A native checkbox toggles on Space only; Enter on the switch toggles it too and never opens the pane.
function onSwitchEnter(e) {
  if (e.key !== 'Enter' || e.repeat || !e.target.classList?.contains('toggle')) return;
  e.preventDefault();
  e.target.click();
}

function resetPane() {
  pane.controller?.abort();
  Object.assign(pane, { ed: null, group: null, list: null, count: null, status: null, scroller: null, start: 0, end: 0, pages: [], done: false, failed: null, controller: null });
}

function paneCount(total, g) {
  return `${plural(total, 'entry', 'entries')}${g.type !== 'Live' || g.split ? ` · ${g.type}` : ''}`;
}

function openPane(ed, g) {
  if (!pane.el) return;
  resetPane();
  ed.paneGroup = g.key;
  pane.ed = ed;
  pane.group = g;
  pane.count = h('p', { class: 'entry-pane-count', text: paneCount(g.count, g) });
  pane.list = h('ol', { class: 'entry-list' });
  pane.status = h('div', { class: 'entry-pane-status', role: 'status' });
  pane.scroller = h('div', { class: 'entry-scroll', onscroll: () => loadMoreIfNeeded(ed) }, pane.list, pane.status);
  pane.el.replaceChildren(
    h('div', { class: 'entry-pane-head' },
      h('div', { class: 'entry-pane-title' }, h('h2', { text: g.title }), pane.count),
      h('button', { class: 'icon-btn', type: 'button', 'aria-label': 'Close entries', onclick: () => closePane(ed) }, icon('close'))),
    pane.scroller);
  pane.el.classList.add('open');
  pane.el.removeAttribute('aria-hidden');
  paintRows();
  loadEntryPage(ed, 'next');
}

function closePane(ed) {
  resetPane();
  ed.paneGroup = null;
  pane.el?.replaceChildren();
  pane.el?.classList.remove('open');
  pane.el?.setAttribute('aria-hidden', 'true');
  paintRows();
}

// Paging is suspended while the pane is hidden: below the breakpoint the scroller has no size,
// so every page would look like the end of the list and fetch the next one.
function paneVisible() {
  return WIDE_EDITOR.matches && pane.scroller?.clientHeight > 0;
}

// Narrowing the window aborts a pending page; widening it again resumes paging where it stopped.
WIDE_EDITOR.addEventListener('change', () => {
  if (!pane.ed) return;
  if (!WIDE_EDITOR.matches) {
    // Only a pending page is aborted and its loading indicator cleared; an error and its
    // "Try again" button stay, because pane.failed still blocks paging until the retry.
    const c = pane.controller;
    if (!c) return;
    pane.controller = null;
    c.abort();
    pane.status?.replaceChildren('');
  } else if (!pane.end && !pane.done) {
    loadEntryPage(pane.ed, 'next');
  } else {
    loadMoreIfNeeded(pane.ed);
  }
});

function loadMoreIfNeeded(ed) {
  const s = pane.scroller;
  if (!s || !paneVisible()) return;
  if (s.scrollTop + s.clientHeight >= s.scrollHeight - 240) loadEntryPage(ed, 'next');
  else if (s.scrollTop < 240 && pane.start > 0) loadEntryPage(ed, 'prev');
}

// Removes the first or last `n` rendered entries and keeps the scroll position by hand
// (.entry-scroll has overflow-anchor: none, so the browser does not adjust it as well).
function dropEntries(n, fromTop) {
  const s = pane.scroller;
  // Read scrollTop before removing: the browser may clamp it to the reduced range at once,
  // and subtracting the removed height from the clamped value would jump backwards.
  const top = s.scrollTop;
  const before = s.scrollHeight;
  for (let i = 0; i < n; i++) (fromTop ? pane.list.firstElementChild : pane.list.lastElementChild)?.remove();
  if (fromTop) s.scrollTop = Math.max(0, top - (before - s.scrollHeight));
}

// Never more than one request at a time; a late response for a group no longer shown is discarded.
async function loadEntryPage(ed, direction) {
  if (pane.ed !== ed || pane.controller || pane.failed) return;
  if (!WIDE_EDITOR.matches) return;
  const prev = direction === 'prev';
  if (prev ? pane.start === 0 : pane.done) return;
  const offset = prev ? Math.max(0, pane.start - MAX_SEARCH_RESULTS) : pane.end;
  const limit = prev ? pane.start - offset : MAX_SEARCH_RESULTS;
  const controller = new AbortController();
  pane.controller = controller;
  pane.status.replaceChildren(h('p', { class: 'search-status', text: pane.end ? 'Loading more…' : 'Loading entries…' }));
  try {
    const query = new URLSearchParams({ cacheKey: ed.cacheKey, group: pane.group.key, offset: String(offset), limit: String(limit) });
    const page = await api(`/playlist/group-entries?${query}`, { signal: controller.signal });
    if (pane.controller !== controller) return;
    pane.controller = null;
    const items = (page.names || []).map((name) => h('li', { text: name }));
    const s = pane.scroller;
    if (prev) {
      const before = s.scrollHeight;
      pane.list.prepend(...items);
      s.scrollTop += s.scrollHeight - before;
      pane.pages.unshift(items.length);
      pane.start = offset;
      if (pane.pages.length > ENTRY_PANE_PAGES) {
        const n = pane.pages.pop();
        dropEntries(n, false);
        pane.end -= n;
        pane.done = false;
      }
    } else {
      pane.list.append(...items);
      pane.pages.push(items.length);
      pane.end = offset + items.length;
      pane.done = items.length === 0 || pane.end >= page.total;
      if (pane.pages.length > ENTRY_PANE_PAGES) {
        const n = pane.pages.shift();
        dropEntries(n, true);
        pane.start += n;
      }
    }
    pane.count.textContent = paneCount(page.total, pane.group);
    pane.status.replaceChildren(page.total === 0 ? h('p', { class: 'search-status', text: 'No entries in this group.' }) : '');
    loadMoreIfNeeded(ed);
  } catch (err) {
    if (isAbort(err) || pane.controller !== controller) return;
    pane.controller = null;
    pane.failed = direction;
    pane.status.replaceChildren(h('div', { class: 'notice error', role: 'alert' },
      h('p', { text: err.message || 'The entries could not be loaded.' }),
      h('div', { class: 'row-actions' },
        h('button', { class: 'btn btn-sm', type: 'button', onclick: () => { const d = pane.failed; pane.failed = null; loadEntryPage(ed, d); } }, 'Try again'))));
  }
}

// ---------- Everything search ----------

const MAX_SEARCH_RESULTS = 50; // Limits.MaxSearchResults in api/Endpoints/EndpointSupport.cs.
let searchTimer = 0;
let searchController = null;
let highlightTimer = 0;

// Mirrors the server's limits (Limits.MinSearchQueryLength, MaxSearchQueryLength), so no request is sent in vain.
function searchHint(query) {
  if (query.length > 100) return 'Search must be 100 characters or less.';
  if ((query.match(/[\p{L}\p{N}]/gu) || []).length < 2) return 'Type at least 2 letters or digits to search every entry.';
  return '';
}

function cancelSearch() {
  clearTimeout(searchTimer);
  searchTimer = 0;
  searchController?.abort();
  searchController = null;
}

function scheduleSearch(ed) {
  cancelSearch();
  searchTimer = setTimeout(() => runSearch(ed), 300);
}

async function runSearch(ed) {
  cancelSearch();
  const query = ed.filter.query.trim();
  const hideDisabled = ed.filter.show === 'kept';
  const hint = searchHint(query);
  if (hint) {
    ed.search = { status: 'hint', query, message: hint };
    renderSearchResults(ed);
    return;
  }
  if (ed.search?.status === 'done' && ed.search.query === query && ed.search.hideDisabled === hideDisabled) {
    renderSearchResults(ed);
    return;
  }
  const controller = new AbortController();
  searchController = controller;
  ed.search = { status: 'loading', query, matches: ed.search?.matches || [], hasMore: false };
  renderSearchResults(ed);
  try {
    const result = await api('/playlist/search', {
      method: 'POST',
      body: { cacheKey: ed.cacheKey, query, disabledGroups: [...ed.disabled], hideDisabled },
      signal: controller.signal,
    });
    if (searchController !== controller) return;
    ed.search = {
      status: 'done', query, hideDisabled, matches: result.matches || [], hasMore: result.hasMore === true,
      hiddenCount: hideDisabled && Number.isInteger(result.hiddenCount) ? result.hiddenCount : 0,
    };
  } catch (err) {
    if (isAbort(err) || searchController !== controller) return;
    ed.search = { status: 'error', query, message: err.message || 'Search failed.' };
  } finally {
    if (searchController === controller) searchController = null;
  }
  renderSearchResults(ed);
}

function renderSearchResults(ed) {
  const panel = picker.results;
  if (!panel || picker.ed !== ed) return;
  const s = ed.search || { status: 'hint', message: searchHint('') };
  const status = (text) => h('p', { class: 'search-status', role: 'status', text });
  if (s.status === 'hint') {
    panel.replaceChildren(status(s.message));
    return;
  }
  if (s.status === 'error') {
    panel.replaceChildren(h('div', { class: 'notice error', role: 'alert' },
      h('p', { text: s.message }),
      h('div', { class: 'row-actions' },
        h('button', { class: 'btn btn-sm', type: 'button', onclick: () => loadGroups(ed, ed.sourceUrl, { sample: ed.isSample }) }, 'Analyse again'),
        // A list too large for the index stays too large after a new analysis; the group search still works.
        h('button', { class: 'btn btn-sm', type: 'button', onclick: () => picker.modes?.querySelector('[data-value="groups"]')?.click() }, 'Search groups'))));
    return;
  }
  const matches = s.matches.slice(0, MAX_SEARCH_RESULTS);
  const hidden = s.status === 'done' ? s.hiddenCount || 0 : 0;
  let text = 'Nothing matches your search.';
  if (s.status === 'loading') text = 'Searching…';
  else if (matches.length && hidden > 0) text = `${plural(matches.length, 'match', 'matches')} · ${fmt(hidden)} in switched-off groups not shown`;
  else if (matches.length) text = plural(matches.length, 'match', 'matches');
  else if (hidden > 0) text = `All ${plural(hidden, 'match', 'matches')} are in switched-off groups. Choose All to see them.`;
  const nodes = [status(text)];
  if (matches.length) nodes.push(h('ul', { class: 'search-list' }, matches.map((m) => searchHit(ed, m))));
  if (s.status === 'done' && s.hasMore) nodes.push(h('p', { class: 'search-more', text: 'More matches, refine your search.' }));
  panel.replaceChildren(...nodes);
  syncSearchBadges(ed);
}

function searchHit(ed, m) {
  const type = CONTENT_TYPES.includes(m.contentType) ? m.contentType : 'Live';
  return h('li', null, h('button', {
    class: 'search-hit',
    type: 'button',
    dataset: { type, groupKey: m.groupKey },
    onclick: () => jumpToGroup(ed, m.groupKey),
  },
  h('span', { class: 'dot', 'aria-hidden': 'true' }),
  h('span', { class: 'search-hit-text' },
    h('span', { class: 'search-hit-name', text: m.name }),
    h('span', { class: 'search-hit-meta', text: `${m.group} · No. ${fmt(m.position)} · ${type}` })),
  h('span', { class: 'search-badge' })));
}

// The badges follow the switches in the list, so they stay right after a change made since the search.
function syncSearchBadges(ed) {
  if (!picker.results || picker.ed !== ed) return;
  for (const hit of picker.results.querySelectorAll('.search-hit')) {
    const off = ed.disabled.has(hit.dataset.groupKey);
    const badge = hit.querySelector('.search-badge');
    badge.textContent = off ? 'Off' : 'On';
    badge.classList.toggle('off', off);
  }
}

function jumpToGroup(ed, key) {
  if (picker.ed !== ed) return;
  const showChanged = ed.filter.show !== 'all';
  ed.filter.type = 'all';
  ed.filter.show = 'all';
  ed.pinned.clear();
  for (const pills of [picker.typeButtons, picker.showButtons]) {
    for (const b of pills?.children || []) b.setAttribute('aria-pressed', String(b.dataset.value === 'all'));
  }
  updateView(false);
  // Show is now All, so the Everything search lists the matches it left out under On as well.
  if (showChanged && ed.searchMode === 'everything') runSearch(ed);
  const index = ed.view.findIndex((g) => g.key === key);
  if (index < 0) {
    toast('That group is not in the list.', { error: true });
    return;
  }
  const listTop = picker.list.getBoundingClientRect().top + window.scrollY;
  const reduceMotion = window.matchMedia('(prefers-reduced-motion: reduce)').matches;
  window.scrollTo({ top: listTop + (index - 1) * picker.rowHeight - stickyOffset(), behavior: reduceMotion ? 'auto' : 'smooth' });
  clearTimeout(highlightTimer);
  picker.highlight = key;
  paintRows();
  highlightTimer = setTimeout(() => {
    if (picker.highlight !== key) return;
    picker.highlight = null;
    paintRows();
  }, 2000);
}

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
  syncAdultSwitch(ed);
  syncSearchBadges(ed);
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

// ---------- Duplicates & variants report (read-only) ----------

// Only shows what /playlist/report finds; it never touches ed.disabled, ed.extras or the download.
let reportRequest = 0;

async function openReport(ed) {
  const request = ++reportRequest;
  menuTitle.textContent = 'Duplicates & variants';
  menuBody.replaceChildren(h('div', { class: 'loading-card' }, h('span', { class: 'spinner', 'aria-hidden': 'true' }), h('span', { class: 'grow', text: 'Checking the playlist…' })));
  if (!menuDialog.open) menuDialog.showModal();
  let report;
  try {
    report = await api('/playlist/report', {
      method: 'POST',
      body: { cacheKey: ed.cacheKey, sourceUrl: ed.isSample ? null : ed.sourceUrl },
    });
  } catch (err) {
    if (request !== reportRequest || !menuDialog.open) return;
    menuBody.replaceChildren(h('div', { class: 'notice error' }, h('p', { text: err.message || 'Could not check the playlist.' })));
    return;
  }
  if (request !== reportRequest || !menuDialog.open) return;
  menuBody.replaceChildren(...reportNodes(report));
}

function reportNodes(report) {
  const duplicates = report.duplicateSets || [];
  const variants = report.variantGroups || [];
  const nodes = [h('p', { class: 'field-hint', text: 'Only a report: nothing is switched off or removed.' }),
    h('dl', { class: 'report-counts' },
      reportCount(report.duplicateSetCount, 'Duplicate sets', plural(report.duplicateEntryCount, 'entry', 'entries')),
      reportCount(report.variantGroupCount, 'Variant groups', plural(report.variantEntryCount, 'entry', 'entries')),
      reportCount(report.totalEntries, 'Entries checked', ''))];

  if (!report.duplicateSetCount && !report.variantGroupCount) {
    nodes.push(h('p', { class: 'report-empty', text: 'No duplicates or variants found' }));
    return nodes;
  }
  if (report.truncated) {
    nodes.push(h('div', { class: 'notice', text: `Showing the first ${fmt(Math.max(duplicates.length, variants.length))} sets of each kind. The counts cover the whole playlist.` }));
  }
  if (duplicates.length) {
    nodes.push(h('h3', { class: 'report-heading', text: 'Duplicates' }),
      h('p', { class: 'field-hint', text: 'Same tvg-id or same stream address.' }),
      h('ol', { class: 'report-sets' }, duplicates.map((set) => reportSet(null, set))));
  }
  if (variants.length) {
    nodes.push(h('h3', { class: 'report-heading', text: 'Variants' }),
      h('p', { class: 'field-hint', text: 'Same name with different quality words.' }),
      h('ol', { class: 'report-sets' }, variants.map((group) => reportSet(group.baseName, group))));
  }
  return nodes;
}

function reportCount(value, label, sub) {
  return h('div', null,
    h('dt', { text: label }),
    h('dd', { class: 'num' }, fmt(value), sub ? h('small', { text: sub }) : null));
}

function reportSet(title, set) {
  const groups = set.groups || [];
  return h('li', { class: 'report-set' },
    title ? h('div', { class: 'report-set-title', text: title }) : null,
    h('div', { class: 'report-set-groups', text: `In ${groups.length === 1 ? groups[0] || 'no group' : plural(groups.length, 'group')}` }),
    h('ul', null, (set.entries || []).map((entry) => h('li', null,
      h('span', { class: 'report-name', text: entry.name || 'Untitled' }),
      ...(entry.qualityWords || []).map((word) => h('span', { class: 'report-word', text: word })),
      groups.length > 1 ? h('small', { text: entry.group || 'No group' }) : null))));
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
  discardBulkUndo();

  const existing = ed.id ? state.playlists.find((p) => p.id === ed.id) : null;
  const body = {
    name,
    sourceUrl: ed.sourceUrl,
    sourceName: existing?.sourceName || sourceHost(ed.sourceUrl),
    disabledGroups,
    totalChannels: loaded ? ed.totalChannels : existing?.totalChannels,
    groupCount: loaded ? ed.groups.length : existing?.groupCount,
    expirationUtc: (loaded ? ed.expirationUtc : existing?.expirationUtc) || null,
    // Without a fresh account answer the fields are left out, so the API keeps the saved ones.
    ...(loaded && ed.account ? ed.account : {}),
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

  // The external sign-in callback comes back with ?auth=failed when the provider refused.
  const params = new URLSearchParams(location.search);
  if (params.get('auth') === 'failed') {
    params.delete('auth');
    const query = params.toString();
    history.replaceState(null, '', `${location.pathname}${query ? `?${query}` : ''}${location.hash}`);
    toast('Sign-in did not complete. Try again.', { error: true });
  }
}

init();
