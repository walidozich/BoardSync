import { useAuth } from '../auth/auth-context';
import { Column } from './Column';
import { PresenceRoster } from './PresenceRoster';
import { useBoardConnection } from './useBoardConnection';

export function BoardPage() {
  const { displayName, logout } = useAuth();
  const { board, presence, status, error } = useBoardConnection();

  return (
    <main>
      <header>
        <span>{displayName}</span>
        <button type="button" onClick={logout}>
          Log out
        </button>
      </header>

      <p>Connection: {status}</p>
      <PresenceRoster users={presence} />

      {error && <p>Could not load the board: {error}</p>}
      {!error && !board && <p>Loading...</p>}
      {board && (
        <>
          <h1>{board.name}</h1>
          <div style={{ display: 'flex', flexDirection: 'row' }}>
            {board.columns.map((column) => (
              <Column key={column.id} column={column} />
            ))}
          </div>
        </>
      )}
    </main>
  );
}
