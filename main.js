const state = {
  groups: new Map(),
  disabledGroups: new Set(),
  sourceName: 'untitled',
  savedPlaylists: [],
  currentPlaylistId: null,
  backendSession: null,
  localPlaylist: null,
  totalChannels: 0,
  groupCount: 0,
  groupEntries: [],
  groupFilter: '',
  lastOutput: '',
  shareCode: null,
  currentUser: null,
  isAuthenticated: false,
  authProviders: [],
  filterSourceSelection: null,
};

const statusPill = document.getElementById('status-pill');
const stats = document.getElementById('stats');
const groupsGrid = document.getElementById('groups');
const groupSearchInput = document.getElementById('group-search');
const outputSize = document.getElementById('output-size');
const shareUrlInput = document.getElementById('share-url');
const playlistNameInput = document.getElementById('playlist-name');
const savedListContainer = document.getElementById('saved-list');
const savedEmptyState = document.getElementById('saved-empty');
const authState = document.getElementById('auth-state');
const authHint = document.getElementById('auth-hint');
const authUserInput = document.getElementById('auth-username');
const authPassInput = document.getElementById('auth-password');
const authLoginButton = document.getElementById('auth-login');
const authRegisterButton = document.getElementById('auth-register');
const authLogoutButton = document.getElementById('auth-logout');
const authProviderButtons = document.querySelectorAll('[data-auth-provider]');
const filterSourceWrap = document.getElementById('filter-source-wrap');
const filterSourceTrigger = document.getElementById('filter-source-trigger');
const filterSourceMenu = document.getElementById('filter-source-menu');
const applyFilterSourceButton = document.getElementById('apply-filter-source');

const samplePlaylist = `#EXTM3U
#EXTINF:-1 tvg-name="News HD" group-title="News" tvg-logo="https://placehold.co/40x40",Global News HD
http://example.com/streams/global-news.m3u8
#EXTINF:-1 tvg-name="Sports 1" group-title="Sports" tvg-logo="https://placehold.co/40x40",Sports One
http://example.com/streams/sports1.m3u8
#EXTINF:-1 tvg-name="Sports 2" group-title="Sports" tvg-logo="https://placehold.co/40x40",Sports Two
http://example.com/streams/sports2.m3u8
#EXTINF:-1 tvg-name="Kids" group-title="Family" tvg-logo="https://placehold.co/40x40",Kids Central
http://example.com/streams/kids.m3u8
#EXTINF:-1 tvg-name="Drama" group-title="Entertainment" tvg-logo="https://placehold.co/40x40",Drama Now
http://example.com/streams/drama.m3u8`;

const fetchButton = document.getElementById('fetch-playlist');
const loadSampleButton = document.getElementById('load-sample');
const toggleAllButton = document.getElementById('toggle-all');
const refreshButton = document.getElementById('refresh-output');
const copyButton = document.getElementById('copy-output');
const downloadButton = document.getElementById('download-output');
const serveButton = document.getElementById('serve-output');
const savePlaylistButton = document.getElementById('save-playlist');
const authTrigger = document.getElementById('auth-trigger');
const authTriggerLabel = document.getElementById('auth-trigger-label');
const authModal = document.getElementById('auth-modal');
const authCloseButton = document.getElementById('auth-close');
const authBackdrop = authModal ? authModal.querySelector('.auth-backdrop') : null;

const savedApiBase = (() => {
  try {
    return localStorage.getItem('strim.apiBase') || '';
  } catch {
    return '';
  }
})();
const queryApiBase = new URLSearchParams(window.location.search).get('api') || '';
const API_BASE = queryApiBase || window.STRIM_API_BASE || savedApiBase || '/api';
if (queryApiBase) {
  try {
    localStorage.setItem('strim.apiBase', queryApiBase);
  } catch {
    // ignore
  }
}

fetchButton.addEventListener('click', () => {
  const urlInput = document.getElementById('playlist-url');
  const url = urlInput.value.trim();
  if (!url) {
    setStatus('Enter a URL to fetch', 'warn');
    return;
  }
  setStatus('Fetching playlist…', 'info');
  loadPlaylistFromSource(url);
});

loadSampleButton.addEventListener('click', () => {
  hydrateLocalPlaylist(samplePlaylist, 'sample.m3u', '', {
    name: 'Sample playlist',
    sourceUrl: 'https://example.com/sample.m3u',
  });
});

refreshButton.addEventListener('click', async () => {
  await updateOutput();
});

copyButton.addEventListener('click', async () => {
  try {
    // Ensure we copy the up-to-date filtered output.
    const text = await updateOutput();
    await navigator.clipboard.writeText(text || '');
    setStatus('Copied filtered playlist to clipboard', 'success');
  } catch (err) {
    console.error(err);
    setStatus('Clipboard failed. You can still download the file.', 'warn');
  }
});

downloadButton.addEventListener('click', async () => {
  setStatus('Generating filtered playlist…', 'info');
  // Prefer backend-streamed download when a saved playlist exists.
  if (state.isAuthenticated && state.currentPlaylistId && state.shareCode && API_BASE) {
    const link = document.createElement('a');
    link.href = buildShareUrl();
    link.download = `${state.sourceName || 'playlist'}-filtered.m3u`;
    link.rel = 'noopener';
    link.click();
    setStatus('Download started', 'success');
    return;
  }
  // Fallback to client-side generation.
  const text = await updateOutput();
  const blob = new Blob([text || ''], { type: 'application/x-mpegURL' });
  const link = document.createElement('a');
  link.href = URL.createObjectURL(blob);
  link.download = `${state.sourceName || 'playlist'}-filtered.m3u`;
  link.click();
  URL.revokeObjectURL(link.href);
  setStatus('Download started', 'success');
});

