const state = {
  channels: [],
  groups: new Map(),
  disabledGroups: new Set(),
  sourceName: 'untitled',
  savedPlaylists: [],
  currentPlaylistId: null,
  rawText: '',
  channelLines: [],
  groupEntries: [],
};

const statusPill = document.getElementById('status-pill');
const stats = document.getElementById('stats');
const groupsGrid = document.getElementById('groups');
const outputArea = document.getElementById('output');
const outputSize = document.getElementById('output-size');
const shareUrlInput = document.getElementById('share-url');
const playlistNameInput = document.getElementById('playlist-name');
const playlistSourceInput = document.getElementById('playlist-source');
const savedListContainer = document.getElementById('saved-list');
const savedEmptyState = document.getElementById('saved-empty');

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
const parseRawButton = document.getElementById('parse-raw');
const loadSampleButton = document.getElementById('load-sample');
const toggleAllButton = document.getElementById('toggle-all');
const refreshButton = document.getElementById('refresh-output');
const copyButton = document.getElementById('copy-output');
const downloadButton = document.getElementById('download-output');
const serveButton = document.getElementById('serve-output');
const savePlaylistButton = document.getElementById('save-playlist');

const STORAGE_KEY = 'strim.playlists.v1';

fetchButton.addEventListener('click', () => {
  const urlInput = document.getElementById('playlist-url');
  const url = urlInput.value.trim();
  if (!url) {
    setStatus('Enter a URL to fetch', 'warn');
    return;
  }
  if (playlistSourceInput) {
    playlistSourceInput.value = url;
  }
  setStatus('Fetching playlist…', 'info');
  fetchPlaylist(url);
});

parseRawButton.addEventListener('click', () => {
  const text = document.getElementById('playlist-raw').value.trim();
  if (!text) {
    setStatus('Paste M3U text to parse', 'warn');
    return;
  }
  const chosenName = (playlistNameInput && playlistNameInput.value.trim()) || 'pasted-playlist';
  const sourceUrl = (playlistSourceInput && playlistSourceInput.value.trim()) || '';
  hydrateState(text, chosenName, '', { name: chosenName, sourceUrl });
});

