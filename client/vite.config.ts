/// <reference types="vitest/config" />
import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';

export default defineConfig({
  plugins: [react()],
  server: {
    host: true,
    port: 5173,
  },
  test: {
    environment: 'jsdom',
    setupFiles: ['./src/setupTests.ts'],
    // e2e/ holds Playwright specs (run via `pnpm test:e2e`), not Vitest ones -- without this,
    // Vitest's default include glob picks up concurrency-race.spec.ts too and fails trying to
    // run Playwright's test() outside a Playwright worker.
    exclude: ['e2e/**', 'node_modules/**'],
  },
});
