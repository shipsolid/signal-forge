---
title: docs-site bespoke theme (match notes look & feel)
date: 2026-09-06
status: approved
repos:
  - shipsolid/docs-site (rebuilt — /home/amit/repos/shipsolid/docs-site, branch bespoke-theme)
  - shipsolid/signal-forge (consumer — one docs-site.yaml edit)
supersedes: the Starlight foundation of superpowers/specs/2026-09-06-centralized-docs-site-design.md (config/pipeline half stays)
---

# docs-site bespoke theme

## Problem

`docs-site` renders with stock `@astrojs/starlight`. It publishes to
`shipsolid.github.io/signal-forge/`, next to `shipsolid.github.io/notes/`, which
is a bespoke Astro + Tailwind 4 site with a distinct identity: Catppuccin Mocha
dark-default palette, Shantell Sans + JetBrains Mono, a `300 / minmax(1000px,1fr)
/ 300` three-column grid, an 18px/1.8 prose style. The two sites read as
unrelated products.

## Goal

`docs-site`'s published output matches `notes`' look and feel — theme, fonts,
column widths, spacing, chrome style, prose rhythm — while keeping the engine's
framework-agnostic value: the config-driven, multi-repo docs pipeline
(`lib/config.mjs`, the wiki-link resolver, `gen-docs`/`check-links`, the reusable
`build-deploy.yml`).

## Decision

Drop Starlight. Rebuild the engine's presentation layer as a bespoke Astro +
Tailwind 4 site adopting `notes`' design system (`global.css` tokens, fonts,
`.doc-grid`, `.prose-doc`) and chrome *style* (fixed translucent header, mono
brand, teal accent, border-line dividers, card sidebar, scroll-spy TOC).
Docs-appropriate page layouts are built on that system — `notes`' Zettelkasten
page structure (knowledge graph, backlinks, typed relations, zettel IDs,
read-state, tag pages) is **not** ported; `docs-site` has no data model for it.

### Settled sub-decisions

- **Fidelity:** pixel-match `notes`' layout — the 3-column grid, header, footer,
  sidebar/TOC styling — not just palette+fonts.
- **Dark default:** forced on first visit (OS preference ignored; only a stored
  `"light"` writes to the DOM), matching `notes`.
- **Column width:** match `notes`' wide measure — content column ~1000px, side
  rails ~300px at ≥1760px, proportional 260/1fr/260 xl–3xl, stacked below xl.
- **Search:** a header search button opening `pagefind-ui` in a themed modal.
  Not `notes`' fuller `/search` page with filters.
- **Reading-progress bar:** dropped.

## Non-goals

- Porting the knowledge graph, backlinks, related-notes, tag pages, zettel IDs,
  read-state, or the reading-progress bar.
- Changing the reusable `build-deploy.yml` contract (`gen-docs` → `check-links` →
  `astro build` → deploy stays byte-for-byte).
- Changing `signal-forge` beyond one `docs-site.yaml` edit (`sidebar:` → `nav:`).
- A visual-regression test harness. The fixture build + a manual screenshot
  check against `notes` is the acceptance gate.
- Re-theming `notes` or `_shipsolid.github.io`.

---

## 1. Component / file disposition

### Ported from `notes` (adapted)

