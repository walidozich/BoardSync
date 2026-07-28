import { useEffect, useState } from 'react';
import { fetchBoard, type BoardDto } from '../api/board';
import { useAuth } from '../auth/auth-context';

export function BoardPage() {
  const { token, displayName, logout } = useAuth();
  const [board, setBoard] = useState<BoardDto | null>(null);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    fetchBoard(token)
      .then(setBoard)
      .catch((err: unknown) => setError(err instanceof Error ? err.message : 'Unknown error'));
  }, [token]);

  return (
    <main>
      <header>
        <span>{displayName}</span>
        <button type="button" onClick={logout}>
          Log out
        </button>
      </header>

      {error && <p>Could not load the board: {error}</p>}
      {!error && !board && <p>Loading...</p>}
      {board && <h1>{board.name}</h1>}
    </main>
  );
}
