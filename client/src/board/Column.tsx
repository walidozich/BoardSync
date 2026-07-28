import { useDroppable } from '@dnd-kit/core';
import { SortableContext, verticalListSortingStrategy } from '@dnd-kit/sortable';
import type { BoardColumnDto } from '../api/board';
import { Card } from './Card';
import { CreateCardForm } from './CreateCardForm';
import type { CreateCardRejectedEvent } from './useBoardConnection';

interface ColumnProps {
  column: BoardColumnDto;
  createCard: (columnId: string, title: string, description: string | null) => void;
  createCardError: CreateCardRejectedEvent | null;
}

export function Column({ column, createCard, createCardError }: ColumnProps) {
  const cardIds = column.cards.map((card) => card.id);

  // A droppable placeholder rendered at the bottom of every column's card
  // list, empty or not. This is what makes an empty column (which has no
  // sortable cards for `over` to resolve against) a valid drop target, and
  // it also gives every column an unambiguous "drop at the end" target.
  const { setNodeRef: setDropzoneRef } = useDroppable({
    id: `${column.id}::dropzone`,
    data: { type: 'column', columnId: column.id },
  });

  return (
    <section>
      <h2>{column.name}</h2>
      <SortableContext items={cardIds} strategy={verticalListSortingStrategy}>
        <div>
          {column.cards.map((card) => (
            <Card key={card.id} card={card} columnId={column.id} />
          ))}
          <div
            ref={setDropzoneRef}
            data-testid={`column-dropzone-${column.id}`}
            style={{ minHeight: 24 }}
          />
        </div>
      </SortableContext>
      <CreateCardForm column={column} createCard={createCard} createCardError={createCardError} />
    </section>
  );
}