| From `notes` | Into engine | Adaptation |
| --- | --- | --- |
| `src/styles/global.css` (776 L) | `src/styles/global.css` | Trim gita / flashcards / knowledge-graph / tools rules (~40%). **Keep, unrenamed:** the `@theme` block (tokens, `--breakpoint-3xl`, fonts, container scale, keyframes), Catppuccin Mocha `:root` + `:root[data-theme='light']`, `@custom-variant light`, `body`/scrollbar/`:focus-visible` base, `.doc-grid` / `.doc-grid-2`, `.card`, `.tag`, `.eyebrow`, `.section-*`, `.prose pre code` + the full `.prose-doc` block, `.copy-btn`, `@media print`. **Drop:** `.filter-tag`, `.callout*`, `.masked-*`, `.card-content`, `.tabs`, `.badge*`, `.btn-*`, gita reading-mode rules, `bg-grid*` utilities. |
| `src/layouts/Base.astro` | `src/layouts/Base.astro` | Keep `<head>` scaffold, canonical/OG tags, the pre-paint theme-init `<script is:inline>` (verbatim — forced dark, only stored `"light"` acts), `@fontsource` imports, `.skip-link`. Replace hardcoded portfolio `description` / `ogImage` / `og:site_name` / GA4 block with values from `docs-site.yaml` (`title`, `description`, `social.github`) passed via props. |
| `src/components/Header.astro` (style only) | `src/components/Header.astro` | Keep the visual + behaviour shell: `fixed` translucent header, rAF-throttled scroll-blur (`bg-bg/95 backdrop-blur-md border-b`), `h-16` brand row, `max-w-6xl` inner, theme-toggle button (sun/moon SVG swap on `[data-theme='light']`), mobile disclosure menu (Escape close, focus move, scroll lock), `themechange` CustomEvent dispatch. **Change:** brand text + home `href` from config (`title`, `base`); a GitHub link from `social.github`; **drop** the portfolio sub-nav strip (`NOTES_NAV`, `data-subnav-row`) entirely; **add** a search trigger button that opens `Search.astro`'s modal. |
| `src/components/Footer.astro` (style only) | `src/components/Footer.astro` | Keep `border-t border-line bg-bg`, `max-w-6xl` inner, mono type, two-row layout. **Change:** brand/tagline/socials from config; "Built with Astro + Tailwind" line kept; `source` link → `social.github`. |
| `src/components/TableOfContents.astro` | `src/components/TableOfContents.astro` | **Verbatim.** Depth 2–3 filter, `border-l` rail, IntersectionObserver scroll-spy with `data-active`, the dual-instance `offsetParent` filter. |
| `src/components/MermaidRenderer.astro` | `src/components/MermaidRenderer.astro` | **Verbatim** (whatever `notes` ships — reads `themechange` to re-render). Replaces the `astro-mermaid` integration. |
| `src/lib/reading-enhancements.ts` | `src/lib/reading-enhancements.ts` | **`initCopyButtons` only.** Drop `initReadingProgress` and any read-state imports. |
| `src/components/ChapterNavTree.astro` (structure + card style) | `src/components/SidebarTree.astro` | New component. Same look: `card sticky top-24 max-h-[calc(100vh-7rem)] overflow-y-auto`, mono uppercase group label, nested `<ul>` with collapsible `<details>` for groups, teal `aria-current` on the active page. Fed by the `NavNode` tree (§3), not a `NotesTree`. |

### Kept from the current engine — untouched

`lib/config.mjs`, `lib/wiki-resolve.mjs`, `lib/wiki-index.mjs`,
`lib/apply-wiki-links.mjs`, `lib/doc-path.mjs`, `lib/remark-rewrite-md-links.mjs`,
`scripts/gen-docs.mjs`, `scripts/check-links.mjs`,
`.github/workflows/build-deploy.yml`, `.github/workflows/ci.yml`, `.nvmrc`,
`tsconfig.json`, `vitest.config.mjs`,
`tests/config.test.mjs`, `tests/wiki-resolve.test.mjs`,
`tests/apply-wiki-links.test.mjs`.

One touch to `lib/config.mjs`: `codeLangAliases` was consumed by Starlight's
`expressiveCode`. Keep the field (now feeds `astro.config.mjs`
`markdown.shikiConfig.langAlias`); no signature change.

### Removed (Starlight-specific)

- deps: `@astrojs/starlight`, `astro-mermaid`
- `astro.config.mjs` — rewritten (no Starlight integration)
- `src/content.config.ts` — `docsSchema` → plain zod (§5)
- `lib/sidebar.mjs` → replaced by `lib/nav-tree.mjs` (§3)
- `tests/sidebar.test.mjs` → replaced by `tests/nav-tree.test.mjs`
- `tests/fixture-build.test.mjs` — assertions updated (§6)

