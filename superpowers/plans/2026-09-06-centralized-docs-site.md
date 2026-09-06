# Centralized docs-site engine — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Move all Astro/Starlight machinery out of tech repos into a new `shipsolid/docs-site` repo that exposes a reusable GitHub Actions pipeline; wire `signal-forge` up as the first consumer so it keeps only `docs/`, `README.md`, `docs-site.yaml`, and a short caller workflow.

**Architecture:** `shipsolid/docs-site` holds the engine (Starlight config factory + `gen-docs`/`check-links` scripts + wiki-link resolver + remark plugins + content schema) and a `workflow_call` pipeline. The pipeline runs in the *consumer's* Actions context: it checks out the consumer repo and the engine side-by-side, runs `gen-docs` against the consumer's `docs/`, builds with Astro, and deploys to the consumer's own GitHub Pages. Consumers track `@main`; a fixture build in the engine's own CI is the contract guardrail.

**Tech Stack:** Astro 7, `@astrojs/starlight` 0.42, `astro-mermaid`, `github-slugger`, `unist-util-visit`, `js-yaml`, Vitest, GitHub Actions (`workflow_call`, `actions/deploy-pages`).

**Spec:** `superpowers/specs/2026-09-06-centralized-docs-site-design.md` (in `signal-forge`)

## Global Constraints

- Engine repo path: `/home/amit/repos/shipsolid/docs-site`, remote `shipsolid/docs-site` (public).
- Consumer repo path: `/home/amit/repos/shipsolid/signal-forge`, branch `docs-site-extraction` (already created; the spec commit is on it).
- Node version: `24` (write `.nvmrc` in the engine; `setup-node` reads it).
- `site` is always `https://shipsolid.github.io`. `base` defaults to `/<repo-name>/` derived from `$GITHUB_REPOSITORY`; a `base:` in config overrides.
- The engine's public interface files live under `lib/` and `scripts/`; `astro.config.mjs`, `.nvmrc`, `.gitignore`, both workflow files at repo root / `.github/`.
- Consumer config file: `docs-site.yaml` at consumer repo root.
- Engine env contract (set by the pipeline, honored by `scripts/*.mjs` and `astro.config.mjs`):
  `DOCS_SRC` (abs path to consumer `docs/`), `PROJECT_README` (abs path or empty), `DOCS_SITE_CONFIG` (abs path to `docs-site.yaml`), `REPO_SLUG` (`owner/name`), `DOCS_OUT` (optional; default `<engine>/src/content/docs`).
- Output must stay byte-comparable for signal-forge: same `https://shipsolid.github.io/signal-forge/` routes, same sidebar, same wiki-link resolution.
- Commit after every task. Commit messages end with the `Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>` trailer.
- Do not `git push` in any task. The true end-to-end check (cross-repo `workflow_call`) only runs after the human pushes both repos; the plan proves as much as possible locally first.

---

## File Structure

### `shipsolid/docs-site` (new)

| Path | Responsibility |
| --- | --- |
| `package.json`, `package-lock.json` | Engine deps: `signal-forge/website/package.json` deps verbatim + `js-yaml`. Scripts: `dev`, `build`, `preview`, `test`, `gen-docs`, `check-links`. |
| `.nvmrc` | `24` |
| `.gitignore` | `.astro/`, `dist/`, `node_modules/`, `src/content/docs/` |
| `tsconfig.json` | Copied from `signal-forge/website/tsconfig.json` verbatim |
| `vitest.config.mjs` | Copied verbatim (`include: ['tests/**/*.test.mjs']`) |
| `astro.config.mjs` | Builds the Starlight config from `lib/config.mjs` output + env; mermaid integration; sidebar (autogen or override); `expressiveCode` lang alias `river→hcl` (+ config extension) |
| `lib/config.mjs` | **NEW.** Load + validate `docs-site.yaml`, apply defaults. Pure (path in, object out). |
| `lib/wiki-resolve.mjs` | Wiki-link resolution rules, refactored to a `createWikiResolver(cfg)` factory (was module-level `DOCS_PREFIX`/`NOTES_SITE`/`OLD_PREFIX_RE`/`NOTES_BOOKS` constants) |
| `lib/wiki-index.mjs` | Copied from `signal-forge/website/wiki-index.mjs` verbatim (imports `./doc-path.mjs`) |
| `lib/apply-wiki-links.mjs` | `applyWikiLinks(md, index, resolver)` — takes the resolver as a 3rd arg (was importing constants) |
| `lib/doc-path.mjs` | Copied from `signal-forge/website/doc-path.mjs` verbatim (slug folding) |
| `lib/remark-rewrite-md-links.mjs` | `remarkRewriteMdLinks({ docsPrefix })` — plugin factory taking `docsPrefix` (was importing `DOCS_PREFIX`) |
| `scripts/gen-docs.mjs` | Env-driven roots + config; walk consumer `docs/` → `DOCS_OUT`, import `PROJECT_README`, run the wiki-link pass |
| `scripts/check-links.mjs` | Env-driven roots + config; broken-link audit, non-zero exit on findings |
| `src/content.config.ts` | Copied from `signal-forge/website/src/content.config.ts` verbatim |
| `fixtures/sample-repo/` | Fake consumer: `docs/` tree + `README.md` + `docs-site.yaml`, all links resolvable |
| `tests/wiki-resolve.test.mjs` | Ported: constructs a resolver via `createWikiResolver` instead of importing constants |
| `tests/apply-wiki-links.test.mjs` | Ported: passes a resolver into `applyWikiLinks` |
| `tests/config.test.mjs` | **NEW.** Defaults, `base` derivation, validation errors |
| `tests/fixture-build.test.mjs` | **NEW.** `gen-docs` + `check-links` + `astro build` against `fixtures/sample-repo/`, assert exit 0 + expected routes in `dist/` |
| `.github/workflows/build-deploy.yml` | **NEW.** `on: workflow_call` (input `deploy: boolean = true`). `build` job + gated `deploy` job. |
| `.github/workflows/ci.yml` | **NEW.** `on: pull_request`. `npm ci` + `npm test`. |
| `README.md` | Rewrite from the placeholder: what the engine is, the env contract, how a consumer opts in. |

### `signal-forge` (consumer, branch `docs-site-extraction`)

| Path | Change |
| --- | --- |
| `docs-site.yaml` | **Create.** `title`, `description`, `social.github`, `sidebar:` (transcribed from `website/astro.config.mjs`), `wiki_links:` (transcribed from `website/wiki-resolve.mjs`) |
| `.github/workflows/docs.yml` | **Create.** ~12 lines: `uses: shipsolid/docs-site/.github/workflows/build-deploy.yml@main` |
| `.github/workflows/ci.yml` | **Modify.** Add a `docs` job calling the reusable workflow with `deploy: false` |
| `website/` | **Delete** (`git rm -r`) |
| `.github/workflows/deploy-docs.yml` | **Delete** (`git rm`) |
| `.pre-commit-config.yaml:35-36` | **Modify.** Remove the two `website/…` `exclude` lines |
| `CLAUDE.md` "Docs map" section | **Modify.** Point at `shipsolid/docs-site` + `docs-site.yaml` + `docs.yml` |
| `README.md` | **Verify/modify.** Repo-tree block near line 398; any "preview docs locally" note |

---

## Phase A — build the engine (`/home/amit/repos/shipsolid/docs-site`)

### Task 1: Scaffold the engine repo, move verbatim files

**Files:**
- Create: `docs-site/package.json`, `docs-site/.nvmrc`, `docs-site/.gitignore`, `docs-site/tsconfig.json`, `docs-site/vitest.config.mjs`
- Create (copy verbatim from `signal-forge/website/`): `docs-site/lib/wiki-index.mjs`, `docs-site/lib/doc-path.mjs`, `docs-site/src/content.config.ts`
- Create: `docs-site/package-lock.json` (generated by `npm install`)

**Interfaces:**
- Produces: `lib/doc-path.mjs` → `resolveContentFile(abs)`, `slugFromBackingFile(backingFile, absRoot)`. `lib/wiki-index.mjs` → `buildDocIndex(docsRoot)`, `titleFor(abs)`, `headingsFor(abs)`, `humanizeSlug(slug)`. (Unchanged from current signal-forge copies.)

- [ ] **Step 1: Create the branch**

```bash
cd /home/amit/repos/shipsolid/docs-site
git checkout -b engine-v1
```

- [ ] **Step 2: Write `package.json`**

