import { DndContext, PointerSensor, useSensor, useSensors, type DragEndEvent } from '@dnd-kit/core';
import { useAuth } from '../auth/auth-context';
import { Avatar } from './Avatar';
import { Column } from './Column';
import { PresenceRoster } from './PresenceRoster';
import { useBoardConnection } from './useBoardConnection';
import styles from './BoardPage.module.css';

interface DropTargetData {
  type: 'card' | 'column';
  columnId: string;
}

export function BoardPage() {
  const { displayName, logout } = useAuth();
  const {
    board,
    presence,
    status,
    error,
    createCard,
    createCardError,
    moveCard,
    moveRejectedNotice,
    deleteCard,
  } = useBoardConnection();
  const sensors = useSensors(useSensor(PointerSensor));
  const isConnected = status === 'connected';

  function handleDragEnd(event: DragEndEvent) {
    const { active, over } = event;
    if (!isConnected || !board || !over || active.id === over.id) {
      return;
    }

    const cardId = String(active.id);
    const overData = over.data.current as DropTargetData | undefined;
    if (!overData) {
      return;
    }

    const targetColumn = board.columns.find((column) => column.id === overData.columnId);
    if (!targetColumn) {
      return;
    }

    // Neighbours are computed from the target column's *current* cards
    // (excluding the dragged card itself, in case it's already in this
    // column), sorted by position, never from a locally-computed index sent
    // as-is to the server.
    const otherCards = [...targetColumn.cards]
      .filter((card) => card.id !== cardId)
      .sort((a, b) => a.position - b.position);

    let afterCardId: string | null;
    let beforeCardId: string | null;

    if (overData.type === 'card') {
      const overIndex = otherCards.findIndex((card) => card.id === over.id);
      if (overIndex === -1) {
        // Dropped on the card being dragged, or a card no longer present;
        // fall back to appending at the end.
        afterCardId = otherCards.length > 0 ? otherCards[otherCards.length - 1].id : null;
        beforeCardId = null;
      } else {
        beforeCardId = otherCards[overIndex].id;
        afterCardId = overIndex > 0 ? otherCards[overIndex - 1].id : null;
      }
    } else {
      // Dropped on the column's end-of-list dropzone: append at the end.
      afterCardId = otherCards.length > 0 ? otherCards[otherCards.length - 1].id : null;
      beforeCardId = null;
    }

    moveCard(cardId, targetColumn.id, afterCardId, beforeCardId);
  }

  return (
    <main className={styles.page}>
      <header className={styles.topbar}>
        <span className={styles.wordmark}>
          <span className={styles.wordmarkDot} aria-hidden="true" />
          BoardSync
        </span>
        <PresenceRoster users={presence} />
        <span className={styles.spacer} />
        <span className={styles.you}>
          <Avatar name={displayName ?? ''} size={22} />
          {displayName}
        </span>
        <span className={styles.divider} aria-hidden="true" />
        <button type="button" className={styles.logout} onClick={logout}>
          Log out
        </button>
      </header>

      {moveRejectedNotice && (
        <p role="status" className={styles.toast}>
          {moveRejectedNotice}
        </p>
      )}

      {error && (
        <p className={`${styles.centerNote} ${styles.error}`}>Could not load the board: {error}</p>
      )}
      {!error && !board && <p className={styles.centerNote}>Loading...</p>}
      {board && (
        <>
          <div className={styles.header}>
            <h1 className={styles.title}>{board.name}</h1>
            <span className={styles.status} data-status={status}>
              <span className={styles.statusDot} aria-hidden="true" />
              Connection: {status}
            </span>
          </div>
          <DndContext sensors={sensors} onDragEnd={handleDragEnd}>
            <div className={styles.board}>
              {board.columns.map((column) => (
                <Column
                  key={column.id}
                  column={column}
                  createCard={createCard}
                  createCardError={createCardError}
                  deleteCard={deleteCard}
                  disabled={!isConnected}
                />
              ))}
            </div>
          </DndContext>
        </>
      )}
    </main>
  );
}
