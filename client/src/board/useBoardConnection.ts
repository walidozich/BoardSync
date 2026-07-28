import { useEffect, useRef, useState } from 'react';
import { HubConnectionBuilder, type HubConnection } from '@microsoft/signalr';
import type { BoardDto, CardDto } from '../api/board';
import { useAuth } from '../auth/auth-context';

const API_URL = import.meta.env.VITE_API_URL ?? 'http://localhost:5080';

export type BoardConnectionStatus = 'connecting' | 'connected' | 'disconnected';

export interface PresenceUser {
  id: string;
  displayName: string;
}

export interface CardCreatedEvent {
  id: string;
  columnId: string;
  title: string;
  description: string | null;
  position: number;
  version: number;
}

export interface CreateCardRejectedEvent {
  reason: 'Invalid' | 'ColumnNotFound' | 'BoardFull';
  errors: Record<string, string[]> | null;
}

export interface BoardConnectionState {
  board: BoardDto | null;
  presence: PresenceUser[];
  status: BoardConnectionStatus;
  error: string | null;
  createCard: (columnId: string, title: string, description: string | null) => void;
  createCardError: CreateCardRejectedEvent | null;
}

/**
 * Immutably appends the card from a `CardCreated` event to its column's card
 * list. Pure so it can be unit tested without a real HubConnection. Returns
 * the same `board` reference (no-op) if there's no board yet or the column
 * isn't found, rather than throwing.
 */
export function applyCardCreated(board: BoardDto | null, event: CardCreatedEvent): BoardDto | null {
  if (!board) {
    return board;
  }

  const columnIndex = board.columns.findIndex((column) => column.id === event.columnId);
  if (columnIndex === -1) {
    return board;
  }

  const newCard: CardDto = {
    id: event.id,
    title: event.title,
    description: event.description,
    position: event.position,
    version: event.version,
  };

  const columns = board.columns.map((column, index) =>
    index === columnIndex ? { ...column, cards: [...column.cards, newCard] } : column,
  );

  return { ...board, columns };
}

export function useBoardConnection(): BoardConnectionState {
  const { token } = useAuth();
  const [board, setBoard] = useState<BoardDto | null>(null);
  const [presence, setPresence] = useState<PresenceUser[]>([]);
  const [status, setStatus] = useState<BoardConnectionStatus>('connecting');
  const [error, setError] = useState<string | null>(null);
  const [createCardError, setCreateCardError] = useState<CreateCardRejectedEvent | null>(null);
  const connectionRef = useRef<HubConnection | null>(null);

  useEffect(() => {
    if (!token) {
      return;
    }

    const connection: HubConnection = new HubConnectionBuilder()
      .withUrl(`${API_URL}/hubs/board?access_token=${encodeURIComponent(token)}`)
      .build();

    let cancelled = false;

    connection.on('BoardSnapshot', (snapshot: BoardDto) => {
      if (!cancelled) {
        setBoard(snapshot);
      }
    });

    connection.on('PresenceChanged', (roster: PresenceUser[]) => {
      if (!cancelled) {
        setPresence(roster);
      }
    });

    connection.on('CardCreated', (event: CardCreatedEvent) => {
      if (!cancelled) {
        setBoard((prev) => applyCardCreated(prev, event));
      }
    });

    connection.on('CreateCardRejected', (event: CreateCardRejectedEvent) => {
      if (!cancelled) {
        setCreateCardError(event);
      }
    });

    connection.onclose(() => {
      if (!cancelled) {
        setStatus('disconnected');
        setPresence([]);
      }
    });

    connectionRef.current = connection;

    connection
      .start()
      .then(() => {
        if (cancelled) {
          return;
        }
        setStatus('connected');
        return connection.invoke('JoinBoard');
      })
      .catch((err: unknown) => {
        if (!cancelled) {
          setError(err instanceof Error ? err.message : 'Unknown error');
          setStatus('disconnected');
        }
      });

    return () => {
      cancelled = true;
      connectionRef.current = null;
      void connection.stop();
    };
  }, [token]);

  function createCard(columnId: string, title: string, description: string | null): void {
    setCreateCardError(null);

    const connection = connectionRef.current;
    if (!connection) {
      return;
    }

    // Fire-and-forget: success/failure arrive via the CardCreated /
    // CreateCardRejected events, not via this promise. We still log a
    // rejection so a transport-level invoke failure (e.g. connection
    // dropped mid-call) isn't silently swallowed.
    connection.invoke('CreateCard', { columnId, title, description }).catch((err: unknown) => {
      console.error('CreateCard invoke failed', err);
    });
  }

  if (!token) {
    return {
      board: null,
      presence: [],
      status: 'disconnected',
      error: null,
      createCard: () => {},
      createCardError: null,
    };
  }

  return { board, presence, status, error, createCard, createCardError };
}
