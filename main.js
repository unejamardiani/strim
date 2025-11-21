const state = {
  channels: [],
  groups: new Map(),
  disabledGroups: new Set(),
  sourceName: 'untitled',
  channelLines: [],
  groupRows: new Map(),
};

const statusPill = document.getElementById('status-pill');
const stats = document.getElementById('stats');
const groupsGrid = document.getElementById('groups');
const outputArea = document.getElementById('output');
const outputSize = document.getElementById('output-size');
const shareUrlInput = document.getElementById('share-url');

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

fetchButton.addEventListener('click', () => {
  const urlInput = document.getElementById('playlist-url');
  const url = urlInput.value.trim();
  if (!url) {
    setStatus('Enter a URL to fetch', 'warn');
    return;
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
  hydrateState(text, 'pasted-playlist');
});

loadSampleButton.addEventListener('click', () => {
  document.getElementById('playlist-raw').value = samplePlaylist;
  hydrateState(samplePlaylist, 'sample.m3u');
});

refreshButton.addEventListener('click', () => {
  updateOutput();
});

copyButton.addEventListener('click', async () => {
  try {
    await navigator.clipboard.writeText(outputArea.value);
    setStatus('Copied filtered playlist to clipboard', 'success');
  } catch (err) {
    console.error(err);
    setStatus('Clipboard failed. You can still download the file.', 'warn');
  }
});

downloadButton.addEventListener('click', () => {
  const blob = new Blob([outputArea.value], { type: 'application/x-mpegURL' });
  const link = document.createElement('a');
  link.href = URL.createObjectURL(blob);
  link.download = `${state.sourceName || 'playlist'}-filtered.m3u`;
  link.click();
  URL.revokeObjectURL(link.href);
});

serveButton.addEventListener('click', () => {
  const blob = new Blob([outputArea.value], { type: 'application/x-mpegURL' });
  const url = URL.createObjectURL(blob);
  shareUrlInput.value = url;
  shareUrlInput.focus();
  shareUrlInput.select();
  setStatus('Shareable object URL generated. Keep this tab open.', 'info');
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
    // Skip the direct attempt if we know the browser will block mixed content when the app is
    // served over HTTPS and the playlist is HTTP only.
    ...(!isHttpOnHttps ? [{ label: 'direct', url }] : []),
    {
      label: 'CORS proxy',
      url: `https://cors.isomorphic-git.org/${encodeURIComponent(url)}`,
    },
    {
      label: 'CORS proxy (fallback)',
      url: `https://api.allorigins.win/raw?url=${encodeURIComponent(url)}`,
    },
    {
      label: 'CORS proxy (alt)',
      url: `https://corsproxy.io/?${encodeURIComponent(url)}`,
    },
    {
      label: 'CORS proxy (jina.ai)',
      url: (() => {
        const stripped = url.replace(/^https?:\/\//, '');
        const scheme = url.startsWith('https:') ? 'https' : 'http';
        return `https://r.jina.ai/${scheme}://${stripped}`;
      })(),
    },
    {
      label: 'CORS proxy (thingproxy)',
      url: `https://thingproxy.freeboard.io/fetch/${url}`,
    },
  ];

  let lastError;
  const errors = [];

  for (const source of sources) {
    try {
      const res = await fetch(source.url, { cache: 'no-store' });
      if (!res.ok) throw new Error(`${res.status} ${res.statusText}`);
      const text = await res.text();
      const note = source.label === 'direct' ? '' : source.label;
      hydrateState(text, url, note);
      return;
    } catch (error) {
      lastError = error;
      errors.push(`${source.label}: ${error.message}`);
      console.warn(`Failed via ${source.label}:`, error);
    }
  }

  console.error(lastError);
  const detail = errors.length ? ` Details: ${errors.join(' | ')}` : '';
  setStatus(
    `Unable to fetch playlist (CORS or network error). Try pasting it instead.${detail}`,
    'warn'
  );
}

function hydrateState(text, sourceName = 'playlist', note = '') {
  const parsed = parseM3U(text);
  state.channels = parsed.channels;
  state.groups = parsed.groups;
  state.channelLines = parsed.channelLines;
  state.disabledGroups = new Set();
  state.sourceName = sourceName;
  renderGroups();
  renderStats();
  updateOutput();
  const noteSuffix = note ? ` (${note})` : '';
  setStatus(`Loaded ${state.channels.length} channels from ${sourceName}${noteSuffix}`.trim(), 'success');
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

function renderGroups() {
  groupsGrid.innerHTML = '';
  state.groupRows = new Map();
  const fragment = document.createDocumentFragment();

  [...state.groups.entries()]
    .sort((a, b) => b[1] - a[1])
    .forEach(([groupTitle, count]) => {
      const item = document.createElement('label');
      item.className = 'group-item';
      item.dataset.group = groupTitle;

      const checkbox = document.createElement('input');
      checkbox.type = 'checkbox';
      checkbox.className = 'group-checkbox';
      checkbox.checked = !state.disabledGroups.has(groupTitle);
      checkbox.addEventListener('change', () => handleGroupToggle(groupTitle, checkbox.checked));

      const box = document.createElement('span');
      box.className = 'checkbox-box';

      const title = document.createElement('div');
      title.className = 'group-title';
      title.textContent = groupTitle;

      const chip = document.createElement('div');
      chip.className = 'count';
      chip.textContent = `${count} channel${count === 1 ? '' : 's'}`;

      item.append(checkbox, box, title, chip);
      fragment.append(item);

      state.groupRows.set(groupTitle, { item, checkbox });
      syncGroupRowState(groupTitle);
    });

  groupsGrid.append(fragment);
  updateToggleAllLabel();
}

function handleGroupToggle(groupTitle, isChecked) {
  if (isChecked) state.disabledGroups.delete(groupTitle);
  else state.disabledGroups.add(groupTitle);
  syncGroupRowState(groupTitle);
  updateToggleAllLabel();
  renderStats();
  updateOutput();
}

function syncGroupRowState(groupTitle) {
  const ref = state.groupRows.get(groupTitle);
  if (!ref) return;
  const isDisabled = state.disabledGroups.has(groupTitle);
  ref.item.classList.toggle('disabled', isDisabled);
  ref.checkbox.checked = !isDisabled;
}

function updateToggleAllLabel() {
  toggleAllButton.textContent = state.disabledGroups.size === state.groups.size ? 'Enable all' : 'Disable none';
}

function renderStats() {
  stats.innerHTML = '';
  const total = state.channels.length;
  const disabled = [...state.disabledGroups.values()].length;
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

function updateOutput() {
  const text = generateFilteredM3U();
  outputArea.value = text;
}

toggleAllButton.addEventListener('click', () => {
  const shouldEnableAll = state.disabledGroups.size === state.groups.size;
  state.disabledGroups = new Set();

  if (!shouldEnableAll) {
    state.groups.forEach((_, key) => state.disabledGroups.add(key));
  }

  state.groupRows.forEach((_, groupTitle) => syncGroupRowState(groupTitle));
  updateToggleAllLabel();
  renderStats();
  updateOutput();
});

// Initialize with sample to give users something to see immediately.
hydrateState(samplePlaylist, 'sample.m3u');
