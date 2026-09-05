import { slug as githubSlug } from 'github-slugger';

// Shared wiki-link resolution rules, used by both remark-wiki-links.mjs (build
// rewrite) and scripts/check-links.mjs (CI audit) so the two never drift.
//
// The docs were authored inside the `notes` repo, so their [[wiki-links]] carry
// notes-isms this module normalizes away:
//   - a `projects/app-signal-forge/` slug prefix (the note's old home) — stripped
//   - `readme` as the folder-index name (Obsidian) vs `index` (Astro) — aliased
//   - links into sibling notes trees (`tech/*`, `patterns/*`) that don't live
//     here — turned into absolute links to the notes site instead of 404s

export const DOCS_PREFIX = '/signal-forge';
export const NOTES_SITE = 'https://shipsolid.github.io/notes';
const OLD_PREFIX_RE = /^projects\/app-signal-forge\//i;
const EXTERNAL_TOP = new Set(['tech', 'patterns']);

// Splits `key#heading` → { notePart, headingPart }. headingPart is null when
// there's no fragment.
export function splitHeading(key) {
  const hash = key.indexOf('#');
  if (hash === -1) return { notePart: key.trim(), headingPart: null };
  return { notePart: key.slice(0, hash).trim(), headingPart: key.slice(hash + 1).trim() };
}

// Normalizes a wiki-link key to how it should resolve against this docs tree:
// trims a trailing table-escape backslash, drops the old notes prefix, aliases
// a trailing `readme` to `index`.
export function normalizeKey(notePart) {
  let k = notePart.replace(/\\$/, '').trim();
  k = k.replace(OLD_PREFIX_RE, '');
  k = k.replace(/(^|\/)readme$/i, '$1index');
  return k;
}

// Is this a link into a sibling notes tree rather than a docs page?
export function isExternalNotesKey(notePart) {
  const k = normalizeKey(notePart);
  const top = k.split('/')[0]?.toLowerCase();
  return EXTERNAL_TOP.has(top);
}

// Absolute URL on the notes site for an external key.
export function externalNotesUrl(notePart) {
  const segments = normalizeKey(notePart)
    .split('/')
    .filter(Boolean)
    .map((s) => githubSlug(s));
  return `${NOTES_SITE}/${segments.join('/')}/`;
}

// index: { byFullSlug: Map<lowerSlug, slug>, byBasename: Map<lowerBasename, slug[]> }
// Returns a resolved slug string, null (unresolved), or 'ambiguous'.
export function resolveInternal(notePart, index) {
  const key = normalizeKey(notePart).toLowerCase();
  if (!key || key === 'index') return null;

  const exact = index.byFullSlug.get(key);
  if (exact) return exact;

  // `index` alias also means "the folder itself" — try the parent slug.
  if (key.endsWith('/index')) {
    const folder = key.slice(0, -'/index'.length);
    const folderHit = index.byFullSlug.get(folder);
    if (folderHit) return folderHit;
  }

  const basename = key.split('/').pop();
  const candidates = index.byBasename.get(basename) ?? [];
  if (candidates.length === 1) return candidates[0];
  if (candidates.length > 1) return 'ambiguous';
  return null;
}
