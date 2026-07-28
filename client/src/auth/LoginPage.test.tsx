import { describe, it, expect, vi, beforeEach } from 'vitest';
import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { AuthProvider } from './AuthContext';
import { LoginPage } from './LoginPage';
import * as authApi from '../api/auth';

vi.mock('../api/auth');

beforeEach(() => {
  localStorage.clear();
  vi.mocked(authApi.login).mockReset();
  vi.mocked(authApi.register).mockReset();
});

function renderLoginPage() {
  return render(
    <AuthProvider>
      <LoginPage />
    </AuthProvider>,
  );
}

describe('LoginPage form validation', () => {
  it('rejects an email with no @', async () => {
    const user = userEvent.setup();
    renderLoginPage();

    await user.type(screen.getByLabelText('Email'), 'not-an-email');
    await user.type(screen.getByLabelText('Password'), 'correcthorse123');
    await user.click(screen.getByRole('button', { name: 'Log in' }));

    expect(await screen.findByText(/must contain/i)).toBeInTheDocument();
    expect(authApi.login).not.toHaveBeenCalled();
  });

  it('rejects a password shorter than 8 characters', async () => {
    const user = userEvent.setup();
    renderLoginPage();

    await user.type(screen.getByLabelText('Email'), 'alice@example.com');
    await user.type(screen.getByLabelText('Password'), 'short');
    await user.click(screen.getByRole('button', { name: 'Log in' }));

    expect(await screen.findByText(/at least 8 characters/i)).toBeInTheDocument();
    expect(authApi.login).not.toHaveBeenCalled();
  });

  it('requires a display name when registering', async () => {
    const user = userEvent.setup();
    renderLoginPage();

    await user.click(screen.getByRole('tab', { name: 'Register' }));
    await user.type(screen.getByLabelText('Email'), 'alice@example.com');
    await user.type(screen.getByLabelText('Password'), 'correcthorse123');
    await user.click(screen.getByRole('button', { name: 'Register' }));

    expect(await screen.findByText(/display name must be between/i)).toBeInTheDocument();
    expect(authApi.register).not.toHaveBeenCalled();
  });

  it('submits valid login credentials', async () => {
    vi.mocked(authApi.login).mockResolvedValue({ token: 'fake-token', displayName: 'Alice' });
    const user = userEvent.setup();
    renderLoginPage();

    await user.type(screen.getByLabelText('Email'), 'alice@example.com');
    await user.type(screen.getByLabelText('Password'), 'correcthorse123');
    await user.click(screen.getByRole('button', { name: 'Log in' }));

    expect(authApi.login).toHaveBeenCalledWith('alice@example.com', 'correcthorse123');
  });
});
