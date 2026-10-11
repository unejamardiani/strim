// Worker to parse and generate filtered M3U without blocking the main thread.

// Keep in sync with detectContentType/groupKey in main.js and PlaylistProcessor on the API.
function detectContentType(url) {
  const match = /^[a-z][a-z0-9+.-]*:\/\/[^/]+\/(movie|series)\//i.exec(url || '');
  if (!match) return 'Live';
  return match[1].toLowerCase() === 'movie' ? 'Movies' : 'Series';
}

function groupKey(groupTitle, contentType) {
  return `${groupTitle} \u00B7 ${contentType}`;
}

function outputGroupTitle(groupTitle, contentType, splitTitles) {
  return contentType !== 'Live' && splitTitles.has(groupTitle) ? groupKey(groupTitle, contentType) : groupTitle;
}

self.addEventListener('message', (ev) => {
  const msg = ev.data || {};
  if (msg.cmd === 'generate') {
    const text = msg.text || '';
    const disabled = new Set(msg.disabled || []);
    const splitTitles = new Set(msg.splitTitles || []);
    // Parse lines lazily and emit progress periodically.
    const lines = text.replace(/\r\n?/g, '\n').split('\n');
    const totalLines = lines.length;

    const outputParts = ['#EXTM3U', '# Created with Strim (https://strim.plis.dev)'];
    let processed = 0;

    // Iterate through lines looking for #EXTINF entries.
    for (let i = 0; i < lines.length; i++) {
      const line = lines[i].trim();
      processed++;
      if (!line.startsWith('#EXTINF')) continue;
      const extinf = line;
      const url = (lines[i + 1] || '').trim();
      i++;

      // Extract group-title attribute
      let match;
      const attrRegex = /(\w[\w-]*)="([^"]*)"/g;
      let groupTitle = 'Ungrouped';
      while ((match = attrRegex.exec(extinf)) !== null) {
        if (match[1] === 'group-title') {
          groupTitle = match[2] || 'Ungrouped';
          break;
        }
      }

      // A plain title disables the whole group; a groupKey only one content type of it.
      const contentType = detectContentType(url);
      if (disabled.has(groupTitle) || disabled.has(groupKey(groupTitle, contentType))) {
        // skip
      } else {
        // Ensure group-title present in extinf; movies/series of a split title get their own group.
        const outputTitle = outputGroupTitle(groupTitle, contentType, splitTitles);
        const hasGroup = /group-title="[^"]*"/i.test(extinf);
        let outExtinf = hasGroup ? extinf : `${extinf} group-title="${outputTitle}"`;
        if (hasGroup && outputTitle !== groupTitle) {
          outExtinf = extinf.replace(/group-title="[^"]*"/i, () => `group-title="${outputTitle}"`);
        }
        outputParts.push(outExtinf);
        outputParts.push(url);
      }

      // Post progress every 2000 processed lines to avoid excessive messaging
      if (processed % 2000 === 0) {
        self.postMessage({ type: 'progress', processed, totalLines });
      }
    }

    // Final progress
    self.postMessage({ type: 'progress', processed: totalLines, totalLines });
    const result = outputParts.join('\n');
    self.postMessage({ type: 'result', text: result });
  }
});
