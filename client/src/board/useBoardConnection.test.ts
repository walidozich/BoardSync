import { describe, it, expect } from 'vitest';
import {
  applyCardCreated,
  applyCardMoved,
  applyMoveRejected,
  type CardCreatedEvent,
  type CardMovedEvent,
  type MoveRejectedEvent,
} from './useBoardConnection';
import type { BoardDto } from '../api/board';

function makeBoard(): BoardDto {
  return {
    id: 'board-1',
    name: 'BoardSync Demo',
    columns: [
      {
        id: 'col-1',
        name: 'To Do',
        position: 0,
        cards: [{ id: 'card-1', title: 'First card', description: null, position: 0, version: 1 }],
      },
      {
        id: 'col-2',
        name: 'Done',
        position: 1,
        cards: [],
      },
    ],
  };
}

function makeThreeCardBoard(): BoardDto {
  return {
    id: 'board-1',
    name: 'BoardSync Demo',
    columns: [
      {
        id: 'col-1',
        name: 'To Do',
        position: 0,
        cards: [
          { id: 'card-a', title: 'A', description: null, position: 0, version: 1 },
          { id: 'card-b', title: 'B', description: null, position: 1, version: 1 },
          { id: 'card-c', title: 'C', description: null, position: 2, version: 1 },
        ],
      },
      {
        id: 'col-2',
        name: 'Done',
        position: 1,
        cards: [
          { id: 'card-x', title: 'X', description: null, position: 0, version: 1 },
          { id: 'card-y', title: 'Y', description: null, position: 1, version: 1 },
        ],
      },
    ],
  };
}

function makeMovedEvent(overrides: Partial<CardMovedEvent> = {}): CardMovedEvent {
  return {
    id: 'card-a',
    columnId: 'col-1',
    position: 0,
    version: 2,
    ...overrides,
  };
}

function makeRejectedEvent(overrides: Partial<MoveRejectedEvent> = {}): MoveRejectedEvent {
  return {
    reason: 'StaleVersion',
    cardId: 'card-a',
    card: makeMovedEvent(),
    winnerDisplayName: 'Ahmed',
    ...overrides,
  };
}

function makeEvent(overrides: Partial<CardCreatedEvent> = {}): CardCreatedEvent {
  return {
    id: 'card-2',
    columnId: 'col-1',
    title: 'New card',
    description: 'Some details',
    position: 1,
    version: 1,
    ...overrides,
  };
}

describe('applyCardCreated', () => {
  it('appends the new card to the matching column, after existing cards', () => {
    const board = makeBoard();
    const result = applyCardCreated(board, makeEvent());

    expect(result).not.toBeNull();
    const column = result!.columns.find((c) => c.id === 'col-1')!;
    expect(column.cards.map((c) => c.id)).toEqual(['card-1', 'card-2']);
    expect(column.cards[1]).toEqual({
      id: 'card-2',
      title: 'New card',
      description: 'Some details',
      position: 1,
      version: 1,
    });
  });

  it('does not mutate the original board object (immutable update)', () => {
    const board = makeBoard();
    const originalColumns = board.columns;
    const originalCards = board.columns[0].cards;

    const result = applyCardCreated(board, makeEvent());

    expect(result).not.toBe(board);
    expect(result!.columns).not.toBe(originalColumns);
    expect(board.columns).toBe(originalColumns);
    expect(board.columns[0].cards).toBe(originalCards);
    expect(board.columns[0].cards).toHaveLength(1);
  });

  it('leaves other columns untouched by reference', () => {
    const board = makeBoard();
    const result = applyCardCreated(board, makeEvent());

    expect(result!.columns.find((c) => c.id === 'col-2')).toBe(board.columns[1]);
  });

  it('returns the same board reference when board is null', () => {
    expect(applyCardCreated(null, makeEvent())).toBeNull();
  });

  it('returns the same board reference when the column is not found', () => {
    const board = makeBoard();
    const result = applyCardCreated(board, makeEvent({ columnId: 'does-not-exist' }));

    expect(result).toBe(board);
  });
});