serveButton.addEventListener('click', async () => {
  if (!state.isAuthenticated) {
    setStatus('Sign in to generate shareable URLs.', 'warn');
    return;
  }
  if (!state.currentPlaylistId || !state.shareCode) {
    setStatus('Save the playlist first to get a shareable URL.', 'warn');
    return;
  }
  const url = buildShareUrl();
  shareUrlInput.value = url;
  shareUrlInput.focus();
  shareUrlInput.select();
  setStatus('Shareable URL ready', 'success');
});

if (savePlaylistButton) {
  savePlaylistButton.addEventListener('click', async () => {
    await handleSavePlaylist();
  });
}

if (savedListContainer) {
  savedListContainer.addEventListener('click', (e) => {
    const target = e.target.closest('button[data-action]');
    if (!target) return;
    const { action, id } = target.dataset;
    if (!id) return;
    if (action === 'load') {
      loadSavedPlaylist(id);
    } else if (action === 'delete') {
      deleteSavedPlaylist(id);
    }
  });
}

if (filterSourceTrigger) {
  filterSourceTrigger.addEventListener('click', () => toggleFilterSourceMenu());
}

if (filterSourceMenu) {
  filterSourceMenu.addEventListener('click', (e) => {
    const option = e.target.closest('[data-id]');
    if (!option) return;
    const id = option.dataset.id;
    selectFilterSource(id);
    closeFilterSourceMenu();
  });
}

if (applyFilterSourceButton) {
  applyFilterSourceButton.addEventListener('click', () => applyFiltersFromPlaylist());
}

document.addEventListener('click', (e) => {
  if (!filterSourceWrap) return;
  if (filterSourceWrap.contains(e.target)) return;
  closeFilterSourceMenu();
});

document.addEventListener('keydown', (e) => {
  if (e.key === 'Escape') {
    closeFilterSourceMenu();
  }
});

if (authLoginButton) {
  authLoginButton.addEventListener('click', async (e) => {
    e.preventDefault();
    await handleAuthSubmit('login');
  });
}

if (authRegisterButton) {
  authRegisterButton.addEventListener('click', async (e) => {
    e.preventDefault();
    await handleAuthSubmit('register');
  });
}

if (authLogoutButton) {
  authLogoutButton.addEventListener('click', async (e) => {
    e.preventDefault();
    await handleLogout();
  });
}

if (authProviderButtons && authProviderButtons.length) {
  authProviderButtons.forEach((btn) => {
    btn.addEventListener('click', (e) => {
      e.preventDefault();
      const provider = btn.dataset.authProvider;
      if (provider) startExternalProvider(provider);
    });
  });
}

if (authTrigger) {
  authTrigger.addEventListener('click', () => {
    openAuthModal();
  });
}

if (authCloseButton) {
  authCloseButton.addEventListener('click', () => closeAuthModal());
}

if (authBackdrop) {
  authBackdrop.addEventListener('click', () => closeAuthModal());
}

document.addEventListener('keydown', (e) => {
  if (e.key === 'Escape') {
    closeAuthModal();
  }
});

toggleAllButton.addEventListener('click', () => {
  const shouldEnableAll = state.disabledGroups.size === state.groups.size;
  state.disabledGroups = new Set();

  if (!shouldEnableAll) {
    state.groups.forEach((_, key) => state.disabledGroups.add(key));
  }

  renderGroups();
  renderStats();
  updateToggleAllLabel();
  // We intentionally do not regenerate output automatically to keep toggling fast for huge lists.
});

if (groupSearchInput) {
  groupSearchInput.addEventListener('input', (e) => {
    state.groupFilter = e.target.value || '';
    if (groupsGrid) groupsGrid.scrollTop = 0;
    renderGroups();
  });
}

function setStatus(text, tone = 'info') {
  statusPill.textContent = text;
  statusPill.className = 'pill';
  statusPill.classList.add(`tone-${tone}`);
}

function openAuthModal() {
  if (!authModal) return;
  authModal.classList.remove('hidden');
}

function closeAuthModal() {
  if (!authModal) return;
  authModal.classList.add('hidden');
}

function updateAuthUi() {
  if (authState) {
    authState.textContent = state.isAuthenticated
      ? `Signed in${state.currentUser?.userName ? ` as ${state.currentUser.userName}` : ''}`
      : 'Guest session';
  }
  if (authHint) {
    authHint.textContent = state.isAuthenticated
      ? 'Saved playlists stay with your account and can be shared.'
      : 'Signed-out sessions are temporary; refresh clears your playlist.';
  }
  if (authLogoutButton) authLogoutButton.style.display = state.isAuthenticated ? 'inline-flex' : 'none';
  if (authLoginButton) authLoginButton.style.display = state.isAuthenticated ? 'none' : 'inline-flex';
  if (authRegisterButton) authRegisterButton.style.display = state.isAuthenticated ? 'none' : 'inline-flex';
  if (authUserInput) authUserInput.disabled = state.isAuthenticated;
  if (authPassInput) authPassInput.disabled = state.isAuthenticated;
  if (authProviderButtons && authProviderButtons.length) {
    authProviderButtons.forEach((btn) => {
      const provider = btn.dataset.authProvider;
      const enabled = state.authProviders.some((p) => p.name === provider);
      btn.style.display = enabled ? 'inline-flex' : 'none';
      btn.disabled = state.isAuthenticated || !enabled;
    });
  }
  if (serveButton) {
    serveButton.disabled = !state.isAuthenticated || !state.currentPlaylistId || !state.shareCode;
  }
  if (authTriggerLabel) {
    authTriggerLabel.textContent = state.isAuthenticated
      ? state.currentUser?.userName || 'Account'
      : 'Sign in';
  }
  updateSaveButtonLabel();
}

