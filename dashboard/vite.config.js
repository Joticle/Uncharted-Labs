import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';

export default defineConfig({
  plugins: [react()],
  build: { outDir: 'dist', emptyOutDir: true },
  server: {
    // `vercel dev` serves /api; in plain `vite dev` these 404 and the app shows
    // its offline state, which is itself worth being able to look at.
    proxy: { '/api': 'http://localhost:3000' },
  },
});
