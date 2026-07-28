import { describe, it, expect } from 'vitest';
import { applyCardCreated, type CardCreatedEvent } from './useBoardConnection';
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