async function fetchAuthProviders() {
  if (!API_BASE) return;
  try {
    const providers = await apiRequest('/auth/providers');
    state.authProviders = Array.isArray(providers) ? providers : [];
  } catch (err) {
    console.warn('Failed to load auth providers', err);
    state.authProviders = [];
  }
  updateAuthUi();
}

async function fetchAuthState() {
  if (!API_BASE) {
    state.isAuthenticated = false;
    state.currentUser = null;
    updateAuthUi();
    return;
  }
  try {
    const me = await apiRequest('/auth/me');
    state.currentUser = me;
    state.isAuthenticated = true;
  } catch (err) {
    state.currentUser = null;
    state.isAuthenticated = false;
  }
  updateAuthUi();
}

async function handleAuthSubmit(mode) {
  if (!API_BASE) {
    setStatus('Backend not configured for authentication.', 'warn');
    return;
  }
  const username = (authUserInput && authUserInput.value.trim()) || '';
  const password = (authPassInput && authPassInput.value) || '';
  if (!username || !password) {
    setStatus('Enter a username and password.', 'warn');
    return;
  }

  try {
    const path = mode === 'register' ? '/auth/register' : '/auth/login';
    await apiRequest(path, {
      method: 'POST',
      body: {
        userName: username,
        password,
        email: username.includes('@') ? username : undefined,
      },
    });
    if (authPassInput) authPassInput.value = '';
    setStatus(mode === 'register' ? 'Account created' : 'Signed in', 'success');
    await fetchAuthState();
    await loadSavedPlaylists();
    closeAuthModal();
  } catch (err) {
    console.error('Authentication failed', err);
    setStatus(err?.message || 'Authentication failed', 'warn');
  }
}

async function handleLogout() {
  try {
    await apiRequest('/auth/logout', { method: 'POST' });
  } catch (err) {
    console.warn('Logout failed', err);
  }
  state.isAuthenticated = false;
  state.currentUser = null;
  state.savedPlaylists = [];
  state.currentPlaylistId = null;
  state.shareCode = null;
  state.filterSourceSelection = null;
  if (shareUrlInput) {
    shareUrlInput.value = '';
  }
  renderSavedPlaylists();
  renderFilterSources();
  updateAuthUi();
  closeAuthModal();
  setStatus('Signed out', 'info');
}

function startExternalProvider(provider) {
  if (!API_BASE) {
    setStatus('Backend not configured for authentication.', 'warn');
    return;
  }
  const enabled = state.authProviders.some((p) => p.name === provider);
  if (!enabled) {
    setStatus('Provider not configured.', 'warn');
    return;
  }
  const returnUrl = `${window.location.pathname}${window.location.search}`;
  const url = `${API_BASE}/auth/external/${provider}?returnUrl=${encodeURIComponent(returnUrl)}`;
  window.location.href = url;
}

async function loadPlaylistFromSource(url, hydrateOptions = {}) {
  if (!API_BASE) {
    setStatus('Backend URL not configured; cannot fetch playlist.', 'warn');
    return;
  }

  try {
    setStatus('Fetching and analyzing playlist via backend…', 'info');
    const payload = { sourceUrl: url };
    const providedName = playlistNameInput && playlistNameInput.value.trim();
    if (providedName) {
      payload.sourceName = providedName;
    }
    const analysis = await apiRequest('/playlist/analyze', { method: 'POST', body: payload });
    const friendlyName = analysis.sourceName || deriveNameFromUrl(url);
    hydrateFromAnalysis(analysis, { ...hydrateOptions, sourceUrl: url, name: friendlyName });
    await updateOutput({ useWorker: false });
  } catch (err) {
    console.error('Backend fetch failed', err);
    setStatus(`Unable to fetch playlist from backend: ${err.message || err}`, 'warn');
  }
}

function hydrateFromAnalysis(analysis, options = {}) {
  const groups = new Map();
  (analysis.groups || []).forEach((g) => groups.set(g.name, g.count));
  state.groups = groups;
  state.totalChannels = analysis.totalChannels || Array.from(groups.values()).reduce((acc, val) => acc + val, 0);
  state.groupCount = analysis.groupCount || groups.size || 0;
  const restoredDisabled = (options.disabledGroups || []).filter((g) => groups.has(g));
  state.disabledGroups = new Set(restoredDisabled);
  state.currentPlaylistId = options.id || null;
  state.backendSession = {
    cacheKey: analysis.cacheKey,
    sourceUrl: analysis.sourceUrl || options.sourceUrl || '',
    totalChannels: state.totalChannels,
    groupCount: state.groupCount,
    expirationUtc: analysis.expirationUtc ? new Date(analysis.expirationUtc).toISOString() : null,
  };
  state.localPlaylist = null;
  state.lastOutput = '';
  state.shareCode = null;

  const nameChoice = options.name || analysis.sourceName || (playlistNameInput && playlistNameInput.value.trim()) || 'playlist';
  state.sourceName = nameChoice;

  if (playlistNameInput) {
    playlistNameInput.value = nameChoice;
  }
  renderGroups();
  renderStats();
  updateAuthUi();
  const noteSuffix = options.note ? ` (${options.note})` : '';
  setStatus(`Loaded ${state.totalChannels.toLocaleString()} channels from ${state.sourceName}${noteSuffix}`.trim(), 'success');
  updateSaveButtonLabel();
}

