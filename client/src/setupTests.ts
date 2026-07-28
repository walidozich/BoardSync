import '@testing-library/jest-dom/vitest';
import { afterEach } from 'vitest';
import { cleanup } from '@testing-library/react';

// No `globals: true` in vite.config.ts (kept out to avoid widening tsconfig's `types`),
// so Testing Library's automatic per-test cleanup — which hooks into a global `afterEach`
// that doesn't exist here — never runs on its own. Without this, every render() call in a
// test file leaves its DOM mounted, so later queries in the same file see duplicates.
afterEach(() => {
  cleanup();
});
