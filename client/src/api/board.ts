import { apiFetch } from './client';

export interface BoardDto {
  id: string;
  name: string;
}

export function fetchBoard(token: string | null): Promise<BoardDto> {
  return apiFetch<BoardDto>('/api/board', { token });
}
