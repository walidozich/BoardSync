const API_URL = import.meta.env.VITE_API_URL ?? 'http://localhost:5080';

export interface BoardDto {
  id: string;
  name: string;
}

export async function fetchBoard(): Promise<BoardDto> {
  const response = await fetch(`${API_URL}/api/board`);

  if (!response.ok) {
    throw new Error(`Failed to load board: ${response.status}`);
  }

  return response.json();
}
