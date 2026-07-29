// A small fixed palette in the spirit of Notion's tag colors -- solid enough to read as white
// text on top, distinct enough from each other at a glance. Shared by presence avatars and
// column accent dots so the same string (a display name, a column name) always maps to the
// same color everywhere it appears, without either caller having to agree on an assignment.
const PALETTE = [
  '#EB5757',
  '#D9730D',
  '#CB912F',
  '#0F7B6C',
  '#2383E2',
  '#9065B0',
  '#C14C8A',
  '#6B7280',
];

export function colorFromString(value: string): string {
  let hash = 0;
  for (let i = 0; i < value.length; i++) {
    hash = (hash * 31 + value.charCodeAt(i)) | 0;
  }
  return PALETTE[Math.abs(hash) % PALETTE.length];
}

export function initialsFromName(name: string): string {
  const parts = name.trim().split(/\s+/).filter(Boolean);
  if (parts.length === 0) {
    return '?';
  }
  if (parts.length === 1) {
    return parts[0].slice(0, 2).toUpperCase();
  }
  return (parts[0][0] + parts[parts.length - 1][0]).toUpperCase();
}