Copy the `dependencies` / `devDependencies` blocks from `/home/amit/repos/shipsolid/signal-forge/website/package.json` verbatim, add `"js-yaml": "^4.1.0"` to `dependencies`, and use these scripts:

```json
{
  "name": "shipsolid-docs-site",
  "type": "module",
  "version": "0.1.0",
  "private": true,
  "engines": { "node": ">=22.12.0" },
  "scripts": {
    "gen-docs": "node scripts/gen-docs.mjs",
    "check-links": "node scripts/check-links.mjs",
    "predev": "npm run gen-docs",
    "dev": "astro dev",
    "prebuild": "npm run gen-docs && npm run check-links",
    "build": "astro build",
    "preview": "astro preview",
    "test": "vitest run"
  }
}
```

- [ ] **Step 3: Write `.nvmrc`, `.gitignore`, copy `tsconfig.json` + `vitest.config.mjs`**

`.nvmrc`:
```
24
```

`.gitignore`:
```
.astro/
dist/
node_modules/
src/content/docs/
```

`tsconfig.json` and `vitest.config.mjs`: `cp` from `signal-forge/website/` verbatim.

- [ ] **Step 4: Copy the three verbatim source files**

```bash
mkdir -p lib src
cp /home/amit/repos/shipsolid/signal-forge/website/wiki-index.mjs lib/wiki-index.mjs
cp /home/amit/repos/shipsolid/signal-forge/website/doc-path.mjs   lib/doc-path.mjs
cp /home/amit/repos/shipsolid/signal-forge/website/src/content.config.ts src/content.config.ts
```

`lib/wiki-index.mjs` already imports `./doc-path.mjs` — the relative path is still correct inside `lib/`. No edits.

- [ ] **Step 5: Install dependencies**

Run: `npm install`
Expected: `node_modules/` populated, `package-lock.json` written, no peer-dep errors that block (Starlight/Astro may print warnings; a clean exit code is the gate).

- [ ] **Step 6: Commit**

```bash
git add -A
git commit -m "Scaffold docs-site engine; move slug + index helpers verbatim

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>"
```

---

### Task 2: `lib/config.mjs` — load, validate, default

**Files:**
- Create: `docs-site/lib/config.mjs`
- Test: `docs-site/tests/config.test.mjs`

**Interfaces:**
- Consumes: `js-yaml` (`load`), Node `fs`.
- Produces:
  `loadDocsSiteConfig({ configPath, repoSlug }) -> { site, base, title, description, social: { github }, docsDir, projectReadme, sidebar, wikiLinks, codeLangAliases }`
  - `site` — always `'https://shipsolid.github.io'`
  - `base` — `cfg.base` if set, else `/${repoSlug.split('/')[1]}/`; guaranteed leading + trailing `/`
  - `title` — required; throw `Error('docs-site.yaml: "title" is required')` if missing/empty
  - `description` — `cfg.description ?? ''`
  - `social.github` — `cfg.social?.github ?? \`https://github.com/${repoSlug}\``
  - `docsDir` — `cfg.docs_dir ?? 'docs'`
  - `projectReadme` — `cfg.project_readme` if a string; `null` if `false`; `'README.md'` if unset
  - `sidebar` — `cfg.sidebar ?? null` (array passthrough, not validated further)
  - `wikiLinks` — `null` if no `wiki_links` block, else `{ stripPrefixes: [], externalBaseUrl: null, externalNamespaces: [] }` merged with the block (snake_case → camelCase)
  - `codeLangAliases` — `{ river: 'hcl', ...(cfg.code_lang_aliases ?? {}) }`
  - Throw `Error(\`docs-site.yaml not found at ${configPath}\`)` if the file is missing.

- [ ] **Step 1: Write the failing test**

```javascript
// docs-site/tests/config.test.mjs
import { describe, it, expect } from 'vitest';
import { writeFileSync, mkdtempSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { loadDocsSiteConfig } from '../lib/config.mjs';

function writeCfg(yaml) {
  const dir = mkdtempSync(join(tmpdir(), 'docs-site-cfg-'));
  const p = join(dir, 'docs-site.yaml');
  writeFileSync(p, yaml);
  return p;
}

describe('loadDocsSiteConfig', () => {
  it('applies defaults from a minimal file', () => {
    const p = writeCfg('title: SignalForge\n');
    const c = loadDocsSiteConfig({ configPath: p, repoSlug: 'shipsolid/signal-forge' });
    expect(c.site).toBe('https://shipsolid.github.io');
    expect(c.base).toBe('/signal-forge/');
    expect(c.title).toBe('SignalForge');
    expect(c.description).toBe('');
    expect(c.social.github).toBe('https://github.com/shipsolid/signal-forge');
    expect(c.docsDir).toBe('docs');
    expect(c.projectReadme).toBe('README.md');
    expect(c.sidebar).toBeNull();
    expect(c.wikiLinks).toBeNull();
    expect(c.codeLangAliases).toEqual({ river: 'hcl' });
  });

  it('honours an explicit base and normalises slashes', () => {
    const p = writeCfg('title: X\nbase: signal-forge\n');
    const c = loadDocsSiteConfig({ configPath: p, repoSlug: 'shipsolid/signal-forge' });
    expect(c.base).toBe('/signal-forge/');
  });

  it('maps the wiki_links block to camelCase', () => {
    const p = writeCfg([
      'title: X',
      'wiki_links:',
      '  strip_prefixes: ["projects/app-signal-forge/"]',
      '  external_base_url: https://shipsolid.github.io/notes',
      '  external_namespaces: [kubernetes, prometheus]',
    ].join('\n'));
    const c = loadDocsSiteConfig({ configPath: p, repoSlug: 'shipsolid/signal-forge' });
    expect(c.wikiLinks).toEqual({
      stripPrefixes: ['projects/app-signal-forge/'],
      externalBaseUrl: 'https://shipsolid.github.io/notes',
      externalNamespaces: ['kubernetes', 'prometheus'],
    });
  });

  it('treats project_readme: false as disabled', () => {
    const p = writeCfg('title: X\nproject_readme: false\n');
    const c = loadDocsSiteConfig({ configPath: p, repoSlug: 'a/b' });
    expect(c.projectReadme).toBeNull();
  });

  it('throws when title is missing', () => {
    const p = writeCfg('description: no title here\n');
    expect(() => loadDocsSiteConfig({ configPath: p, repoSlug: 'a/b' })).toThrow(/"title" is required/);
  });

  it('throws when the file is missing', () => {
    expect(() => loadDocsSiteConfig({ configPath: '/no/such/docs-site.yaml', repoSlug: 'a/b' }))
      .toThrow(/not found/);
  });
});
```

- [ ] **Step 2: Run test to verify it fails**

Run: `npx vitest run tests/config.test.mjs`
Expected: FAIL — `Cannot find module '../lib/config.mjs'`

- [ ] **Step 3: Write `lib/config.mjs`**

```javascript
import fs from 'node:fs';
import { load } from 'js-yaml';

const SITE = 'https://shipsolid.github.io';

function normaliseBase(v) {
  let b = String(v).trim();
  if (!b.startsWith('/')) b = `/${b}`;
  if (!b.endsWith('/')) b = `${b}/`;
  return b;
}

export function loadDocsSiteConfig({ configPath, repoSlug }) {
  if (!fs.existsSync(configPath)) {
    throw new Error(`docs-site.yaml not found at ${configPath}`);
  }
  const cfg = load(fs.readFileSync(configPath, 'utf8')) ?? {};

  if (typeof cfg.title !== 'string' || cfg.title.trim() === '') {
    throw new Error('docs-site.yaml: "title" is required');
  }

  const repoName = repoSlug.split('/')[1] ?? repoSlug;

  let projectReadme = 'README.md';
  if (cfg.project_readme === false) projectReadme = null;
  else if (typeof cfg.project_readme === 'string') projectReadme = cfg.project_readme;

  let wikiLinks = null;
  if (cfg.wiki_links && typeof cfg.wiki_links === 'object') {
    wikiLinks = {
      stripPrefixes: cfg.wiki_links.strip_prefixes ?? [],
      externalBaseUrl: cfg.wiki_links.external_base_url ?? null,
      externalNamespaces: cfg.wiki_links.external_namespaces ?? [],
    };
  }

  return {
    site: SITE,
    base: cfg.base ? normaliseBase(cfg.base) : `/${repoName}/`,
    title: cfg.title.trim(),
    description: cfg.description ?? '',
    social: { github: cfg.social?.github ?? `https://github.com/${repoSlug}` },
    docsDir: cfg.docs_dir ?? 'docs',
    projectReadme,
    sidebar: cfg.sidebar ?? null,
    wikiLinks,
    codeLangAliases: { river: 'hcl', ...(cfg.code_lang_aliases ?? {}) },
  };
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `npx vitest run tests/config.test.mjs`
Expected: PASS (6 tests)