function hydrateLocalPlaylist(text, sourceName = 'playlist', note = '', options = {}) {
  const parsed = parseM3U(text);
  state.groups = parsed.groups;
  state.totalChannels = parsed.channels.length;
  state.groupCount = parsed.groups.size;
  const restoredDisabled = (options.disabledGroups || []).filter((g) => parsed.groups.has(g));
  state.disabledGroups = new Set(restoredDisabled);
  state.currentPlaylistId = options.id || null;
  state.backendSession = null;
  state.localPlaylist = {
    text,
    channels: parsed.channels,
    channelLines: parsed.channelLines,
  };
  state.lastOutput = '';
  state.shareCode = options.shareCode || null;

  const nameChoice = options.name || (playlistNameInput && playlistNameInput.value.trim()) || sourceName;
  state.sourceName = nameChoice;

  if (playlistNameInput) {
    playlistNameInput.value = nameChoice;
  }
  renderGroups();
  renderStats();
  updateAuthUi();
  const noteSuffix = note ? ` (${note})` : '';
  setStatus(`Loaded ${state.totalChannels.toLocaleString()} channels from ${state.sourceName}${noteSuffix}`.trim(), 'success');
  updateSaveButtonLabel();
}

function parseM3U(text) {
  const lines = text
    .replace(/\r\n?/g, '\n')
    .split('\n')
    .map((l) => l.trim())
    .filter(Boolean);

  const channels = [];
  const groups = new Map();
  const channelLines = [];

  for (let i = 0; i < lines.length; i++) {
    const line = lines[i];
    if (!line.startsWith('#EXTINF')) continue;
    const extinf = line;
    const url = lines[i + 1] || '';
    i++;

    const attributes = {};
    const attrRegex = /(\w[\w-]*)="([^"]*)"/g;
    let match;
    while ((match = attrRegex.exec(extinf)) !== null) {
      attributes[match[1]] = match[2];
    }

    const name = extinf.split(',').slice(1).join(',').trim() || attributes['tvg-name'] || 'Unknown';
    const groupTitle = attributes['group-title'] || 'Ungrouped';

    const hasGroup = /group-title="[^"]*"/i.test(extinf);
    const normalizedExtinf = hasGroup ? extinf : `${extinf} group-title="${groupTitle}"`;

    const channel = {
      extinf,
      normalizedExtinf,
      url,
      name,
      groupTitle,
      attributes,
    };

    channels.push(channel);
    groups.set(groupTitle, (groups.get(groupTitle) || 0) + 1);
    channelLines.push(`${normalizedExtinf}\n${url}`);
  }

  return { channels, groups, channelLines };
}

function deriveNameFromUrl(url) {
  try {
    const parsed = new URL(url);
    const lastSegment = parsed.pathname.split('/').filter(Boolean).pop();
    return lastSegment || parsed.hostname || url || 'playlist';
  } catch {
    return url || 'playlist';
  }
}

function sanitizePlaylistRecord(pl) {
  if (!pl) return null;
  return {
    id: pl.id,
    name: pl.name,
    sourceUrl: pl.sourceUrl,
    sourceName: pl.sourceName,
    disabledGroups: Array.isArray(pl.disabledGroups) ? pl.disabledGroups : [],
    totalChannels: pl.totalChannels || 0,
    groupCount: pl.groupCount || 0,
    expirationUtc: pl.expirationUtc || null,
    shareCode: pl.shareCode || null,
    createdAt: pl.createdAt,
    updatedAt: pl.updatedAt,
  };
}

function hasLoadedPlaylist() {
  return Boolean(state.backendSession || state.localPlaylist);
}

function buildShareUrl() {
  if (!state.isAuthenticated || !API_BASE || !state.currentPlaylistId || !state.shareCode) return '';
  // Ensure absolute URL even if API_BASE is a relative /api path.
  const base = API_BASE.startsWith('http')
    ? API_BASE.replace(/\/$/, '')
    : new URL(API_BASE.replace(/\/$/, ''), window.location.origin).toString();
  return `${base}/playlists/${state.currentPlaylistId}/share/${state.shareCode}`;
}

function formatExpiration(expiration) {
  if (!expiration) return 'Expiry unknown';
  const date = new Date(expiration);
  if (Number.isNaN(date.getTime())) return 'Expiry unknown';
  return date.toLocaleString(undefined, {
    year: 'numeric',
    month: 'short',
    day: 'numeric',
    hour: '2-digit',
    minute: '2-digit',
  });
}

async function apiRequest(path, { method = 'GET', body } = {}) {
  if (!API_BASE) {
    throw new Error('Backend API not configured.');
  }
  const res = await fetch(`${API_BASE}${path}`, {
    method,
    headers: {
      'Content-Type': 'application/json',
    },
    credentials: 'include',
    body: body ? JSON.stringify(body) : undefined,
  });
  if (res.status === 401) {
    const err = new Error('Unauthorized');
    err.code = 'unauthorized';
    throw err;
  }
  if (res.status === 403) {
    const err = new Error('Forbidden');
    err.code = 'forbidden';
    throw err;
  }
  if (res.status === 204) return null;
  if (!res.ok) {
    const text = await res.text();
    const err = new Error(text || res.statusText);
    err.code = res.status;
    throw err;
  }
  return res.json();
}

