import { test, expect, type Page } from '@playwright/test';

const CARD_TITLE = 'Set up the SignalR hub';
const ALL_COLUMNS = ['Backlog', 'In Progress', 'Review', 'Done'];

async function registerAndLandOnBoard(page: Page, displayName: string) {
  await page.goto('/');
  await page.getByRole('tab', { name: 'Register' }).click();
  await page
    .getByLabel('Email')
    .fill(`${displayName.toLowerCase()}-${crypto.randomUUID()}@example.com`);
  await page.getByLabel('Password').fill('correct-horse-battery-staple');
  await page.getByLabel('Display name').fill(displayName);
  await page.getByRole('button', { name: 'Register' }).click();

  await expect(page.getByText('Connection: connected')).toBeVisible();
}

function columnSection(page: Page, columnName: string) {
  // No two column names are substrings of one another ('Backlog', 'In Progress', 'Review',
  // 'Done'), so plain hasText is unambiguous here; `locator()`'s options (unlike
  // `getByText()`'s) don't support an `exact` flag at all.
  return page.locator('section', { has: page.locator('h2', { hasText: columnName }) });
}

async function columnContainingCard(page: Page, cardTitle: string): Promise<string | null> {
  const sections = page.locator('section');
  const count = await sections.count();
  for (let i = 0; i < count; i++) {
    const section = sections.nth(i);
    if ((await section.getByText(cardTitle, { exact: true }).count()) > 0) {
      return section.locator('h2').innerText();
    }
  }
  return null;
}

// dnd-kit's PointerSensor reacts to real pointer events, not the HTML5 drag-and-drop API, so
// this drives a manual mouse sequence (down, several intermediate moves, up) rather than
// Playwright's dragTo helper, which only dispatches HTML5 dragstart/dragover/drop events.
async function dragCardToColumn(page: Page, cardTitle: string, targetColumnName: string) {
  const card = page.locator('article', { hasText: cardTitle });
  const dropzone = columnSection(page, targetColumnName).locator(
    '[data-testid^="column-dropzone-"]',
  );

  const cardBox = await card.boundingBox();
  const dropBox = await dropzone.boundingBox();
  if (!cardBox || !dropBox) {
    throw new Error(`Could not locate card "${cardTitle}" or dropzone for "${targetColumnName}"`);
  }

  const startX = cardBox.x + cardBox.width / 2;
  const startY = cardBox.y + cardBox.height / 2;
  const endX = dropBox.x + dropBox.width / 2;
  const endY = dropBox.y + dropBox.height / 2;

  await page.mouse.move(startX, startY);
  await page.mouse.down();
  const steps = 12;
  for (let i = 1; i <= steps; i++) {
    await page.mouse.move(
      startX + ((endX - startX) * i) / steps,
      startY + ((endY - startY) * i) / steps,
    );
  }
  await page.mouse.up();
}

test('two users dragging the same card to different columns at once: one wins, one snaps back with a toast', async ({
  browser,
}) => {
  // Two independent browser contexts, not two tabs sharing one -- each gets its own
  // localStorage-backed session, so the two registrations below don't clobber each other.
  // Contexts created manually like this bypass Playwright Test's fixture-injected
  // context/page, which is the only place config's `use.video` gets applied -- so video
  // has to be requested explicitly here, per context, to get a recording of each browser.
  // Full-size viewport so the board's 4 columns lay out without overflow/scrolling (a
  // cramped viewport pushed columns past the visible edge, breaking drop-zone bounding
  // boxes); ffmpeg downscales the recording afterward for the write-up GIF.
  const videoOptions = { dir: 'test-results/videos', size: { width: 1280, height: 720 } };
  const contextA = await browser.newContext({
    recordVideo: videoOptions,
    viewport: videoOptions.size,
  });
  const contextB = await browser.newContext({
    recordVideo: videoOptions,
    viewport: videoOptions.size,
  });
  const pageA = await contextA.newPage();
  const pageB = await contextB.newPage();

  try {
    await Promise.all([
      registerAndLandOnBoard(pageA, 'Alice'),
      registerAndLandOnBoard(pageB, 'Bob'),
    ]);

    await expect(pageA.locator('article', { hasText: CARD_TITLE })).toBeVisible();
    await expect(pageB.locator('article', { hasText: CARD_TITLE })).toBeVisible();

    // The demo board is shared, persistent state (not reset between runs), so the card's
    // starting column from a previous run is unknown -- picking targets that exclude wherever
    // it currently sits keeps this a genuine cross-column move both times, on every run,
    // rather than assuming fixed 'In Progress'/'Review' targets that a prior run may have
    // already made a no-op.
    const startColumn = await columnContainingCard(pageA, CARD_TITLE);
    const [targetA, targetB] = ALL_COLUMNS.filter((c) => c !== startColumn);

    // Fired concurrently via Promise.all (not awaited one after the other) so both MoveCard
    // invokes land on the server within the same artificial-delay window, forcing the real
    // race rather than two moves that happen to run sequentially.
    await Promise.all([
      dragCardToColumn(pageA, CARD_TITLE, targetA),
      dragCardToColumn(pageB, CARD_TITLE, targetB),
    ]);

    // moveRejectedNotice auto-clears itself after 4s (useBoardConnection.ts), so the loser's
    // toast has to be caught as it appears, not found afterwards -- checking final board state
    // first (which itself takes a moment to settle) risks the toast already being gone by the
    // time this looks for it. Both pages are polled together, capturing which one shows it into
    // `loserId` for the assertions below.
    // dnd-kit also injects its own `role="status"` live region for drag accessibility
    // announcements, so the app's own toast (a `<p role="status">`) needs a tag-scoped locator.
    let loserId: 'A' | 'B' | null = null;
    await expect
      .poll(
        async () => {
          const [aVisible, bVisible] = await Promise.all([
            pageA.locator('p[role="status"]').isVisible(),
            pageB.locator('p[role="status"]').isVisible(),
          ]);
          loserId = aVisible ? 'A' : bVisible ? 'B' : null;
          return loserId;
        },
        { timeout: 8_000, message: 'expected exactly one page to show the rejection toast' },
      )
      .not.toBeNull();

    const [loserPage, winnerPage] = loserId === 'A' ? [pageA, pageB] : [pageB, pageA];
    await expect(loserPage.locator('p[role="status"]')).toContainText(/moved this card first/);
    await expect(winnerPage.locator('p[role="status"]')).toHaveCount(0);

    await expect
      .poll(
        async () => {
          const colA = await columnContainingCard(pageA, CARD_TITLE);
          const colB = await columnContainingCard(pageB, CARD_TITLE);
          return colA !== null && colA === colB ? colA : null;
        },
        {
          timeout: 10_000,
          message: 'both browsers should converge on the same column once the race resolves',
        },
      )
      .not.toBeNull();

    const finalColumn = await columnContainingCard(winnerPage, CARD_TITLE);
    expect([targetA, targetB]).toContain(finalColumn);
  } finally {
    await contextA.close();
    await contextB.close();
  }
});
