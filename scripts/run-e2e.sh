#!/usr/bin/env sh
# Runs phase 14's Playwright concurrency race end to end:
# bring the api container up with the artificial delay overlay, wait for it to be healthy,
# run the test, restore the api container to its normal (delay = 0) config, then convert
# Playwright's .webm recording to a .gif for the write-up.
set -eu

cd "$(dirname "$0")/.."

echo "==> Starting api with the E2E delay overlay (ConcurrencyDemo__ArtificialDelayMs=1500)"
docker compose -f docker-compose.yml -f docker-compose.e2e.yml up -d db api client

echo "==> Waiting for the api to report healthy"
for _ in $(seq 1 30); do
  if curl -sf http://localhost:"${API_PORT:-5080}"/health > /dev/null; then
    break
  fi
  sleep 1
done
curl -sf http://localhost:"${API_PORT:-5080}"/health > /dev/null || {
  echo "api never became healthy" >&2
  exit 1
}

restore_api() {
  echo "==> Restoring api to the normal dev config (ConcurrencyDemo__ArtificialDelayMs=0)"
  docker compose up -d api
}
trap restore_api EXIT

echo "==> Running the Playwright test"
rm -rf client/test-results
(cd client && pnpm exec playwright test)

# Each browser context records its own .webm; sorted by name (Alice's context is created
# first) so the composited GIF is deterministic left/right rather than whichever file glob
# order the filesystem happens to return.
videos=$(find client/test-results/videos -name '*.webm' | sort)
videoCount=$(echo "$videos" | grep -c . || true)

if [ "$videoCount" -eq 2 ]; then
  left=$(echo "$videos" | sed -n '1p')
  right=$(echo "$videos" | sed -n '2p')
  echo "==> Compositing both browsers side by side into a GIF"
  ffmpeg -y -i "$left" -i "$right" \
    -filter_complex "[0:v][1:v]hstack=inputs=2,fps=10,scale=960:-1:flags=lanczos" \
    client/e2e/concurrency-race.gif
  echo "==> GIF written to client/e2e/concurrency-race.gif"
else
  echo "Expected 2 .webm recordings under client/test-results/videos, found $videoCount" >&2
fi
