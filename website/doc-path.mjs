import path from 'node:path';
import fs from 'node:fs';
import { slug as githubSlug } from 'github-slugger';

// Ported from the `notes` repo's note-path.mjs — same slug rules so a docs
// page's URL here matches how Astro/Starlight and GitHub Pages' case-sensitive
// hosting will actually serve it.

// Given an extensionless path, find the real source file backing it: a direct
// .md/.mdx first, an index.md/mdx inside it second. null if nothing backs it.
export function resolveContentFile(resolvedAbs) {
  const candidates = [
    `${resolvedAbs}.md`,
    `${resolvedAbs}.mdx`,
    path.join(resolvedAbs, 'index.md'),
    path.join(resolvedAbs, 'index.mdx'),
  ];
  return candidates.find((candidate) => fs.existsSync(candidate)) ?? null;
}

// Site slug (extensionless, index-collapsed, posix-separated) for a content
// file, given its absolute path and the collection root. null if the file is
// outside absRoot. Each segment is folded through github-slugger to match
// Astro's own content-collection slug generation — without this a segment with
// uppercase (README.md) builds fine but 404s on GitHub Pages.
export function slugFromBackingFile(backingFile, absRoot) {
  const relFromRoot = path.relative(absRoot, backingFile);
  if (relFromRoot.startsWith('..')) return null;

  const folded = relFromRoot
    .replace(/\.mdx?$/i, '')
    .split(path.sep)
    .filter(Boolean)
    .map((segment) => githubSlug(segment))
    .join('/');

  return folded.replace(/(^|\/)index$/i, '');
}