async function loadSavedPlaylists() {
  if (!state.isAuthenticated) {
    state.savedPlaylists = [];
    renderSavedPlaylists();
    renderFilterSources();
    return;
  }

  try {
    const data = await apiRequest('/playlists');
    state.savedPlaylists = Array.isArray(data) ? data.map(sanitizePlaylistRecord).filter(Boolean) : [];
  } catch (err) {
    console.warn('Failed to load saved playlists', err);
    state.savedPlaylists = [];
  }
  renderSavedPlaylists();
  renderFilterSources();
}

async function handleSavePlaylist() {
  if (!hasLoadedPlaylist()) {
    setStatus('Load a playlist before saving', 'warn');
    return;
  }

  if (!state.isAuthenticated) {
    setStatus('Sign in to save or share playlists', 'warn');
    return;
  }

  const name = (playlistNameInput && playlistNameInput.value.trim()) || state.sourceName || 'Untitled playlist';
  const sourceUrl = document.getElementById('playlist-url').value.trim();
  const disabledGroups = Array.from(state.disabledGroups);
  const isUpdate = Boolean(state.currentPlaylistId);
  let statusTone = 'success';
  let statusMessage = isUpdate ? 'Playlist updated' : 'Playlist saved';

  setStatus('Saving playlist…', 'info');
  const payload = {
    name,
    sourceUrl,
    sourceName: state.sourceName,
    disabledGroups,
    totalChannels: state.totalChannels,
    groupCount: state.groupCount,
    expirationUtc: state.backendSession?.expirationUtc || null,
    shareCode: state.shareCode,
  };

  let saved;
  try {
    if (state.currentPlaylistId) {
      saved = await apiRequest(`/playlists/${state.currentPlaylistId}`, { method: 'PUT', body: payload });
    } else {
      saved = await apiRequest('/playlists', { method: 'POST', body: payload });
    }
  } catch (err) {
    console.warn('API save failed', err);
    setStatus('Unable to save playlist. Please try again.', 'warn');
    return;
  }

  state.savedPlaylists = [saved, ...state.savedPlaylists.filter((p) => p.id !== saved.id)];
  state.currentPlaylistId = saved.id;
  state.shareCode = saved.shareCode || state.shareCode;
  renderSavedPlaylists();
  renderFilterSources();
  updateSaveButtonLabel();
  updateAuthUi();
  setStatus(statusMessage, statusTone);
}

async function loadSavedPlaylist(id) {
  if (!state.isAuthenticated) {
    setStatus('Sign in to load saved playlists', 'warn');
    return;
  }

  const playlist = state.savedPlaylists.find((p) => p.id === id);
  if (!playlist) return;

  const name = playlist.name || playlist.sourceName || 'playlist';
  if (playlistNameInput) playlistNameInput.value = name;
  const urlField = document.getElementById('playlist-url');
  if (urlField) urlField.value = playlist.sourceUrl || '';
  state.shareCode = playlist.shareCode || null;

  const hydrateOpts = {
    disabledGroups: playlist.disabledGroups || [],
    id: playlist.id,
    name,
    sourceUrl: playlist.sourceUrl,
    note: playlist.sourceUrl ? `saved from ${playlist.sourceUrl}` : 'saved playlist',
  };

  // Backward compatibility: if a legacy saved entry still has rawText, use it locally.
  if (playlist.rawText) {
    hydrateLocalPlaylist(playlist.rawText, name, hydrateOpts.note, hydrateOpts);
    renderSavedPlaylists();
    updateSaveButtonLabel();
    await updateOutput({ useWorker: true });
    setStatus(`Loaded saved playlist "${name}"`, 'success');
    return;
  }

  if (playlist.sourceUrl) {
    await loadPlaylistFromSource(playlist.sourceUrl, hydrateOpts);
    renderSavedPlaylists();
    updateSaveButtonLabel();
    state.shareCode = playlist.shareCode || state.shareCode;
    setStatus(`Loaded saved playlist "${name}"`, 'success');
    return;
  }

  setStatus('Saved playlist is missing a source URL', 'warn');
}

async function deleteSavedPlaylist(id) {
  if (!state.isAuthenticated) {
    setStatus('Sign in to manage saved playlists', 'warn');
    return;
  }

  try {
    await apiRequest(`/playlists/${id}`, { method: 'DELETE' });
  } catch (err) {
    console.warn('API delete failed', err);
  }
  state.savedPlaylists = state.savedPlaylists.filter((p) => p.id !== id);
  if (state.currentPlaylistId === id) {
    state.currentPlaylistId = null;
    state.shareCode = null;
    updateSaveButtonLabel();
  }
  renderSavedPlaylists();
  renderFilterSources();
  updateAuthUi();
  setStatus('Playlist deleted', 'success');
}

