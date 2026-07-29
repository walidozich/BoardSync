import { describe, it, expect, vi, beforeEach } from 'vitest';
import { renderHook, waitFor, act } from '@testing-library/react';
import type { ReactNode } from 'react';
import { useBoardConnection } from './useBoardConnection';
import { AuthContext } from '../auth/auth-context';

// @microsoft/signalr's real HubConnection opens a real transport, which this hook-level test
// has no interest in -- only the reconnect *lifecycle wiring* (onreconnecting/onreconnected)
// is under test here. A fake HubConnectionBuilder/connection lets the test drive that
// lifecycle directly and deterministically, the same way the pure applyX functions elsewhere
// in this file are tested without a real HubConnection at all.
let onHandlers: Record<string, (...args: unknown[]) => void>;
let reconnectingHandler: (() => void) | null;
let reconnectedHandler: (() => void) | null;
const invoke = vi.fn().mockResolvedValue(undefined);
const start = vi.fn().mockResolvedValue(undefined);
const stop = vi.fn().mockResolvedValue(undefined);

vi.mock('@microsoft/signalr', () => {
  class FakeHubConnectionBuilder {
    withUrl() {
      return this;
    }
    withAutomaticReconnect() {
      return this;
    }
    build() {
      return {
        on: (event: string, handler: (...args: unknown[]) => void) => {
          onHandlers[event] = handler;
        },
        onreconnecting: (handler: () => void) => {
          reconnectingHandler = handler;
        },
        onreconnected: (handler: () => void) => {
          reconnectedHandler = handler;
        },
        onclose: () => {},
        start,
        invoke,
        stop,
      };
    }
  }

  return { HubConnectionBuilder: FakeHubConnectionBuilder };
});

function wrapper({ children }: { children: ReactNode }) {
  return (
    <AuthContext.Provider
      value={{
        token: 'test-token',
        displayName: 'Tester',
        isAuthenticated: true,
        login: vi.fn(),
        register: vi.fn(),
        logout: vi.fn(),
      }}
    >
      {children}
    </AuthContext.Provider>
  );
}

beforeEach(() => {
  onHandlers = {};
  reconnectingHandler = null;
  reconnectedHandler = null;
  invoke.mockClear();
  start.mockClear();
  stop.mockClear();
});

describe('useBoardConnection reconnect lifecycle', () => {
  it('goes to reconnecting on onreconnecting, back to connected and re-joins on onreconnected', async () => {
    const { result } = renderHook(() => useBoardConnection(), { wrapper });

    await waitFor(() => expect(result.current.status).toBe('connected'));
    invoke.mockClear();

    act(() => {
      reconnectingHandler?.();
    });
    expect(result.current.status).toBe('reconnecting');

    act(() => {
      reconnectedHandler?.();
    });
    await waitFor(() => expect(result.current.status).toBe('connected'));
    expect(invoke).toHaveBeenCalledWith('JoinBoard');
  });

  it('a fresh BoardSnapshot after reconnect replaces state wholesale, not merges into it', async () => {
    const { result } = renderHook(() => useBoardConnection(), { wrapper });
    await waitFor(() => expect(result.current.status).toBe('connected'));

    act(() => {
      onHandlers.BoardSnapshot({
        id: 'board-1',
        name: 'BoardSync Demo',
        columns: [
          {
            id: 'col-1',
            name: 'To Do',
            position: 0,
            cards: [{ id: 'card-a', title: 'A', description: null, position: 0, version: 1 }],
          },
        ],
      });
    });
    expect(result.current.board?.columns[0].cards.map((c) => c.id)).toEqual(['card-a']);

    act(() => {
      reconnectingHandler?.();
    });

    // A completely different snapshot arrives on reconnect (as if the board changed while
    // this client was gone) -- the old card must be gone entirely, not merged alongside it.
    act(() => {
      onHandlers.BoardSnapshot({
        id: 'board-1',
        name: 'BoardSync Demo',
        columns: [
          {
            id: 'col-1',
            name: 'To Do',
            position: 0,
            cards: [{ id: 'card-b', title: 'B', description: null, position: 0, version: 1 }],
          },
        ],
      });
    });

    expect(result.current.board?.columns[0].cards.map((c) => c.id)).toEqual(['card-b']);
  });
});
