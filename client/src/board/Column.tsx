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
  return (
    <section>
      <h2>{column.name}</h2>
      <div>
        {column.cards.map((card) => (
          <Card key={card.id} card={card} />
        ))}
      </div>
      <CreateCardForm column={column} createCard={createCard} createCardError={createCardError} />
    </section>
  );
}