function renderSavedPlaylists() {
  if (!savedListContainer || !savedEmptyState) return;
  savedListContainer.replaceChildren();
  const playlists = [...state.savedPlaylists].sort((a, b) => {
    const aTime = new Date(a.updatedAt || a.createdAt || 0).getTime();
    const bTime = new Date(b.updatedAt || b.createdAt || 0).getTime();
    return bTime - aTime;
  });
  savedEmptyState.textContent = state.isAuthenticated
    ? 'No saved playlists yet.'
    : 'Sign in to sync playlists across sessions.';
  if (playlists.length === 0) {
    savedEmptyState.style.display = 'block';
    return;
  }
  savedEmptyState.style.display = 'none';

  playlists.forEach((pl) => {
    const row = document.createElement('div');
    row.className = 'saved-item';
    if (pl.id === state.currentPlaylistId) row.classList.add('active');

    const meta = document.createElement('div');
    meta.className = 'saved-meta';

    const name = document.createElement('div');
    name.className = 'saved-name';
    name.textContent = pl.name || pl.sourceName || 'Untitled playlist';

    const sub = document.createElement('div');
    sub.className = 'saved-sub';
    const filters = pl.disabledGroups ? pl.disabledGroups.length : 0;
    const parts = [];
    const channels = Number.isFinite(Number(pl.totalChannels)) ? Number(pl.totalChannels) : 0;
    const groups = Number.isFinite(Number(pl.groupCount)) ? Number(pl.groupCount) : 0;
    parts.push(formatExpiration(pl.expirationUtc));
    parts.push(`${channels.toLocaleString()} channel${channels === 1 ? '' : 's'}`);
    parts.push(`${groups.toLocaleString()} group${groups === 1 ? '' : 's'}`);
    parts.push(`${filters} filter${filters === 1 ? '' : 's'}`);
    sub.textContent = parts.join(' • ');

    meta.append(name, sub);

    const actions = document.createElement('div');
    actions.className = 'saved-actions';

    const loadBtn = document.createElement('button');
    loadBtn.className = pl.id === state.currentPlaylistId ? 'primary' : 'ghost';
    loadBtn.textContent = pl.id === state.currentPlaylistId ? 'Loaded' : 'Load';
    loadBtn.dataset.action = 'load';
    loadBtn.dataset.id = pl.id;
    loadBtn.disabled = pl.id === state.currentPlaylistId;

    const deleteBtn = document.createElement('button');
    deleteBtn.className = 'ghost';
    deleteBtn.textContent = 'Delete';
    deleteBtn.dataset.action = 'delete';
    deleteBtn.dataset.id = pl.id;

    actions.append(loadBtn, deleteBtn);
    row.append(meta, actions);
    savedListContainer.append(row);
  });
}

function updateSaveButtonLabel() {
  if (!savePlaylistButton) return;
  if (!state.isAuthenticated) {
    savePlaylistButton.textContent = 'Sign in to save';
    savePlaylistButton.disabled = true;
    return;
  }
  savePlaylistButton.disabled = !hasLoadedPlaylist();
  savePlaylistButton.textContent = state.currentPlaylistId ? 'Update playlist' : 'Save playlist';
}

function renderFilterSources() {
  if (!filterSourceWrap || !filterSourceTrigger || !filterSourceMenu) return;
  filterSourceMenu.replaceChildren();
  closeFilterSourceMenu();

  if (!state.isAuthenticated) {
    filterSourceTrigger.textContent = 'Sign in to reuse filters';
    filterSourceWrap.classList.add('disabled');
    state.filterSourceSelection = null;
    updateFilterSourceButtonState();
    return;
  }

  if (!state.savedPlaylists.length) {
    filterSourceTrigger.textContent = 'No saved playlists yet';
    filterSourceWrap.classList.add('disabled');
    state.filterSourceSelection = null;
    updateFilterSourceButtonState();
    return;
  }

  filterSourceWrap.classList.remove('disabled');

  state.savedPlaylists.forEach((pl) => {
    const option = document.createElement('div');
    option.className = 'select-option';
    option.setAttribute('role', 'option');
    option.dataset.id = pl.id;
    const name = document.createElement('span');
    name.textContent = pl.name || pl.sourceName || 'Untitled';
    const meta = document.createElement('small');
    const filterCount = pl.disabledGroups ? pl.disabledGroups.length : 0;
    meta.textContent = `${filterCount} filter${filterCount === 1 ? '' : 's'}`;
    option.append(name, meta);
    if (pl.id === state.filterSourceSelection) {
      option.classList.add('active');
    }
    filterSourceMenu.append(option);
  });

  const selected =
    state.savedPlaylists.find((p) => p.id === state.filterSourceSelection) ?? state.savedPlaylists[0];
  state.filterSourceSelection = selected?.id || null;
  if (selected) {
    const count = selected.disabledGroups ? selected.disabledGroups.length : 0;
    filterSourceTrigger.textContent = `${selected.name || selected.sourceName || 'Untitled'} • ${count} filter${count === 1 ? '' : 's'}`;
  } else {
    filterSourceTrigger.textContent = 'Select a saved playlist';
  }
  updateFilterSourceButtonState();
}

function selectFilterSource(id) {
  state.filterSourceSelection = id || null;
  renderFilterSources();
}

function updateFilterSourceButtonState() {
  const disabled = !state.filterSourceSelection || !state.isAuthenticated;
  if (applyFilterSourceButton) applyFilterSourceButton.disabled = disabled;
}

function toggleFilterSourceMenu() {
  if (!filterSourceMenu || !filterSourceWrap) return;
  if (filterSourceWrap.classList.contains('disabled')) return;
  filterSourceMenu.classList.toggle('hidden');
}

function closeFilterSourceMenu() {
  if (filterSourceMenu) {
    filterSourceMenu.classList.add('hidden');
  }
}

function applyFiltersFromPlaylist() {
  if (!state.isAuthenticated) {
    setStatus('Sign in to copy filters from a saved playlist.', 'warn');
    return;
  }
  if (state.groups.size === 0) {
    setStatus('Load a playlist before copying filters.', 'warn');
    return;
  }
  const sourceId = state.filterSourceSelection;
  if (!sourceId) {
    setStatus('Choose a playlist to copy filters from.', 'warn');
    return;
  }
  const source = state.savedPlaylists.find((p) => p.id === sourceId);
  if (!source) {
    setStatus('Could not find the selected playlist.', 'warn');
    return;
  }
  const disabled = new Set((source.disabledGroups || []).filter((g) => state.groups.has(g)));
  state.disabledGroups = disabled;
  renderGroups();
  renderStats();
  updateToggleAllLabel();
  setStatus(
    `Copied ${disabled.size} filter${disabled.size === 1 ? '' : 's'} from "${source.name || source.sourceName || 'playlist'}"`,
    'success'
  );
}

