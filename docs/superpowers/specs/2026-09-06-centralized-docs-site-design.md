---
title: Centralized docs-site engine
date: 2026-09-06
status: approved
repos:
  - shipsolid/docs-site (new — /home/amit/repos/shipsolid/docs-site)
  - shipsolid/signal-forge (first consumer)
---

# Centralized docs-site engine

## Problem

Every tech repo that wants a docs site currently carries a full Astro/Starlight
project. `signal-forge/website/` is 9 engine `.mjs` files + `package.json` +
lockfile + `content.config.ts` + 2 test suites + a bespoke deploy workflow. The
`shipsolid/notes` repo has a near-identical cousin. Fixes (the slug-folding
GitHub-Pages 404 fix in `doc-path.mjs`, for example) have to be hand-ported.

## Goal

- **Zero Astro code in a tech repo.** A consumer repo keeps only `docs/`,
  `README.md`, one config file, and one short caller workflow.
- A single `shipsolid/docs-site` repo owns the engine and a reusable GitHub
  Actions pipeline.
- Consumer repos track the engine's `main` — no version pins, no release cadence.
- Reusable by design; only `signal-forge` is wired up in this pass.

## Decisions (settled during brainstorming)

| Question | Decision |
| --- | --- |
| Where the engine lives | New dedicated repo `shipsolid/docs-site` |
| Build/deploy mechanism | Reusable `workflow_call` workflow; deploy runs in the consumer's context |
| Rollout | Build reusable; wire up only `signal-forge` now |
| Engine versioning | Consumers reference `@main` |
| Sidebar | Engine auto-generates from `docs/` structure; optional per-repo `sidebar:` block overrides |
| Local preview | None. PRs get a non-deploying build check; `main` deploys |
| Config file location | Consumer repo root, `docs-site.yaml` |
| Cross-repo `[[wiki-links]]` | Engine keeps the logic; notes-specific data moves to a `wiki_links:` config block |

## Non-goals

- Release tags, changelogs, canary rollout for the engine. `main`-tracking is
  the chosen model; a fixture build is the guardrail (see §6).
- Migrating `shipsolid/notes` or `_shipsolid.github.io`. Different sites,
  separate follow-up.
- Per-PR preview *deploys*. PRs build but do not publish.
- Local `astro dev` in consumer repos.

---

## 1. `shipsolid/docs-site` layout

Astro + Starlight project at repo root.

```
docs-site/
  astro.config.mjs            # Starlight config built from $DOCS_SITE_CONFIG (YAML) + env
  package.json                # signal-forge/website/package.json deps verbatim (@astrojs/starlight ^0.42,
  package-lock.json           #   astro ^7, astro-mermaid, mermaid, @mermaid-js/layout-elk, @astrojs/markdown-remark,
                              #   github-slugger, unist-util-visit, vitest) + js-yaml
  tsconfig.json
  vitest.config.mjs
  .nvmrc                      # Node version — consumed by setup-node in build-deploy.yml
  lib/
    config.mjs                # NEW — load + validate docs-site.yaml, apply defaults
    wiki-index.mjs            # moved verbatim from signal-forge/website/
    wiki-resolve.mjs          # moved; NOTES_* constants replaced by config params
    apply-wiki-links.mjs      # moved verbatim
    doc-path.mjs              # moved verbatim (slug folding)
    remark-rewrite-md-links.mjs  # moved verbatim
  scripts/
    gen-docs.mjs              # moved; source paths from env, not '../..'
    check-links.mjs           # moved; source paths from env
  src/
    content.config.ts         # moved verbatim (keeps notes-graph passthrough keys)
    content/docs/             # gitignored — gen-docs output
  fixtures/
    sample-repo/              # fake consumer: docs/ tree + README.md + docs-site.yaml
  tests/
    apply-wiki-links.test.mjs # moved
    wiki-resolve.test.mjs     # moved; updated to pass config in
    config.test.mjs           # NEW — defaults, validation errors
    fixture-build.test.mjs    # NEW — gen-docs + check-links + astro build against fixtures/sample-repo
  .github/workflows/
    build-deploy.yml           # on: workflow_call — the reusable pipeline
    ci.yml                     # on: pull_request — engine self-test
  README.md
  .gitignore                   # .astro/  dist/  node_modules/  src/content/docs/
```

### `lib/config.mjs` contract

