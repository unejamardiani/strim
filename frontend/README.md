# Strim Frontend

This folder contains the React single-page application scaffolded with Vite and TypeScript. It now surfaces playlist ingestion workflows and visualizes stored playlists.

## Available Scripts

- `npm install` – Install dependencies.
- `npm run dev` – Start the Vite development server at `http://localhost:5173` with proxying to the backend API.
- `npm run build` – Type-check and bundle the application for production.
- `npm run lint` – Run ESLint across the TypeScript source files.

## Features

- Backend health status with manual refresh controls.
- Playlist ingestion form accepting either a provider URL or a filesystem path reachable by the API container.
- Preview of the most recent ingest (first 25 channels).
- Table of all stored playlists, including channel counts and timestamps.

## Environment Variables

Add a `.env.local` file to override the backend API URL if required:

```
VITE_API_URL=https://example.com
```

The application automatically trims trailing slashes and falls back to relative paths when no override is specified.
