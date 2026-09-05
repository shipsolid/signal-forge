import { defineConfig } from 'astro/config';
import starlight from '@astrojs/starlight';
import mermaid from 'astro-mermaid';
import { remarkRewriteMdLinks } from './remark-rewrite-md-links.mjs';

// GitHub Pages project-site deploy: this repo publishes to its own Pages, not
// the shipsolid.github.io user-page repo, so it is served under /signal-forge/
// and every asset + link must resolve beneath that base.
export default defineConfig({
  site: 'https://shipsolid.github.io',
  base: '/signal-forge/',
  trailingSlash: 'always',
  integrations: [
    // Must precede starlight() — it hooks the markdown engine first and
    // rewrites ```mermaid fences to <pre class="mermaid"> for client render.
    mermaid({ theme: 'default', autoTheme: true }),
    starlight({
      title: 'SignalForge',
      description:
        'OpenTelemetry microservices validation lab — architecture, services, observability pipeline, deployment, and operations.',
      social: [
        { icon: 'github', label: 'GitHub', href: 'https://github.com/shipsolid/signal-forge' },
      ],
      pagefind: true,
      // Alloy's config language is River — Shiki has no grammar for it, and its
      // HCL-ish block syntax highlights acceptably as HCL.
      expressiveCode: { shiki: { langAlias: { river: 'hcl' } } },
      sidebar: [
        { label: 'Overview', link: '/' },
        { label: 'Architecture', items: [{ autogenerate: { directory: 'architecture' } }] },
        { label: 'Services', items: [{ autogenerate: { directory: 'services' } }] },
        { label: 'Observability', items: [{ autogenerate: { directory: 'observability' } }] },
        { label: 'Infrastructure', items: [{ autogenerate: { directory: 'infrastructure' } }] },
        { label: 'Deployment', items: [{ autogenerate: { directory: 'deployment' } }] },
        { label: 'Operations', items: [{ autogenerate: { directory: 'operations' } }] },
        { label: 'API', items: [{ autogenerate: { directory: 'api' } }] },
        { label: 'Guides', items: [{ autogenerate: { directory: 'guides' } }] },
        {
          label: 'Reference',
          items: [
            { slug: 'spec' },
            { slug: 'otel-patterns' },
            { slug: 'testing' },
            { slug: 'project-readme' },
          ],
        },
      ],
    }),
  ],
  markdown: {
    // Wiki-links are rewritten upstream by scripts/gen-docs.mjs (raw text, so
    // formatted display survives). This only fixes sibling `../foo.md` links.
    remarkPlugins: [remarkRewriteMdLinks],
  },
});