- [ ] **Step 5: Commit**

```bash
git add lib/config.mjs tests/config.test.mjs
git commit -m "Add docs-site.yaml loader with defaults and validation

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>"
```

---

### Task 3: Refactor `lib/wiki-resolve.mjs` to a factory

**Files:**
- Create: `docs-site/lib/wiki-resolve.mjs` (adapted from `signal-forge/website/wiki-resolve.mjs`)
- Test: `docs-site/tests/wiki-resolve.test.mjs` (ported from the signal-forge copy)

**Interfaces:**
- Consumes: `github-slugger` (`slug`).
- Produces:
  - `export function splitHeading(key) -> { notePart, headingPart }` — pure, unchanged, also available standalone.
  - `export function createWikiResolver({ docsPrefix, externalBaseUrl = null, stripPrefixes = [], externalNamespaces = [] }) -> { docsPrefix, splitHeading, normalizeKey, isExternalNotesKey, externalNotesUrl, resolveInternal }`
    - `docsPrefix` — stored without a trailing slash (`/signal-forge`).
    - `normalizeKey(notePart)` — trims trailing `\`, strips any `stripPrefixes` entry (anchored, case-insensitive), aliases `…/readme` → `…/index`.
    - `isExternalNotesKey(notePart)` — `false` if it matches a strip prefix; else `true` iff normalized key contains `/` and its first segment is in `externalNamespaces` (lower-cased Set).
    - `externalNotesUrl(notePart)` — `\`${externalBaseUrl}/${slugFoldedSegments}/\``; requires `externalBaseUrl` non-null (only reached when `isExternalNotesKey` is true, which needs a non-empty namespace list — pair them in config).
    - `resolveInternal(notePart, index)` — unchanged logic; returns `''` (root), a slug, `null`, or `'ambiguous'`.

- [ ] **Step 1: Port the test to use the factory**

```javascript
// docs-site/tests/wiki-resolve.test.mjs
import { describe, it, expect } from 'vitest';
import { splitHeading, createWikiResolver } from '../lib/wiki-resolve.mjs';

const resolver = createWikiResolver({
  docsPrefix: '/signal-forge',
  externalBaseUrl: 'https://shipsolid.github.io/notes',
  stripPrefixes: ['projects/app-signal-forge/'],
  externalNamespaces: ['networks', 'patterns', 'prometheus', 'kubernetes'],
});
const { normalizeKey, isExternalNotesKey, externalNotesUrl, resolveInternal } = resolver;

const slugs = [
  'architecture/overview',
  'architecture/adrs/adr-log-tailing-not-otlp-export',
  'services/gateway-api',
  'observability/pipeline',
  'deployment/helm',
  'deployment/grafana-cloud',
  'operations/runbooks',
  'guides',
  'spec',
];
const index = {
  byFullSlug: new Map(slugs.map((s) => [s.toLowerCase(), s])),
  byBasename: (() => {
    const m = new Map();
    for (const s of slugs) {
      const b = s.split('/').pop().toLowerCase();
      if (!m.has(b)) m.set(b, []);
      m.get(b).push(s);
    }
    return m;
  })(),
};

describe('splitHeading', () => {
  it('separates a #fragment', () => {
    expect(splitHeading('operations/runbooks#No traces in Jaeger')).toEqual({
      notePart: 'operations/runbooks',
      headingPart: 'No traces in Jaeger',
    });
  });
  it('handles no fragment', () => {
    expect(splitHeading('overview')).toEqual({ notePart: 'overview', headingPart: null });
  });
});

describe('normalizeKey', () => {
  it('drops a configured strip prefix', () => {
    expect(normalizeKey('projects/app-signal-forge/deployment/helm')).toBe('deployment/helm');
  });
  it('aliases a trailing readme to index', () => {
    expect(normalizeKey('projects/app-signal-forge/guides/readme')).toBe('guides/index');
  });
  it('strips a table-escape backslash', () => {
    expect(normalizeKey('tech/jaeger\\')).toBe('tech/jaeger');
  });
});

describe('external notes links', () => {
  it('flags <namespace>/<subpath> keys', () => {
    expect(isExternalNotesKey('networks/05-http-ecosystem/05-grpc/05-grpc')).toBe(true);
    expect(isExternalNotesKey('prometheus/readme')).toBe(true);
    expect(isExternalNotesKey('kubernetes/readme')).toBe(true);
  });
  it('does NOT flag a bare namespace name', () => {
    expect(isExternalNotesKey('kubernetes')).toBe(false);
  });
  it('does NOT flag an unknown top segment', () => {
    expect(isExternalNotesKey('tech/jaeger')).toBe(false);
  });
  it('does NOT flag a strip-prefixed key', () => {
    expect(isExternalNotesKey('projects/app-signal-forge/observability/pipeline')).toBe(false);
  });
  it('builds an absolute URL, folding readme to the namespace root', () => {
    expect(externalNotesUrl('networks/05-http-ecosystem/05-grpc/05-grpc')).toBe(
      'https://shipsolid.github.io/notes/networks/05-http-ecosystem/05-grpc/05-grpc/',
    );
    expect(externalNotesUrl('prometheus/readme')).toBe('https://shipsolid.github.io/notes/prometheus/');
  });
});

describe('resolveInternal', () => {
  it('resolves a bare basename', () => {
    expect(resolveInternal('pipeline', index)).toBe('observability/pipeline');
  });
  it('resolves a full slug after prefix strip', () => {
    expect(resolveInternal('projects/app-signal-forge/deployment/helm', index)).toBe('deployment/helm');
  });
  it('resolves a prefixed folder-readme to the folder index', () => {
    expect(resolveInternal('projects/app-signal-forge/guides/readme', index)).toBe('guides');
  });
  it('returns null for an unknown key', () => {
    expect(resolveInternal('does-not-exist', index)).toBeNull();
  });
  it('resolves the root index to the root slug', () => {
    expect(resolveInternal('projects/app-signal-forge/readme', index)).toBe('');
    expect(resolveInternal('index', index)).toBe('');
  });
  it('resolves an ADR basename', () => {
    expect(resolveInternal('adr-log-tailing-not-otlp-export', index)).toBe(
      'architecture/adrs/adr-log-tailing-not-otlp-export',
    );
  });

  it('with no wiki_links config, nothing is external', () => {
    const bare = createWikiResolver({ docsPrefix: '/x' });
    expect(bare.isExternalNotesKey('kubernetes/readme')).toBe(false);
    expect(bare.normalizeKey('a/readme')).toBe('a/index');
  });
});
```

- [ ] **Step 2: Run test to verify it fails**

Run: `npx vitest run tests/wiki-resolve.test.mjs`
Expected: FAIL — `Cannot find module '../lib/wiki-resolve.mjs'`

- [ ] **Step 3: Write `lib/wiki-resolve.mjs`**

Start from `/home/amit/repos/shipsolid/signal-forge/website/wiki-resolve.mjs`. Apply exactly these changes:

1. Remove `export const DOCS_PREFIX`, `export const NOTES_SITE`, `const OLD_PREFIX_RE`, `const NOTES_BOOKS`.
2. Keep `splitHeading` as a top-level `export function` (it uses no config).
3. Wrap `normalizeKey`, `isExternalNotesKey`, `externalNotesUrl`, `resolveInternal` inside `createWikiResolver`, closing over the config:

```javascript
import { slug as githubSlug } from 'github-slugger';

export function splitHeading(key) {
  const hash = key.indexOf('#');
  if (hash === -1) return { notePart: key.trim(), headingPart: null };
  return { notePart: key.slice(0, hash).trim(), headingPart: key.slice(hash + 1).trim() };
}

const escapeRe = (s) => s.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');

export function createWikiResolver({
  docsPrefix,
  externalBaseUrl = null,
  stripPrefixes = [],
  externalNamespaces = [],
}) {
  const prefix = docsPrefix.replace(/\/$/, '');
  const oldPrefixRe = stripPrefixes.length
    ? new RegExp(`^(?:${stripPrefixes.map(escapeRe).join('|')})`, 'i')
    : null;
  const namespaces = new Set(externalNamespaces.map((s) => s.toLowerCase()));

  function normalizeKey(notePart) {
    let k = notePart.replace(/\\$/, '').trim();
    if (oldPrefixRe) k = k.replace(oldPrefixRe, '');
    return k.replace(/(^|\/)readme$/i, '$1index');
  }

  function isExternalNotesKey(notePart) {
    const trimmed = notePart.replace(/\\$/, '').trim();
    if (oldPrefixRe && oldPrefixRe.test(trimmed)) return false;
    const k = normalizeKey(notePart);
    if (!k.includes('/')) return false;
    return namespaces.has(k.split('/')[0].toLowerCase());
  }

  function externalNotesUrl(notePart) {
    const segments = normalizeKey(notePart)
      .replace(/\/index$/, '')
      .split('/')
      .filter(Boolean)
      .map((s) => githubSlug(s));
    return `${externalBaseUrl}/${segments.join('/')}/`;
  }

  function resolveInternal(notePart, index) {
    const key = normalizeKey(notePart).toLowerCase();
    if (!key || key === 'index') return '';
    const exact = index.byFullSlug.get(key);
    if (exact !== undefined) return exact;
    let basename = key.split('/').pop();
    if (key.endsWith('/index')) {
      const folder = key.slice(0, -'/index'.length);
      const folderHit = index.byFullSlug.get(folder);
      if (folderHit !== undefined) return folderHit;
      basename = folder.split('/').pop();
    }
    const candidates = index.byBasename.get(basename) ?? [];
    if (candidates.length === 1) return candidates[0];
    if (candidates.length > 1) return 'ambiguous';
    return null;
  }

  return { docsPrefix: prefix, splitHeading, normalizeKey, isExternalNotesKey, externalNotesUrl, resolveInternal };
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `npx vitest run tests/wiki-resolve.test.mjs`
Expected: PASS

- [ ] **Step 5: Commit**

```bash
git add lib/wiki-resolve.mjs tests/wiki-resolve.test.mjs
git commit -m "Refactor wiki-resolve into a config-driven factory

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>"
```

---

### Task 4: Port `lib/apply-wiki-links.mjs` and `lib/remark-rewrite-md-links.mjs` to take config

**Files:**
- Create: `docs-site/lib/apply-wiki-links.mjs`, `docs-site/lib/remark-rewrite-md-links.mjs`
- Test: `docs-site/tests/apply-wiki-links.test.mjs` (ported)

**Interfaces:**
- Consumes: `createWikiResolver` from `lib/wiki-resolve.mjs`; `headingsFor`, `titleFor`, `humanizeSlug` from `lib/wiki-index.mjs`; `resolveContentFile`, `slugFromBackingFile` from `lib/doc-path.mjs`; `unist-util-visit`.
- Produces:
  - `applyWikiLinks(md, index, resolver) -> { md, unresolved: string[] }` — `resolver` is a `createWikiResolver(...)` result; uses `resolver.docsPrefix` and the resolver's methods (no more top-level `DOCS_PREFIX` import).
  - `remarkRewriteMdLinks({ docsPrefix }) -> (tree, file) => void` — plugin factory; `docsPrefix` has no trailing slash.

- [ ] **Step 1: Port the test**

```javascript
// docs-site/tests/apply-wiki-links.test.mjs
import { describe, it, expect } from 'vitest';
import { applyWikiLinks } from '../lib/apply-wiki-links.mjs';
import { createWikiResolver } from '../lib/wiki-resolve.mjs';

const resolver = createWikiResolver({
  docsPrefix: '/signal-forge',
  externalBaseUrl: 'https://shipsolid.github.io/notes',
  stripPrefixes: ['projects/app-signal-forge/'],
  externalNamespaces: ['networks'],
});

const slugs = ['observability/pipeline', 'architecture/adrs/adr-spanlink-for-async-rabbitmq', 'api/grpc'];
const index = {
  byFullSlug: new Map(slugs.map((s) => [s.toLowerCase(), s])),
  byBasename: (() => {
    const m = new Map();
    for (const s of slugs) m.set(s.split('/').pop().toLowerCase(), [s]);
    return m;
  })(),
  absOf: () => '/does/not/exist.md',
};

const run = (md) => applyWikiLinks(md, index, resolver).md;

describe('applyWikiLinks', () => {
  it('keeps bold/code formatting inside the display text', () => {
    expect(run('see [[architecture/adrs/adr-spanlink-for-async-rabbitmq|**SpanLink**]] now')).toBe(
      'see [**SpanLink**](/signal-forge/architecture/adrs/adr-spanlink-for-async-rabbitmq/) now',
    );
    expect(run('[[api/grpc|`GetOrdersByProject`]] streams')).toBe(
      '[`GetOrdersByProject`](/signal-forge/api/grpc/) streams',
    );
  });
  it('resolves a bare basename and the strip prefix alike', () => {
    expect(run('[[pipeline]]')).toBe('[Pipeline](/signal-forge/observability/pipeline/)');
    expect(run('[[projects/app-signal-forge/observability/pipeline|the pipeline]]')).toBe(
      '[the pipeline](/signal-forge/observability/pipeline/)',
    );
  });
  it('routes a known namespace to an absolute URL', () => {
    expect(run('[[networks/05-http-ecosystem/05-grpc/05-grpc|gRPC]] over HTTP/2')).toBe(
      '[gRPC](https://shipsolid.github.io/notes/networks/05-http-ecosystem/05-grpc/05-grpc/) over HTTP/2',
    );
  });
  it('unwraps an unresolved link and reports it', () => {
    const { md, unresolved } = applyWikiLinks('use [[tech/jaeger|Jaeger]] here', index, resolver);
    expect(md).toBe('use Jaeger here');
    expect(unresolved).toEqual(['tech/jaeger']);
  });
  it('leaves [[ ]] inside fenced code alone', () => {
    const src = ['```bash', 'if [[ -d "$x" && -f "$y" ]]; then :; fi', '```'].join('\n');
    expect(run(src)).toBe(src);
  });
  it('leaves [[ ]] inside inline code alone', () => {
    expect(run('the `[[ x ]]` test operator')).toBe('the `[[ x ]]` test operator');
  });
});
```

- [ ] **Step 2: Run test to verify it fails**

Run: `npx vitest run tests/apply-wiki-links.test.mjs`
Expected: FAIL — module not found.

- [ ] **Step 3: Write `lib/apply-wiki-links.mjs`**

Start from `signal-forge/website/apply-wiki-links.mjs`. Changes:
1. Delete the `import { DOCS_PREFIX, splitHeading, isExternalNotesKey, externalNotesUrl, resolveInternal } from './wiki-resolve.mjs';` line.
2. `applyWikiLinks(md, index)` → `applyWikiLinks(md, index, resolver)`.
3. `linkFor(rawKey, rawDisplay, index)` → `linkFor(rawKey, rawDisplay, index, resolver)`, and inside it use `resolver.splitHeading`, `resolver.resolveInternal`, `resolver.isExternalNotesKey`, `resolver.externalNotesUrl`, and `resolver.docsPrefix` in place of the bare names / `DOCS_PREFIX`.
4. Pass `resolver` through from `applyWikiLinks`'s `.replace()` callback into `linkFor`.

Everything else (fence tracking, backtick-parity guard, `headingsFor`/`titleFor`/`humanizeSlug` usage) is unchanged.

- [ ] **Step 4: Write `lib/remark-rewrite-md-links.mjs`**

Start from `signal-forge/website/remark-rewrite-md-links.mjs`. Changes:
1. Delete `import { DOCS_PREFIX } from './wiki-resolve.mjs';`.
2. `export function remarkRewriteMdLinks()` → `export function remarkRewriteMdLinks({ docsPrefix })`.
3. In the node rewrite, `${DOCS_PREFIX}/${slug}/` → `${docsPrefix}/${slug}/`.
4. Keep `const DOCS_ROOT = 'src/content/docs';` and the `absRoot` logic as-is.

- [ ] **Step 5: Run test to verify it passes**

Run: `npx vitest run tests/apply-wiki-links.test.mjs`
Expected: PASS

- [ ] **Step 6: Commit**

```bash
git add lib/apply-wiki-links.mjs lib/remark-rewrite-md-links.mjs tests/apply-wiki-links.test.mjs
git commit -m "Thread resolver + docsPrefix into wiki-link rewriters

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>"
```

---

### Task 5: `scripts/gen-docs.mjs` — env-driven roots + config

**Files:**
- Create: `docs-site/scripts/gen-docs.mjs`

**Interfaces:**
- Consumes: env `DOCS_SRC`, `PROJECT_README`, `DOCS_SITE_CONFIG`, `REPO_SLUG`, `DOCS_OUT` (optional); `loadDocsSiteConfig`; `createWikiResolver`; `buildDocIndex`; `applyWikiLinks`.
- Produces: writes markdown into `DOCS_OUT` (default `<repo>/src/content/docs`). Side-effect script; exit 0 on success, prints an `unresolved` summary (non-fatal — `check-links` is the gate). Exit 1 with a clear message if `DOCS_SRC` is unset or missing.
- Defaults for local runs: if `DOCS_SITE_CONFIG` unset → `fixtures/sample-repo/docs-site.yaml`; if `DOCS_SRC` unset → `fixtures/sample-repo/docs`; if `PROJECT_README` unset → `fixtures/sample-repo/README.md`; if `REPO_SLUG` unset → `shipsolid/sample-repo`.

- [ ] **Step 1: Write `scripts/gen-docs.mjs`**

Adapt from `signal-forge/website/scripts/gen-docs.mjs`. Replace the constants block:

```javascript
#!/usr/bin/env node
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { loadDocsSiteConfig } from '../lib/config.mjs';
import { createWikiResolver } from '../lib/wiki-resolve.mjs';
import { buildDocIndex } from '../lib/wiki-index.mjs';
import { applyWikiLinks } from '../lib/apply-wiki-links.mjs';

