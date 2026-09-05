import { defineCollection } from 'astro:content';
import { z } from 'astro/zod';
import { docsLoader } from '@astrojs/starlight/loaders';
import { docsSchema } from '@astrojs/starlight/schema';

// The docs were authored inside the `notes` repo, so their frontmatter carries
// notes-graph keys Starlight doesn't know. Accept them as optional passthrough
// rather than stripping them in gen-docs (fewer moving parts).
export const collections = {
  docs: defineCollection({
    loader: docsLoader(),
    schema: docsSchema({
      extend: z.object({
        zettelId: z.string().optional(),
        noteType: z.string().optional(),
        tags: z.array(z.string()).optional(),
        updated: z.coerce.date().optional(),
        relations: z
          .array(z.object({ slug: z.string(), kind: z.string() }))
          .optional(),
      }),
    }),
  }),
};
