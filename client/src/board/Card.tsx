import type { CSSProperties } from 'react';
import { useSortable } from '@dnd-kit/sortable';
import type { CardDto } from '../api/board';

interface CardProps {
  card: CardDto;
  columnId: string;
}

export function Card({ card, columnId }: CardProps) {
  const { attributes, listeners, setNodeRef, transform, transition, isDragging } = useSortable({
    id: card.id,
    data: { type: 'card', columnId },
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
    </article>
  );
}