describe('applyCardMoved', () => {
  it('inserts a card moved to a different column at the sorted top position', () => {
    const board = makeThreeCardBoard();
    const result = applyCardMoved(
      board,
      makeMovedEvent({ id: 'card-a', columnId: 'col-2', position: -1 }),
    );

    const source = result!.columns.find((c) => c.id === 'col-1')!;
    const target = result!.columns.find((c) => c.id === 'col-2')!;
    expect(source.cards.map((c) => c.id)).toEqual(['card-b', 'card-c']);
    expect(target.cards.map((c) => c.id)).toEqual(['card-a', 'card-x', 'card-y']);
  });

  it('inserts a card moved to a different column at a sorted middle position', () => {
    const board = makeThreeCardBoard();
    const result = applyCardMoved(
      board,
      makeMovedEvent({ id: 'card-a', columnId: 'col-2', position: 0.5 }),
    );

    const target = result!.columns.find((c) => c.id === 'col-2')!;
    expect(target.cards.map((c) => c.id)).toEqual(['card-x', 'card-a', 'card-y']);
  });

  it('inserts a card moved to a different column at the sorted bottom position', () => {
    const board = makeThreeCardBoard();
    const result = applyCardMoved(
      board,
      makeMovedEvent({ id: 'card-a', columnId: 'col-2', position: 5 }),
    );

    const target = result!.columns.find((c) => c.id === 'col-2')!;
    expect(target.cards.map((c) => c.id)).toEqual(['card-x', 'card-y', 'card-a']);
  });

  it('removes the moved card from its original column when the column changes', () => {
    const board = makeThreeCardBoard();
    const result = applyCardMoved(
      board,
      makeMovedEvent({ id: 'card-b', columnId: 'col-2', position: 5 }),
    );

    const source = result!.columns.find((c) => c.id === 'col-1')!;
    expect(source.cards.map((c) => c.id)).toEqual(['card-a', 'card-c']);
  });

  it('re-sorts a card moved within its own column without duplicating it', () => {
    const board = makeThreeCardBoard();
    // Move card-c (currently last) to the front of col-1.
    const result = applyCardMoved(
      board,
      makeMovedEvent({ id: 'card-c', columnId: 'col-1', position: -1 }),
    );

    const column = result!.columns.find((c) => c.id === 'col-1')!;
    expect(column.cards.map((c) => c.id)).toEqual(['card-c', 'card-a', 'card-b']);
    expect(column.cards).toHaveLength(3);
  });

  it('re-sorts a card moved to a middle position within its own column', () => {
    const board = makeThreeCardBoard();
    // Move card-a (currently first) between card-b and card-c.
    const result = applyCardMoved(
      board,
      makeMovedEvent({ id: 'card-a', columnId: 'col-1', position: 1.5 }),
    );

    const column = result!.columns.find((c) => c.id === 'col-1')!;
    expect(column.cards.map((c) => c.id)).toEqual(['card-b', 'card-a', 'card-c']);
    expect(column.cards).toHaveLength(3);
  });

  it('updates the moved card position and version fields', () => {
    const board = makeThreeCardBoard();
    const result = applyCardMoved(
      board,
      makeMovedEvent({ id: 'card-a', columnId: 'col-1', position: 1.5, version: 7 }),
    );

    const column = result!.columns.find((c) => c.id === 'col-1')!;
    const moved = column.cards.find((c) => c.id === 'card-a')!;
    expect(moved.position).toBe(1.5);
    expect(moved.version).toBe(7);
    expect(moved.title).toBe('A');
  });

  it('returns the same board reference when board is null', () => {
    expect(applyCardMoved(null, makeMovedEvent())).toBeNull();
  });

  it('returns the same board reference when the card id is not found in any column', () => {
    const board = makeThreeCardBoard();
    const result = applyCardMoved(
      board,
      makeMovedEvent({ id: 'does-not-exist', columnId: 'col-1' }),
    );

    expect(result).toBe(board);
  });

  it('returns the same board reference when the target column is not found', () => {
    const board = makeThreeCardBoard();
    const result = applyCardMoved(
      board,
      makeMovedEvent({ id: 'card-a', columnId: 'does-not-exist' }),
    );

    expect(result).toBe(board);
  });

  it('does not mutate the original board object (immutable update)', () => {
    const board = makeThreeCardBoard();
    const originalColumns = board.columns;

    const result = applyCardMoved(
      board,
      makeMovedEvent({ id: 'card-a', columnId: 'col-2', position: 5 }),
    );

    expect(result).not.toBe(board);
    expect(result!.columns).not.toBe(originalColumns);
    expect(board.columns).toBe(originalColumns);
    expect(board.columns[0].cards).toHaveLength(3);
    expect(board.columns[1].cards).toHaveLength(2);
  });
});

describe('applyMoveRejected', () => {
  it('StaleVersion with a card payload restores the authoritative position', () => {
    const board = makeThreeCardBoard();

    const result = applyMoveRejected(
      board,
      makeRejectedEvent({
        cardId: 'card-a',
        card: makeMovedEvent({ id: 'card-a', columnId: 'col-2', position: 5, version: 9 }),
      }),
    );

    expect(result!.columns[0].cards.map((c) => c.id)).toEqual(['card-b', 'card-c']);
    const moved = result!.columns[1].cards.find((c) => c.id === 'card-a');
    expect(moved).toMatchObject({ position: 5, version: 9 });
  });

  it('StaleVersion restores a same-column reorder to its authoritative position', () => {
    const board = makeThreeCardBoard();

    const result = applyMoveRejected(
      board,
      makeRejectedEvent({
        cardId: 'card-c',
        card: makeMovedEvent({ id: 'card-c', columnId: 'col-1', position: -1, version: 7 }),
      }),
    );

    expect(result!.columns[0].cards.map((c) => c.id)).toEqual(['card-c', 'card-a', 'card-b']);
  });

  it('returns the same board reference for CardNotFound (no authoritative card to apply)', () => {
    const board = makeThreeCardBoard();

    const result = applyMoveRejected(
      board,
      makeRejectedEvent({ reason: 'CardNotFound', card: null, winnerDisplayName: null }),
    );

    expect(result).toBe(board);
  });

  it('returns the same board reference for ColumnNotFound (no authoritative card to apply)', () => {
    const board = makeThreeCardBoard();

    const result = applyMoveRejected(
      board,
      makeRejectedEvent({ reason: 'ColumnNotFound', card: null, winnerDisplayName: null }),
    );

    expect(result).toBe(board);
  });

  it('returns the same board reference when a null board is passed through', () => {
    const result = applyMoveRejected(null, makeRejectedEvent());

    expect(result).toBeNull();
  });
});
