import path from 'node:path';
import fs from 'node:fs';
import * as yaml from 'js-yaml';
import { visit } from 'unist-util-visit';
import GithubSlugger from 'github-slugger';
import { slugFromBackingFile } from './doc-path.mjs';
import {
  DOCS_PREFIX,
  splitHeading,
  isExternalNotesKey,
  externalNotesUrl,
  resolveInternal,
} from './wiki-resolve.mjs';

// Rewrites `[[slug]]` / `[[slug|Display]]` wiki-links in the generated docs
// collection into real links. Adapted from the `notes` repo's plugin, minus
// the backlink-graph frontmatter channel Starlight doesn't consume. Resolution
// rules live in wiki-resolve.mjs (shared with scripts/check-links.mjs).

const WIKI_LINK_RE = /\[\[([^\[\]|]+)(?:\|([^\[\]]+))?\]\]/g;
const ATX_HEADING_RE = /^#{1,6}\s+(.*?)\s*#*\s*$/;
const FENCE_RE = /^(```|~~~)/;

let docIndex = null;
const titleCache = new Map();
const headingCache = new Map();

function buildDocIndex(docsRoot) {
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
      const basename = slug.split('/').pop().toLowerCase();
      if (!byBasename.has(basename)) byBasename.set(basename, []);
      byBasename.get(basename).push(slug);
    }
  })(docsRoot);

  return { byFullSlug, byBasename, absOf: (slug) => path.join(docsRoot, `${slug || 'index'}.md`) };
}

function titleFor(absPath) {
  if (titleCache.has(absPath)) return titleCache.get(absPath);
  let title = null;
  try {
    const match = fs.readFileSync(absPath, 'utf8').match(/^---\r?\n([\s\S]*?)\r?\n---/);
    if (match) {
      const data = yaml.load(match[1]) || {};
      if (typeof data.title === 'string') title = data.title;
    }
  } catch {
    /* fall back to humanized slug */
  }
  titleCache.set(absPath, title);
  return title;
}

function headingsFor(absPath) {
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
    /* leave empty — fragment resolves to nothing */
  }
  headingCache.set(absPath, map);
  return map;
}

function humanize(slug) {
  return slug
    .split('/')
    .pop()
    .split('-')
    .map((w) => w.charAt(0).toUpperCase() + w.slice(1))
    .join(' ');
}

export function remarkWikiLinks() {
  return (tree, file) => {
    const filePath = file.path ? path.resolve(file.path) : null;
    if (!filePath) return;
    const cwd = file.cwd || process.cwd();
    const docsRoot = path.resolve(cwd, 'src/content/docs');
    if (!filePath.startsWith(docsRoot + path.sep)) return;

    docIndex ??= buildDocIndex(docsRoot);
    const unresolved = [];

    visit(tree, 'text', (node, index, parent) => {
      if (!parent || index === null || !node.value.includes('[[')) return undefined;
      WIKI_LINK_RE.lastIndex = 0;
      const matches = [...node.value.matchAll(WIKI_LINK_RE)];
      if (matches.length === 0) return undefined;

      const out = [];
      let cursor = 0;

      for (const match of matches) {
        const [full, rawKey, displayOverride] = match;
        if (match.index > cursor) out.push({ type: 'text', value: node.value.slice(cursor, match.index) });
        cursor = match.index + full.length;

        const { notePart, headingPart } = splitHeading(rawKey);
        const display = displayOverride?.trim();

        if (isExternalNotesKey(notePart)) {
          out.push({
            type: 'link',
            url: externalNotesUrl(notePart),
            children: [{ type: 'text', value: display || humanize(notePart) }],
          });
          continue;
        }

        const resolved = resolveInternal(notePart, docIndex);
        if (resolved && resolved !== 'ambiguous') {
          const abs = docIndex.absOf(resolved);
          let hash = '';
          if (headingPart) {
            const id = headingsFor(abs).get(headingPart.toLowerCase());
            hash = id ? `#${id}` : '';
            if (!id) unresolved.push(`${notePart}#${headingPart}`);
          }
          out.push({
            type: 'link',
            url: `${DOCS_PREFIX}/${resolved}/${hash}`,
            children: [{ type: 'text', value: display || titleFor(abs) || humanize(resolved) }],
          });
        } else {
          unresolved.push(resolved === 'ambiguous' ? `${notePart} (ambiguous)` : notePart);
          out.push({ type: 'text', value: display || notePart });
        }
      }

      if (cursor < node.value.length) out.push({ type: 'text', value: node.value.slice(cursor) });
      parent.children.splice(index, 1, ...out);
      return index + out.length;
    });

    if (unresolved.length > 0) {
      console.warn(
        `[remark-wiki-links] ${path.relative(cwd, filePath)}: unresolved -> ${unresolved.join(', ')}`,
      );
    }
  };
}
