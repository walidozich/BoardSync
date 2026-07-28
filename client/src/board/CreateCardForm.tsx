import { useState, type FormEvent } from 'react';
import type { BoardColumnDto } from '../api/board';
import type { CreateCardRejectedEvent } from './useBoardConnection';

const MAX_TITLE_LENGTH = 200;
const MAX_DESCRIPTION_LENGTH = 2000;

interface CreateCardFormProps {
  column: BoardColumnDto;
  createCard: (columnId: string, title: string, description: string | null) => void;
  createCardError: CreateCardRejectedEvent | null;
}

function validate(title: string, description: string): Record<string, string> {
  const errors: Record<string, string> = {};

  if (title.trim().length < 1 || title.trim().length > MAX_TITLE_LENGTH) {
    errors.title = `Title must be between 1 and ${MAX_TITLE_LENGTH} characters.`;
  }
  if (description.trim().length > MAX_DESCRIPTION_LENGTH) {
    errors.description = `Description must be at most ${MAX_DESCRIPTION_LENGTH} characters.`;
  }

  return errors;
}

function reasonMessage(reason: CreateCardRejectedEvent['reason']): string {
  switch (reason) {
    case 'ColumnNotFound':
      return 'This column no longer exists.';
    case 'BoardFull':
      return 'This board is full. Remove a card before adding another.';
    case 'Invalid':
    default:
      return 'Could not create the card. Please check your input.';
  }
}

export function CreateCardForm({ column, createCard, createCardError }: CreateCardFormProps) {
  const [expanded, setExpanded] = useState(false);
  const [title, setTitle] = useState('');
  const [description, setDescription] = useState('');
  const [fieldErrors, setFieldErrors] = useState<Record<string, string>>({});
  const [formError, setFormError] = useState<string | null>(null);
  const [submitting, setSubmitting] = useState(false);

  // These two blocks adjust state in response to prop changes (a rejection
  // arriving, or a card landing in this column) directly during render,
  // per React's guidance for "adjusting state when a prop changes" —
  // https://react.dev/learn/you-might-not-need-an-effect — rather than in a
  // useEffect, which would cause an extra commit + effect + re-render.

  const [lastHandledError, setLastHandledError] = useState<CreateCardRejectedEvent | null>(null);
  if (createCardError !== lastHandledError) {
    setLastHandledError(createCardError);

    // Server rejected the create attempt: surface it and stop submitting.
    if (submitting && createCardError) {
      setSubmitting(false);

      if (createCardError.errors) {
        const mapped: Record<string, string> = {};
        for (const [field, messages] of Object.entries(createCardError.errors)) {
          if (messages.length > 0) {
            mapped[field.charAt(0).toLowerCase() + field.slice(1)] = messages[0];
          }
        }
        setFieldErrors(mapped);
      } else {
        setFormError(reasonMessage(createCardError.reason));
      }
    }
  }

  const [lastSeenCardCount, setLastSeenCardCount] = useState(column.cards.length);
  if (column.cards.length !== lastSeenCardCount) {
    const grew = column.cards.length > lastSeenCardCount;
    setLastSeenCardCount(column.cards.length);

    // A new card landed in this column while we were waiting, and no
    // rejection arrived instead: treat that as our submission succeeding.
    if (submitting && !createCardError && grew) {
      setSubmitting(false);
      setTitle('');
      setDescription('');
      setFieldErrors({});
      setFormError(null);
      setExpanded(false);
    }
  }

  function handleSubmit(event: FormEvent) {
    event.preventDefault();
    setFormError(null);

    const errors = validate(title, description);
    setFieldErrors(errors);
    if (Object.keys(errors).length > 0) {
      return;
    }

    setSubmitting(true);
    const trimmedDescription = description.trim();
    createCard(column.id, title.trim(), trimmedDescription.length > 0 ? trimmedDescription : null);
  }

  function handleCancel() {
    setExpanded(false);
    setTitle('');
    setDescription('');
    setFieldErrors({});
    setFormError(null);
  }

  if (!expanded) {
    return (
      <button type="button" onClick={() => setExpanded(true)}>
        + Add card
      </button>
    );
  }

  return (
    <form onSubmit={handleSubmit} noValidate aria-label={`Add card to ${column.name}`}>
      <label>
        Title
        <input type="text" value={title} onChange={(e) => setTitle(e.target.value)} autoFocus />
      </label>
      {fieldErrors.title && <p role="alert">{fieldErrors.title}</p>}

      <label>
        Description
        <textarea value={description} onChange={(e) => setDescription(e.target.value)} />
      </label>
      {fieldErrors.description && <p role="alert">{fieldErrors.description}</p>}

      {formError && <p role="alert">{formError}</p>}

      <button type="submit" disabled={submitting}>
        {submitting ? 'Adding…' : 'Add card'}
      </button>
      <button type="button" onClick={handleCancel} disabled={submitting}>
        Cancel
      </button>
    </form>
  );
}
