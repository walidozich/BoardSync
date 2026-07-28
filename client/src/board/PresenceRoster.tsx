import type { PresenceUser } from './useBoardConnection';

interface PresenceRosterProps {
  users: PresenceUser[];
}

export function PresenceRoster({ users }: PresenceRosterProps) {
  return (
    <ul aria-label="Currently connected">
      {users.map((user) => (
        <li key={user.id}>{user.displayName}</li>
      ))}
    </ul>
  );
}
