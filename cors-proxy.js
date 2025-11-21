#!/usr/bin/env node

const http = require('http');
const https = require('https');
const url = require('url');

const PORT = 8080;

const server = http.createServer((req, res) => {
  // Set CORS headers to allow all origins
  res.setHeader('Access-Control-Allow-Origin', '*');
  res.setHeader('Access-Control-Allow-Methods', 'GET, OPTIONS');
  res.setHeader('Access-Control-Allow-Headers', 'Content-Type');

  // Handle preflight
  if (req.method === 'OPTIONS') {
    res.writeHead(200);
    res.end();
    return;
  }

  // Only allow GET requests
  if (req.method !== 'GET') {
    res.writeHead(405, { 'Content-Type': 'text/plain' });
    res.end('Method Not Allowed');
    return;
  }

  // Extract target URL from query parameter
  const parsedUrl = url.parse(req.url, true);
  const targetUrl = parsedUrl.query.url;

  if (!targetUrl) {
    res.writeHead(400, { 'Content-Type': 'text/plain' });
    res.end('Missing url parameter. Usage: http://localhost:8080/?url=YOUR_URL');
    return;
  }

  console.log(`Proxying request to: ${targetUrl}`);

  // Determine if HTTP or HTTPS
  const protocol = targetUrl.startsWith('https') ? https : http;

  // Make the request
  protocol.get(targetUrl, (proxyRes) => {
    // Forward status code
    res.writeHead(proxyRes.statusCode, {
      'Access-Control-Allow-Origin': '*',
      'Content-Type': proxyRes.headers['content-type'] || 'text/plain',
    });

    // Pipe the response
    proxyRes.pipe(res);
  }).on('error', (err) => {
    console.error('Proxy error:', err.message);
    res.writeHead(500, { 'Content-Type': 'text/plain' });
    res.end(`Proxy error: ${err.message}`);
  });
});

server.listen(PORT, () => {
  console.log(`CORS Proxy running on http://localhost:${PORT}`);
  console.log(`Usage: http://localhost:${PORT}/?url=YOUR_M3U_URL`);
});