const __dirname = path.dirname(fileURLToPath(import.meta.url));
const ENGINE = path.resolve(__dirname, '..');
const FIX = path.join(ENGINE, 'fixtures', 'sample-repo');

const CONFIG_PATH = process.env.DOCS_SITE_CONFIG || path.join(FIX, 'docs-site.yaml');
const REPO_SLUG = process.env.REPO_SLUG || 'shipsolid/sample-repo';
const cfg = loadDocsSiteConfig({ configPath: CONFIG_PATH, repoSlug: REPO_SLUG });

const SRC = process.env.DOCS_SRC || path.join(FIX, cfg.docsDir);
const README_SRC = process.env.PROJECT_README || (cfg.projectReadme ? path.join(FIX, cfg.projectReadme) : '');
const OUT = process.env.DOCS_OUT || path.join(ENGINE, 'src', 'content', 'docs');
const REPO_URL = cfg.social.github;

if (!fs.existsSync(SRC)) {
  console.error(`gen-docs: source docs tree not found at ${SRC}`);
  process.exit(1);
}

fs.rmSync(OUT, { recursive: true, force: true });
fs.mkdirSync(OUT, { recursive: true });
```

Keep the directory `walk` + `README.md → index.md` rename exactly as in the source. Then the project-README import block, with two changes vs the source:
- guard on `README_SRC` being non-empty **and** existing (`cfg.projectReadme` may be `null`);
- read from `README_SRC` (not `path.join(REPO, 'README.md')`), and keep the existing repo-relative-link → `${REPO_URL}/blob/main/…` rewrite and the injected frontmatter verbatim.

Then the wiki-link second pass, replacing the bare `buildDocIndex`/`applyWikiLinks` wiring with a resolver:

```javascript
const resolver = createWikiResolver({
  docsPrefix: cfg.base.replace(/\/$/, ''),
  externalBaseUrl: cfg.wikiLinks?.externalBaseUrl ?? null,
  stripPrefixes: cfg.wikiLinks?.stripPrefixes ?? [],
  externalNamespaces: cfg.wikiLinks?.externalNamespaces ?? [],
});
const index = buildDocIndex(OUT);
const unresolved = [];
for (const file of written) {
  const { md, unresolved: u } = applyWikiLinks(fs.readFileSync(file, 'utf8'), index, resolver);
  if (u.length) unresolved.push(`  ${path.relative(OUT, file)}: ${u.join(', ')}`);
  fs.writeFileSync(file, md);
}
console.log(`gen-docs: wrote ${written.length} files to ${path.relative(ENGINE, OUT)}`);
if (unresolved.length) {
  console.warn(`gen-docs: ${unresolved.length} file(s) with unresolved wiki-links (see check-links):`);
  console.warn(unresolved.join('\n'));
}
```

- [ ] **Step 2: Smoke-run against the (not-yet-existing) fixture**

This task has no unit test; it is exercised by `tests/fixture-build.test.mjs` in Task 7. For now just verify it parses:

Run: `node --check scripts/gen-docs.mjs`
Expected: no output, exit 0.

- [ ] **Step 3: Commit**

```bash
git add scripts/gen-docs.mjs
git commit -m "Port gen-docs to env-driven roots + docs-site.yaml

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>"
```

---

### Task 6: `scripts/check-links.mjs` — env-driven roots + config

**Files:**
- Create: `docs-site/scripts/check-links.mjs`

**Interfaces:**
- Consumes: same env as `gen-docs.mjs`; `loadDocsSiteConfig`; `createWikiResolver`; `buildDocIndex`; `splitHeading` (standalone export).
- Produces: prints 4 sections (unresolved / ambiguous / leaked `[[ ]]` / broken relative), exits `1` if any total > 0, else `0`.

- [ ] **Step 1: Write `scripts/check-links.mjs`**

Adapt from `signal-forge/website/scripts/check-links.mjs`. Replace the constants + imports:

```javascript
#!/usr/bin/env node
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { loadDocsSiteConfig } from '../lib/config.mjs';
import { createWikiResolver, splitHeading } from '../lib/wiki-resolve.mjs';
import { buildDocIndex } from '../lib/wiki-index.mjs';

const __dirname = path.dirname(fileURLToPath(import.meta.url));
const ENGINE = path.resolve(__dirname, '..');
const FIX = path.join(ENGINE, 'fixtures', 'sample-repo');

const cfg = loadDocsSiteConfig({
  configPath: process.env.DOCS_SITE_CONFIG || path.join(FIX, 'docs-site.yaml'),
  repoSlug: process.env.REPO_SLUG || 'shipsolid/sample-repo',
});
const SRC = process.env.DOCS_SRC || path.join(FIX, cfg.docsDir);
const OUT = process.env.DOCS_OUT || path.join(ENGINE, 'src', 'content', 'docs');
const resolver = createWikiResolver({
  docsPrefix: cfg.base.replace(/\/$/, ''),
  externalBaseUrl: cfg.wikiLinks?.externalBaseUrl ?? null,
  stripPrefixes: cfg.wikiLinks?.stripPrefixes ?? [],
  externalNamespaces: cfg.wikiLinks?.externalNamespaces ?? [],
});
const { isExternalNotesKey, resolveInternal } = resolver;
```

Keep `mdFiles`, `stripCode`, `lineOf`, the `WIKI_LINK_RE` / `MD_LINK_RE` constants, and the three check blocks verbatim, except: `splitHeading` now comes from the import (already handled), and `resolveInternal(notePart, index)` / `isExternalNotesKey(notePart)` are the resolver's methods (already destructured). Keep the `if (!fs.existsSync(OUT))` guard and the `process.exit(total > 0 ? 1 : 0)` tail.

- [ ] **Step 2: Verify it parses**

Run: `node --check scripts/check-links.mjs`
Expected: exit 0.

- [ ] **Step 3: Commit**

```bash
git add scripts/check-links.mjs
git commit -m "Port check-links to env-driven roots + docs-site.yaml

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>"
```

---

### Task 7: `astro.config.mjs` + fixture repo + end-to-end build test

**Files:**
- Create: `docs-site/astro.config.mjs`
- Create: `docs-site/fixtures/sample-repo/docs-site.yaml`, `docs-site/fixtures/sample-repo/README.md`, and a `docs-site/fixtures/sample-repo/docs/` tree
- Create: `docs-site/tests/fixture-build.test.mjs`

**Interfaces:**
- Consumes: `loadDocsSiteConfig`, `remarkRewriteMdLinks`, `astro-mermaid`, `@astrojs/starlight`.
- Produces: a default-exported Astro config. A `buildSidebar(docsDir, override, absDocsSrc)` helper: returns `override` unchanged when non-null; else `[{ label: 'Overview', link: '/' }, ...one autogenerate group per immediate subdir of absDocsSrc..., ...(loose top-level *.md ? [{ label: 'Reference', items: [<slugs>] }] : [])]`. Directory label = name with a leading `NN-` stripped, `-`/`_` → space, Title Cased. Ordering: numeric prefix first (ascending), then the rest alphabetically.

- [ ] **Step 1: Write `astro.config.mjs`**

```javascript
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { defineConfig } from 'astro/config';
import starlight from '@astrojs/starlight';
import mermaid from 'astro-mermaid';
import { loadDocsSiteConfig } from './lib/config.mjs';
import { remarkRewriteMdLinks } from './lib/remark-rewrite-md-links.mjs';

