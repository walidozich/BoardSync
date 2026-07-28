import { useEffect, useState } from 'react';
import { HubConnectionBuilder, type HubConnection } from '@microsoft/signalr';
import type { BoardDto } from '../api/board';
import { useAuth } from '../auth/auth-context';

const API_URL = import.meta.env.VITE_API_URL ?? 'http://localhost:5080';

export type BoardConnectionStatus = 'connecting' | 'connected' | 'disconnected';

export interface PresenceUser {
  id: string;
  displayName: string;
}

export interface BoardConnectionState {
  board: BoardDto | null;
  presence: PresenceUser[];
  status: BoardConnectionStatus;
  error: string | null;
}

export function useBoardConnection(): BoardConnectionState {
  const { token } = useAuth();
  const [board, setBoard] = useState<BoardDto | null>(null);
  const [presence, setPresence] = useState<PresenceUser[]>([]);
  const [status, setStatus] = useState<BoardConnectionStatus>('connecting');
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    if (!token) {
      return;
    }

    const connection: HubConnection = new HubConnectionBuilder()
      .withUrl(`${API_URL}/hubs/board?access_token=${encodeURIComponent(token)}`)
      .build();

    let cancelled = false;

    connection.on('BoardSnapshot', (snapshot: BoardDto) => {
      if (!cancelled) {
        setBoard(snapshot);
      }
    });

    connection.on('PresenceChanged', (roster: PresenceUser[]) => {
      if (!cancelled) {
        setPresence(roster);
      }
    });

    connection.onclose(() => {
      if (!cancelled) {
        setStatus('disconnected');
        setPresence([]);
      }
    });

    connection
      .start()
      .then(() => {
        if (cancelled) {
          return;
        }
        setStatus('connected');
        return connection.invoke('JoinBoard');
      })
      .catch((err: unknown) => {
        if (!cancelled) {
          setError(err instanceof Error ? err.message : 'Unknown error');
          setStatus('disconnected');
        }
      });

    return () => {
      cancelled = true;
      void connection.stop();
    };
  }, [token]);

  if (!token) {
    return { board: null, presence: [], status: 'disconnected', error: null };
  }

  return { board, presence, status, error };
}
