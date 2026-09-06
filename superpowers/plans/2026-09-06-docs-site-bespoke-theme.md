# docs-site bespoke theme — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace `shipsolid/docs-site`'s stock Starlight rendering with a bespoke Astro + Tailwind 4 presentation layer that matches `shipsolid/notes`' look and feel (Catppuccin Mocha dark-default, Shantell Sans / JetBrains Mono, `.doc-grid` three-column, `.prose-doc` rhythm, `notes`-style header/footer/TOC), while keeping the engine's framework-agnostic pipeline.

**Architecture:** The engine's value — `lib/config.mjs`, the wiki-link resolver (`lib/wiki-*.mjs`, `lib/doc-path.mjs`, `lib/remark-rewrite-md-links.mjs`), `scripts/gen-docs.mjs`, `scripts/check-links.mjs`, `.github/workflows/build-deploy.yml` — is unchanged. Starlight (`@astrojs/starlight`, `astro-mermaid`, `docsSchema`, `lib/sidebar.mjs`) is removed. New: a bespoke `astro.config.mjs`, a ported+trimmed `src/styles/global.css`, layouts (`Base.astro`, `Doc.astro`), components (`Header`, `Footer`, `TableOfContents`, `SidebarTree`, `MermaidRenderer`, `Search`), a `[...slug].astro` catch-all route, a plain zod content schema, and `lib/nav-tree.mjs` (replaces `lib/sidebar.mjs`).

**Tech Stack:** Astro 7 (`output: 'static'`), Tailwind 4 via `@tailwindcss/vite`, `@tailwindcss/typography`, `@fontsource-variable/shantell-sans`, `@fontsource/jetbrains-mono`, `astro-pagefind`, `@astrojs/sitemap`, Shiki (`css-variables` theme) via Astro's built-in markdown, `unified` + `remark-parse` (already dev-deps), Vitest, mermaid (CDN, via ported `MermaidRenderer.astro`).

**Spec:** `superpowers/specs/2026-09-06-docs-site-bespoke-theme-design.md` (in `signal-forge`)

## Global Constraints