let groupsResizeObserver = null;
let windowResizeHooked = false;

function renderGroups() {
  const filterTerm = (state.groupFilter || '').trim().toLowerCase();
  const groupsArray = [...state.groups.entries()];
  const filtered = filterTerm ? groupsArray.filter(([name]) => name.toLowerCase().includes(filterTerm)) : groupsArray;
  state.groupEntries = filtered.sort((a, b) => b[1] - a[1]);

  const ITEM_HEIGHT = 56;
  const BUFFER = 6;

  groupsGrid.classList.remove('group-list');
  groupsGrid.classList.add('group-virtual');
  groupsGrid.style.position = 'relative';
  groupsGrid.style.overflowY = 'auto';
  groupsGrid.replaceChildren();

  const totalItems = state.groupEntries.length;
  if (totalItems === 0) {
    const message = document.createElement('div');
    message.className = 'hint';
    message.textContent = filterTerm ? 'No groups match your search.' : 'Load a playlist to see groups.';
    groupsGrid.append(message);
    return;
  }

  const spacer = document.createElement('div');
  spacer.className = 'group-spacer';
  spacer.style.height = `${totalItems * ITEM_HEIGHT}px`;
  spacer.style.width = '100%';
  spacer.style.pointerEvents = 'none';

  const itemsLayer = document.createElement('div');
  itemsLayer.className = 'group-items-layer';
  itemsLayer.style.position = 'absolute';
  itemsLayer.style.top = '0';
  itemsLayer.style.left = '0';
  itemsLayer.style.right = '0';

  groupsGrid.append(spacer, itemsLayer);

  const containerHeight = Math.max(groupsGrid.clientHeight, 300);
  const visibleCount = Math.ceil(containerHeight / ITEM_HEIGHT);
  const poolSize = Math.min(totalItems, visibleCount + BUFFER * 2);

  const pool = [];
  for (let i = 0; i < poolSize; i++) {
    const node = document.createElement('div');
    node.className = 'group-list-item';
    node.style.position = 'absolute';
    node.style.height = `${ITEM_HEIGHT - 6}px`;
    node.style.left = '0';
    node.style.right = '0';
    node.dataset.poolIndex = i;

    const info = document.createElement('div');
    info.className = 'group-info';
    info.style.display = 'flex';
    info.style.flexDirection = 'column';

    const title = document.createElement('span');
    title.className = 'group-title';
    title.style.fontSize = '14px';
    title.style.fontWeight = '600';

    const count = document.createElement('span');
    count.className = 'group-count';
    count.style.fontSize = '12px';
    count.style.color = 'var(--muted)';

    info.append(title, count);

    const switchLabel = document.createElement('label');
    switchLabel.className = 'switch';
    const checkbox = document.createElement('input');
    checkbox.type = 'checkbox';
    checkbox.className = 'group-toggle';
    const slider = document.createElement('span');
    slider.className = 'slider';
    switchLabel.append(checkbox, slider);

    node.append(info, switchLabel);
    pool.push({ node, title, count, checkbox });
    itemsLayer.append(node);
  }

  let lastStart = -1;
  function renderSlice(startIndex) {
    startIndex = Math.max(0, Math.min(startIndex, Math.max(0, totalItems - poolSize)));
    if (startIndex === lastStart) return;
    lastStart = startIndex;
    for (let i = 0; i < pool.length; i++) {
      const idx = startIndex + i;
      const poolItem = pool[i];
      if (idx >= totalItems) {
        poolItem.node.style.display = 'none';
        continue;
      }
      const [groupTitle, cnt] = state.groupEntries[idx];
      poolItem.node.style.display = 'flex';
      poolItem.node.style.transform = `translateY(${idx * ITEM_HEIGHT}px)`;
      poolItem.title.textContent = groupTitle;
      poolItem.count.textContent = `${cnt.toLocaleString()} channel${cnt === 1 ? '' : 's'}`;
      const disabled = state.disabledGroups.has(groupTitle);
      poolItem.checkbox.checked = !disabled;
      poolItem.node.classList.toggle('disabled', disabled);
      poolItem.checkbox.dataset.index = idx;
    }
  }

  renderSlice(0);

  let scrollTick = null;
  groupsGrid.onscroll = () => {
    if (scrollTick) return;
    scrollTick = requestAnimationFrame(() => {
      const scrollTop = groupsGrid.scrollTop;
      const start = Math.floor(scrollTop / ITEM_HEIGHT) - BUFFER;
      renderSlice(start);
      scrollTick = null;
    });
  };

  if (!groupsResizeObserver && window.ResizeObserver) {
    groupsResizeObserver = new ResizeObserver(() => {
      renderGroups();
    });
    groupsResizeObserver.observe(groupsGrid);
  } else if (!window.ResizeObserver && !windowResizeHooked) {
    window.addEventListener('resize', () => renderGroups());
    windowResizeHooked = true;
  }

  itemsLayer.onchange = (e) => {
    const target = e.target;
    if (!target.classList.contains('group-toggle')) return;
    const idx = Number(target.dataset.index);
    const [groupTitle] = state.groupEntries[idx] || [];
    if (!groupTitle) return;
    if (target.checked) {
      state.disabledGroups.delete(groupTitle);
    } else {
      state.disabledGroups.add(groupTitle);
    }
    renderStats();
    updateToggleAllLabel();
    const poolIdx = idx - lastStart;
    if (poolIdx >= 0 && poolIdx < pool.length) {
      pool[poolIdx].node.classList.toggle('disabled', state.disabledGroups.has(groupTitle));
    }
  };

  updateToggleAllLabel();
}

