import { useDroppable } from '@dnd-kit/core';
import { SortableContext, verticalListSortingStrategy } from '@dnd-kit/sortable';
import type { BoardColumnDto } from '../api/board';
import { colorFromString } from '../lib/colorFromString';
import { Card } from './Card';
import { CreateCardForm } from './CreateCardForm';
import type { CreateCardRejectedEvent } from './useBoardConnection';
import styles from './Column.module.css';

interface ColumnProps {
  column: BoardColumnDto;
  createCard: (columnId: string, title: string, description: string | null) => void;
  createCardError: CreateCardRejectedEvent | null;
  deleteCard: (cardId: string) => void;
  disabled: boolean;
}

export function Column({ column, createCard, createCardError, deleteCard, disabled }: ColumnProps) {
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
    <section className={styles.column}>
      <div className={styles.header}>
        <span className={styles.dot} style={{ backgroundColor: colorFromString(column.name) }} />
        <h2 className={styles.name}>{column.name}</h2>
        <span className={styles.count}>{column.cards.length}</span>
      </div>
      <SortableContext items={cardIds} strategy={verticalListSortingStrategy}>
        <div className={styles.cards}>
          {column.cards.map((card) => (
            <Card
              key={card.id}
              card={card}
              columnId={column.id}
              deleteCard={deleteCard}
              disabled={disabled}
            />
          ))}
          <div
            ref={setDropzoneRef}
            data-testid={`column-dropzone-${column.id}`}
            className={styles.dropzone}
          />
        </div>
      </SortableContext>
      <CreateCardForm
        column={column}
        createCard={createCard}
        createCardError={createCardError}
        disabled={disabled}
      />
    </section>
  );
}
