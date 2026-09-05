#!/usr/bin/env node
// Materializes the Starlight `docs` content collection from the repo's own
// docs/ tree — the single source of truth, kept at the repo root so it stays
// browsable on GitHub. Runs as predev/prebuild; the output dir is gitignored.
//
//   docs/**/*.md            -> website/src/content/docs/**/*.md
//   docs/**/README.md       -> .../index.md                      (folder index)
//   <repo>/README.md        -> .../project-readme.md             (+ injected
//                              frontmatter, repo-relative links -> GitHub blob)
//
// Then a second pass rewrites `[[wiki-links]]` in the copied files (see
// apply-wiki-links.mjs — done on raw text, not via remark, so formatted display
// text survives). Sibling `../foo.md` links are left for remark-rewrite-md-links.

import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { buildDocIndex } from '../wiki-index.mjs';
import { applyWikiLinks } from '../apply-wiki-links.mjs';

const __dirname = path.dirname(fileURLToPath(import.meta.url));
const REPO = path.resolve(__dirname, '../..');
const SRC = path.join(REPO, 'docs');
const OUT = path.resolve(__dirname, '../src/content/docs');
const REPO_URL = 'https://github.com/shipsolid/signal-forge';

if (!fs.existsSync(SRC)) {
  console.error(`gen-docs: source docs tree not found at ${SRC}`);
  process.exit(1);
}

fs.rmSync(OUT, { recursive: true, force: true });
fs.mkdirSync(OUT, { recursive: true });

const written = [];
(function walk(dir) {
  for (const entry of fs.readdirSync(dir, { withFileTypes: true })) {
    const abs = path.join(dir, entry.name);
    if (entry.isDirectory()) {
      walk(abs);
      continue;
    }
    if (!/\.md$/i.test(entry.name)) continue;
    const rel = path.relative(SRC, abs);
    const destRel =
      entry.name.toLowerCase() === 'readme.md'
        ? path.join(path.dirname(rel), 'index.md')
        : rel;
    const dest = path.join(OUT, destRel);
    fs.mkdirSync(path.dirname(dest), { recursive: true });
    fs.copyFileSync(abs, dest);
    written.push(dest);
  }
})(SRC);

// Repo-root README -> its own page, with repo-relative links pointed at GitHub.
const readmeSrc = path.join(REPO, 'README.md');
if (fs.existsSync(readmeSrc)) {
  const body = fs.readFileSync(readmeSrc, 'utf8').replace(
    /\]\((?!https?:|#|mailto:|\/)([^)\s]+)\)/g,
    (_m, target) => {
      const clean = target.replace(/^\.\//, '');
      const [pathPart, frag] = clean.split('#');
      const kind = pathPart.endsWith('/') ? 'tree/main' : 'blob/main';
      return `](${REPO_URL}/${kind}/${pathPart}${frag ? `#${frag}` : ''})`;
    },
  );
  const frontmatter = [
    '---',
    'title: "Project README"',
    'description: "The full SignalForge repository README — deploy model, ownership boundary, dependencies, and operational model."',
    'sidebar:',
    '  order: 99',
    '---',
    '',
  ].join('\n');
  const dest = path.join(OUT, 'project-readme.md');
  fs.writeFileSync(dest, frontmatter + body);
  written.push(dest);
}

// Second pass: rewrite [[wiki-links]] now that every target file exists.
const index = buildDocIndex(OUT);
const unresolved = [];
for (const file of written) {
  const { md, unresolved: u } = applyWikiLinks(fs.readFileSync(file, 'utf8'), index);
  if (u.length) unresolved.push(`  ${path.relative(OUT, file)}: ${u.join(', ')}`);
  fs.writeFileSync(file, md);
}

console.log(`gen-docs: wrote ${written.length} files to ${path.relative(REPO, OUT)}`);
if (unresolved.length) {
  console.warn(`gen-docs: ${unresolved.length} file(s) with unresolved wiki-links (see check-links):`);
  console.warn(unresolved.join('\n'));
}