### Explicitly NOT ported

Knowledge graph (`NotesGraphCanvas`, `KnowledgeGraphTree`, `notes-graph` lib,
topic colours), backlinks, related-notes, tag pages (`/tag/…`), zettel IDs, MOC
badge, `kind`/`maturity`, read-state / "✓ Read" badge, reading-progress bar,
`NOTES_NAV` sub-nav.

---

## 2. New engine file structure

```
docs-site/
  astro.config.mjs          # bespoke — see §4
  package.json
  .nvmrc tsconfig.json vitest.config.mjs
  src/
    styles/global.css       # ported + trimmed (§1)
    layouts/
      Base.astro            # ported (§1)
      Doc.astro             # NEW (§2a)
    components/
      Header.astro Footer.astro TableOfContents.astro
      SidebarTree.astro MermaidRenderer.astro Search.astro
    lib/
      reading-enhancements.ts   # ported, copy-buttons only
    pages/
      [...slug].astro        # NEW (§2b)
    content.config.ts        # NEW plain zod (§5)
    content/docs/            # gen-docs output, gitignored
  lib/
    nav-tree.mjs             # NEW (§3) — replaces sidebar.mjs
    config.mjs wiki-index.mjs wiki-resolve.mjs apply-wiki-links.mjs
    doc-path.mjs remark-rewrite-md-links.mjs        # unchanged
  scripts/gen-docs.mjs check-links.mjs              # unchanged
  fixtures/sample-repo/       # content unchanged; docs-site.yaml `nav:` reshaped
  tests/
    config.test.mjs wiki-resolve.test.mjs apply-wiki-links.test.mjs  # unchanged
    nav-tree.test.mjs          # NEW
    fixture-build.test.mjs      # assertions updated
  .github/workflows/build-deploy.yml ci.yml         # unchanged
  README.md                  # update the "local dev" + stack notes
```

### 2a. `src/layouts/Doc.astro`

Wraps `Base` + `Header` + `<main>` + `Footer`. Inside `<main class="pt-36 pb-20">`:

- **Article header** — `max-w-9xl mx-auto px-6 mb-12`: breadcrumb (mono, `text-faint`,
  `Home / <group> / <page>` from the nav path), `<h1 class="text-3xl md:text-4xl font-bold text-ink">`,
  optional `<p class="text-lg text-muted">` description, a mono meta row
  (`Updated <date>` when frontmatter has `updated`) under a `border-t border-line`.
- **Three-column** — `max-w-9xl mx-auto px-6` › `.doc-grid`:
  - left `<aside class="hidden xl:block">` → `<SidebarTree>` in the sticky card;
  - `<article class="min-w-0" data-pagefind-body>` with pagefind meta spans
    (`title`, `url`) → `<div class="prose prose-invert prose-doc"><slot/></div>` →
    **prev/next** (`grid sm:grid-cols-2`, card style) → a footer row with
    `← All docs` (to `base`) and `Edit on GitHub`
    (`https://github.com/<repo>/edit/<branch>/docs/<path>`; repo + branch from
    `docs-site.yaml` `social.github` + a new optional `edit_branch`, default `main`);
  - right `<aside class="hidden xl:block">` → sticky `<TableOfContents>`.
  - below xl: a `<details class="card">` "On this page" holding the TOC, and a
    `<details class="card">` "Navigation" holding a client-cloned copy of the
    sidebar tree (same `#…-source` → `#…-mobile` innerHTML clone `notes` uses).
- `<MermaidRenderer/>` before `</main>`.
- `<script>` → `initCopyButtons()` only.

Props: `{ entry, headings, navTree, crumbs, prev, next }`.

### 2b. `src/pages/[...slug].astro`

