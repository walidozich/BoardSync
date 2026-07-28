import { apiFetch } from './client';

export interface CardDto {
  id: string;
  title: string;
  description: string | null;
  position: number;
  version: number;
}

export interface BoardColumnDto {
  id: string;
  name: string;
  position: number;
  cards: CardDto[];
}

export interface BoardDto {
  id: string;
  name: string;
  columns: BoardColumnDto[];
}

export function fetchBoard(token: string | null): Promise<BoardDto> {
  return apiFetch<BoardDto>('/api/board', { token });
}
