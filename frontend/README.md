# Strim Frontend

This folder contains the React single-page application scaffolded with Vite and TypeScript. It provides a lightweight integration surface to communicate with the Strim backend during development.

## Available Scripts

- `npm install` – Install dependencies.
- `npm run dev` – Start the Vite development server at `http://localhost:5173` with proxying to the backend API.
- `npm run build` – Type-check and bundle the application for production.
- `npm run lint` – Run ESLint across the TypeScript source files.

## Environment Variables

Add a `.env.local` file to override the backend API URL if required:

```bash
VITE_API_URL=https://example.com
```

Update the Axios instance in `src/App.tsx` to use the custom value if you plan to deploy separately from the backend.
