import type { PresenceUser } from './useBoardConnection';
import { Avatar } from './Avatar';
import styles from './PresenceRoster.module.css';

interface PresenceRosterProps {
  users: PresenceUser[];
}

export function PresenceRoster({ users }: PresenceRosterProps) {
  return (
    <ul aria-label="Currently connected" className={styles.list}>
      {users.map((user) => (
        <li key={user.id} className={styles.chip}>
          <Avatar name={user.displayName} size={20} />
          {user.displayName}
        </li>
      ))}
    </ul>
  );
}