function updateToggleAllLabel() {
  toggleAllButton.textContent = state.disabledGroups.size === state.groups.size ? 'Enable all' : 'Disable none';
}

function renderStats() {
  stats.innerHTML = '';
  const total = state.totalChannels || 0;
  const disabled = state.disabledGroups.size;
  const groupsTotal = state.groupCount || state.groups.size;
  const keptGroups = Math.max(0, groupsTotal - disabled);

  const cards = [
    { label: 'Channels', value: total },
    { label: 'Groups enabled', value: keptGroups },
    { label: 'Groups disabled', value: disabled },
  ];

  cards.forEach((card) => {
    const div = document.createElement('div');
    div.className = 'stat-card';
    div.innerHTML = `<div class="eyebrow">${card.label}</div><div class="stat-value">${card.value}</div>`;
    stats.append(div);
  });
}

function generateFilteredM3U() {
  if (!state.localPlaylist) return '';
  const disabled = state.disabledGroups;
  const outputLines = ['#EXTM3U'];

  let kept = 0;
  for (let i = 0; i < state.localPlaylist.channels.length; i++) {
    const channel = state.localPlaylist.channels[i];
    if (disabled.has(channel.groupTitle)) continue;
    outputLines.push(state.localPlaylist.channelLines[i]);
    kept++;
  }

  outputSize.textContent = `${kept} channel${kept === 1 ? '' : 's'}`;
  return outputLines.join('\n');
}

async function updateOutput({ useWorker = true } = {}) {
  if (state.backendSession && API_BASE) {
    try {
      setStatus('Generating filtered playlist on server…', 'info');
      setControlsDisabled(true);
      const body = {
        cacheKey: state.backendSession.cacheKey,
        sourceUrl: state.backendSession.sourceUrl,
        disabledGroups: Array.from(state.disabledGroups),
      };
      const res = await apiRequest('/playlist/generate', { method: 'POST', body });
      state.lastOutput = res.filteredText || '';
      const channelCount = res.keptChannels ?? (res.filteredText ? (res.filteredText.match(/\nhttps?:\/\//g) || []).length : 0);
      outputSize.textContent = `${channelCount} channel${channelCount === 1 ? '' : 's'}`;
      state.totalChannels = res.totalChannels || state.totalChannels;
      renderStats();
      setStatus('Filtered playlist ready', 'success');
      setControlsDisabled(false);
      return state.lastOutput;
    } catch (err) {
      console.error('Backend generation failed; attempting local fallback', err);
      setStatus('Backend generation failed; falling back to local processing.', 'warn');
      setControlsDisabled(false);
      if (state.lastOutput) {
        return state.lastOutput;
      }
    }
  }

  if (useWorker && window.Worker && state.localPlaylist?.text) {
    return new Promise((resolve) => {
      setStatus('Generating filtered playlist (background)…', 'info');
      setControlsDisabled(true);
      const worker = new Worker('filter-worker.js');
      const disabledArr = Array.from(state.disabledGroups);
      worker.postMessage({ cmd: 'generate', text: state.localPlaylist.text, disabled: disabledArr });
      worker.addEventListener('message', (ev) => {
        const msg = ev.data || {};
        if (msg.type === 'progress') {
          setStatus(
            `Generating… ${Math.min(100, Math.round((msg.processed / msg.totalLines) * 100))}%`,
            'info'
          );
        } else if (msg.type === 'result') {
          state.lastOutput = msg.text;
          const channelCount = (msg.text.match(/\nhttps?:\/\//g) || []).length;
          outputSize.textContent = `${channelCount} channel${channelCount === 1 ? '' : 's'}`;
          setStatus('Filtered playlist ready', 'success');
          setControlsDisabled(false);
          worker.terminate();
          resolve(msg.text);
        }
      });
      worker.addEventListener('error', (err) => {
        console.error('Worker error', err);
        setControlsDisabled(false);
        worker.terminate();
        setStatus('Worker failed; falling back to synchronous generation.', 'warn');
        const text = generateFilteredM3U();
        state.lastOutput = text;
        setStatus('Filtered playlist ready', 'success');
        resolve(text);
      });
    });
  }

  const text = generateFilteredM3U();
  state.lastOutput = text;
  setStatus(text ? 'Filtered playlist ready' : 'Load a playlist to generate output', text ? 'success' : 'warn');
  return text;
}

function setControlsDisabled(disabled) {
  [downloadButton, copyButton, refreshButton].forEach((b) => {
    if (b) b.disabled = disabled;
  });
  if (serveButton) {
    serveButton.disabled = disabled || !state.isAuthenticated || !state.currentPlaylistId || !state.shareCode;
  }
}

async function init() {
  await fetchAuthProviders();
  await fetchAuthState();
  await loadSavedPlaylists();
  hydrateLocalPlaylist(samplePlaylist, 'sample.m3u');
  await updateOutput({ useWorker: false });
}

init().catch((err) => {
  console.error('Failed to initialize app', err);
  setStatus('Failed to load saved playlists', 'warn');
  hydrateLocalPlaylist(samplePlaylist, 'sample.m3u');
  updateOutput({ useWorker: false });
});
