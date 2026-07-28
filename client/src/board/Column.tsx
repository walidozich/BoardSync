import type { BoardColumnDto } from '../api/board';
import { Card } from './Card';

interface ColumnProps {
  column: BoardColumnDto;
}

export function Column({ column }: ColumnProps) {
  return (
    <section>
      <h2>{column.name}</h2>
      <div>
        {column.cards.map((card) => (
          <Card key={card.id} card={card} />
        ))}
      </div>
    </section>
  );
}
