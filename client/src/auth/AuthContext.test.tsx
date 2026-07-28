import { describe, it, expect, vi, beforeEach } from 'vitest';
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { AuthProvider } from './AuthContext';
import { useAuth } from './auth-context';
import * as authApi from '../api/auth';

vi.mock('../api/auth');

const STORAGE_KEY = 'boardsync.auth';

beforeEach(() => {
  localStorage.clear();
  vi.mocked(authApi.login).mockReset();
});

function Probe() {
  const { isAuthenticated, token, displayName, login, logout } = useAuth();
  return (
    <div>
      <p>authenticated: {String(isAuthenticated)}</p>
      <p>token: {token ?? 'none'}</p>
      <p>displayName: {displayName ?? 'none'}</p>
      <button type="button" onClick={() => login('alice@example.com', 'correcthorse123')}>
        login
      </button>
      <button type="button" onClick={logout}>
        logout
      </button>
    </div>
  );
}

describe('AuthContext persistence', () => {
  it('persists the token and display name to localStorage on login', async () => {
    vi.mocked(authApi.login).mockResolvedValue({ token: 'abc.def.ghi', displayName: 'Alice' });
    const user = userEvent.setup();
    render(
      <AuthProvider>
        <Probe />
      </AuthProvider>,
    );

    await user.click(screen.getByRole('button', { name: 'login' }));

    await waitFor(() => expect(screen.getByText('authenticated: true')).toBeInTheDocument());
    expect(screen.getByText('token: abc.def.ghi')).toBeInTheDocument();
    expect(screen.getByText('displayName: Alice')).toBeInTheDocument();

    const stored = JSON.parse(localStorage.getItem(STORAGE_KEY) ?? 'null');
    expect(stored).toEqual({ token: 'abc.def.ghi', displayName: 'Alice' });
  });

  it('restores authentication state from localStorage on mount', () => {
    localStorage.setItem(STORAGE_KEY, JSON.stringify({ token: 'xyz', displayName: 'Bob' }));

    render(
      <AuthProvider>
        <Probe />
      </AuthProvider>,
    );

    expect(screen.getByText('authenticated: true')).toBeInTheDocument();
    expect(screen.getByText('displayName: Bob')).toBeInTheDocument();
  });

  it('clears localStorage and state on logout', async () => {
    localStorage.setItem(STORAGE_KEY, JSON.stringify({ token: 'xyz', displayName: 'Bob' }));
    const user = userEvent.setup();
    render(
      <AuthProvider>
        <Probe />
      </AuthProvider>,
    );

    await user.click(screen.getByRole('button', { name: 'logout' }));

    expect(screen.getByText('authenticated: false')).toBeInTheDocument();
    expect(localStorage.getItem(STORAGE_KEY)).toBeNull();
  });
});
