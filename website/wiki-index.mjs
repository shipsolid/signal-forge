import fs from 'node:fs';
import path from 'node:path';
import GithubSlugger from 'github-slugger';
import { slugFromBackingFile } from './doc-path.mjs';

// Filesystem-backed lookups over the generated docs tree, shared by
// scripts/gen-docs.mjs (the wiki-link rewrite) and scripts/check-links.mjs.

const ATX_HEADING_RE = /^#{1,6}\s+(.*?)\s*#*\s*$/;
const FENCE_RE = /^(```|~~~)/;

// { byFullSlug: Map<lowerSlug, slug>, byBasename: Map<lowerBasename, slug[]>,
//   absOf(slug) }
export function buildDocIndex(docsRoot) {
  const byFullSlug = new Map();
  const byBasename = new Map();

  (function walk(dir) {
    for (const entry of fs.readdirSync(dir, { withFileTypes: true })) {
      const full = path.join(dir, entry.name);
      if (entry.isDirectory()) {
        walk(full);
        continue;
      }
      if (!/\.mdx?$/i.test(entry.name)) continue;
      const slug = slugFromBackingFile(full, docsRoot);
      if (slug === null) continue;
      byFullSlug.set(slug.toLowerCase(), slug);
      const base = slug.split('/').pop().toLowerCase();
      if (!byBasename.has(base)) byBasename.set(base, []);
      byBasename.get(base).push(slug);
    }
  })(docsRoot);

  return {
    byFullSlug,
    byBasename,
    absOf: (slug) => path.join(docsRoot, `${slug || 'index'}.md`),
  };
}

const titleCache = new Map();
export function titleFor(absPath) {
  if (titleCache.has(absPath)) return titleCache.get(absPath);
  let title = null;
  try {
    const fm = fs.readFileSync(absPath, 'utf8').match(/^---\r?\n([\s\S]*?)\r?\n---/);
    const m = fm && fm[1].match(/^title:[ \t]*(.+?)[ \t]*$/m);
    if (m) title = m[1].replace(/^(['"])(.*)\1$/, '$2');
  } catch {
    /* humanized-slug fallback happens in the caller */
  }
  titleCache.set(absPath, title);
  return title;
}

const headingCache = new Map();
// Map<lowercased heading text, the id Astro/github-slugger assigns it>.
export function headingsFor(absPath) {
  if (headingCache.has(absPath)) return headingCache.get(absPath);
  const map = new Map();
  try {
    const raw = fs.readFileSync(absPath, 'utf8').replace(/^---\r?\n[\s\S]*?\r?\n---/, '');
    const slugger = new GithubSlugger();
    let inFence = false;
    for (const line of raw.split(/\r?\n/)) {
      if (FENCE_RE.test(line.trim())) {
        inFence = !inFence;
        continue;
      }
      if (inFence) continue;
      const m = line.match(ATX_HEADING_RE);
      if (m) map.set(m[1].trim().toLowerCase(), slugger.slug(m[1].trim()));
    }
  } catch {
    /* empty map → fragment silently drops */
  }
  headingCache.set(absPath, map);
  return map;
}

export function humanizeSlug(slug) {
  return (slug || 'index')
    .split('/')
    .pop()
    .split('-')
    .map((w) => w.charAt(0).toUpperCase() + w.slice(1))
    .join(' ');
}
