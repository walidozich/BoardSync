import type { CSSProperties } from 'react';
import { useSortable } from '@dnd-kit/sortable';
import type { CardDto } from '../api/board';

interface CardProps {
  card: CardDto;
  columnId: string;
  deleteCard: (cardId: string) => void;
  disabled: boolean;
}

export function Card({ card, columnId, deleteCard, disabled }: CardProps) {
  const { attributes, listeners, setNodeRef, transform, transition, isDragging } = useSortable({
    id: card.id,
    data: { type: 'card', columnId },
    disabled,
  });

  // Built by hand (rather than pulling in @dnd-kit/utilities' CSS.Transform
  // helper) since only @dnd-kit/core and @dnd-kit/sortable are approved
  // dependencies for this project; the transform shape dnd-kit produces is
  // small enough not to warrant a third package for one string.
  const style: CSSProperties = {
    transform: transform ? `translate3d(${transform.x}px, ${transform.y}px, 0)` : undefined,
    transition: transition ?? undefined,
    opacity: isDragging ? 0.5 : 1,
  };

  return (
    <article ref={setNodeRef} style={style} {...attributes} {...listeners}>
      <p>{card.title}</p>
      {card.description !== null && <p>{card.description}</p>}
      {/* Dev-only: makes renormalization's effect (positions halving toward the
          threshold, then snapping back to round numbers) visible while dragging.
          import.meta.env.DEV is Vite's own dev/prod flag -- stripped from
          production builds, no new env var needed. */}
      {import.meta.env.DEV && (
        <p style={{ fontSize: '0.75em', opacity: 0.6 }}>pos: {card.position.toFixed(4)}</p>
      )}
      {/* `listeners` above (spread onto the article) is dnd-kit's PointerSensor
          hook: it starts a drag on pointerdown anywhere inside the article,
          including bubbled from this button. Stopping propagation on
          pointerdown keeps a delete click from also kicking off a drag. */}
      <button
        type="button"
        disabled={disabled}
        onPointerDown={(e) => e.stopPropagation()}
        onClick={() => deleteCard(card.id)}
      >
        Delete
      </button>
    </article>
  );
}