- `getStaticPaths()` — `getCollection('docs')`; one route per entry; `entry.id`
  (github-slugger'd path, `index` collapsed) is the slug. Root `index` → `base`.
- Per page: `render(entry)` for `<Content/>` + `headings`; build `navTree` once
  via `lib/nav-tree.mjs` (cache across paths); derive `crumbs`, and `prev`/`next`
  from the depth-first flatten of `navTree` (page nodes only).
- Renders `<Doc …><Content/></Doc>`.

---

## 3. Nav tree — replaces the Starlight sidebar

```
NavNode = {
  label: string,
  href: string | null,   // null = a pure group heading (rare; groups usually have an index.md landing)
  order: number,
  children: NavNode[],
}
buildNavTree({ contentDir, base, navConfig }) -> NavNode[]   // top-level list
flattenPages(nodes) -> { label, href }[]                     // DFS, href != null, for prev/next
```

`lib/nav-tree.mjs`:

- **Auto (no `nav:` in `docs-site.yaml`):** walk immediate subdirs of the
  generated `src/content/docs/`. Each subdir → a group node; label = dir name
  with a leading `NN-`/`NN_` stripped + title-cased; order = the numeric prefix
  (ascending), else `localeCompare` after numbered ones. A subdir's `index.md`
  (materialised from `README.md`) → the group's own `href`; its remaining `*.md`
  → child page nodes, ordered by frontmatter `sidebar_order` then `title`.
  Loose top-level `*.md` (e.g. `spec.md`) → a trailing "Reference" group.
  Root `index.md` is the site landing (`base`), not shown as a nav item.
- **Override (`nav:` present):** an ordered list; each item is
  `{ label, dir }` (auto-populate that dir's pages) or
  `{ label, items: [slug, …] }` (explicit page slugs, in the given order).
  Anything not covered by the override is appended in auto order.
- Same "auto by default, config overrides" contract as `lib/sidebar.mjs` today.

### `docs-site.yaml` schema change

`sidebar:` (Starlight-shaped) → `nav:`:

```yaml
nav:
  - { label: Architecture, dir: architecture }
  - { label: Services, dir: services }
  - { label: Reference, items: [spec, otel-patterns, testing, project-readme] }
```

`lib/config.mjs`: rename the parsed field `sidebar` → `nav` (raw passthrough
array, unchanged otherwise). `title`, `description`, `base`, `social`,
`wiki_links`, `docs_dir`, `project_readme` unchanged. Add optional `edit_branch`
(default `"main"`).

---

## 4. `astro.config.mjs` (bespoke)

```
integrations: [sitemap(), pagefind()]
vite: { plugins: [tailwindcss()] }
markdown: {
  remarkPlugins: [remarkRewriteMdLinks({ docsPrefix: base w/o trailing slash })],
  shikiConfig: { theme: 'css-variables', langAlias: cfg.codeLangAliases },
}
site: cfg.site, base: cfg.base, trailingSlash: 'always', output: 'static'
```

- No Starlight. `remarkWikiLinks` stays out of markdown — the wiki-link rewrite
  runs in `gen-docs.mjs` on raw text (unchanged).
- Shiki `css-variables` theme so code colours ride the `--astro-code-*` vars,
  set in `global.css` to Catppuccin Mocha values (dark) + a light pair; the
  `.prose-doc pre` frame (bg `#313244`, `rounded-xl`, `border`) already ported.
- Config resolution identical to today: `DOCS_SITE_CONFIG` / `DOCS_SRC` /
  `REPO_SLUG` env with `fixtures/sample-repo` fallbacks.

---

## 5. Content schema

`src/content.config.ts` — replace `docsSchema(...)` with:

```
loader: glob({ pattern: '**/[^_]*.md', base: './src/content/docs' })
schema: z.object({
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
}).passthrough()
```

`.passthrough()` (not `.strict()`) so an unforeseen frontmatter key from a
consumer's `docs/` never fails the build — a broken docs build in the engine
breaks every consumer at once.

`gen-docs.mjs` already injects `title` + `description` into the imported
`project-readme.md`, so it satisfies the schema unchanged.

---

## 6. Testing

- **`tests/nav-tree.test.mjs`** (new): `NN-` ordering then alpha; label
  derivation + `sidebar_label` override; `index.md` → group `href`; loose
  top-level md → Reference group; `nav:` override (`{dir}` and `{items}` forms)
  + append-the-rest; `flattenPages` DFS order for prev/next; root `index`
  excluded from nav.
- **`tests/fixture-build.test.mjs`** (updated): `gen-docs` → `check-links`
  (exit 0) → `astro build`; assert `dist/index.html`, `dist/spec/index.html`,
  `dist/architecture/adr-example/index.html` (bespoke flat routes),
  `dist/_pagefind/pagefind.js` exists, and that `dist/index.html` contains
  `data-theme` is **absent** by default (forced-dark = no attribute) and the
  Shantell Sans `@font-face` / stylesheet link is present.
- `config` / `wiki-resolve` / `apply-wiki-links` tests unchanged.
- Fixture `docs-site.yaml`: `sidebar:` → `nav:` reshape; content files unchanged.
- Manual gate: `npm run dev` in the engine, screenshot vs `notes`, confirm
  fonts / palette / column widths / prose rhythm / header + footer + TOC read
  as the same family.

---

## 7. Consumer impact + rollout

`signal-forge`: one edit — `docs-site.yaml` `sidebar:` → `nav:`. Nothing else
(`docs.yml`, `build-deploy.yml`, `website/` removal, README injection all
unaffected; the pipeline stages are identical).

Rollout — branch `bespoke-theme` in `docs-site`:

1. Rebuild the engine per §1–5.
2. Engine `ci.yml` green: unit tests + the updated fixture build.
3. Local dry-run: engine build against `signal-forge/docs` + a reshaped
   `signal-forge/docs-site.yaml` → visual check, route count 49, `check-links` 0.
4. Update `signal-forge/docs-site.yaml` (`sidebar:` → `nav:`).
5. Merge `bespoke-theme`; `signal-forge`'s next `docs.yml` run tracks `@main`.

## 8. Risks

| Risk | Mitigation |
| --- | --- |
| Losing a Starlight freebie unnoticed (anchor links, code copy, search) | Each has a named replacement: heading anchors via `@astrojs/markdown-remark`'s built-in `rehype-slug`/autolink (enable in `astro.config`); copy buttons via ported `reading-enhancements.ts`; search via `astro-pagefind`. `fixture-build.test.mjs` asserts `_pagefind` output. |
| `global.css` trim removes a rule a `.prose-doc` element needs | Trim only the enumerated non-doc blocks in §1; keep the whole `@theme` + `.prose-doc` + base layers. Fixture build renders a doc with headings/code/table/blockquote/list. |
| `nav:` reshape breaks a consumer mid-flight | Only `signal-forge` consumes today, edited in the same rollout (step 4) before merge (step 5). |
| Bespoke build drops pages vs Starlight | Step 3 asserts route count stays 49 for `signal-forge`. |
| Shiki `css-variables` looks flat | Ship explicit Catppuccin `--astro-code-*` values (dark + light) in `global.css`; visual gate in §6. |

## 9. Rollout order (task sequencing hint for the plan)

1. Branch + deps swap + `astro.config.mjs` rewrite + `content.config.ts`.
2. `global.css` port/trim + fonts + `Base.astro`.
3. `lib/nav-tree.mjs` + `tests/nav-tree.test.mjs` (+ `config.mjs` `sidebar`→`nav`).
4. `Header` / `Footer` / `TableOfContents` / `MermaidRenderer` / `Search`.
5. `SidebarTree.astro`.
6. `Doc.astro` + `pages/[...slug].astro`.
7. `fixtures/sample-repo/docs-site.yaml` reshape + `fixture-build.test.mjs` update; green the whole suite.
8. README update.
9. `signal-forge/docs-site.yaml` reshape + local dry-run route/visual check.
