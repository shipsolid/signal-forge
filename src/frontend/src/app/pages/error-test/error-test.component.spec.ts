import { ComponentFixture, TestBed } from '@angular/core/testing';
import { of, throwError } from 'rxjs';
import { ErrorTestComponent } from './error-test.component';
import { ApiService } from '../../services/api.service';

describe('ErrorTestComponent', () => {
  let fixture: ComponentFixture<ErrorTestComponent>;
  let component: ErrorTestComponent;
  let apiSpy: jest.Mocked<ApiService>;

  beforeEach(async () => {
    apiSpy = {
      triggerError: jest.fn(),
    } as unknown as jest.Mocked<ApiService>;

    await TestBed.configureTestingModule({
      imports: [ErrorTestComponent],
      providers: [{ provide: ApiService, useValue: apiSpy }],
    }).compileComponents();

    fixture = TestBed.createComponent(ErrorTestComponent);
    component = fixture.componentInstance;
    fixture.detectChanges();
  });

  // ── triggerBackendError ───────────────────────────────────────────────────

  it('triggerBackendError() calls api.triggerError', () => {
    apiSpy.triggerError.mockReturnValue(of({}));
    component.triggerBackendError();
    expect(apiSpy.triggerError).toHaveBeenCalledTimes(1);
  });

  it('shows backendError when the backend call fails', () => {
    apiSpy.triggerError.mockReturnValue(
      throwError(() => ({ status: 500, message: 'Internal Server Error' })),
    );
    component.triggerBackendError();
    fixture.detectChanges();

    expect(component.backendError).toContain('Internal Server Error');
    expect(fixture.nativeElement.textContent).toContain('Internal Server Error');
  });

  it('shows backendResult when the backend call unexpectedly succeeds', () => {
    apiSpy.triggerError.mockReturnValue(of({ message: 'ok' }));
    component.triggerBackendError();
    fixture.detectChanges();

    expect(component.backendResult).toBeTruthy();
  });

  it('clears previous error before each backend call', () => {
    apiSpy.triggerError.mockReturnValue(
      throwError(() => ({ status: 500, message: 'First error' })),
    );
    component.triggerBackendError();

    apiSpy.triggerError.mockReturnValue(of({ message: 'ok' }));
    component.triggerBackendError();

    expect(component.backendError).toBe('');
  });

  // ── triggerFrontendError ──────────────────────────────────────────────────
  // The throw is intentional — Faro captures it as a JS error in the browser.

  it('sets frontendMsg before throwing', () => {
    expect(() => component.triggerFrontendError()).toThrow(
      'Intentional frontend error for Faro validation',
    );
    expect(component.frontendMsg).toContain('JS exception thrown');
  });

  // ── triggerUnhandledRejection ─────────────────────────────────────────────
  // Creates a Promise.reject() that Faro captures as an unhandled rejection.
  // We only verify the side-effect; the rejection itself is the OTel signal.

  it('sets frontendMsg on triggerUnhandledRejection()', () => {
    component.triggerUnhandledRejection();
    expect(component.frontendMsg).toContain('Unhandled promise rejection triggered');
  });
});
