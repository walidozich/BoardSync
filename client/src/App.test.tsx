import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { render, screen } from '@testing-library/react';
import App from './App';
import * as useBoardConnectionModule from './board/useBoardConnection';

const STORAGE_KEY = 'boardsync.auth';

vi.mock('./board/useBoardConnection');

beforeEach(() => {
  localStorage.clear();
});

afterEach(() => {
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
});

describe('App protected route', () => {
  it('shows the login screen when there is no stored token', () => {
    render(<App />);

    expect(screen.getByRole('heading', { name: 'BoardSync' })).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Log out' })).not.toBeInTheDocument();
  });

  it('shows the board when a token is already stored', () => {
    localStorage.setItem(STORAGE_KEY, JSON.stringify({ token: 'xyz', displayName: 'Bob' }));
    vi.spyOn(useBoardConnectionModule, 'useBoardConnection').mockReturnValue({
      status: 'connected',
      error: null,
      presence: [{ id: 'user-1', displayName: 'Carol' }],
      createCard: vi.fn(),
      createCardError: null,
      moveCard: vi.fn(),
      moveRejectedNotice: null,
      deleteCard: vi.fn(),
      board: {
        id: '1',
        name: 'BoardSync Demo',
        columns: [
          {
            id: 'col-1',
            name: 'To Do',
            position: 0,
            cards: [
              {
                id: 'card-1',
                title: 'First card',
                description: 'Details',
                position: 0,
                version: 1,
              },
            ],
          },
        ],
      },
    });

    render(<App />);

    expect(screen.getByText('Bob')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Log out' })).toBeInTheDocument();
    expect(screen.getByText('BoardSync Demo')).toBeInTheDocument();
    expect(screen.getByText('To Do')).toBeInTheDocument();
    expect(screen.getByText('First card')).toBeInTheDocument();
    expect(screen.getByRole('list', { name: 'Currently connected' })).toBeInTheDocument();
    expect(screen.getByText('Carol')).toBeInTheDocument();
  });
});
