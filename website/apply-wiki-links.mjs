import {
  DOCS_PREFIX,
  splitHeading,
  isExternalNotesKey,
  externalNotesUrl,
  resolveInternal,
} from './wiki-resolve.mjs';
import { headingsFor, titleFor, humanizeSlug } from './wiki-index.mjs';

// Rewrites `[[slug]]` / `[[slug|Display]]` in RAW markdown, before Astro parses
// it. Done here rather than as a remark plugin for two reasons a remark plugin
// can't handle:
//   - a remark plugin only sees `text` nodes, so `[[page|**bold**]]` or a
//     `[[page#heading with `code`]]` fragment is already split across
//     strong/inlineCode nodes and slips through;
//   - `[[a | b]]` inside a Markdown table cell gets torn in half by the table
//     parser at the `|`. Rewriting to `[b](url)` on the source string sidesteps
//     both — the link is whole and pipe-free by the time Markdown sees it.
//
// Resolution rules live in wiki-resolve.mjs. Fenced code is skipped line-wise;
// a `[[` that opens inside an unclosed inline-code span on its line (the bash
// `[[ -d x ]]` test operator in prose) is left alone via backtick parity.

const WIKI_LINK_RE = /\[\[([^\[\]|]+)(?:\|([^\[\]]+))?\]\]/g;
const FENCE_RE = /^\s*(```|~~~)/;

function linkFor(rawKey, rawDisplay, index) {
  const { notePart, headingPart } = splitHeading(rawKey);
  const display = rawDisplay?.trim();

  const resolved = resolveInternal(notePart, index);
  if (resolved !== null && resolved !== 'ambiguous') {
    let hash = '';
    if (headingPart) {
      const id = headingsFor(index.absOf(resolved)).get(headingPart.toLowerCase());
      if (id) hash = `#${id}`;
    }
    const url = resolved === '' ? `${DOCS_PREFIX}/${hash}` : `${DOCS_PREFIX}/${resolved}/${hash}`;
    const text = display || titleFor(index.absOf(resolved)) || humanizeSlug(resolved);
    return { link: `[${text}](${url})`, unresolved: null };
  }

  if (isExternalNotesKey(notePart)) {
    return {
      link: `[${display || humanizeSlug(notePart)}](${externalNotesUrl(notePart)})`,
      unresolved: null,
    };
  }

  return {
    link: display || notePart,
    unresolved: resolved === 'ambiguous' ? `${notePart} (ambiguous)` : notePart,
  };
}

// Returns { md, unresolved: string[] }.
export function applyWikiLinks(md, index) {
  const unresolved = [];
  let inFence = false;

  const out = md.split('\n').map((line) => {
    if (FENCE_RE.test(line)) {
      inFence = !inFence;
      return line;
    }
    if (inFence || !line.includes('[[')) return line;

    return line.replace(WIKI_LINK_RE, (whole, key, display, offset) => {
      // Odd backtick count before the match ⇒ the `[[` sits inside an inline
      // code span (prose about the `[[ ]]` operator) — leave it verbatim.
      const backticks = (line.slice(0, offset).match(/`/g) || []).length;
      if (backticks % 2 === 1) return whole;

      const { link, unresolved: u } = linkFor(key, display, index);
      if (u) unresolved.push(u);
      return link;
    });
  });

  return { md: out.join('\n'), unresolved };
}