- Engine repo: `/home/amit/repos/shipsolid/docs-site`. Work on a new branch `bespoke-theme` off `main` (`main` is `de26b88`, synced with origin). Do **not** `git push`.
- Consumer: `/home/amit/repos/shipsolid/signal-forge`, branch `main` (per the user, this session's docs-site consumer edits land on `main`). One edit only: `docs-site.yaml` `sidebar:` → `nav:` (Task 9).
- Source of ported files: `/home/amit/repos/shipsolid/notes/` — `src/styles/global.css`, `src/layouts/Base.astro`, `src/components/{Header,Footer,TableOfContents,MermaidRenderer}.astro`, `src/lib/reading-enhancements.ts`, `src/components/NotesSidebarTree.astro` (structure reference for `SidebarTree.astro`). Read them from that path.
- **No design-token renames.** `--color-*`, `--font-*`, `--breakpoint-3xl`, `.doc-grid`, `.doc-grid-2`, `.prose-doc`, `.card`, `.eyebrow` keep the exact names `notes` uses, so the two sites remain one portable design system.
- Node: `.nvmrc` = `24` (unchanged). `npm test` = `vitest run` over `tests/**/*.test.mjs`.
- Engine env contract unchanged: `DOCS_SITE_CONFIG`, `DOCS_SRC`, `PROJECT_README`, `REPO_SLUG`, `DOCS_OUT` — with `fixtures/sample-repo/` fallbacks.
- `base` always has a leading + trailing `/`; `docsPrefix` = `base` with the trailing slash stripped.
- Forced dark: only a stored `localStorage['theme'] === 'light'` writes `data-theme="light"` to `<html>`; the default DOM carries no `data-theme` attribute.
- Commit after each task; messages end with the `Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>` trailer.
- `dist/` and `src/content/docs/` are gitignored — never commit them.
- The reusable `.github/workflows/build-deploy.yml` and `.github/workflows/ci.yml` are **not** edited by this plan.

---

## File Structure

### `shipsolid/docs-site` — after this plan

| Path | State | Responsibility |
| --- | --- | --- |
| `package.json` | modified | − `@astrojs/starlight`, `astro-mermaid`; + `tailwindcss`, `@tailwindcss/vite`, `@tailwindcss/typography`, `@fontsource-variable/shantell-sans`, `@fontsource/jetbrains-mono`, `astro-pagefind`, `@astrojs/sitemap` |
| `astro.config.mjs` | rewritten | bespoke: sitemap + pagefind integrations, tailwind vite plugin, `remarkRewriteMdLinks`, Shiki `css-variables`, rehype heading anchors |
| `src/content.config.ts` | rewritten | plain zod schema, `.passthrough()` |
| `lib/config.mjs` | modified | parsed field `sidebar` → `nav`; add optional `editBranch` (default `"main"`) |
| `lib/nav-tree.mjs` | **new** | `buildNavTree`, `flattenPages` — replaces `lib/sidebar.mjs` |
| `lib/sidebar.mjs` | **deleted** | |
| `src/styles/global.css` | **new** (ported+trimmed) | Catppuccin Mocha token system, `.doc-grid`, `.prose-doc`, `--astro-code-*` |
| `src/layouts/Base.astro` | **new** (ported) | `<head>`, pre-paint theme init, `@fontsource` imports, skip link |
| `src/layouts/Doc.astro` | **new** | 3-column doc page shell |
| `src/components/Header.astro` | **new** (ported style) | fixed translucent header, theme toggle, mobile menu, search trigger |
| `src/components/Footer.astro` | **new** (ported style) | config-driven footer |
| `src/components/TableOfContents.astro` | **new** (ported ~verbatim) | scroll-spy TOC |
| `src/components/SidebarTree.astro` | **new** | recursive `NavNode[]` renderer, `notes` sidebar visual style |
| `src/components/MermaidRenderer.astro` | **new** (ported verbatim) | mermaid CDN render + theme re-render |
| `src/components/Search.astro` | **new** | `pagefind-ui` modal + header trigger wiring |
| `src/lib/reading-enhancements.ts` | **new** (ported, trimmed) | `initCopyButtons` only |
| `src/pages/[...slug].astro` | **new** | catch-all doc route |
| `tests/nav-tree.test.mjs` | **new** | replaces `tests/sidebar.test.mjs` |
| `tests/sidebar.test.mjs` | **deleted** | |
| `tests/fixture-build.test.mjs` | modified | bespoke-route assertions + `_pagefind` check |
| `fixtures/sample-repo/docs-site.yaml` | modified | `sidebar:` → `nav:` |
| `README.md` | modified | local-dev + stack notes |
| `lib/{config-unchanged parts},wiki-index,wiki-resolve,apply-wiki-links,doc-path,remark-rewrite-md-links}.mjs` | unchanged | |
| `scripts/gen-docs.mjs`, `scripts/check-links.mjs` | unchanged | |
| `tests/{config,wiki-resolve,apply-wiki-links}.test.mjs` | unchanged | |
| `.github/workflows/{build-deploy,ci}.yml`, `.nvmrc`, `tsconfig.json`, `vitest.config.mjs` | unchanged | |

### `shipsolid/signal-forge`

| Path | Change |
| --- | --- |
| `docs-site.yaml` | `sidebar:` (Starlight-shaped) → `nav:` (Task 9) |

---

## Task 1: Branch, dependency swap, `astro.config.mjs`, `content.config.ts`, config field rename

**Files:**
- Create branch `bespoke-theme` in `/home/amit/repos/shipsolid/docs-site`
- Modify: `package.json`
- Rewrite: `astro.config.mjs`, `src/content.config.ts`
- Modify: `lib/config.mjs`
- Test: `tests/config.test.mjs` (update the `sidebar` → `nav` expectations)

**Interfaces:**
- Consumes: `loadDocsSiteConfig` env contract; `remarkRewriteMdLinks({ docsPrefix })` (unchanged, `lib/remark-rewrite-md-links.mjs`).
- Produces:
  - `loadDocsSiteConfig({ configPath, repoSlug })` returns `{ site, base, title, description, social: { github }, docsDir, projectReadme, nav, wikiLinks, codeLangAliases, editBranch }` — `nav` (was `sidebar`) is the raw passthrough array or `null`; `editBranch` = `cfg.edit_branch ?? 'main'`.
  - `astro.config.mjs` default-exports a bespoke config (no Starlight).
  - `src/content.config.ts` exports `collections = { docs }` with a `glob` loader + plain zod schema.

- [ ] **Step 1: Branch**

```bash
cd /home/amit/repos/shipsolid/docs-site && git checkout -b bespoke-theme
```

- [ ] **Step 2: Swap dependencies in `package.json`**

Remove from `dependencies`: `@astrojs/starlight`, `astro-mermaid`, `@mermaid-js/layout-elk`, `mermaid`.
Add to `dependencies`: `"@astrojs/sitemap": "^3.7.3"`, `"@fontsource-variable/shantell-sans": "^5.3.0"`, `"@fontsource/jetbrains-mono": "^5.3.0"`, `"@tailwindcss/typography": "^0.5.20"`, `"@tailwindcss/vite": "^4.3.3"`, `"astro-pagefind": "^2.0.1"`, `"tailwindcss": "^4.3.3"`.
Add to `devDependencies`: `"remark-parse": "^11.0.0"`, `"unified": "^11.0.5"` (used by `lib/nav-tree.mjs` only if it parses markdown — it does not; include anyway for parity with `notes` tooling and future TOC needs). Keep `github-slugger`, `unist-util-visit`, `js-yaml`, `vitest`, `@types/node`, `typescript`.
Keep `scripts` as-is (`gen-docs`, `check-links`, `predev`, `dev`, `prebuild`, `build`, `preview`, `test`).

- [ ] **Step 3: Run `npm install`**

Run: `npm install`
Expected: exit 0; `package-lock.json` updated; `node_modules/@astrojs/starlight` gone, `node_modules/tailwindcss` + `@fontsource*` present.

- [ ] **Step 4: Rewrite `astro.config.mjs`**

```javascript
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { defineConfig } from 'astro/config';
import sitemap from '@astrojs/sitemap';
import pagefind from 'astro-pagefind';
import tailwindcss from '@tailwindcss/vite';
import rehypeSlug from 'rehype-slug';
import rehypeAutolinkHeadings from 'rehype-autolink-headings';
import { loadDocsSiteConfig } from './lib/config.mjs';
import { remarkRewriteMdLinks } from './lib/remark-rewrite-md-links.mjs';

const __dirname = path.dirname(fileURLToPath(import.meta.url));
const FIX = path.join(__dirname, 'fixtures', 'sample-repo');

const cfg = loadDocsSiteConfig({
  configPath: process.env.DOCS_SITE_CONFIG || path.join(FIX, 'docs-site.yaml'),
  repoSlug: process.env.REPO_SLUG || 'shipsolid/sample-repo',
});

export default defineConfig({
  site: cfg.site,
  base: cfg.base,
  trailingSlash: 'always',
  output: 'static',
  integrations: [sitemap(), pagefind()],
  vite: { plugins: [tailwindcss()] },
  markdown: {
    remarkPlugins: [remarkRewriteMdLinks({ docsPrefix: cfg.base.replace(/\/$/, '') })],
    rehypePlugins: [
      rehypeSlug,
      [rehypeAutolinkHeadings, { behavior: 'wrap' }],
    ],
    shikiConfig: {
      theme: 'css-variables',
      langAlias: cfg.codeLangAliases,
    },
  },
});
```

Add `rehype-slug` + `rehype-autolink-headings` to `package.json` `dependencies` (`"rehype-slug": "^6.0.0"`, `"rehype-autolink-headings": "^7.1.0"`) and re-run `npm install`. (Astro extracts `headings` for the TOC on its own; these give the in-page anchor links Starlight provided.)

- [ ] **Step 5: Rewrite `src/content.config.ts`**

```typescript
import { defineCollection, z } from 'astro:content';
import { glob } from 'astro/loaders';

const docs = defineCollection({
  loader: glob({ pattern: '**/[^_]*.md', base: './src/content/docs' }),
  schema: z
    .object({
      title: z.string(),
      description: z.string().optional(),
      sidebar_order: z.number().optional(),
      sidebar_label: z.string().optional(),
      updated: z.coerce.date().optional(),
      // passthrough — signal-forge docs were authored in the notes repo
      zettelId: z.string().optional(),
      noteType: z.string().optional(),
      tags: z.array(z.string()).optional(),
      relations: z.array(z.object({ slug: z.string(), kind: z.string() })).optional(),
    })
    .passthrough(),
});

export const collections = { docs };
```

- [ ] **Step 6: `lib/config.mjs` — `sidebar` → `nav`, add `editBranch`**

In `loadDocsSiteConfig`'s returned object: rename the key `sidebar: cfg.sidebar ?? null` to `nav: cfg.nav ?? null`. Add `editBranch: cfg.edit_branch ?? 'main'`. No other change (the YAML key is now `nav:` not `sidebar:`).

- [ ] **Step 7: Update `tests/config.test.mjs`**

Anywhere the test writes `sidebar:` YAML or asserts `c.sidebar`, change to `nav:` / `c.nav`. Add one assertion: a minimal file yields `c.editBranch === 'main'`; a file with `edit_branch: develop` yields `c.editBranch === 'develop'`.

- [ ] **Step 8: Run the still-valid tests**

Run: `npx vitest run tests/config.test.mjs`
Expected: PASS (updated count). `wiki-resolve` + `apply-wiki-links` tests are untouched and still pass — do not run the full suite yet (`nav-tree.test.mjs` and the reshaped `fixture-build.test.mjs` come later; `sidebar.test.mjs` is deleted in Task 3).

- [ ] **Step 9: `astro check`-level parse**

Run: `npx astro sync` (generates `.astro/` types from the new content config)
Expected: exit 0, no schema errors. A full `astro build` will fail here (no layouts/pages yet) — that is expected; do not run it.

- [ ] **Step 10: Commit**

```bash
git add package.json package-lock.json astro.config.mjs src/content.config.ts lib/config.mjs tests/config.test.mjs
git commit -m "Drop Starlight deps; bespoke astro.config + plain content schema

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>"
```

---

## Task 2: `global.css` port + trim, fonts, `Base.astro`

**Files:**
- Create: `src/styles/global.css` (from `notes/src/styles/global.css`, trimmed)
- Create: `src/layouts/Base.astro` (from `notes/src/layouts/Base.astro`, adapted)

**Interfaces:**
- Consumes: `@fontsource-variable/shantell-sans/wght.css`, `@fontsource/jetbrains-mono/{400,500}.css` (imported in `Base.astro`); Tailwind 4 via `@tailwindcss/vite` (Task 1).
- Produces: `Base.astro` — `<Base title description ogImage?>` renders `<html><head>…</head><body><a.skip-link/><slot/></body></html>` with the pre-paint theme script. `global.css` defines the token system + `.doc-grid`/`.doc-grid-2`/`.prose-doc`/`.card`/`.eyebrow` + `--astro-code-*` code-block vars.

- [ ] **Step 1: Port `src/styles/global.css`**

Copy `notes/src/styles/global.css` verbatim, then **delete** these blocks (all clearly demarcated by their own comments):
- `@utility bg-grid-overlay`, `@utility bg-grid`
- `.filter-tag`, `.filter-tag:hover*`, `.filter-tag.active*`, `.tag-count`
- `.callout`, `.callout-*`
- `.masked-blank`, `.masked-answer`
- `.card-content*`
- `.tabs`, `.tab`, `.tab:hover`, `.tab[aria-selected]`
- `.badge-grid`, `.badge`, `.badge.unlocked`, `.badge-icon`, `.badge-label`, `.badge-desc`
- `.btn-primary`, `.btn-outline`
- `.section-container`, `.section-label`, `.section-title` — KEEP `.eyebrow`
- the trailing `[data-reading-width]` / `[data-reading-size]` "Gita Reading Mode" block

**Keep:** the `@import 'tailwindcss'` + `@source` + `@plugin` header (adjust `@source` glob to `'../**/*.{astro,html,js,jsx,ts,tsx}'` — relative to `src/styles/`, covering `src/`), `@custom-variant light`, the entire `@theme` block, the entire `@layer base` (`:root`, `:root[data-theme='light']`, `html`, `button`/placeholder preflight, `body`, scrollbar, `:focus-visible`, `#main:focus`, `prefers-reduced-motion`), `.skip-link` + `.skip-link:focus`, `.doc-grid` / `.doc-grid-2`, `.card` / `.card:hover`, `.tag`, `.eyebrow`, the `.prose pre code` rule, the entire `.prose-doc` `@layer utilities` block, the `.copy-btn` `@layer components` block, `@media print`.

Then **add**, at the end of `@layer base` (`:root` and the light block), the Shiki `css-variables` code-block palette so ` ``` ` blocks match `notes`' `.prose-doc pre`:

```css
  :root {
    --astro-code-color-text: #cdd6f4;
    --astro-code-color-background: #313244;
    --astro-code-token-comment: #7f849c;
    --astro-code-token-keyword: #cba6f7;
    --astro-code-token-string: #a6e3a1;
    --astro-code-token-function: #89b4fa;
    --astro-code-token-constant: #fab387;
    --astro-code-token-parameter: #f5c2e7;
    --astro-code-token-string-expression: #a6e3a1;
    --astro-code-token-punctuation: #bac2de;
    --astro-code-token-link: #94e2d5;
  }
  :root[data-theme='light'] {
    --astro-code-color-text: #0f172a;
    --astro-code-color-background: #f8fafc;
    --astro-code-token-comment: #64748b;
    --astro-code-token-keyword: #7c3aed;
    --astro-code-token-string: #15803d;
    --astro-code-token-function: #1d4ed8;
    --astro-code-token-constant: #b45309;
    --astro-code-token-parameter: #a21caf;
    --astro-code-token-string-expression: #15803d;
    --astro-code-token-punctuation: #334155;
    --astro-code-token-link: #0f766e;
  }
```

- [ ] **Step 2: Port `src/layouts/Base.astro`**

From `notes/src/layouts/Base.astro`. Keep verbatim: the `@fontsource` imports, `import '../styles/global.css'`, the `<!doctype html><html lang="en"><head>` scaffold, the pre-paint `<script is:inline>` theme init, `<meta charset/viewport/generator>`, `<title>`, canonical, the OG/Twitter tag block, `<link rel="icon">`, `<body><a href="#main" class="skip-link">Skip to content</a><slot/></body>`.

Changes:
- `Props`: `{ title: string; description?: string; ogImage?: string; article?: boolean }` — drop nothing, but the `description` default becomes `cfg.description` and `og:site_name` becomes `cfg.title`. Since `Base.astro` has no config access, pass them from `Doc.astro` (Task 6); give `description` a plain `''` default and `og:site_name` the `title` prop value.
- Delete the GA4 `<script>` block and the `ga4Id` const.
- `favicon`: keep `<link rel="icon" type="image/svg+xml" href="/favicon.svg" />` but prefix with `base` — use `import.meta.env.BASE_URL` (`href={\`${import.meta.env.BASE_URL}favicon.svg\`}`). Ship a minimal `public/favicon.svg` (a simple mono glyph — a teal `‹›` on transparent, 32×32).

- [ ] **Step 3: Sanity**

Run: `npx astro sync` then `node -e "console.log('css', require('fs').statSync('src/styles/global.css').size)"`
Expected: sync exit 0; global.css materially smaller than notes' 776-line original (roughly 450–520 lines).

- [ ] **Step 4: Commit**

```bash
git add src/styles/global.css src/layouts/Base.astro public/favicon.svg
git commit -m "Port notes' global.css (trimmed) + Base.astro; forced-dark theme init

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>"
```

---

## Task 3: `lib/nav-tree.mjs` + `tests/nav-tree.test.mjs` (TDD); delete `lib/sidebar.mjs`

**Files:**
- Create: `lib/nav-tree.mjs`
- Create test: `tests/nav-tree.test.mjs`
- Delete: `lib/sidebar.mjs`, `tests/sidebar.test.mjs`

**Interfaces:**
- Consumes: Node `fs`/`path`; `github-slugger` (`slug`) for label→segment parity if needed.
- Produces:
  - `NavNode = { label: string, href: string | null, order: number, children: NavNode[] }`
  - `buildNavTree({ contentDir, base, navConfig }) -> NavNode[]` — `contentDir` is an absolute path to the generated `src/content/docs`; `base` has leading+trailing `/`; `navConfig` is `cfg.nav` (array or `null`).
  - `flattenPages(nodes) -> { label: string, href: string }[]` — depth-first, only nodes with `href !== null`, in tree order (used for prev/next).
  - Label rule: `sidebar_label` frontmatter wins; else dir/file name with a leading `\d+[-_]` stripped, `[-_]+`→space, Title Cased.
  - Order rule: numeric prefix on the name (ascending) beats non-numbered; within a group, page nodes order by `sidebar_order` (ascending, missing = `Infinity`) then label `localeCompare`.
  - `href` rule: a page `foo/bar.md` → `${base}foo/bar/`; a folder with `index.md` → `${base}foo/` on the group node; root `index.md` is excluded from the returned list (it is the site landing).

- [ ] **Step 1: Write the failing test**

```javascript
// tests/nav-tree.test.mjs
import { describe, it, expect, beforeEach, afterEach } from 'vitest';
import { mkdtempSync, mkdirSync, writeFileSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { buildNavTree, flattenPages } from '../lib/nav-tree.mjs';

let root;
const fm = (title, extra = '') => `---\ntitle: ${title}\n${extra}---\n\nbody\n`;
function write(rel, title, extra) {
  const abs = join(root, rel);
  mkdirSync(join(abs, '..'), { recursive: true });
  writeFileSync(abs, fm(title, extra));
}

beforeEach(() => { root = mkdtempSync(join(tmpdir(), 'nav-')); });
afterEach(() => { rmSync(root, { recursive: true, force: true }); });

describe('buildNavTree — auto', () => {
  it('orders numeric-prefixed dirs by number, then the rest alphabetically; strips + titlecases', () => {
    write('index.md', 'Home');
    write('02-deploy/index.md', 'Deploy');
    write('01-intro/index.md', 'Intro');
    write('architecture/index.md', 'Architecture');
    write('architecture/overview.md', 'Overview');
    const tree = buildNavTree({ contentDir: root, base: '/x/', navConfig: null });
    expect(tree.map((n) => n.label)).toEqual(['Intro', 'Deploy', 'Architecture']);
    expect(tree[0].href).toBe('/x/01-intro/');
    const arch = tree.find((n) => n.label === 'Architecture');
    expect(arch.href).toBe('/x/architecture/');
    expect(arch.children.map((c) => c.href)).toEqual(['/x/architecture/overview/']);
  });

  it('orders pages within a group by sidebar_order then title', () => {
    write('g/index.md', 'G');
    write('g/b.md', 'B', 'sidebar_order: 1\n');
    write('g/a.md', 'A', 'sidebar_order: 2\n');
    write('g/c.md', 'C');
    const [g] = buildNavTree({ contentDir: root, base: '/x/', navConfig: null });
    expect(g.children.map((c) => c.label)).toEqual(['B', 'A', 'C']);
  });

  it('honours sidebar_label', () => {
    write('g/index.md', 'G');
    write('g/p.md', 'Real Title', "sidebar_label: Short\n");
    const [g] = buildNavTree({ contentDir: root, base: '/x/', navConfig: null });
    expect(g.children[0].label).toBe('Short');
  });

  it('puts loose top-level markdown into a trailing Reference group', () => {
    write('index.md', 'Home');
    write('a/index.md', 'A');
    write('spec.md', 'Spec');
    write('testing.md', 'Testing');
    const tree = buildNavTree({ contentDir: root, base: '/x/', navConfig: null });
    expect(tree.at(-1).label).toBe('Reference');
    expect(tree.at(-1).children.map((c) => c.href)).toEqual(['/x/spec/', '/x/testing/']);
  });

  it('excludes root index.md from the tree', () => {
    write('index.md', 'Home');
    write('a/index.md', 'A');
    const tree = buildNavTree({ contentDir: root, base: '/x/', navConfig: null });
    expect(tree.every((n) => n.href !== '/x/')).toBe(true);
  });
});

describe('buildNavTree — nav: override', () => {
  it('applies the given order and supports {dir} and {items}', () => {
    write('index.md', 'Home');
    write('architecture/index.md', 'Architecture');
    write('architecture/overview.md', 'Overview');
    write('services/index.md', 'Services');
    write('spec.md', 'Spec');
    write('testing.md', 'Testing');
    const nav = [
      { label: 'Services', dir: 'services' },
      { label: 'Architecture', dir: 'architecture' },
      { label: 'Reference', items: ['spec', 'testing'] },
    ];
    const tree = buildNavTree({ contentDir: root, base: '/x/', navConfig: nav });
    expect(tree.map((n) => n.label)).toEqual(['Services', 'Architecture', 'Reference']);
    expect(tree[1].children.map((c) => c.label)).toEqual(['Overview']);
    expect(tree[2].children.map((c) => c.href)).toEqual(['/x/spec/', '/x/testing/']);
  });

  it('appends dirs the override omitted, in auto order, after the listed ones', () => {
    write('index.md', 'Home');
    write('a/index.md', 'A');
    write('b/index.md', 'B');
    const tree = buildNavTree({ contentDir: root, base: '/x/', navConfig: [{ label: 'B', dir: 'b' }] });
    expect(tree.map((n) => n.label)).toEqual(['B', 'A']);
  });
});

describe('flattenPages', () => {
  it('is depth-first over href-bearing nodes in tree order', () => {
    write('index.md', 'Home');
    write('a/index.md', 'A');
    write('a/one.md', 'One');
    write('a/two.md', 'Two');
    write('b/index.md', 'B');
    const tree = buildNavTree({ contentDir: root, base: '/x/', navConfig: null });
    expect(flattenPages(tree).map((p) => p.href)).toEqual([
      '/x/a/', '/x/a/one/', '/x/a/two/', '/x/b/',
    ]);
  });
});
```

- [ ] **Step 2: Run — RED**

Run: `npx vitest run tests/nav-tree.test.mjs`
Expected: FAIL — `Cannot find module '../lib/nav-tree.mjs'`.

- [ ] **Step 3: Implement `lib/nav-tree.mjs`**

Write it to satisfy the spec in the Interfaces block and every test above. Notes:
- Read frontmatter with a small `^---\n([\s\S]*?)\n---` slice + line scan for `title:`, `sidebar_order:`, `sidebar_label:` (no YAML dep needed for these flat scalars; trim quotes).
- Directory walk: `fs.readdirSync(contentDir, { withFileTypes: true })`. `index.md` in a dir → that dir's group `href`. A dir with no `index.md` still becomes a group node with `href: null`.
- `stripAndTitle(name)`: `name.replace(/^\d+[-_]/, '').replace(/[-_]+/g, ' ').replace(/\b\w/g, (c) => c.toUpperCase())`.
- `numOf(name)`: `const m = name.match(/^(\d+)[-_]/); return m ? Number(m[1]) : null;` — numbered sort ascending first, then `localeCompare`.
- Override: build the auto tree first (as a `Map<dir, NavNode>` + a loose-md list), then reorder/relabel per `navConfig`; `{ items: [...] }` builds page nodes by slug against the generated tree (a slug like `spec` → the top-level `spec.md`; `foo/bar` → nested). Unlisted auto dirs append after, in auto order. If `navConfig` is `null`, return the auto tree + Reference group.

- [ ] **Step 4: Run — GREEN**

Run: `npx vitest run tests/nav-tree.test.mjs`
Expected: PASS (all cases).

- [ ] **Step 5: Delete the Starlight sidebar module + test**

```bash
git rm lib/sidebar.mjs tests/sidebar.test.mjs
```

- [ ] **Step 6: Full unit suite (no build yet)**

Run: `npx vitest run tests/config.test.mjs tests/wiki-resolve.test.mjs tests/apply-wiki-links.test.mjs tests/nav-tree.test.mjs`
Expected: all PASS. (`fixture-build.test.mjs` still references Starlight output — it is reshaped in Task 7; do not run it yet.)

- [ ] **Step 7: Commit**

```bash
git add lib/nav-tree.mjs tests/nav-tree.test.mjs
git commit -m "Add nav-tree builder (replaces Starlight sidebar); drop lib/sidebar.mjs

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>"
```

---

## Task 4: `Header`, `Footer`, `TableOfContents`, `MermaidRenderer`, `Search`, `reading-enhancements`

**Files:**
- Create: `src/components/Header.astro`, `src/components/Footer.astro`, `src/components/TableOfContents.astro`, `src/components/MermaidRenderer.astro`, `src/components/Search.astro`
- Create: `src/lib/reading-enhancements.ts`

**Interfaces:**
- Consumes: `global.css` tokens (Task 2).
- Produces:
  - `<Header title href githubUrl />` — fixed translucent header; brand = `title` linking to `href` (the site `base`); a GitHub icon link to `githubUrl`; a theme-toggle button; a "Search" trigger button (`id="search-trigger"`); mobile disclosure. Dispatches `themechange`.
  - `<Footer title githubUrl />` — config-driven; `border-t border-line bg-bg`, mono type, `© <year> · Built with Astro + Tailwind` + a `source` link to `githubUrl`.
  - `<TableOfContents headings={{ depth, slug, text }[]} />` — unchanged behaviour from `notes`.
  - `<MermaidRenderer />` — `window.__renderMermaid`, `themechange` re-render; verbatim from `notes`.
  - `<Search />` — renders a hidden `<dialog id="search-modal">` containing a `<div id="pagefind-ui">`; an inline module script that lazy-loads `/pagefind/pagefind-ui.js` (path prefixed with `import.meta.env.BASE_URL`) on first open, wires `#search-trigger` (click) + `/` key + `Escape`.
  - `initCopyButtons(): void` in `reading-enhancements.ts`.

- [ ] **Step 1: `TableOfContents.astro` — port verbatim**

Copy `notes/src/components/TableOfContents.astro` byte-for-byte. No changes (it already keys off `--color-*` tokens and `depth`/`slug`/`text`).

- [ ] **Step 2: `MermaidRenderer.astro` — port verbatim**

Copy `notes/src/components/MermaidRenderer.astro` byte-for-byte. It targets `pre[data-language="mermaid"]` — which Astro's Shiki emits for ` ```mermaid ` fences — and listens for `themechange`.

- [ ] **Step 3: `reading-enhancements.ts` — port `initCopyButtons` only**

Copy `notes/src/lib/reading-enhancements.ts`, then delete `initReadingProgress` and update the file's top comment to say "copy button only".

- [ ] **Step 4: `Header.astro` — port style, config-drive content**

From `notes/src/components/Header.astro`. Keep: the `<header id="site-header" class="fixed top-0 left-0 right-0 z-50 transition duration-300">` shell, the `max-w-6xl mx-auto px-6 h-16` brand row, the rAF-throttled `syncHeader` scroll-blur script (`SCROLLED_CLASSES`), the theme-toggle button + sun/moon SVGs + the `<style>` icon-swap rules + the `applyTheme`/`currentTheme`/`themechange` script, the mobile `#menu-btn` + `#mobile-menu` disclosure + its full script (Escape, focus move, scroll lock, matchMedia).
Change:
- `Props`: `{ title: string; href: string; githubUrl: string }`.
- Brand `<a href={href}>` renders `title` in the same `font-mono` treatment (keep the `‹ … ›` faint brackets around it).
- **Delete** the `import { NOTES_NAV }` line, the entire `<div data-subnav-row>` sub-nav block, and the `NOTES_NAV.map(...)` list inside `#mobile-menu` (keep the theme-toggle-mobile button there).
- Add, in the desktop `<nav>` before the theme toggle: a `<button id="search-trigger" aria-label="Search" class="p-2 text-muted hover:text-ink rounded-lg hover:bg-surface-hover/50 transition">` with a magnifier SVG; and a GitHub `<a href={githubUrl} target="_blank" rel="noopener noreferrer" class="p-2 …">` with the GitHub mark SVG. Add the same two to `#mobile-menu`.
- The `html:has([data-subnav-row])` scroll-padding rule in `global.css` is now dead — leave it (harmless) or drop it; dropping is cleaner.

- [ ] **Step 5: `Footer.astro` — port style, config-drive content**

From `notes/src/components/Footer.astro`. Keep the `<footer class="border-t border-line bg-bg">` + `max-w-6xl mx-auto px-6 py-12` + two-row `border-t border-line` layout + `© {year}` + mono type.
Change:
- `Props`: `{ title: string; githubUrl: string }`.
- **Delete** `import { FOOTER_SOCIALS }` and the `.map` — replace with a single GitHub icon link to `githubUrl`.
- Brand line renders `title`; drop the "Observability Architect · …" personal tagline (or replace with `cfg.description` passed as a prop — simplest: drop it).
- "Built with Astro + Tailwind." kept; `source` link → `githubUrl`.

- [ ] **Step 6: `Search.astro` — new**

```astro
---
// pagefind-ui modal. The index is produced by the astro-pagefind integration
// into /pagefind/ at build; dev has no index, so the modal shows an empty state.
---
<dialog id="search-modal" class="w-full max-w-2xl rounded-xl border border-line bg-surface p-0 backdrop:bg-black/50">
  <div class="p-4">
    <div id="pagefind-ui"></div>
  </div>
</dialog>

<script>
  const modal = document.getElementById('search-modal') as HTMLDialogElement | null;
  const trigger = document.getElementById('search-trigger');
  let loaded = false;

  async function ensureUI() {
    if (loaded || !modal) return;
    loaded = true;
    const base = import.meta.env.BASE_URL;
    await import(/* @vite-ignore */ `${base}pagefind/pagefind-ui.js`).catch(() => {});
    // @ts-expect-error — PagefindUI is a global from the loaded script
    if (window.PagefindUI) new window.PagefindUI({ element: '#pagefind-ui', showSubResults: true, baseUrl: base });
  }

  function open() { ensureUI().then(() => modal?.showModal()); }

  trigger?.addEventListener('click', open);
  document.addEventListener('keydown', (e) => {
    if (e.key === '/' && !/(input|textarea)/i.test((e.target as HTMLElement)?.tagName)) { e.preventDefault(); open(); }
    if (e.key === 'Escape') modal?.close();
  });
  modal?.addEventListener('click', (e) => { if (e.target === modal) modal.close(); });
</script>

<style>
  #pagefind-ui {
    --pagefind-ui-primary: rgb(var(--color-accent-rgb));
    --pagefind-ui-text: var(--color-body);
    --pagefind-ui-background: var(--color-surface);
    --pagefind-ui-border: var(--color-border);
    --pagefind-ui-font: var(--font-sans);
  }
</style>
```

- [ ] **Step 7: Sync**

Run: `npx astro sync`
Expected: exit 0. No build yet (still no pages/Doc.astro).

- [ ] **Step 8: Commit**

```bash
git add src/components/ src/lib/reading-enhancements.ts
git commit -m "Port chrome components (Header/Footer/TOC/Mermaid) + add Search modal

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>"
```

---

## Task 5: `SidebarTree.astro`

**Files:**
- Create: `src/components/SidebarTree.astro`

**Interfaces:**
- Consumes: `NavNode[]` (from `lib/nav-tree.mjs`, Task 3); `global.css` (`.card`, `--color-*`).
- Produces: `<SidebarTree nodes={NavNode[]} currentPath={string} />` — a recursive `<ul>`/`<details>` nav. A node with `children.length === 0` renders `<a href={node.href}>`; a node with children renders `<details><summary>{label}{href ? ' link' : ''}</summary>` + nested list, with the group's own `href` (if any) as the first child link ("Overview"). `aria-current="page"` + teal styling on the link whose `href` matches `currentPath`. `<details>` default-open on the branch containing `currentPath`. Persist per-group open state to `localStorage['docs-nav-expanded']` (best-effort try/catch). Visual style mirrors `notes/src/components/NotesSidebarTree.astro`: `flex flex-col gap-1`, `px-3 py-2.5 rounded-lg text-sm text-muted hover:text-ink hover:bg-surface-hover/50`, a `tree-chevron` `▸` that rotates on `[open]`, `summary { list-style: none }`.

- [ ] **Step 1: Implement**

Write `SidebarTree.astro` per the interface. Use `Astro.self` for recursion (as `NotesSidebarTree` does). The client `<script>`: on load, mark the active link (`href === location.pathname`), open its `<details>` ancestors, `scrollIntoView({ block: 'center' })` after a frame; wire `toggle` listeners that write the open set to `localStorage`.

- [ ] **Step 2: Sync**

Run: `npx astro sync`
Expected: exit 0.

- [ ] **Step 3: Commit**

```bash
git add src/components/SidebarTree.astro
git commit -m "Add SidebarTree — recursive NavNode renderer in notes' sidebar style

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>"
```

---

## Task 6: `Doc.astro` + `pages/[...slug].astro`

**Files:**
- Create: `src/layouts/Doc.astro`
- Create: `src/pages/[...slug].astro`

**Interfaces:**
- Consumes: `Base.astro`, `Header.astro`, `Footer.astro`, `SidebarTree.astro`, `TableOfContents.astro`, `MermaidRenderer.astro`, `Search.astro`, `reading-enhancements.ts`; `lib/config.mjs` (`loadDocsSiteConfig`), `lib/nav-tree.mjs` (`buildNavTree`, `flattenPages`); `astro:content` (`getCollection`, `render`).
- Produces:
  - `Doc.astro` props: `{ entry, headings, navTree, crumbs, prev, next }` where `entry` is a `CollectionEntry<'docs'>`, `crumbs` is `{ label, href? }[]`, `prev`/`next` are `{ label, href } | undefined`.
  - `[...slug].astro` — one static route per `docs` entry; root `index` → `base` (`""` param).

- [ ] **Step 1: `Doc.astro`**

Structure (matches spec §2a):

```astro
---
import Base from './Base.astro';
import Header from '../components/Header.astro';
import Footer from '../components/Footer.astro';
import SidebarTree from '../components/SidebarTree.astro';
import TableOfContents from '../components/TableOfContents.astro';
import MermaidRenderer from '../components/MermaidRenderer.astro';
import Search from '../components/Search.astro';
import { loadDocsSiteConfig } from '../../lib/config.mjs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const __dirname = path.dirname(fileURLToPath(import.meta.url));
const FIX = path.join(__dirname, '../../fixtures/sample-repo');
const cfg = loadDocsSiteConfig({
  configPath: process.env.DOCS_SITE_CONFIG || path.join(FIX, 'docs-site.yaml'),
  repoSlug: process.env.REPO_SLUG || 'shipsolid/sample-repo',
});
const repo = cfg.social.github.replace(/^https:\/\/github\.com\//, '');

const { entry, headings, navTree, crumbs, prev, next } = Astro.props;
const { title, description, updated } = entry.data;
const editUrl = `https://github.com/${repo}/edit/${cfg.editBranch}/${cfg.docsDir}/${entry.filePath?.split('/').map(encodeURIComponent).join('/') ?? ''}`;
const fmtUpdated = updated
  ? new Intl.DateTimeFormat('en-US', { year: 'numeric', month: 'long', day: 'numeric' }).format(updated)
  : null;
---
<Base title={`${title} — ${cfg.title}`} description={description ?? cfg.description}>
  <Header title={cfg.title} href={cfg.base} githubUrl={cfg.social.github} />
  <main id="main" tabindex="-1" class="pt-36 pb-20">
    <header class="max-w-9xl mx-auto px-6 mb-12">
      <nav class="flex items-center gap-2 text-xs font-mono text-faint mb-4 flex-wrap" aria-label="Breadcrumb">
        {crumbs.map((c, i) => (
          <>
            {i > 0 && <span>/</span>}
            {c.href ? <a href={c.href} class="hover:text-accent transition-colors">{c.label}</a> : <span>{c.label}</span>}
          </>
        ))}
      </nav>
      <h1 class="text-3xl md:text-4xl font-bold text-ink leading-tight mb-4">{title}</h1>
      {description && <p class="text-lg text-muted mb-6">{description}</p>}
      {fmtUpdated && (
        <div class="text-sm text-dim font-mono border-t border-line pt-4">Updated {fmtUpdated}</div>
      )}
    </header>

    <div class="max-w-9xl mx-auto px-6">
      <div class="doc-grid">
        <aside class="hidden xl:block">
          <div class="card sticky top-24 max-h-[calc(100vh-7rem)] overflow-y-auto">
            <p class="eyebrow mb-4">{cfg.title}</p>
            <div id="nav-source"><SidebarTree nodes={navTree} currentPath={Astro.url.pathname} /></div>
          </div>
        </aside>

        <div class="xl:hidden space-y-3">
          <details class="card"><summary class="eyebrow cursor-pointer">On this page</summary>
            <div class="mt-4"><TableOfContents headings={headings} /></div></details>
          <details class="card"><summary class="eyebrow cursor-pointer">Navigation</summary>
            <div id="nav-mobile" class="mt-4"></div></details>
        </div>
        <script>
          // Clone the build-time-rendered sidebar into the mobile disclosure so
          // the nav tree ships once. cloneNode (not innerHTML) — no parse, and
          // no innerHTML sink even though the source is trusted static DOM.
          const s = document.getElementById('nav-source'), m = document.getElementById('nav-mobile');
          if (s && m) for (const child of s.cloneNode(true).childNodes) m.appendChild(child);
        </script>

        <article class="min-w-0" data-pagefind-body>
          <span hidden data-pagefind-meta={`title:${title}`}></span>
          <div class="prose prose-invert prose-doc"><slot /></div>

          {(prev || next) && (
            <div class="mt-16 pt-6 border-t border-line grid grid-cols-1 sm:grid-cols-2 gap-4">
              <div>{prev && (
                <a href={prev.href} class="card group flex flex-col gap-1 no-underline hover:-translate-y-0.5">
                  <span class="text-xs font-mono text-faint">← Previous</span>
                  <span class="text-sm font-medium text-ink group-hover:text-accent transition-colors">{prev.label}</span>
                </a>)}</div>
              <div class="sm:text-right">{next && (
                <a href={next.href} class="card group flex flex-col gap-1 no-underline hover:-translate-y-0.5 sm:items-end">
                  <span class="text-xs font-mono text-faint">Next →</span>
                  <span class="text-sm font-medium text-ink group-hover:text-accent transition-colors">{next.label}</span>
                </a>)}</div>
            </div>
          )}

          <div class="mt-10 flex items-center justify-between text-sm font-mono">
            <a href={cfg.base} class="text-accent hover:text-accent-hover transition-colors">← All docs</a>
            <a href={editUrl} target="_blank" rel="noopener noreferrer" class="text-dim hover:text-body transition-colors">Edit on GitHub</a>
          </div>
        </article>

        <aside class="hidden xl:block">
          <div class="sticky top-24 max-h-[calc(100vh-7rem)] overflow-y-auto"><TableOfContents headings={headings} /></div>
        </aside>
      </div>
    </div>
  </main>
  <Footer title={cfg.title} githubUrl={cfg.social.github} />
  <Search />
  <MermaidRenderer />
  <script>import { initCopyButtons } from '../lib/reading-enhancements'; initCopyButtons();</script>
</Base>
```

Adjust import depth (`../../lib` vs `../lib`) to the real location — `Doc.astro` is `src/layouts/`, so engine `lib/` is `../../lib/` and `src/lib/` is `../lib/`.

- [ ] **Step 2: `pages/[...slug].astro`**

```astro
---
import { getCollection, render } from 'astro:content';
import Doc from '../layouts/Doc.astro';
import { loadDocsSiteConfig } from '../../lib/config.mjs';
import { buildNavTree, flattenPages } from '../../lib/nav-tree.mjs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

export async function getStaticPaths() {
  const __dirname = path.dirname(fileURLToPath(import.meta.url));
  const engineRoot = path.join(__dirname, '../..');
  const FIX = path.join(engineRoot, 'fixtures', 'sample-repo');
  const cfg = loadDocsSiteConfig({
    configPath: process.env.DOCS_SITE_CONFIG || path.join(FIX, 'docs-site.yaml'),
    repoSlug: process.env.REPO_SLUG || 'shipsolid/sample-repo',
  });
  const contentDir = process.env.DOCS_OUT || path.join(engineRoot, 'src', 'content', 'docs');

  const navTree = buildNavTree({ contentDir, base: cfg.base, navConfig: cfg.nav });
  const flat = flattenPages(navTree);

  const entries = await getCollection('docs');
  const rendered = await Promise.all(entries.map((e) => render(e)));

  return entries.map((entry, i) => {
    const slug = entry.id === 'index' ? undefined : entry.id;
    const url = entry.id === 'index' ? cfg.base : `${cfg.base}${entry.id}/`;
    const fi = flat.findIndex((p) => p.href === url);
    const crumbs = [{ label: 'Home', href: cfg.base }];
    if (entry.id !== 'index') {
      const parts = entry.id.split('/');
      // group crumb (first segment) if it has its own index page
      const groupHref = `${cfg.base}${parts[0]}/`;
      if (flat.some((p) => p.href === groupHref) && parts.length > 1) {
        const g = navTree.find((n) => n.href === groupHref);
        crumbs.push({ label: g?.label ?? parts[0], href: groupHref });
      }
      crumbs.push({ label: entry.data.sidebar_label ?? entry.data.title });
    }
    return {
      params: { slug },
      props: {
        entry,
        headings: rendered[i].headings,
        navTree,
        crumbs,
        prev: fi > 0 ? flat[fi - 1] : undefined,
        next: fi >= 0 && fi < flat.length - 1 ? flat[fi + 1] : undefined,
        Content: rendered[i].Content,
      },
    };
  });
}

const { Content, ...props } = Astro.props;
---
<Doc {...props}><Content /></Doc>
```

- [ ] **Step 3: Build against the fixture**

Run: `npm run build` (runs `gen-docs` + `check-links` via `prebuild`, then `astro build`)
Expected: exit 0. `dist/index.html`, `dist/spec/index.html`, `dist/architecture/adr-example/index.html`, `dist/_pagefind/pagefind.js` all exist. Iterate on import paths / prop shapes until green — do **not** change `lib/` or `scripts/`.

- [ ] **Step 4: Visual smoke (dev)**

Run: `npm run dev`, open `http://localhost:4321/sample-repo/` and a nested page. Confirm: dark by default, Shantell Sans body + JetBrains Mono code, 3-column layout with wide centre + card sidebar + right TOC, `.prose-doc` h2 underline + teal links, header blur on scroll, theme toggle works, mermaid renders, code copy button on hover. Note any gap in the report; fix CSS/markup (never tokens).

- [ ] **Step 5: Commit**

```bash
git add src/layouts/Doc.astro src/pages/
git commit -m "Add Doc layout + catch-all route; bespoke build renders end to end

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>"
```

---

## Task 7: Fixture reshape + `fixture-build.test.mjs` update + green whole suite

**Files:**
- Modify: `fixtures/sample-repo/docs-site.yaml`
- Modify: `tests/fixture-build.test.mjs`

**Interfaces:**
- Consumes: everything from Tasks 1–6.
- Produces: `npm test` green (5 files: `config`, `wiki-resolve`, `apply-wiki-links`, `nav-tree`, `fixture-build`).

- [ ] **Step 1: Reshape `fixtures/sample-repo/docs-site.yaml`**

Replace the `sidebar:` block with:

```yaml
nav:
  - { label: Architecture, dir: architecture }
  - { label: Guides, dir: guides }
  - { label: Reference, items: [spec, project-readme] }
```

Keep `title`, `description`, `wiki_links` unchanged.

- [ ] **Step 2: Rewrite `tests/fixture-build.test.mjs` assertions**

Keep the `execFileSync` structure + env. Change the post-build assertions to:

```javascript
expect(existsSync(join(ENGINE, 'dist/index.html'))).toBe(true);
expect(existsSync(join(ENGINE, 'dist/spec/index.html'))).toBe(true);
expect(existsSync(join(ENGINE, 'dist/architecture/adr-example/index.html'))).toBe(true);
expect(existsSync(join(ENGINE, 'dist/_pagefind/pagefind.js'))).toBe(true);
const home = readFileSync(join(ENGINE, 'dist/index.html'), 'utf8');
expect(home).not.toMatch(/<html[^>]*data-theme=/);          // forced-dark: no attribute by default
expect(home).toMatch(/shantell-sans/i);                      // font stylesheet linked/inlined
```

Add `readFileSync` to the imports. Keep the 120 s timeout.

- [ ] **Step 3: Run the whole suite**

Run: `npm test`
Expected: 5 files pass. If `fixture-build` fails on a route path, reconcile the assertion with the actual `dist/` layout (the engine build is authoritative for flat routes — see Task 6 Step 3).

- [ ] **Step 4: Commit**

```bash
git add fixtures/sample-repo/docs-site.yaml tests/fixture-build.test.mjs
git commit -m "Reshape fixture nav; fixture-build asserts bespoke routes + pagefind

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>"
```

---

## Task 8: README update

**Files:**
- Modify: `README.md`

- [ ] **Step 1: Update the stack + local-dev sections**

In `docs-site/README.md`: change any "Starlight" mention to "bespoke Astro + Tailwind 4 (matches shipsolid/notes)". Update the env-contract table only if wording referenced Starlight. Update "Local engine dev": `npm run dev` serves the fixture consumer at `http://localhost:4321/sample-repo/`. Keep the `@main` contract paragraph and the `tests/fixture-build.test.mjs` guardrail note (still true). Add one line: "Theme/design system is ported from `shipsolid/notes` — keep `--color-*` / `.doc-grid` / `.prose-doc` names in sync with that repo."

- [ ] **Step 2: Commit**

```bash
git add README.md
git commit -m "README: bespoke stack, local dev URL, notes design-system note

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>"
```

---

## Task 9: `signal-forge/docs-site.yaml` reshape + local dry-run

**Files:**
- Modify: `/home/amit/repos/shipsolid/signal-forge/docs-site.yaml`

**Interfaces:**
- Consumes: the `nav:` schema (Task 1 `lib/config.mjs`, Task 3 `lib/nav-tree.mjs`).

- [ ] **Step 1: Reshape `signal-forge/docs-site.yaml`**

Replace the `sidebar:` block with `nav:`, preserving the same groups + order + the Reference slug list:

```yaml
nav:
  - { label: Architecture, dir: architecture }
  - { label: Services, dir: services }
  - { label: Observability, dir: observability }
  - { label: Infrastructure, dir: infrastructure }
  - { label: Deployment, dir: deployment }
  - { label: Operations, dir: operations }
  - { label: API, dir: api }
  - { label: Guides, dir: guides }
  - { label: Reference, items: [spec, otel-patterns, testing, project-readme] }
```

Keep `title`, `description`, `social`, `wiki_links` exactly as they are. Do not add `base`.

- [ ] **Step 2: Local dry-run — engine against signal-forge's real docs**

From `/home/amit/repos/shipsolid/docs-site` (branch `bespoke-theme`):

```bash
rm -rf dist src/content/docs
DOCS_SITE_CONFIG=/home/amit/repos/shipsolid/signal-forge/docs-site.yaml \
DOCS_SRC=/home/amit/repos/shipsolid/signal-forge/docs \
PROJECT_README=/home/amit/repos/shipsolid/signal-forge/README.md \
REPO_SLUG=shipsolid/signal-forge \
sh -c 'node scripts/gen-docs.mjs && node scripts/check-links.mjs && npx astro build'
find dist -name '*.html' | sed 's#^dist/##' | sort | wc -l
```

Expected: `gen-docs` ~50 files; `check-links` exit 0, 0 findings; `astro build` exit 0; HTML page count **49** (matches the pre-migration Starlight build). Investigate any page-count delta (missing group index / nav override slug typo). `npm run dev` with the same env → eyeball the SignalForge docs in the notes theme.

- [ ] **Step 3: Clean up the engine working tree**

```bash
rm -rf /home/amit/repos/shipsolid/docs-site/dist /home/amit/repos/shipsolid/docs-site/src/content/docs
```

- [ ] **Step 4: Commit (signal-forge, branch `main`)**

```bash
cd /home/amit/repos/shipsolid/signal-forge
git add docs-site.yaml
git commit -m "docs-site.yaml: sidebar -> nav for the bespoke docs-site engine

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>"
```

- [ ] **Step 5: No push.** Record the dry-run page count + a one-line visual note for the human. Merging `bespoke-theme` → engine `main` and pushing both repos is the human's call (post-plan).

---

## Post-plan (human, not tasks)

1. Review `bespoke-theme` in `docs-site`; merge to `main`.
2. Push `docs-site` `main` and `signal-forge` `main` (the latter carries Tasks 11–12 of the prior plan + this Task 9).
3. Watch the `signal-forge` `Docs` workflow deploy; confirm `https://shipsolid.github.io/signal-forge/` renders in the notes theme with all 49 pages.
4. Deferred (unchanged from the prior effort): `tests/sidebar.test.mjs` cleanup no longer applies (deleted); the project-README `description` hardcode in `gen-docs.mjs` still wants parameterising before consumer #2; add a no-`nav:` fixture case if a second consumer will rely on auto nav.

---

## Self-Review

**Spec coverage:**
- §1 disposition table (ported / kept / removed / not-ported) → Tasks 1 (deps, config, schema), 2 (global.css, Base), 3 (nav-tree replaces sidebar), 4 (Header/Footer/TOC/Mermaid/reading-enhancements + Search), 5 (SidebarTree). ✅
- §2 file structure → File Structure table + Tasks 1–8. ✅
- §2a `Doc.astro` structure → Task 6 Step 1 (full code). ✅
- §2b `[...slug].astro` → Task 6 Step 2 (full code). ✅
- §3 nav-tree model + `docs-site.yaml` `sidebar`→`nav` → Task 3 (impl + tests) + Task 1 Step 6 (`config.mjs`) + Tasks 7/9 (fixture + signal-forge reshape). ✅
- §4 `astro.config.mjs` → Task 1 Step 4 (full code) incl. Shiki `css-variables` + rehype anchors. ✅
- §5 content schema → Task 1 Step 5 (full code), `.passthrough()`. ✅
- §6 testing → Task 3 (`nav-tree.test.mjs`), Task 7 (`fixture-build.test.mjs`), Task 6 Step 4 (manual visual gate). ✅
- §7 consumer impact + rollout → Task 9. ✅
- §8 risks → mitigations land in: rehype anchors (Task 1), `initCopyButtons` (Task 4), pagefind + `_pagefind` assertion (Tasks 4/7), `global.css` trim scoped to enumerated blocks (Task 2), route-count 49 check (Task 9), explicit `--astro-code-*` values (Task 2). ✅
- §9 rollout order → Tasks 1→9 follow it. ✅

**Placeholder scan:** No "TBD"/"handle edge cases"/"similar to Task N". Ported-file steps name the exact source path under `/home/amit/repos/shipsolid/notes/`. `nav-tree.mjs` impl (Task 3 Step 3) is described by behaviour + backed by 9 concrete test cases with expected values rather than pasted code — acceptable because the tests pin every rule. `Doc.astro` / `[...slug].astro` / `astro.config.mjs` / `content.config.ts` / `Search.astro` are given as complete code.

**Type/name consistency:**
- `NavNode = { label, href, order, children }` — same shape in Task 3 (def + tests), Task 5 (`SidebarTree` consumes), Task 6 (`[...slug]` builds, `Doc` renders). ✅
- `buildNavTree({ contentDir, base, navConfig })` / `flattenPages(nodes)` — same signature Task 3 → Task 6. ✅
- `loadDocsSiteConfig(...)` returns `nav` (not `sidebar`) + `editBranch` — Task 1 def, consumed in Task 6 (`Doc`, `[...slug]`). ✅
- Env vars `DOCS_SITE_CONFIG` / `DOCS_SRC` / `REPO_SLUG` / `DOCS_OUT` — identical across Tasks 1, 6, 9. ✅
- `initCopyButtons` — exported Task 4, imported Task 6. ✅
- Component props (`Header {title, href, githubUrl}`, `Footer {title, githubUrl}`, `SidebarTree {nodes, currentPath}`, `TableOfContents {headings}`, `Doc {entry, headings, navTree, crumbs, prev, next}`) — defined in Tasks 4/5/6, consumed in Task 6's `Doc.astro`. ✅