```
loadDocsSiteConfig({ configPath, repoSlug }) -> {
  site: 'https://shipsolid.github.io',           // constant
  base: cfg.base ?? `/${repoSlug.split('/')[1]}/`,
  title: cfg.title,                               // required — throw if missing
  description: cfg.description ?? '',
  social: { github: cfg.social?.github ?? `https://github.com/${repoSlug}` },
  docsDir: cfg.docs_dir ?? 'docs',
  projectReadme: cfg.project_readme ?? 'README.md',   // false disables
  sidebar: cfg.sidebar ?? null,                   // null => engine autogenerates
  wikiLinks: cfg.wiki_links ?? null,              // null => internal resolution only
  codeLangAliases: { river: 'hcl', ...(cfg.code_lang_aliases ?? {}) },
}
```

Missing file or missing `title` → non-zero exit with a one-line diagnostic
(`astro.config.mjs` imports this at module load, so the build fails fast rather
than producing an unstyled site).

### Sidebar auto-generation

When `sidebar` is null: one Starlight group per immediate subdirectory of
`docsDir`, label = directory name title-cased, `autogenerate: { directory: <dir> }`,
plus a leading `{ label: 'Overview', link: '/' }`. Top-level loose `.md` files
(e.g. `spec.md`) go into a trailing `Reference` group. Ordering: a leading
`NN-` numeric prefix on a directory name is honored then stripped; otherwise
alphabetical.

### `wiki-resolve.mjs` parametrization

Today's module-level constants become function parameters threaded from config:

| Current constant | Config source |
| --- | --- |
| `DOCS_PREFIX = '/signal-forge'` | `base` (trailing slash trimmed) |
| `NOTES_SITE` | `wiki_links.external_base_url` |
| `OLD_PREFIX_RE` | `wiki_links.strip_prefixes[]` (compiled to an anchored alternation) |
| `NOTES_BOOKS` (Set) | `wiki_links.external_namespaces[]` |

Generic behavior stays in the engine: `readme`→`index` aliasing, "first segment
in the namespace set **and** key contains a `/`" ⇒ external, slug-folding the
external URL, internal exact / basename / ambiguous resolution. With no
`wiki_links` block, `strip_prefixes` and `external_namespaces` are empty and
every `[[link]]` resolves internally or is left for the caller to unwrap.

---

## 2. Consumer interface — `docs-site.yaml`

Repo root. Full example (this is signal-forge's, transcribed from its current
`astro.config.mjs` + `wiki-resolve.mjs`):

```yaml
title: SignalForge
description: >-
  OpenTelemetry microservices validation lab — architecture, services,
  observability pipeline, deployment, and operations.

# base: /signal-forge/        # optional — default is /<repo-name>/
# docs_dir: docs              # optional — default
# project_readme: README.md   # optional — default; false to skip

social:
  github: https://github.com/shipsolid/signal-forge   # optional — derived from $GITHUB_REPOSITORY

# Optional. Absent => engine autogenerates from docs/ structure.
sidebar:
  - { label: Overview, link: / }
  - { label: Architecture,   autogenerate: { directory: architecture } }
  - { label: Services,       autogenerate: { directory: services } }
  - { label: Observability,  autogenerate: { directory: observability } }
  - { label: Infrastructure, autogenerate: { directory: infrastructure } }
  - { label: Deployment,     autogenerate: { directory: deployment } }
  - { label: Operations,     autogenerate: { directory: operations } }
  - { label: API,            autogenerate: { directory: api } }
  - { label: Guides,         autogenerate: { directory: guides } }
  - { label: Reference, items: [spec, otel-patterns, testing, project-readme] }

# Optional. Cross-repo [[wiki-link]] resolution.
wiki_links:
  strip_prefixes: ["projects/app-signal-forge/"]
  external_base_url: https://shipsolid.github.io/notes
  external_namespaces:
    - agentic-ai-engineering
    - ci-cd
    - data-structures-algorithms
    - grafana-cloud
    - kubernetes
    - networks
    - observability
    - prometheus
    - sre
    - system-design
    # …full list carried over verbatim from NOTES_BOOKS

# code_lang_aliases: { river: hcl }   # optional — engine already defaults river->hcl
```

A greenfield repo's file can be just `title` + `description`.

---

## 3. Reusable pipeline — `.github/workflows/build-deploy.yml`

```yaml
on:
  workflow_call:
    inputs:
      deploy:
        type: boolean
        default: true
```

Runs in the **caller's** context — `GITHUB_TOKEN`, `GITHUB_REPOSITORY`, and the
default checkout are all the consumer repo's.

Steps:

1. `actions/checkout` — consumer repo → `$GITHUB_WORKSPACE`
2. `actions/checkout` — `repository: shipsolid/docs-site`, `ref: main`,
   `path: .docs-engine` (public repo, no PAT)
3. `actions/setup-node` — version from `.docs-engine/.nvmrc`; `cache: npm`,
   `cache-dependency-path: .docs-engine/package-lock.json`
4. `npm ci` in `.docs-engine`
5. `node scripts/gen-docs.mjs` with env:
   - `DOCS_SRC=$GITHUB_WORKSPACE/docs`
   - `PROJECT_README=$GITHUB_WORKSPACE/README.md`
   - `DOCS_SITE_CONFIG=$GITHUB_WORKSPACE/docs-site.yaml`
   - `REPO_SLUG=$GITHUB_REPOSITORY`
6. `node scripts/check-links.mjs` (same env) — non-zero exit fails the job
7. `npx astro build` in `.docs-engine` → `.docs-engine/dist`
8. if `inputs.deploy`: `actions/configure-pages` → `actions/upload-pages-artifact`
   (`path: .docs-engine/dist`) → `actions/deploy-pages` (needs its own job with
   `environment: github-pages`)

`gen-docs.mjs` / `check-links.mjs` change only in how they resolve roots:
`process.env.DOCS_SRC` instead of `path.resolve(__dirname, '../..')`. Everything
downstream (walk, README import + link rewrite, wiki-link pass, index build) is
unchanged.

---

## 4. Consumer wiring — `signal-forge`

### New: `.github/workflows/docs.yml`

```yaml
name: Docs
on:
  push:
    branches: [main]
    paths: ["docs/**", "README.md", "docs-site.yaml", ".github/workflows/docs.yml"]
  workflow_dispatch:
concurrency: { group: pages, cancel-in-progress: false }
permissions: { contents: read, pages: write, id-token: write }
jobs:
  docs:
    uses: shipsolid/docs-site/.github/workflows/build-deploy.yml@main
```

Same trigger surface as the retired `deploy-docs.yml` (main-only, path-filtered).

### New: PR build check

A `docs` job added to the existing `.github/workflows/ci.yml`, triggered on
`pull_request`, calling the same reusable workflow with `deploy: false`. Catches
broken wiki-links / dead relative links / malformed `docs-site.yaml` before
merge. ~2 min, no Pages or token setup.

> Note: `ci.yml` is currently `workflow_dispatch`-only. The docs check is added
> consistent with that — either as another `workflow_dispatch` job or promoted
> to `pull_request`; decide at implementation time to match how the other CI
> stacks are expected to run.

### New: `docs-site.yaml`

As in §2.

### Removed

- `website/` (entire directory) — `git rm -r`
- `.github/workflows/deploy-docs.yml` — `git rm`
- `.pre-commit-config.yaml` lines 35–36 (the two `website/…` `exclude` patterns)

### Updated

- `CLAUDE.md` "Docs map" section — replace the `website/` + `deploy-docs.yml`
  description with: published by `shipsolid/docs-site`'s reusable workflow,
  configured by `docs-site.yaml`, triggered from `.github/workflows/docs.yml`.
- Root `README.md` — no `website/` path references found in prose; verify the
  repo-tree block near line 398 and any "how to preview docs" note.

### Unchanged

`docs/**`, `README.md` content, every `https://shipsolid.github.io/signal-forge/…`
link. Output is byte-comparable: same URL, same pages, same sidebar (via the
`sidebar:` override), same wiki-link resolution (via `wiki_links:`).

---

## 5. Engine self-test — `shipsolid/docs-site/.github/workflows/ci.yml`

`on: pull_request`. One job:

1. `npm ci`
2. `npm test` — vitest: `apply-wiki-links`, `wiki-resolve` (now config-driven),
   `config` (defaults + validation)
3. `fixture-build.test.mjs` — runs `gen-docs` + `check-links` + `astro build`
   against `fixtures/sample-repo/`, asserts exit 0 and that `dist/` contains the
   expected routes.

`fixtures/sample-repo/` exercises: a nested `docs/` tree, an ADR-style folder, a
folder `README.md` → `index.md`, at least one resolvable `[[wiki-link]]` and one
`external_namespaces` link, a `../sibling.md` relative link, a `sidebar:`
override, and a `wiki_links:` block. This fixture build **is** the contract that
`main`-tracking consumers depend on; a red run blocks the merge.

---

## 6. Risks

| Risk | Mitigation |
| --- | --- |
| Engine `main` breaks every consumer's next build | `fixture-build.test.mjs` gates every engine PR; `check-links` still gates each consumer build |
| Config typo yields a silently unstyled site | `lib/config.mjs` throws on missing file / missing `title` at `astro.config.mjs` load |
| `base` mismatch (must be `/<repo>/` for a project Pages site) | Derived from `$GITHUB_REPOSITORY` by default; override only if intentional |
| Case-sensitive Pages 404s | `doc-path.mjs` slug folding moves over verbatim; covered by the fixture build |
| Second checkout rate limiting / outage | Public repo, anonymous checkout; acceptable for a docs deploy |
| `notes` site book list drifts from `external_namespaces` | Lives in `signal-forge/docs-site.yaml`; a stale entry only means a wiki-link renders as a link to a 404, same failure mode as today |

## 7. Rollout order

1. Populate `shipsolid/docs-site`: move engine files, parametrize the 3
   repo-coupled ones, add `lib/config.mjs`, fixtures, tests, both workflows.
2. Prove it: engine `ci.yml` green (fixture build passes).
3. `signal-forge`: add `docs-site.yaml` + `docs.yml` + CI docs job; `git rm`
   `website/` + `deploy-docs.yml`; edit `.pre-commit-config.yaml`, `CLAUDE.md`,
   `README.md`.
4. Merge `signal-forge`; confirm `https://shipsolid.github.io/signal-forge/`
   rebuilds identically.
5. Later / not this pass: an ADR in `docs/architecture/adrs/` recording the
   split; onboard a second repo by copying `docs.yml` + `docs-site.yaml`.