const __dirname = path.dirname(fileURLToPath(import.meta.url));
const ENGINE = __dirname;
const FIX = path.join(ENGINE, 'fixtures', 'sample-repo');

const cfg = loadDocsSiteConfig({
  configPath: process.env.DOCS_SITE_CONFIG || path.join(FIX, 'docs-site.yaml'),
  repoSlug: process.env.REPO_SLUG || 'shipsolid/sample-repo',
});
const DOCS_SRC = process.env.DOCS_SRC || path.join(FIX, cfg.docsDir);

const titleCase = (name) =>
  name.replace(/^\d+[-_]/, '').replace(/[-_]+/g, ' ').replace(/\b\w/g, (c) => c.toUpperCase());

function buildSidebar(absDocsSrc, override) {
  if (override) return override;
  const entries = fs.existsSync(absDocsSrc)
    ? fs.readdirSync(absDocsSrc, { withFileTypes: true })
    : [];
  const dirs = entries
    .filter((e) => e.isDirectory())
    .map((e) => e.name)
    .sort((a, b) => {
      const na = a.match(/^(\d+)[-_]/), nb = b.match(/^(\d+)[-_]/);
      if (na && nb) return Number(na[1]) - Number(nb[1]);
      if (na) return -1;
      if (nb) return 1;
      return a.localeCompare(b);
    });
  const looseMd = entries.some(
    (e) => e.isFile() && /\.mdx?$/.test(e.name) && !/^readme\.mdx?$/i.test(e.name),
  );
  return [
    { label: 'Overview', link: '/' },
    ...dirs.map((d) => ({ label: titleCase(d), autogenerate: { directory: d } })),
    ...(looseMd
      ? [
          {
            label: 'Reference',
            items: entries
              .filter((e) => e.isFile() && /\.mdx?$/.test(e.name) && !/^readme\.mdx?$/i.test(e.name))
              .map((e) => e.name.replace(/\.mdx?$/, '')),
          },
        ]
      : []),
  ];
}

export default defineConfig({
  site: cfg.site,
  base: cfg.base,
  trailingSlash: 'always',
  integrations: [
    mermaid({ theme: 'default', autoTheme: true }),
    starlight({
      title: cfg.title,
      description: cfg.description,
      social: [{ icon: 'github', label: 'GitHub', href: cfg.social.github }],
      pagefind: true,
      expressiveCode: { shiki: { langAlias: cfg.codeLangAliases } },
      sidebar: buildSidebar(DOCS_SRC, cfg.sidebar),
    }),
  ],
  markdown: {
    remarkPlugins: [remarkRewriteMdLinks({ docsPrefix: cfg.base.replace(/\/$/, '') })],
  },
});
```

- [ ] **Step 2: Build the fixture consumer**

`fixtures/sample-repo/docs-site.yaml`:
```yaml
title: Sample Repo
description: Fixture consumer exercising the docs-site engine contract.
sidebar:
  - { label: Overview, link: / }
  - { label: Architecture, autogenerate: { directory: architecture } }
  - { label: Guides, autogenerate: { directory: guides } }
  - { label: Reference, items: [spec, project-readme] }
wiki_links:
  strip_prefixes: ["projects/sample/"]
  external_base_url: https://shipsolid.github.io/notes
  external_namespaces: [kubernetes]
```

`fixtures/sample-repo/README.md`:
```markdown
# Sample Repo

A fixture. See [the spec](docs/spec.md) and [an ADR](docs/architecture/adrs/adr-example.md).
```

`fixtures/sample-repo/docs/` tree (every link resolvable so `check-links` exits 0):

- `docs/README.md` — frontmatter `title: Overview`; body links `[[spec]]` and `[[guides/getting-started]]`.
- `docs/spec.md` — `title: Spec`; a `[[architecture/overview]]` link and a `[external k8s]([[kubernetes/readme]])`-style `[[kubernetes/readme]]` link.
- `docs/architecture/README.md` — `title: Architecture`.
- `docs/architecture/overview.md` — `title: Overview`; a relative link `[ADR](adrs/adr-example.md)`.
- `docs/architecture/adrs/README.md` — `title: ADRs`.
- `docs/architecture/adrs/adr-example.md` — `title: 'ADR: Example'`.
- `docs/guides/README.md` — `title: Guides`.
- `docs/guides/getting-started.md` — `title: Getting Started`; a `[[spec]]` back-link and a `../architecture/overview.md` relative link.

Keep every markdown file's frontmatter minimal (`title` only) so it satisfies `docsSchema`.

- [ ] **Step 3: Write the failing end-to-end test**

```javascript
// docs-site/tests/fixture-build.test.mjs
import { describe, it, expect } from 'vitest';
import { execFileSync } from 'node:child_process';
import { existsSync, rmSync } from 'node:fs';
import { join } from 'node:path';

const ENGINE = join(import.meta.dirname, '..');
const env = {
  ...process.env,
  DOCS_SITE_CONFIG: join(ENGINE, 'fixtures/sample-repo/docs-site.yaml'),
  DOCS_SRC: join(ENGINE, 'fixtures/sample-repo/docs'),
  PROJECT_README: join(ENGINE, 'fixtures/sample-repo/README.md'),
  REPO_SLUG: 'shipsolid/sample-repo',
};
const run = (cmd, args) => execFileSync(cmd, args, { cwd: ENGINE, env, stdio: 'pipe' });

describe('fixture build (engine contract)', () => {
  it('gen-docs + check-links + astro build all succeed', () => {
    rmSync(join(ENGINE, 'dist'), { recursive: true, force: true });
    expect(() => run('node', ['scripts/gen-docs.mjs'])).not.toThrow();
    expect(() => run('node', ['scripts/check-links.mjs'])).not.toThrow();
    expect(() => run('npx', ['astro', 'build'])).not.toThrow();
    expect(existsSync(join(ENGINE, 'dist/sample-repo/index.html'))).toBe(true);
    expect(existsSync(join(ENGINE, 'dist/sample-repo/spec/index.html'))).toBe(true);
    expect(existsSync(join(ENGINE, 'dist/sample-repo/project-readme/index.html'))).toBe(true);
  }, 120_000);
});
```

- [ ] **Step 4: Run it, fix the fixture until green**

Run: `npx vitest run tests/fixture-build.test.mjs`
Expected after iteration: PASS. Common fixes: a `[[link]]` in the fixture that doesn't resolve (adjust the fixture text, not the engine), a missing `title` in frontmatter, a relative link with the wrong depth.

- [ ] **Step 5: Run the whole suite**

Run: `npm test`
Expected: all four test files pass (`config`, `wiki-resolve`, `apply-wiki-links`, `fixture-build`).

- [ ] **Step 6: Commit**

```bash
git add astro.config.mjs fixtures/ tests/fixture-build.test.mjs
git commit -m "Add Starlight config factory, fixture consumer, end-to-end build test

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>"
```

---

### Task 8: Reusable pipeline + engine self-test workflow + README

**Files:**
- Create: `docs-site/.github/workflows/build-deploy.yml`
- Create: `docs-site/.github/workflows/ci.yml`
- Modify: `docs-site/README.md`

**Interfaces:**
- Produces: a `workflow_call` workflow at `shipsolid/docs-site/.github/workflows/build-deploy.yml` with input `deploy` (boolean, default `true`), consumed by `signal-forge/.github/workflows/docs.yml` (Task 10).

- [ ] **Step 1: Write `.github/workflows/build-deploy.yml`**

```yaml
name: Build & deploy docs

on:
  workflow_call:
    inputs:
      deploy:
        description: Publish to the caller repo's GitHub Pages
        type: boolean
        default: true

