import { useEffect, useState } from 'react';
import { fetchBoard, type BoardDto } from './api/board';

function App() {
  const [board, setBoard] = useState<BoardDto | null>(null);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    fetchBoard()
      .then(setBoard)
      .catch((err: unknown) => setError(err instanceof Error ? err.message : 'Unknown error'));
  }, []);

  if (error) {
    return <p>Could not load the board: {error}</p>;
  }

  if (!board) {
    return <p>Loading...</p>;
  }

  return (
    <main>
      <h1>{board.name}</h1>
    </main>
  );
}

export default App;
