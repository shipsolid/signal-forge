import { slug as githubSlug } from 'github-slugger';

// Shared wiki-link resolution rules, used by both remark-wiki-links.mjs (build
// rewrite) and scripts/check-links.mjs (CI audit) so the two never drift.
//
// The docs were authored inside the `notes` repo, so their [[wiki-links]] carry
// notes-isms this module normalizes away:
//   - a `projects/app-signal-forge/` slug prefix (the note's old home) — stripped
//   - `readme` as the folder-index name (Obsidian) vs `index` (Astro) — aliased
//   - links into OTHER notes books (`networks/...`, `prometheus/...`) that don't
//     live here — turned into absolute links to the notes site
// A `[[foo/bar]]` whose first segment isn't a known notes book and doesn't
// resolve here is left to the caller to unwrap to plain text.

export const DOCS_PREFIX = '/signal-forge';
export const NOTES_SITE = 'https://shipsolid.github.io/notes';
const OLD_PREFIX_RE = /^projects\/app-signal-forge\//i;

// Top-level directories of the `notes` content collection — a `[[<book>/...]]`
// link (note the required subpath) points at a page on the notes site.
const NOTES_BOOKS = new Set([
  'agentic-ai-engineering',
  'agentic-ai-projects-and-mastery',
  'ai-architecture-and-system-design',
  'ai-foundations',
  'aptitude',
  'building-agentic-systems',
  'ci-cd',
  'data-engineering',
  'data-structures-algorithms',
  'dbms',
  'grafana-cloud',
  'infrastructure-platform-engineering',
  'internal-developer-platforms',
  'kubernetes',
  'kubernetes-platform-engineering',
  'low-level-design',
  'networks',
  'object-oriented-programming',
  'observability',
  'operating-system',
  'patterns',
  'philosophy',
  'platform-engineering-fundamentals',
  'production-agent-systems',
  'productivity',
  'projects',
  'prometheus',
  'sre',
  'system-design',
]);

// Splits `key#heading` → { notePart, headingPart }.
export function splitHeading(key) {
  const hash = key.indexOf('#');
  if (hash === -1) return { notePart: key.trim(), headingPart: null };
  return { notePart: key.slice(0, hash).trim(), headingPart: key.slice(hash + 1).trim() };
}

// Normalizes a key to how it should resolve here: trims a trailing
// table-escape backslash, drops the old notes prefix, aliases `readme`→`index`.
export function normalizeKey(notePart) {
  return notePart
    .replace(/\\$/, '')
    .trim()
    .replace(OLD_PREFIX_RE, '')
    .replace(/(^|\/)readme$/i, '$1index');
}

// A link into another notes book: must have a subpath (`kubernetes/readme`,
// not a bare `kubernetes`, which is this repo's own infrastructure page), and
// must not carry the `projects/app-signal-forge/` prefix (that's one of ours,
// even when the tail — `observability/...` — collides with a book name).
export function isExternalNotesKey(notePart) {
  if (OLD_PREFIX_RE.test(notePart.replace(/\\$/, '').trim())) return false;
  const k = normalizeKey(notePart);
  if (!k.includes('/')) return false;
  return NOTES_BOOKS.has(k.split('/')[0].toLowerCase());
}

// Absolute URL on the notes site for an external key (`foo/readme` → `foo/`).
export function externalNotesUrl(notePart) {
  const segments = normalizeKey(notePart)
    .replace(/\/index$/, '')
    .split('/')
    .filter(Boolean)
    .map((s) => githubSlug(s));
  return `${NOTES_SITE}/${segments.join('/')}/`;
}

// index: { byFullSlug: Map<lowerSlug, slug>, byBasename: Map<lowerBasename, slug[]> }
// Returns a resolved slug ('' is the root index), null (unresolved), or 'ambiguous'.
export function resolveInternal(notePart, index) {
  const key = normalizeKey(notePart).toLowerCase();
  if (!key || key === 'index') return '';

  const exact = index.byFullSlug.get(key);
  if (exact !== undefined) return exact;

  let basename = key.split('/').pop();
  if (key.endsWith('/index')) {
    const folder = key.slice(0, -'/index'.length);
    const folderHit = index.byFullSlug.get(folder);
    if (folderHit !== undefined) return folderHit;
    basename = folder.split('/').pop(); // `kubernetes/readme` → basename `kubernetes`
  }

  const candidates = index.byBasename.get(basename) ?? [];
  if (candidates.length === 1) return candidates[0];
  if (candidates.length > 1) return 'ambiguous';
  return null;
}
