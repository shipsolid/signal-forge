# website/

The [Starlight](https://starlight.astro.build/) build that publishes this
repo's `docs/` tree (plus the top-level `README.md`) to GitHub Pages at
<https://shipsolid.github.io/signal-forge/>.

`docs/` at the repo root stays the single source of truth — edit Markdown
there, not here. `src/content/docs/` is **generated** (gitignored) by
`scripts/gen-docs.mjs` on `predev` / `prebuild`.

```bash
npm install
npm run dev        # gen-docs, then astro dev at /signal-forge/
npm run build      # gen-docs + check-links + astro build -> dist/
npm run preview    # serve dist/
npm test           # vitest — wiki-link resolver rules
```

## How content flows

| Step | What |
| ---- | ---- |
| `scripts/gen-docs.mjs` | copies `../docs/**/*.md` → `src/content/docs/`, renames each `README.md` → `index.md`, and pulls `../README.md` in as `project-readme.md` (injected frontmatter, repo-relative links rewritten to GitHub blob URLs) |
| `remark-wiki-links.mjs` | resolves `[[wiki-links]]` — internal → `/signal-forge/<slug>/`, `tech/*` & `patterns/*` → `https://shipsolid.github.io/notes/...`, unresolved → plain text + warning |
| `remark-rewrite-md-links.mjs` | relative `../foo.md` links between docs → the served URL |
| `scripts/check-links.mjs` | fails the build on any unresolved wiki-link or dead relative link |
| `wiki-resolve.mjs` | the resolution rules, shared by the plugin and the checker (covered by `tests/`) |

## Deploy

`.github/workflows/deploy-docs.yml` builds `website/` and deploys `dist/` on
every push to `main`. One-time: repo **Settings → Pages → Source = GitHub
Actions** (or `gh api -X POST repos/shipsolid/signal-forge/pages -f build_type=workflow`).