jobs:
  build:
    runs-on: ubuntu-latest
    steps:
      - name: Checkout consumer repo
        uses: actions/checkout@v4

      - name: Checkout docs-site engine
        uses: actions/checkout@v4
        with:
          repository: shipsolid/docs-site
          ref: main
          path: .docs-engine

      - name: Setup Node.js
        uses: actions/setup-node@v4
        with:
          node-version-file: .docs-engine/.nvmrc
          cache: npm
          cache-dependency-path: .docs-engine/package-lock.json

      - name: Install engine deps
        working-directory: .docs-engine
        run: npm ci

      - name: Generate content
        working-directory: .docs-engine
        env:
          DOCS_SRC: ${{ github.workspace }}/docs
          PROJECT_README: ${{ github.workspace }}/README.md
          DOCS_SITE_CONFIG: ${{ github.workspace }}/docs-site.yaml
          REPO_SLUG: ${{ github.repository }}
        run: node scripts/gen-docs.mjs

      - name: Check links
        working-directory: .docs-engine
        env:
          DOCS_SRC: ${{ github.workspace }}/docs
          DOCS_SITE_CONFIG: ${{ github.workspace }}/docs-site.yaml
          REPO_SLUG: ${{ github.repository }}
        run: node scripts/check-links.mjs

      - name: Build
        working-directory: .docs-engine
        env:
          DOCS_SRC: ${{ github.workspace }}/docs
          DOCS_SITE_CONFIG: ${{ github.workspace }}/docs-site.yaml
          REPO_SLUG: ${{ github.repository }}
        run: npx astro build

      - name: Upload Pages artifact
        if: ${{ inputs.deploy }}
        uses: actions/upload-pages-artifact@v3
        with:
          path: .docs-engine/dist

  deploy:
    needs: build
    if: ${{ inputs.deploy }}
    runs-on: ubuntu-latest
    permissions:
      pages: write
      id-token: write
    environment:
      name: github-pages
      url: ${{ steps.deployment.outputs.page_url }}
    steps:
      - id: deployment
        uses: actions/deploy-pages@v4
```

- [ ] **Step 2: Write `.github/workflows/ci.yml`**

```yaml
name: CI

on:
  pull_request:
  workflow_dispatch:

jobs:
  test:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v4
      - uses: actions/setup-node@v4
        with:
          node-version-file: .nvmrc
          cache: npm
      - run: npm ci
      - run: npm test
