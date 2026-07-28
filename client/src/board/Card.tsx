import type { CardDto } from '../api/board';

interface CardProps {
  card: CardDto;
}

export function Card({ card }: CardProps) {
  return (
    <article>
      <p>{card.title}</p>
      {card.description !== null && <p>{card.description}</p>}
    </article>
  );
}
