import type { CSSProperties } from 'react';
import { useSortable } from '@dnd-kit/sortable';
import { Trash2 } from 'lucide-react';
import type { CardDto } from '../api/board';
import styles from './Card.module.css';

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
  };

  return (
    <article
      ref={setNodeRef}
      style={style}
      className={styles.card}
      data-disabled={disabled}
      data-dragging={isDragging}
      {...attributes}
      {...listeners}
    >
      <div className={styles.row}>
        <p className={styles.title}>{card.title}</p>
        {/* `listeners` above (spread onto the article) is dnd-kit's PointerSensor
            hook: it starts a drag on pointerdown anywhere inside the article,
            including bubbled from this button. Stopping propagation on
            pointerdown keeps a delete click from also kicking off a drag. */}
        <button
          type="button"
          className={styles.deleteButton}
          disabled={disabled}
          aria-label="Delete"
          onPointerDown={(e) => e.stopPropagation()}
          onClick={() => deleteCard(card.id)}
        >
          <Trash2 size={14} aria-hidden="true" />
        </button>
      </div>
      {card.description !== null && <p className={styles.description}>{card.description}</p>}
      {/* Dev-only: makes renormalization's effect (positions halving toward the
          threshold, then snapping back to round numbers) visible while dragging.
          import.meta.env.DEV is Vite's own dev/prod flag -- stripped from
          production builds, no new env var needed. */}
      {import.meta.env.DEV && <p className={styles.position}>pos: {card.position.toFixed(4)}</p>}
    </article>
  );
}