```

- [ ] **Step 3: Lint both workflows**

Run: `npx --yes actionlint@1.7.7 .github/workflows/build-deploy.yml .github/workflows/ci.yml` (or `actionlint` if already on PATH)
Expected: no errors. If `actionlint` is unavailable, fall back to `python -c "import yaml,sys; [yaml.safe_load(open(f)) for f in sys.argv[1:]]" .github/workflows/*.yml` for a syntax check and note that CI will do the real validation.

- [ ] **Step 4: Rewrite `README.md`**

Replace the placeholder content with: what the engine is; the env contract table (`DOCS_SRC`, `PROJECT_README`, `DOCS_SITE_CONFIG`, `REPO_SLUG`, `DOCS_OUT`); a "consuming this" section showing the ~12-line caller (`uses: shipsolid/docs-site/.github/workflows/build-deploy.yml@main`) and a minimal `docs-site.yaml`; a "local engine dev" note (`npm run dev` builds the fixture consumer); and a "contract" note that `tests/fixture-build.test.mjs` gates every change because consumers track `@main`.

- [ ] **Step 5: Commit**

```bash
git add .github/workflows/ README.md
git commit -m "Add reusable build-deploy workflow, engine CI, and README

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>"
```

---

## Phase B — wire up `signal-forge` (`/home/amit/repos/shipsolid/signal-forge`, branch `docs-site-extraction`)

### Task 9: Add `docs-site.yaml`

**Files:**
- Create: `signal-forge/docs-site.yaml`

**Interfaces:**
- Consumes: the schema in `lib/config.mjs` (Task 2).
- Produces: the consumer config the pipeline reads.

- [ ] **Step 1: Transcribe from the current engine config**

Build `docs-site.yaml` from the current `signal-forge/website/astro.config.mjs` (`title`, `description`, `social`, `sidebar`) and `signal-forge/website/wiki-resolve.mjs` (`DOCS_PREFIX` → leave `base` unset so it derives to `/signal-forge/`; `NOTES_SITE` → `external_base_url`; `OLD_PREFIX_RE` → `strip_prefixes`; `NOTES_BOOKS` → `external_namespaces`, **the full list**):

```yaml
title: SignalForge
description: >-
  OpenTelemetry microservices validation lab — architecture, services,
  observability pipeline, deployment, and operations.

social:
  github: https://github.com/shipsolid/signal-forge

sidebar:
  - { label: Overview, link: / }
  - { label: Architecture,   items: [{ autogenerate: { directory: architecture } }] }
  - { label: Services,       items: [{ autogenerate: { directory: services } }] }
  - { label: Observability,  items: [{ autogenerate: { directory: observability } }] }
  - { label: Infrastructure, items: [{ autogenerate: { directory: infrastructure } }] }
  - { label: Deployment,     items: [{ autogenerate: { directory: deployment } }] }
  - { label: Operations,     items: [{ autogenerate: { directory: operations } }] }
  - { label: API,            items: [{ autogenerate: { directory: api } }] }
  - { label: Guides,         items: [{ autogenerate: { directory: guides } }] }
  - label: Reference
    items: [spec, otel-patterns, testing, project-readme]

wiki_links:
  strip_prefixes: ["projects/app-signal-forge/"]
  external_base_url: https://shipsolid.github.io/notes
  external_namespaces:
    - agentic-ai-engineering
    - agentic-ai-projects-and-mastery
    - ai-architecture-and-system-design
    - ai-foundations
    - aptitude
    - building-agentic-systems
    - ci-cd
    - data-engineering
    - data-structures-algorithms
    - dbms
    - grafana-cloud
    - infrastructure-platform-engineering
    - internal-developer-platforms
    - kubernetes
    - kubernetes-platform-engineering
    - low-level-design
    - networks
    - object-oriented-programming
    - observability
    - operating-system
    - patterns
    - philosophy
    - platform-engineering-fundamentals
    - production-agent-systems
    - productivity
    - projects
    - prometheus
    - sre
    - system-design
```

> The `items: [{ autogenerate: ... }]` shape matches the current `astro.config.mjs` exactly — keep it so the rendered sidebar is identical. `buildSidebar` passes `cfg.sidebar` straight through.

- [ ] **Step 2: Validate against the schema locally**

From the engine repo (`/home/amit/repos/shipsolid/docs-site`, branch `engine-v1`):

```bash
DOCS_SITE_CONFIG=/home/amit/repos/shipsolid/signal-forge/docs-site.yaml \
DOCS_SRC=/home/amit/repos/shipsolid/signal-forge/docs \
PROJECT_README=/home/amit/repos/shipsolid/signal-forge/README.md \
REPO_SLUG=shipsolid/signal-forge \
node scripts/gen-docs.mjs
```

Expected: `gen-docs: wrote N files …`. Note any unresolved wiki-links (compare to the current `website` build's known set — should match).

- [ ] **Step 3: Commit (in signal-forge)**

```bash
cd /home/amit/repos/shipsolid/signal-forge
git add docs-site.yaml
git commit -m "Add docs-site.yaml — config for the centralized docs pipeline

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>"
```

---

### Task 10: Caller workflow + PR docs check

**Files:**
- Create: `signal-forge/.github/workflows/docs.yml`
- Modify: `signal-forge/.github/workflows/ci.yml`

**Interfaces:**
- Consumes: `shipsolid/docs-site/.github/workflows/build-deploy.yml@main` (Task 8).

- [ ] **Step 1: Write `.github/workflows/docs.yml`**

```yaml
name: Docs

on:
  push:
    branches: [main]
    paths:
      - "docs/**"
      - "README.md"
      - "docs-site.yaml"
      - ".github/workflows/docs.yml"
  workflow_dispatch:

concurrency:
  group: pages
  cancel-in-progress: false

permissions:
  contents: read
  pages: write
  id-token: write

jobs:
  docs:
    uses: shipsolid/docs-site/.github/workflows/build-deploy.yml@main
```

- [ ] **Step 2: Add a non-deploying docs job to `ci.yml`**

Open `signal-forge/.github/workflows/ci.yml`. It is currently `workflow_dispatch`-only; add a `docs` job consistent with the other jobs. Append:

```yaml
  docs:
    name: Docs build (no deploy)
    permissions:
      contents: read
    uses: shipsolid/docs-site/.github/workflows/build-deploy.yml@main
    with:
      deploy: false
```

If `ci.yml`'s top-level `on:` needs `pull_request` for this to run on PRs and the maintainer wants that, add it; otherwise it runs under the existing `workflow_dispatch`. Match whatever the surrounding jobs assume (check the file header comment).

- [ ] **Step 3: Lint**

Run: `npx --yes actionlint@1.7.7 .github/workflows/docs.yml .github/workflows/ci.yml`
Expected: no errors. (`actionlint` cannot resolve the cross-repo `uses:` until the engine repo is pushed — a "could not read workflow" note there is expected and fine.)

- [ ] **Step 4: Commit**

```bash
git add .github/workflows/docs.yml .github/workflows/ci.yml
git commit -m "Call the centralized docs pipeline; add PR docs build check

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>"
```

---

### Task 11: Remove `website/`, retire `deploy-docs.yml`, update references

**Files:**
- Delete: `signal-forge/website/` (whole tree), `signal-forge/.github/workflows/deploy-docs.yml`
- Modify: `signal-forge/.pre-commit-config.yaml`, `signal-forge/CLAUDE.md`, `signal-forge/README.md`

- [ ] **Step 1: Remove the Astro code and the old workflow**

```bash
cd /home/amit/repos/shipsolid/signal-forge
git rm -r website
git rm .github/workflows/deploy-docs.yml
```

- [ ] **Step 2: Drop the `website/` excludes from `.pre-commit-config.yaml`**

Remove these two lines (currently 35–36) from the `exclude:` block:

```
    website/package-lock\.json|
    website/(node_modules|dist|\.astro|src/content/docs)/.*
```

Leave the line above them (`k8s/monitoring/grafana-helm/generated/.*|`) without a trailing `|` only if it becomes the last entry — check the block still parses (the entry before the closing `)` must not end with `|`).

- [ ] **Step 3: Update `CLAUDE.md` "Docs map"**

Replace the paragraph that begins "It is published to GitHub Pages … by the Starlight build in [`website/`]…" with:

```markdown
It is published to GitHub Pages at <https://shipsolid.github.io/signal-forge/> by the reusable
pipeline in **`shipsolid/docs-site`** (`.github/workflows/build-deploy.yml@main`), invoked from
[`.github/workflows/docs.yml`](.github/workflows/docs.yml). Per-repo settings (title, sidebar,
cross-repo wiki-links) live in [`docs-site.yaml`](docs-site.yaml). This repo holds **no Astro
code** — `docs/` is the only docs input.
```

- [ ] **Step 4: Check `README.md`**

`grep -n "website" README.md` — expect no hits after the tree block. Inspect the repo-tree block near line 398: if it lists `website/`, remove that line and, if helpful, add `docs-site.yaml`. Fix any "preview docs with `npm run dev` in website/" note to "docs render on push to `main`; see `docs-site.yaml`".

- [ ] **Step 5: Run pre-commit to confirm nothing references the removed tree**

Run: `pre-commit run --all-files` (or at least `check-yaml` + the kustomize hook)
Expected: PASS. If pre-commit isn't installed, run `python -c "import yaml; yaml.safe_load(open('.pre-commit-config.yaml'))"` and `yamllint .github/workflows/docs.yml conf.yml`.

- [ ] **Step 6: Commit**

```bash
git add -A
git commit -m "Remove website/ Astro build; docs now build from shipsolid/docs-site

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>"
```

---

### Task 12: Byte-comparable output check against the old build

**Files:** none (verification only)

- [ ] **Step 1: Capture the current published routes**

From `git show HEAD~4:...` is impractical; instead check out the pre-removal `website/` in a scratch worktree:

```bash
cd /home/amit/repos/shipsolid/signal-forge
git worktree add /tmp/sf-oldsite docs-site-extraction~1   # commit before Task 11
cd /tmp/sf-oldsite/website && npm ci && npm run build
find dist -name '*.html' | sed 's#^dist/##' | sort > /tmp/routes-old.txt
```

- [ ] **Step 2: Build via the engine and diff routes**

```bash
cd /home/amit/repos/shipsolid/docs-site
DOCS_SITE_CONFIG=/home/amit/repos/shipsolid/signal-forge/docs-site.yaml \
DOCS_SRC=/home/amit/repos/shipsolid/signal-forge/docs \
PROJECT_README=/home/amit/repos/shipsolid/signal-forge/README.md \
REPO_SLUG=shipsolid/signal-forge \
sh -c 'node scripts/gen-docs.mjs && node scripts/check-links.mjs && npx astro build'
find dist -name '*.html' | sed 's#^dist/##' | sort > /tmp/routes-new.txt
diff /tmp/routes-old.txt /tmp/routes-new.txt
```

Expected: empty diff. Investigate any difference (a missing page = a sidebar/gen-docs regression; an extra page = a stray fixture leak). `check-links` must exit 0.

- [ ] **Step 3: Clean up**

```bash
git worktree remove /tmp/sf-oldsite --force
rm -rf /home/amit/repos/shipsolid/docs-site/dist /home/amit/repos/shipsolid/docs-site/src/content/docs
```

- [ ] **Step 4: No commit** (verification task). Record the diff result in the PR description.

---

## Post-plan (not tasks — handoff notes for the human)

1. Push `shipsolid/docs-site` branch `engine-v1`; open a PR; confirm the engine `ci.yml` (fixture build) is green; merge to `main`.
2. Ensure `shipsolid/signal-forge` repo **Settings → Pages → Source = GitHub Actions** is already set (it is, from the old `deploy-docs.yml`).
3. Push `signal-forge` branch `docs-site-extraction`; open a PR; the new `docs` CI job runs the real cross-repo `workflow_call` with `deploy: false`.
4. Merge `signal-forge`; watch `Docs` workflow deploy; verify `https://shipsolid.github.io/signal-forge/` is unchanged.
5. Follow-up (separate change): write an ADR in `docs/architecture/adrs/` recording the split; onboard a second repo by copying `docs.yml` + `docs-site.yaml`.

---

## Self-Review

**Spec coverage:**
- §1 engine layout → Tasks 1–8. ✅
- §1 `lib/config.mjs` contract → Task 2 (test + impl match the spec's field list). ✅
- §1 sidebar auto-generation → Task 7 `buildSidebar` (numeric-prefix ordering, Overview + Reference groups). ✅
- §1 `wiki-resolve.mjs` parametrization table → Task 3 (`createWikiResolver` params map 1:1 to the table). ✅
- §2 `docs-site.yaml` interface → Task 9 (full field set, full `external_namespaces` list). ✅
- §3 reusable pipeline steps 1–8 → Task 8 `build-deploy.yml` (dual checkout, env contract, gated deploy job). ✅
- §4 caller `docs.yml` + PR build check → Task 10. ✅
- §4 removed/updated/unchanged file lists → Task 11. ✅
- §5 engine self-test + fixture contents → Task 7 (fixture) + A8 (`ci.yml`). Fixture exercises nested tree, folder README→index, resolvable `[[wiki-link]]` + external namespace, `../sibling.md`, sidebar override, `wiki_links` block. ✅
- §6 risk mitigations → fixture build gate (A7), fail-fast config (A2), base derivation (A2), slug folding verbatim (A1), byte-compare (B4). ✅
- §7 rollout order → Phase A before Phase B; post-plan handoff notes. ✅

**Placeholder scan:** No "TBD"/"handle edge cases"/"similar to Task N". Verbatim-copy steps name the exact source path. The one soft spot — B2 Step 2 "match whatever the surrounding jobs assume" — is a real repo-specific judgment (ci.yml is `workflow_dispatch`-only today); the step says exactly what to check and both outcomes are spelled out.

**Type consistency:**
- `createWikiResolver({ docsPrefix, externalBaseUrl, stripPrefixes, externalNamespaces })` — same shape in A3 (def), A4 (test), A5/A6 (callers). ✅
- `applyWikiLinks(md, index, resolver)` — 3-arg form in A4 (def + test), A5 (caller). ✅
- `remarkRewriteMdLinks({ docsPrefix })` — factory form in A4 (def), A7 (`astro.config.mjs` caller). ✅
- `loadDocsSiteConfig({ configPath, repoSlug })` → object with `base`, `wikiLinks` (camelCase), `codeLangAliases`, `projectReadme` — consistent A2 → A5/A6/A7. ✅
- Env var names (`DOCS_SRC`, `PROJECT_README`, `DOCS_SITE_CONFIG`, `REPO_SLUG`, `DOCS_OUT`) — identical in A5, A6, A7, A8, B1, B4. ✅
