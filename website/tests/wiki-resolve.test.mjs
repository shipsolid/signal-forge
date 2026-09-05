import { describe, it, expect } from 'vitest';
import {
  splitHeading,
  normalizeKey,
  isExternalNotesKey,
  externalNotesUrl,
  resolveInternal,
} from '../wiki-resolve.mjs';

// Mirrors a slice of the real generated docs tree.
const slugs = [
  'architecture/overview',
  'architecture/adrs/adr-log-tailing-not-otlp-export',
  'services/gateway-api',
  'observability/pipeline',
  'deployment/helm',
  'deployment/grafana-cloud',
  'operations/runbooks',
  'guides', // guides/README.md -> guides/index.md -> slug "guides"
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
  it('drops the old notes prefix', () => {
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
  it('flags <book>/<subpath> keys for real notes books', () => {
    expect(isExternalNotesKey('networks/05-http-ecosystem/05-grpc/05-grpc')).toBe(true);
    expect(isExternalNotesKey('patterns/04-microservice-patterns/14-outbox/14-outbox')).toBe(true);
    expect(isExternalNotesKey('prometheus/readme')).toBe(true);
    expect(isExternalNotesKey('kubernetes/readme')).toBe(true);
  });
  it('does NOT flag a bare book name (that is this repo\'s own page)', () => {
    expect(isExternalNotesKey('kubernetes')).toBe(false);
  });
  it('does NOT flag an unknown top segment (dead tech/* refs)', () => {
    expect(isExternalNotesKey('tech/jaeger')).toBe(false);
  });
  it('does NOT flag internal keys', () => {
    expect(isExternalNotesKey('overview')).toBe(false);
    expect(isExternalNotesKey('projects/app-signal-forge/observability/pipeline')).toBe(false);
  });
  it('builds an absolute notes-site URL, folding readme to the book root', () => {
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
  it('resolves the root index (readme / index / empty) to the root slug', () => {
    expect(resolveInternal('projects/app-signal-forge/readme', index)).toBe('');
    expect(resolveInternal('index', index)).toBe('');
  });
  it('resolves an ADR basename', () => {
    expect(resolveInternal('adr-log-tailing-not-otlp-export', index)).toBe(
      'architecture/adrs/adr-log-tailing-not-otlp-export',
    );
  });
});
