import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { render, screen } from '@testing-library/react';
import App from './App';

const STORAGE_KEY = 'boardsync.auth';

beforeEach(() => {
  localStorage.clear();
});

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('App protected route', () => {
  it('shows the login screen when there is no stored token', () => {
    render(<App />);

    expect(screen.getByRole('heading', { name: 'BoardSync' })).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Log out' })).not.toBeInTheDocument();
  });

  it('shows the board when a token is already stored', () => {
    localStorage.setItem(STORAGE_KEY, JSON.stringify({ token: 'xyz', displayName: 'Bob' }));
    vi.stubGlobal(
      'fetch',
      vi.fn().mockResolvedValue({
        ok: true,
        status: 200,
        json: () => Promise.resolve({ id: '1', name: 'BoardSync Demo' }),
      }),
    );

    render(<App />);

    expect(screen.getByText('Bob')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Log out' })).toBeInTheDocument();
  });
});
