import { describe, it, expect } from 'vitest';
import { applyWikiLinks } from '../apply-wiki-links.mjs';

// Fake index over a slice of the real tree. absOf points nowhere — titleFor /
// headingsFor swallow the ENOENT, so link text falls back to the display
// override or the humanized slug, which is all these cases exercise.
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

const run = (md) => applyWikiLinks(md, index).md;

describe('applyWikiLinks', () => {
  it('keeps bold/code formatting inside the display text', () => {
    expect(run('see [[architecture/adrs/adr-spanlink-for-async-rabbitmq|**SpanLink**]] now')).toBe(
      'see [**SpanLink**](/signal-forge/architecture/adrs/adr-spanlink-for-async-rabbitmq/) now',
    );
    expect(run('[[api/grpc|`GetOrdersByProject`]] streams')).toBe(
      '[`GetOrdersByProject`](/signal-forge/api/grpc/) streams',
    );
  });

  it('resolves a bare basename and the old projects/ prefix alike', () => {
    expect(run('[[pipeline]]')).toBe('[Pipeline](/signal-forge/observability/pipeline/)');
    expect(run('[[projects/app-signal-forge/observability/pipeline|the pipeline]]')).toBe(
      '[the pipeline](/signal-forge/observability/pipeline/)',
    );
  });

  it('routes a known notes book to an absolute URL', () => {
    expect(run('[[networks/05-http-ecosystem/05-grpc/05-grpc|gRPC]] over HTTP/2')).toBe(
      '[gRPC](https://shipsolid.github.io/notes/networks/05-http-ecosystem/05-grpc/05-grpc/) over HTTP/2',
    );
  });

  it('unwraps an unresolved link to its display text and reports it', () => {
    const { md, unresolved } = applyWikiLinks('use [[tech/jaeger|Jaeger]] here', index);
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
