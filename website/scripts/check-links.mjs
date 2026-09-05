#!/usr/bin/env node
// Broken-link audit. Gates the build (non-zero exit on any finding), in the
// spirit of the `notes` repo's check-links.mjs. Run after gen-docs.
//
//   node scripts/check-links.mjs
//
// Checks:
//   1. every [[wiki-link]] in the SOURCE docs/ tree resolves — internally, or
//      to a known notes book (with docs/file:line for anything that doesn't)
//   2. no literal [[ ]] survived gen-docs' rewrite into src/content/docs/
//   3. no relative ../foo.md link in src/content/docs/ points at a missing file

import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { buildDocIndex } from '../wiki-index.mjs';
import { splitHeading, isExternalNotesKey, resolveInternal } from '../wiki-resolve.mjs';

const __dirname = path.dirname(fileURLToPath(import.meta.url));
const SRC = path.resolve(__dirname, '../../docs');
const OUT = path.resolve(__dirname, '../src/content/docs');
const WIKI_LINK_RE = /\[\[([^\[\]|]+)(?:\|([^\[\]]+))?\]\]/g;
const MD_LINK_RE = /\[[^\]]*\]\(([^)]+)\)/g;

if (!fs.existsSync(OUT)) {
  console.error('check-links: run `npm run gen-docs` first');
  process.exit(1);
}

function mdFiles(root) {
  const out = [];
  (function walk(dir) {
    for (const entry of fs.readdirSync(dir, { withFileTypes: true })) {
      const abs = path.join(dir, entry.name);
      if (entry.isDirectory()) walk(abs);
      else if (/\.mdx?$/i.test(entry.name)) out.push(abs);
    }
  })(root);
  return out;
}

// Blank frontmatter + fenced/inline code, keeping newline positions.
function stripCode(raw) {
  const blank = (m) => m.replace(/[^\n]/g, ' ');
  return raw
    .replace(/^---\r?\n[\s\S]*?\r?\n---/, blank)
    .replace(/^(```|~~~)[^\n]*\n[\s\S]*?^\1[^\n]*$/gm, blank)
    .replace(/`[^`\n]+`/g, blank);
}
const lineOf = (text, offset) => text.slice(0, offset).split('\n').length;

const index = buildDocIndex(OUT);
const unresolvedWiki = [];
const ambiguousWiki = [];
const leakedWiki = [];
const brokenRelative = [];

// 1. wiki-links in source
for (const abs of mdFiles(SRC)) {
  const rel = `docs/${path.relative(SRC, abs)}`;
  const text = stripCode(fs.readFileSync(abs, 'utf8'));
  for (const m of text.matchAll(WIKI_LINK_RE)) {
    const { notePart } = splitHeading(m[1]);
    const res = resolveInternal(notePart, index); // internal first (see wiki-resolve.mjs)
    if (res === 'ambiguous') ambiguousWiki.push(`${rel}:${lineOf(text, m.index)}  [[${notePart}]]`);
    else if (res === null && !isExternalNotesKey(notePart))
      unresolvedWiki.push(`${rel}:${lineOf(text, m.index)}  [[${notePart}]]`);
  }
}

// 2 + 3. generated output
for (const abs of mdFiles(OUT)) {
  const rel = path.relative(OUT, abs);
  const text = stripCode(fs.readFileSync(abs, 'utf8'));
  for (const m of text.matchAll(WIKI_LINK_RE))
    leakedWiki.push(`${rel}:${lineOf(text, m.index)}  [[${m[1]}]]`);
  for (const m of text.matchAll(MD_LINK_RE)) {
    const url = m[1].split(/\s/)[0];
    if (!url || /^([a-z][a-z0-9+.-]*:|[#/])/i.test(url) || url.startsWith('mailto:')) continue;
    const [urlPath] = url.split('#');
    const target = path.resolve(path.dirname(abs), urlPath);
    const candidates = /\.mdx?$/i.test(urlPath)
      ? [target]
      : [`${target}.md`, `${target}.mdx`, path.join(target, 'index.md'), path.join(target, 'index.mdx')];
    if (!candidates.some((c) => fs.existsSync(c)))
      brokenRelative.push(`${rel}:${lineOf(text, m.index)}  -> ${url}`);
  }
}

function section(title, items) {
  console.log(`\n${title} (${items.length})`);
  for (const i of items) console.log(`  ${i}`);
}
section('Unresolved wiki-links (source)', unresolvedWiki);
section('Ambiguous wiki-links (source)', ambiguousWiki);
section('Wiki-links that survived gen-docs', leakedWiki);
section('Broken relative links (generated)', brokenRelative);

const total =
  unresolvedWiki.length + ambiguousWiki.length + leakedWiki.length + brokenRelative.length;
console.log(`\n${mdFiles(SRC).length} source docs scanned, ${total} finding(s).`);
process.exit(total > 0 ? 1 : 0);
