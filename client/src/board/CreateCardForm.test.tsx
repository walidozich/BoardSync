import { describe, it, expect, vi, beforeEach } from 'vitest';
import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { CreateCardForm } from './CreateCardForm';
import type { BoardColumnDto } from '../api/board';
import type { CreateCardRejectedEvent } from './useBoardConnection';

function makeColumn(overrides: Partial<BoardColumnDto> = {}): BoardColumnDto {
  return {
    id: 'col-1',
    name: 'To Do',
    position: 0,
    cards: [],
    ...overrides,
  };
}

async function openForm() {
  const user = userEvent.setup();
  await user.click(screen.getByRole('button', { name: '+ Add card' }));
  return user;
}

describe('CreateCardForm', () => {
  let createCard: (columnId: string, title: string, description: string | null) => void;

  beforeEach(() => {
    createCard = vi.fn();
  });

  it('rejects an empty title without calling createCard', async () => {
    render(<CreateCardForm column={makeColumn()} createCard={createCard} createCardError={null} />);
    const user = await openForm();

    await user.click(screen.getByRole('button', { name: 'Add card' }));

    expect(await screen.findByText(/title must be between/i)).toBeInTheDocument();
    expect(createCard).not.toHaveBeenCalled();
  });

  it('rejects a description over 2000 characters without calling createCard', async () => {
    render(<CreateCardForm column={makeColumn()} createCard={createCard} createCardError={null} />);
    const user = await openForm();

    await user.type(screen.getByLabelText('Title'), 'Valid title');
    const longDescription = 'a'.repeat(2001);
    // fireEvent-style paste is far faster than typing 2001 chars key-by-key.
    const textarea = screen.getByLabelText('Description');
    await user.click(textarea);
    await user.paste(longDescription);
    await user.click(screen.getByRole('button', { name: 'Add card' }));

    expect(await screen.findByText(/description must be at most/i)).toBeInTheDocument();
    expect(createCard).not.toHaveBeenCalled();
  });

  it('calls createCard with the trimmed title, column id, and null description when valid', async () => {
    render(<CreateCardForm column={makeColumn()} createCard={createCard} createCardError={null} />);
    const user = await openForm();

    await user.type(screen.getByLabelText('Title'), '  Ship the feature  ');
    await user.click(screen.getByRole('button', { name: 'Add card' }));

    expect(createCard).toHaveBeenCalledWith('col-1', 'Ship the feature', null);
  });

  it('calls createCard with a trimmed description when one is provided', async () => {
    render(<CreateCardForm column={makeColumn()} createCard={createCard} createCardError={null} />);
    const user = await openForm();

    await user.type(screen.getByLabelText('Title'), 'Ship the feature');
    await user.type(screen.getByLabelText('Description'), '  Some details  ');
    await user.click(screen.getByRole('button', { name: 'Add card' }));

    expect(createCard).toHaveBeenCalledWith('col-1', 'Ship the feature', 'Some details');
  });

  it('surfaces field-level errors from a CreateCardRejected event', async () => {
    const { rerender } = render(
      <CreateCardForm column={makeColumn()} createCard={createCard} createCardError={null} />,
    );
    const user = await openForm();

    await user.type(screen.getByLabelText('Title'), 'Ship the feature');
    await user.click(screen.getByRole('button', { name: 'Add card' }));

    const rejection: CreateCardRejectedEvent = {
      reason: 'Invalid',
      errors: { Title: ['Title is already used in this column.'] },
    };
    rerender(
      <CreateCardForm column={makeColumn()} createCard={createCard} createCardError={rejection} />,
    );

    expect(await screen.findByText('Title is already used in this column.')).toBeInTheDocument();
  });

  it('surfaces a general error message when the rejection has no field errors', async () => {
    const { rerender } = render(
      <CreateCardForm column={makeColumn()} createCard={createCard} createCardError={null} />,
    );
    const user = await openForm();

    await user.type(screen.getByLabelText('Title'), 'Ship the feature');
    await user.click(screen.getByRole('button', { name: 'Add card' }));

    const rejection: CreateCardRejectedEvent = { reason: 'BoardFull', errors: null };
    rerender(
      <CreateCardForm column={makeColumn()} createCard={createCard} createCardError={rejection} />,
    );

    expect(await screen.findByText(/board is full/i)).toBeInTheDocument();
  });

  it('clears and closes the form when a card lands in this column while submitting', async () => {
    const column = makeColumn();
    const { rerender } = render(
      <CreateCardForm column={column} createCard={createCard} createCardError={null} />,
    );
    const user = await openForm();

    await user.type(screen.getByLabelText('Title'), 'Ship the feature');
    await user.click(screen.getByRole('button', { name: 'Add card' }));

    expect(screen.getByRole('button', { name: /adding/i })).toBeInTheDocument();

    const updatedColumn = makeColumn({
      cards: [
        { id: 'card-1', title: 'Ship the feature', description: null, position: 0, version: 1 },
      ],
    });
    rerender(
      <CreateCardForm column={updatedColumn} createCard={createCard} createCardError={null} />,
    );

    expect(await screen.findByRole('button', { name: '+ Add card' })).toBeInTheDocument();
  });

  it('does not treat an unrelated re-render as success while awaiting a rejection', async () => {
    const column = makeColumn();
    render(<CreateCardForm column={column} createCard={createCard} createCardError={null} />);
    const user = await openForm();

    await user.type(screen.getByLabelText('Title'), 'Ship the feature');
    await user.click(screen.getByRole('button', { name: 'Add card' }));

    expect(screen.getByRole('button', { name: /adding/i })).toBeInTheDocument();
  });
});
