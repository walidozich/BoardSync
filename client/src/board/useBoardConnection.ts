import { useEffect, useRef, useState } from 'react';
import { HubConnectionBuilder, type HubConnection } from '@microsoft/signalr';
import type { BoardDto, CardDto } from '../api/board';
import { useAuth } from '../auth/auth-context';

const API_URL = import.meta.env.VITE_API_URL ?? 'http://localhost:5080';

export type BoardConnectionStatus = 'connecting' | 'connected' | 'reconnecting' | 'disconnected';

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

export interface CardMovedEvent {
  id: string;
  columnId: string;
  position: number;
  version: number;
}

export interface MoveRejectedEvent {
  reason: 'CardNotFound' | 'ColumnNotFound' | 'StaleVersion' | 'CardDeleted';
  cardId: string;
  card: CardMovedEvent | null;
  winnerDisplayName: string | null;
}

export interface BoardConnectionState {
  board: BoardDto | null;
  presence: PresenceUser[];
  status: BoardConnectionStatus;
  error: string | null;
  createCard: (columnId: string, title: string, description: string | null) => void;
  createCardError: CreateCardRejectedEvent | null;
  moveCard: (
    cardId: string,
    targetColumnId: string,
    afterCardId: string | null,
    beforeCardId: string | null,
  ) => void;
  moveRejectedNotice: string | null;
  deleteCard: (cardId: string) => void;
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

/**
 * Immutably moves the card from a `CardMoved` event to its resolved column
 * and position. Pure so it can be unit tested without a real HubConnection.
 * The card is located by id across *all* columns (it may already be in the
 * target column, for a same-column reorder), removed from wherever it
 * currently lives, then re-inserted into the target column at the index
 * that keeps that column's cards sorted by `position` ascending. Returns the
 * same `board` reference (no-op) if there's no board yet, the card isn't
 * found in any column, or the target column isn't found.
 */
export function applyCardMoved(board: BoardDto | null, event: CardMovedEvent): BoardDto | null {
  if (!board) {
    return board;
  }

  let movedCard: CardDto | null = null;
  for (const column of board.columns) {
    const found = column.cards.find((card) => card.id === event.id);
    if (found) {
      movedCard = found;
      break;
    }
  }
  if (!movedCard) {
    return board;
  }

  const targetColumnExists = board.columns.some((column) => column.id === event.columnId);
  if (!targetColumnExists) {
    return board;
  }

  const updatedCard: CardDto = { ...movedCard, position: event.position, version: event.version };

  const columns = board.columns.map((column) => {
    const withoutCard = column.cards.filter((card) => card.id !== event.id);

    if (column.id !== event.columnId) {
      return withoutCard.length === column.cards.length
        ? column
        : { ...column, cards: withoutCard };
    }

    const insertIndex = withoutCard.findIndex((card) => card.position > updatedCard.position);
    const cards =
      insertIndex === -1
        ? [...withoutCard, updatedCard]
        : [...withoutCard.slice(0, insertIndex), updatedCard, ...withoutCard.slice(insertIndex)];

    return { ...column, cards };
  });

  return { ...board, columns };
}

/**
 * Reducer for a `MoveRejected` event's effect on board state. Pure, so the
 * "StaleVersion restores the authoritative position" and "CardDeleted removes
 * the card" behaviors are testable without mocking HubConnection, matching
 * applyCardCreated/applyCardMoved/applyCardDeleted. Two outcomes, not one
 * generic "revert": StaleVersion has a real position to snap to (someone else
 * moved it first); CardDeleted does not (someone else deleted it mid-drag) --
 * repositioning a card whose row no longer exists is exactly the bug this
 * distinction exists to avoid. CardNotFound/ColumnNotFound have no
 * authoritative payload either way (the hook falls back to re-fetching a
 * fresh snapshot for those instead, which is a side effect, not board-reducer
 * logic, so it isn't part of this function).
 */
export function applyMoveRejected(
  board: BoardDto | null,
  event: MoveRejectedEvent,
): BoardDto | null {
  if (event.reason === 'StaleVersion' && event.card) {
    return applyCardMoved(board, event.card);
  }

  if (event.reason === 'CardDeleted') {
    return applyCardDeleted(board, event.cardId);
  }

  return board;
}

/**
 * Immutably removes the card with the given id from whichever column
 * currently contains it, from a `CardDeleted` event's payload (a bare card
 * id). Pure so it can be unit tested without a real HubConnection, matching
 * applyCardCreated/applyCardMoved. Returns the same `board` reference
 * (no-op) if there's no board yet, or the card isn't found in any column.
 */
export function applyCardDeleted(board: BoardDto | null, cardId: string): BoardDto | null {
  if (!board) {
    return board;
  }

  const columnIndex = board.columns.findIndex((column) =>
    column.cards.some((card) => card.id === cardId),
  );
  if (columnIndex === -1) {
    return board;
  }

  const columns = board.columns.map((column, index) =>
    index === columnIndex
      ? { ...column, cards: column.cards.filter((card) => card.id !== cardId) }
      : column,
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
  const [moveRejectedNotice, setMoveRejectedNotice] = useState<string | null>(null);
  const connectionRef = useRef<HubConnection | null>(null);

  // Auto-dismiss: a genuine timer side effect (not state derived from props), so this is
  // exactly the case useEffect is for, unlike the "adjust state during render" pattern used
  // elsewhere in this codebase for prop-driven state.
  useEffect(() => {
    if (!moveRejectedNotice) {
      return;
    }

    const timeoutId = setTimeout(() => setMoveRejectedNotice(null), 4000);
    return () => clearTimeout(timeoutId);
  }, [moveRejectedNotice]);

  useEffect(() => {
    if (!token) {
      return;
    }

    // withAutomaticReconnect uses SignalR's default retry delays (0s, 2s, 10s, 30s) before
    // giving up and firing onclose. The same HubConnection object keeps retrying underneath --
    // onreconnecting/onreconnected fire on it, not a fresh connection -- so connectionRef.current
    // stays valid throughout. invoke() itself throws while the connection isn't in the
    // Connected state, which is exactly why card interactions are gated on status === 'connected'
    // below rather than relying on invoke() to fail safely on its own.
    const connection: HubConnection = new HubConnectionBuilder()
      .withUrl(`${API_URL}/hubs/board?access_token=${encodeURIComponent(token)}`)
      .withAutomaticReconnect()
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

    connection.on('CardMoved', (event: CardMovedEvent) => {
      if (!cancelled) {
        setBoard((prev) => applyCardMoved(prev, event));
      }
    });

    connection.on('MoveRejected', (event: MoveRejectedEvent) => {
      if (cancelled) {
        return;
      }

      if (event.reason === 'StaleVersion' && event.card) {
        // Someone else's move already landed first. Snap this card to the
        // authoritative state the server just sent -- the same applyCardMoved
        // path a real CardMoved broadcast uses, so the card animates to its
        // true position exactly like any other move (dnd-kit's own sortable
        // transition handles the animation, driven by the reordered list).
        setBoard((prev) => applyMoveRejected(prev, event));
        setMoveRejectedNotice(`${event.winnerDisplayName ?? 'Someone'} moved this card first.`);
        return;
      }

      if (event.reason === 'CardDeleted') {
        // Someone else deleted this card while it was being dragged. There's
        // no authoritative position to snap to -- the row is gone -- so the
        // only correct response is removing it, not repositioning it.
        setBoard((prev) => applyMoveRejected(prev, event));
        setMoveRejectedNotice('That card was deleted.');
        return;
      }

      // Minimal, defensive recovery for the remaining reasons (a move
      // referenced a card/column that no longer exists -- shouldn't happen
      // via normal UI use). Rather than building bespoke per-card revert
      // logic for cases with no authoritative payload, just re-fetch a
      // fresh snapshot the same way the initial connect does.
      connection.invoke('JoinBoard').catch((err: unknown) => {
        console.error('JoinBoard invoke failed', err);
      });
    });

    connection.on('CardDeleted', (cardId: string) => {
      if (!cancelled) {
        setBoard((prev) => applyCardDeleted(prev, cardId));
      }
    });

    // Fires once the transport actually drops, after which SignalR starts retrying on its
    // own. Presence self-heals server-side (a dropped connection triggers OnDisconnectedAsync,
    // and the eventual reconnect triggers a fresh OnConnectedAsync with its own PresenceChanged
    // broadcast) -- no client-side presence handling needed here.
    connection.onreconnecting(() => {
      if (!cancelled) {
        setStatus('reconnecting');
      }
    });

    // Fires once the retry actually succeeds. The board may have changed in ways no single
    // targeted event could describe while this client was gone, so JoinBoard's fresh
    // BoardSnapshot replaces local state wholesale rather than trying to reconcile deltas.
    connection.onreconnected(() => {
      if (cancelled) {
        return;
      }

      setStatus('connected');
      connection.invoke('JoinBoard').catch((err: unknown) => {
        console.error('JoinBoard invoke failed', err);
      });
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

    // Guards against more than just a disabled button: invoke() itself throws while the
    // underlying connection isn't in the Connected state (e.g. mid-reconnect), so this check
    // has to be here too, not just on the UI controls that call these functions.
    if (status !== 'connected') {
      return;
    }

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

  function moveCard(
    cardId: string,
    targetColumnId: string,
    afterCardId: string | null,
    beforeCardId: string | null,
  ): void {
    if (status !== 'connected') {
      return;
    }

    const connection = connectionRef.current;
    if (!connection) {
      return;
    }

    let expectedVersion: number | null = null;
    let afterPosition: number | null = null;
    let beforePosition: number | null = null;
    for (const column of board?.columns ?? []) {
      for (const card of column.cards) {
        if (card.id === cardId) {
          expectedVersion = card.version;
        }
        if (card.id === afterCardId) {
          afterPosition = card.position;
        }
        if (card.id === beforeCardId) {
          beforePosition = card.position;
        }
      }
    }
    if (expectedVersion === null) {
      return;
    }

    // Estimate a landing position purely so the drag feels instant; the
    // server's authoritative CardMoved broadcast (resolved from neighbour
    // ids, not this estimate) arrives shortly after and applyCardMoved
    // re-sorts it into the correct spot regardless of this guess.
    const estimatedPosition =
      afterPosition !== null && beforePosition !== null
        ? (afterPosition + beforePosition) / 2
        : afterPosition !== null
          ? afterPosition + 1
          : beforePosition !== null
            ? beforePosition - 1
            : 0;

    setBoard((prev) =>
      applyCardMoved(prev, {
        id: cardId,
        columnId: targetColumnId,
        position: estimatedPosition,
        version: expectedVersion,
      }),
    );

    connection
      .invoke('MoveCard', { cardId, targetColumnId, afterCardId, beforeCardId, expectedVersion })
      .catch((err: unknown) => {
        console.error('MoveCard invoke failed', err);
      });
  }

  function deleteCard(cardId: string): void {
    if (status !== 'connected') {
      return;
    }

    const connection = connectionRef.current;
    if (!connection) {
      return;
    }

    let expectedVersion: number | null = null;
    for (const column of board?.columns ?? []) {
      for (const card of column.cards) {
        if (card.id === cardId) {
          expectedVersion = card.version;
        }
      }
    }
    if (expectedVersion === null) {
      return;
    }

    setBoard((prev) => applyCardDeleted(prev, cardId));

    connection.invoke('DeleteCard', { cardId, expectedVersion }).catch((err: unknown) => {
      console.error('DeleteCard invoke failed', err);
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
      moveCard: () => {},
      moveRejectedNotice: null,
      deleteCard: () => {},
    };
  }

  return {
    board,
    presence,
    status,
    error,
    createCard,
    createCardError,
    moveCard,
    moveRejectedNotice,
    deleteCard,
  };
}
