import { defineConfig, devices } from '@playwright/test';

// Points at the docker-compose dev stack's client container, not a Playwright-managed
// webServer -- this project already has one dev stack (docker-compose.yml) and the
// concurrency race additionally needs the api container's artificial delay enabled, which
// only docker-compose.e2e.yml can do. See scripts/run-e2e.sh for the full sequence.
export default defineConfig({
  testDir: './e2e',
  timeout: 30_000,
  fullyParallel: false,
  retries: 0,
  reporter: 'list',
  use: {
    baseURL: process.env.PLAYWRIGHT_BASE_URL ?? 'http://localhost:5173',
    video: 'on',
    trace: 'retain-on-failure',
  },
  projects: [{ name: 'chromium', use: { ...devices['Desktop Chrome'] } }],
});
