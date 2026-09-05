import path from 'node:path';
import fs from 'node:fs';
import { visit } from 'unist-util-visit';
import { resolveContentFile, slugFromBackingFile } from './doc-path.mjs';
import { DOCS_PREFIX } from './wiki-resolve.mjs';

// Rewrites relative links between sibling docs files — written GitHub-style
// (`../observability/pipeline.md`) or already as extensionless site paths —
// into the URL Starlight actually serves the target at. Ported from `notes`,
// narrowed to the single `docs` collection. Links that don't resolve to a real
// docs file are left untouched (they get reported by scripts/check-links.mjs).

const DOCS_ROOT = 'src/content/docs';

function isSkippable(url) {
  return (
    !url ||
    url.startsWith('#') ||
    url.startsWith('/') ||
    url.startsWith('mailto:') ||
    /^[a-z][a-z0-9+.-]*:\/\//i.test(url)
  );
}

export function remarkRewriteMdLinks() {
  return (tree, file) => {
    const filePath = file.path ? path.resolve(file.path) : null;
    if (!filePath) return;
    const cwd = file.cwd || process.cwd();
    const absRoot = path.resolve(cwd, DOCS_ROOT);
    if (!filePath.startsWith(absRoot + path.sep)) return;

    const fileDir = path.dirname(filePath);

    visit(tree, 'link', (node) => {
      if (isSkippable(node.url)) return;
      const [urlPath, hash] = node.url.split('#');
      const resolvedAbs = path.resolve(fileDir, urlPath);

      const backingFile = /\.mdx?$/i.test(urlPath)
        ? fs.existsSync(resolvedAbs)
          ? resolvedAbs
          : null
        : resolveContentFile(resolvedAbs);
      if (!backingFile) return;

      const slug = slugFromBackingFile(backingFile, absRoot);
      if (slug === null) return;
      node.url = `${DOCS_PREFIX}/${slug}/${hash ? `#${hash}` : ''}`;
    });
  };
}
