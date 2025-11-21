const state = {
  channels: [],
  groups: new Map(),
  disabledGroups: new Set(),
  sourceName: 'untitled',
<<<<<<< HEAD
  rawText: '',
=======
  channelLines: [],
  groupRows: new Map(),
>>>>>>> ef544c15608d31de0a9d87e24711a0a4d366aeaa
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
    // Try direct first with no CORS restrictions (won't read response but may work for same-origin)
    ...(!isHttpOnHttps ? [
      { label: 'direct', url, mode: 'cors' },
    ] : []),
    // Local CORS proxy (if running)
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
      const timeoutId = setTimeout(() => controller.abort(), 15000); // 15 second timeout
      
      const res = await fetch(source.url, { 
        cache: 'no-store',
        signal: controller.signal,
        mode: source.mode || 'cors',
        headers: {
          'Accept': 'text/plain, application/x-mpegurl, */*',
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
      
      // Basic validation that we got M3U content
      if (!text || (!text.includes('#EXTM3U') && !text.includes('#EXTINF'))) {
        throw new Error('Response does not appear to be an M3U playlist');
      }
      
      const note = source.label === 'direct' ? '' : `via ${source.label}`;
      hydrateState(text, url, note);
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

function hydrateState(text, sourceName = 'playlist', note = '') {
  const parsed = parseM3U(text);
  state.channels = parsed.channels;
  state.groups = parsed.groups;
<<<<<<< HEAD
  state.rawText = text;
=======
  state.channelLines = parsed.channelLines;
>>>>>>> ef544c15608d31de0a9d87e24711a0a4d366aeaa
  state.disabledGroups = new Set();
  state.sourceName = sourceName;
  renderGroups();
  renderStats();
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

// Debounce helper
let outputUpdateTimer = null;
function scheduleOutputUpdate() {
  if (outputUpdateTimer) clearTimeout(outputUpdateTimer);
  outputUpdateTimer = setTimeout(() => {
    updateOutput();
    outputUpdateTimer = null;
  }, 150);
}

function renderGroups() {
  // Virtualized list implementation: render only visible items into a small pool.
  // Prepare sorted entries array for consistent indexing.
  state.groupEntries = [...state.groups.entries()].sort((a, b) => b[1] - a[1]);

  // Constants for virtualization
  const ITEM_HEIGHT = 56; // px, should match CSS
  const BUFFER = 6; // items buffer above and below

  // Clear and set up container
  groupsGrid.innerHTML = '';
<<<<<<< HEAD
  groupsGrid.classList.remove('group-list');
  groupsGrid.classList.add('group-virtual');
  groupsGrid.style.position = 'relative';
  groupsGrid.style.overflowY = 'auto';

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

  // Calculate pool size based on container height
  const containerHeight = Math.max(groupsGrid.clientHeight, 300);
  const visibleCount = Math.ceil(containerHeight / ITEM_HEIGHT);
  const poolSize = Math.min(totalItems, visibleCount + BUFFER * 2);

  // Create pooled nodes
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
    // Keep references for quick updates
    pool.push({ node, title, count, checkbox });
    itemsLayer.append(node);
  }

  // Render a slice of items starting at startIndex
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
      // attach dataset index for change handler
      poolItem.checkbox.dataset.index = idx;
    }
  }

  // Initial render
  renderSlice(0);

  // Scroll handler to compute visible start index
  let scrollTick = null;
  function onScroll() {
    if (scrollTick) return;
    scrollTick = requestAnimationFrame(() => {
      const scrollTop = groupsGrid.scrollTop;
      const start = Math.floor(scrollTop / ITEM_HEIGHT) - BUFFER;
      renderSlice(start);
      scrollTick = null;
=======
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
>>>>>>> ef544c15608d31de0a9d87e24711a0a4d366aeaa
    });
  }

<<<<<<< HEAD
  groupsGrid.addEventListener('scroll', onScroll);

  // Resize handling
  let resizeObserver = null;
  if (window.ResizeObserver) {
    resizeObserver = new ResizeObserver(() => {
      // recompute pool/visible counts on resize by re-rendering
      renderGroups();
    });
    resizeObserver.observe(groupsGrid);
  } else {
    window.addEventListener('resize', () => renderGroups());
  }

  // Event delegation for toggles
  itemsLayer.addEventListener('change', (e) => {
    const target = e.target;
    if (!target.classList.contains('group-toggle')) return;
    const idx = Number(target.dataset.index);
    const [groupTitle] = state.groupEntries[idx];
    if (target.checked) {
      state.disabledGroups.delete(groupTitle);
    } else {
      state.disabledGroups.add(groupTitle);
    }
    // Update stats quickly
    renderStats();
    // Update visual state of the pool item
    const poolIdx = idx - lastStart;
    if (poolIdx >= 0 && poolIdx < pool.length) {
      pool[poolIdx].node.classList.toggle('disabled', state.disabledGroups.has(groupTitle));
    }
  });
=======
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
>>>>>>> ef544c15608d31de0a9d87e24711a0a4d366aeaa
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
  // Synchronous fallback kept for small playlists or when workers are unavailable.
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

// Updated to optionally run generation in a Web Worker and report progress.
function updateOutput({ useWorker = true } = {}) {
  // If no raw text available or worker support missing, fall back to sync
  if (useWorker && window.Worker && state.rawText) {
    return new Promise((resolve, reject) => {
      setStatus('Generating filtered playlist (background)…', 'info');
      // Disable action buttons while generating
      setControlsDisabled(true);
      const worker = new Worker('filter-worker.js');
      const disabledArr = Array.from(state.disabledGroups);
      worker.postMessage({ cmd: 'generate', text: state.rawText, disabled: disabledArr });
      worker.addEventListener('message', (ev) => {
        const msg = ev.data || {};
        if (msg.type === 'progress') {
          // Update status with progress
          setStatus(`Generating… ${Math.min(100, Math.round((msg.processed / msg.totalLines) * 100))}%`, 'info');
        } else if (msg.type === 'result') {
          outputArea.value = msg.text;
          // Count channels in output by counting lines that are not comments and are URLs
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
        reject(err);
      });
    });
  }

  // Fallback synchronous generation
  const text = generateFilteredM3U();
  outputArea.value = text;
  return Promise.resolve(text);
}

function setControlsDisabled(disabled) {
  [downloadButton, copyButton, serveButton, refreshButton].forEach((b) => {
    if (b) b.disabled = disabled;
  });
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
  // Do not regenerate output here to avoid heavy work on large lists.
  // The user can click "Refresh" or Download to get the updated file.
});

// Initialize with sample to give users something to see immediately.
hydrateState(samplePlaylist, 'sample.m3u');