loadSampleButton.addEventListener('click', () => {
  document.getElementById('playlist-raw').value = samplePlaylist;
  hydrateState(samplePlaylist, 'sample.m3u', '', {
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
    await updateOutput();
    await navigator.clipboard.writeText(outputArea.value);
    setStatus('Copied filtered playlist to clipboard', 'success');
  } catch (err) {
    console.error(err);
    setStatus('Clipboard failed. You can still download the file.', 'warn');
  }
});

downloadButton.addEventListener('click', async () => {
  setStatus('Generating filtered playlist…', 'info');
  // Ensure output is up-to-date with current toggles before downloading.
  await updateOutput();
  const blob = new Blob([outputArea.value], { type: 'application/x-mpegURL' });
  const link = document.createElement('a');
  link.href = URL.createObjectURL(blob);
  link.download = `${state.sourceName || 'playlist'}-filtered.m3u`;
  link.click();
  URL.revokeObjectURL(link.href);
  setStatus('Download started', 'success');
});

serveButton.addEventListener('click', async () => {
  // Generate output on demand so it matches the current selection.
  await updateOutput();
  const blob = new Blob([outputArea.value], { type: 'application/x-mpegURL' });
  const url = URL.createObjectURL(blob);
  shareUrlInput.value = url;
  shareUrlInput.focus();
  shareUrlInput.select();
  setStatus('Shareable object URL generated. Keep this tab open.', 'info');
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

function setStatus(text, tone = 'info') {
  statusPill.textContent = text;
  statusPill.className = 'pill';
  statusPill.classList.add(`tone-${tone}`);
}

async function fetchPlaylist(url) {
  const isHttpOnHttps = window.location.protocol === 'https:' && url.startsWith('http:');

  if (isHttpOnHttps) {
    setStatus('HTTPS pages cannot fetch HTTP playlists directly. Trying through proxies…', 'warn');
  }

  const sources = [
    ...(!isHttpOnHttps
      ? [
          {
            label: 'direct',
            url,
            mode: 'cors',
          },
        ]
      : []),
    {
      label: 'local proxy',
      url: `http://localhost:8080/?url=${encodeURIComponent(url)}`,
      mode: 'cors',
    },
    {
      label: 'AllOrigins (JSON)',
      url: `https://api.allorigins.win/get?url=${encodeURIComponent(url)}`,
      mode: 'cors',
      parseJson: true,
    },
    {
      label: 'AllOrigins (raw)',
      url: `https://api.allorigins.win/raw?url=${encodeURIComponent(url)}`,
      mode: 'cors',
    },
    {
      label: 'CORS.io',
      url: `https://cors.io/?${url}`,
      mode: 'cors',
    },
    {
      label: 'ThingProxy',
      url: `https://thingproxy.freeboard.io/fetch/${url}`,
      mode: 'cors',
    },
    {
      label: 'CORS Proxy',
      url: `https://corsproxy.io/?${encodeURIComponent(url)}`,
      mode: 'cors',
    },
  ];

  let lastError;
  const errors = [];

  for (const source of sources) {
    try {
      setStatus(`Trying to fetch via ${source.label}…`, 'info');
      const controller = new AbortController();
      const timeoutId = setTimeout(() => controller.abort(), 15000);

      const res = await fetch(source.url, {
        cache: 'no-store',
        signal: controller.signal,
        mode: source.mode || 'cors',
        headers: {
          Accept: 'text/plain, application/x-mpegurl, */*',
        },
      });
      clearTimeout(timeoutId);

      if (!res.ok) throw new Error(`${res.status} ${res.statusText}`);

      let text;
      if (source.parseJson) {
        const json = await res.json();
        text = json.contents || json.data || '';
      } else {
        text = await res.text();
      }

      if (!text || (!text.includes('#EXTM3U') && !text.includes('#EXTINF'))) {
        throw new Error('Response does not appear to be an M3U playlist');
      }

      const note = source.label === 'direct' ? '' : `via ${source.label}`;
      const friendlyName = (playlistNameInput && playlistNameInput.value.trim()) || deriveNameFromUrl(url);
      hydrateState(text, friendlyName, note, { sourceUrl: url, name: friendlyName });
      return;
    } catch (error) {
      lastError = error;
      const errorMsg = error.name === 'AbortError' ? 'timeout' : error.message;
      errors.push(`${source.label}: ${errorMsg}`);
      console.warn(`Failed via ${source.label}:`, error);
    }
  }

  console.error(lastError);
  const detail = errors.length > 0 ? `\n\nAttempts: ${errors.join(' | ')}` : '';
  setStatus(
    `Unable to fetch playlist. All proxy attempts failed. Try pasting the content directly instead.${detail}`,
    'warn'
  );
}

function hydrateState(text, sourceName = 'playlist', note = '', options = {}) {
  const parsed = parseM3U(text);
  state.channels = parsed.channels;
  state.groups = parsed.groups;
  state.rawText = text;
  state.channelLines = parsed.channelLines;
  const restoredDisabled = (options.disabledGroups || []).filter((g) => parsed.groups.has(g));
  state.disabledGroups = new Set(restoredDisabled);
  state.currentPlaylistId = options.id || null;

  const nameChoice = options.name || (playlistNameInput && playlistNameInput.value.trim()) || sourceName;
  state.sourceName = nameChoice;

  if (playlistNameInput) {
    playlistNameInput.value = nameChoice;
  }
  if (playlistSourceInput && options.sourceUrl) {
    playlistSourceInput.value = options.sourceUrl;
  }
  renderGroups();
  renderStats();
  const noteSuffix = note ? ` (${note})` : '';
  setStatus(`Loaded ${state.channels.length} channels from ${state.sourceName}${noteSuffix}`.trim(), 'success');
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

function loadSavedPlaylistsFromStorage() {
  try {
    const raw = localStorage.getItem(STORAGE_KEY);
    const parsed = raw ? JSON.parse(raw) : [];
    state.savedPlaylists = Array.isArray(parsed) ? parsed : [];
  } catch (err) {
    console.warn('Failed to read saved playlists', err);
    state.savedPlaylists = [];
  }
  renderSavedPlaylists();
}

function persistSavedPlaylists() {
  try {
    localStorage.setItem(STORAGE_KEY, JSON.stringify(state.savedPlaylists));
  } catch (err) {
    console.warn('Failed to persist playlists', err);
  }
}

async function handleSavePlaylist() {
  if (!state.rawText) {
    setStatus('Load a playlist before saving', 'warn');
    return;
  }

  const name = (playlistNameInput && playlistNameInput.value.trim()) || state.sourceName || 'Untitled playlist';
  const sourceUrl =
    (playlistSourceInput && playlistSourceInput.value.trim()) ||
    document.getElementById('playlist-url').value.trim();
  const disabledGroups = Array.from(state.disabledGroups);

  setStatus('Saving playlist…', 'info');
  const filteredText = await updateOutput();
  const now = Date.now();
  const existing = state.savedPlaylists.find((p) => p.id === state.currentPlaylistId);
  const id =
    (existing && existing.id) ||
    (crypto.randomUUID ? crypto.randomUUID() : `${now}-${Math.random().toString(16).slice(2)}`);

  const record = {
    id,
    name,
    sourceUrl,
    sourceName: state.sourceName,
    rawText: state.rawText,
    disabledGroups,
    filteredText,
    updatedAt: now,
    createdAt: existing?.createdAt || now,
  };

  state.savedPlaylists = [record, ...state.savedPlaylists.filter((p) => p.id !== id)];
  state.currentPlaylistId = id;
  persistSavedPlaylists();
  renderSavedPlaylists();
  updateSaveButtonLabel();
  setStatus(existing ? 'Playlist updated' : 'Playlist saved', 'success');
}

function loadSavedPlaylist(id) {
  const playlist = state.savedPlaylists.find((p) => p.id === id);
  if (!playlist) return;

  const name = playlist.name || playlist.sourceName || 'playlist';
  if (playlistNameInput) playlistNameInput.value = name;
  if (playlistSourceInput) playlistSourceInput.value = playlist.sourceUrl || '';
  const urlField = document.getElementById('playlist-url');
  if (urlField) urlField.value = playlist.sourceUrl || '';

  hydrateState(
    playlist.rawText,
    name,
    playlist.sourceUrl ? `saved from ${playlist.sourceUrl}` : 'saved playlist',
    {
      disabledGroups: playlist.disabledGroups || [],
      id: playlist.id,
      name,
      sourceUrl: playlist.sourceUrl,
    }
  );
  renderSavedPlaylists();
  if (playlist.filteredText) {
    outputArea.value = playlist.filteredText;
    const channelCount = (playlist.filteredText.match(/\nhttps?:\/\//g) || []).length;
    outputSize.textContent = `${channelCount} channel${channelCount === 1 ? '' : 's'}`;
  }
  updateOutput({ useWorker: false });
  setStatus(`Loaded saved playlist "${name}"`, 'success');
}

function deleteSavedPlaylist(id) {
  const next = state.savedPlaylists.filter((p) => p.id !== id);
  if (next.length === state.savedPlaylists.length) return;
  state.savedPlaylists = next;
  if (state.currentPlaylistId === id) {
    state.currentPlaylistId = null;
    updateSaveButtonLabel();
  }
  persistSavedPlaylists();
  renderSavedPlaylists();
  setStatus('Playlist deleted', 'success');
}

function renderSavedPlaylists() {
  if (!savedListContainer || !savedEmptyState) return;
  savedListContainer.replaceChildren();
  const playlists = [...state.savedPlaylists].sort((a, b) => (b.updatedAt || 0) - (a.updatedAt || 0));
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
    if (pl.sourceUrl) parts.push(pl.sourceUrl);
    parts.push(`${filters} group filter${filters === 1 ? '' : 's'}`);
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
  savePlaylistButton.textContent = state.currentPlaylistId ? 'Update playlist' : 'Save playlist';
}

let groupsResizeObserver = null;
let windowResizeHooked = false;

function renderGroups() {
  state.groupEntries = [...state.groups.entries()].sort((a, b) => b[1] - a[1]);

  const ITEM_HEIGHT = 56;
  const BUFFER = 6;

  groupsGrid.classList.remove('group-list');
  groupsGrid.classList.add('group-virtual');
  groupsGrid.style.position = 'relative';
  groupsGrid.style.overflowY = 'auto';
  groupsGrid.replaceChildren();

  const totalItems = state.groupEntries.length;
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
  const total = state.channels.length;
  const disabled = state.disabledGroups.size;
  const groupsTotal = state.groups.size;
  const keptGroups = groupsTotal - disabled;

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
  const disabled = state.disabledGroups;
  const outputLines = ['#EXTM3U'];

  let kept = 0;
  for (let i = 0; i < state.channels.length; i++) {
    const channel = state.channels[i];
    if (disabled.has(channel.groupTitle)) continue;
    outputLines.push(state.channelLines[i]);
    kept++;
  }

  outputSize.textContent = `${kept} channel${kept === 1 ? '' : 's'}`;
  return outputLines.join('\n');
}

function updateOutput({ useWorker = true } = {}) {
  if (useWorker && window.Worker && state.rawText) {
    return new Promise((resolve, reject) => {
      setStatus('Generating filtered playlist (background)…', 'info');
      setControlsDisabled(true);
      const worker = new Worker('filter-worker.js');
      const disabledArr = Array.from(state.disabledGroups);
      worker.postMessage({ cmd: 'generate', text: state.rawText, disabled: disabledArr });
      worker.addEventListener('message', (ev) => {
        const msg = ev.data || {};
        if (msg.type === 'progress') {
          setStatus(
            `Generating… ${Math.min(100, Math.round((msg.processed / msg.totalLines) * 100))}%`,
            'info'
          );
        } else if (msg.type === 'result') {
          outputArea.value = msg.text;
          const channelCount = (msg.text.match(/\nhttps?:\/\//g) || []).length;
          outputSize.textContent = `${channelCount} channels`;
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
        outputArea.value = text;
        setStatus('Filtered playlist ready', 'success');
        resolve(text);
      });
    });
  }

  const text = generateFilteredM3U();
  outputArea.value = text;
  setStatus('Filtered playlist ready', 'success');
  return Promise.resolve(text);
}

function setControlsDisabled(disabled) {
  [downloadButton, copyButton, serveButton, refreshButton].forEach((b) => {
    if (b) b.disabled = disabled;
  });
}

loadSavedPlaylistsFromStorage();
updateSaveButtonLabel();
hydrateState(samplePlaylist, 'sample.m3u');
updateOutput({ useWorker: false });
