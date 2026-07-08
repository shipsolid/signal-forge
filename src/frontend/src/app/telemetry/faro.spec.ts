import { TransportItem, TransportItemType } from '@grafana/faro-web-sdk';
import { scrubTelemetryItem } from './faro';

function logItem(message: string): TransportItem {
  return {
    type: TransportItemType.LOG,
    payload: { message, level: 'info', timestamp: '2026-01-01T00:00:00Z', context: undefined },
  } as TransportItem;
}

describe('scrubTelemetryItem', () => {
  it('drops LOG items whose message mentions /healthz', () => {
    expect(scrubTelemetryItem(logItem('GET /healthz 200'))).toBeNull();
  });

  it('does not drop a non-LOG item just because /healthz appears somewhere in its payload', () => {
    // Regression guard: the old implementation JSON.stringify()'d the whole
    // payload of *every* item type and substring-matched it, so a stack trace
    // mentioning a file path containing "healthz" (or any unrelated field)
    // would be dropped as a false positive. Scoping to LOG + .message fixes this.
    const item = {
      type: TransportItemType.EXCEPTION,
      payload: { value: 'Error at /app/routes/healthz-adjacent.ts:12' },
    } as unknown as TransportItem;

    expect(scrubTelemetryItem(item)).toBe(item);
  });

  it('redacts an email address in a LOG message instead of dropping it', () => {
    const result = scrubTelemetryItem(logItem('Order created by amit@example.com'));

    expect(result).not.toBeNull();
    const payload = result!.payload as { message: string };
    expect(payload.message).toBe('Order created by [redacted-email]');
  });

  it('passes through a LOG item with no PII and no /healthz mention unchanged', () => {
    const result = scrubTelemetryItem(logItem('Order #42 created'));

    expect(result).not.toBeNull();
    const payload = result!.payload as { message: string };
    expect(payload.message).toBe('Order #42 created');
  });
});
