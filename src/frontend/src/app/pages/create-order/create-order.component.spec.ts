import { ComponentFixture, TestBed } from '@angular/core/testing';
import { RouterTestingModule } from '@angular/router/testing';
import { ActivatedRoute, Router } from '@angular/router';
import { of, throwError } from 'rxjs';
import { CreateOrderComponent } from './create-order.component';
import { ApiService } from '../../services/api.service';

describe('CreateOrderComponent', () => {
  let fixture: ComponentFixture<CreateOrderComponent>;
  let component: CreateOrderComponent;
  let apiSpy: jest.Mocked<ApiService>;
  let router: Router;

  const mockRoute = {
    snapshot: {
      queryParamMap: { get: jest.fn().mockReturnValue(null) },
    },
  };

  beforeEach(async () => {
    mockRoute.snapshot.queryParamMap.get = jest.fn().mockReturnValue(null);

    apiSpy = {
      createOrder: jest.fn(),
    } as unknown as jest.Mocked<ApiService>;

    await TestBed.configureTestingModule({
      imports: [CreateOrderComponent, RouterTestingModule],
      providers: [
        { provide: ApiService, useValue: apiSpy },
        { provide: ActivatedRoute, useValue: mockRoute },
      ],
    }).compileComponents();

    fixture = TestBed.createComponent(CreateOrderComponent);
    component = fixture.componentInstance;
    router = TestBed.inject(Router);
    fixture.detectChanges();
  });

  // ── ngOnInit — query param handling ──────────────────────────────────────

  it('pre-fills projectId from a valid query param', async () => {
    mockRoute.snapshot.queryParamMap.get = jest.fn().mockReturnValue('7');
    fixture = TestBed.createComponent(CreateOrderComponent);
    component = fixture.componentInstance;
    fixture.detectChanges();

    expect(component.projectId).toBe(7);
  });

  it('ignores a non-numeric query param', async () => {
    mockRoute.snapshot.queryParamMap.get = jest.fn().mockReturnValue('abc');
    fixture = TestBed.createComponent(CreateOrderComponent);
    component = fixture.componentInstance;
    fixture.detectChanges();

    expect(component.projectId).toBe(0);
  });

  it('ignores a zero or negative query param', async () => {
    mockRoute.snapshot.queryParamMap.get = jest.fn().mockReturnValue('0');
    fixture = TestBed.createComponent(CreateOrderComponent);
    component = fixture.componentInstance;
    fixture.detectChanges();

    expect(component.projectId).toBe(0);
  });

  // ── submit() ──────────────────────────────────────────────────────────────

  it('submit() calls createOrder with the current form values', () => {
    apiSpy.createOrder.mockReturnValue(of({ id: 42, status: 'Created' }));
    jest.spyOn(router, 'navigate').mockResolvedValue(true);

    component.projectId = 5;
    component.description = 'Server rack';
    component.amount = 4500;
    component.submit();

    expect(apiSpy.createOrder).toHaveBeenCalledWith({
      projectId: 5,
      description: 'Server rack',
      amount: 4500,
    });
  });

  it('shows success state and stores the returned order ID', () => {
    apiSpy.createOrder.mockReturnValue(of({ id: 42, status: 'Created' }));
    jest.spyOn(router, 'navigate').mockResolvedValue(true);

    component.submit();

    expect(component.success).toBe(true);
    expect(component.createdId).toBe(42);
    expect(component.submitting).toBe(false);
  });

  it('navigates to the project detail page after 1500ms on success', () => {
    jest.useFakeTimers();
    apiSpy.createOrder.mockReturnValue(of({ id: 42, status: 'Created' }));
    const navSpy = jest.spyOn(router, 'navigate').mockResolvedValue(true);
    component.projectId = 5;

    component.submit();
    expect(navSpy).not.toHaveBeenCalled();

    jest.advanceTimersByTime(1500);
    expect(navSpy).toHaveBeenCalledWith(['/projects', 5]);

    jest.useRealTimers();
  });

  it('shows error message when createOrder fails', () => {
    apiSpy.createOrder.mockReturnValue(throwError(() => new Error('Bad Request')));
    component.submit();
    fixture.detectChanges();

    expect(component.error).toBe('Bad Request');
    expect(component.success).toBe(false);
    expect(component.submitting).toBe(false);
  });

  it('clears a previous error before each submission', () => {
    apiSpy.createOrder.mockReturnValue(throwError(() => new Error('First error')));
    component.submit();
    expect(component.error).toBe('First error');

    apiSpy.createOrder.mockReturnValue(of({ id: 1, status: 'Created' }));
    jest.spyOn(router, 'navigate').mockResolvedValue(true);
    component.submit();
    expect(component.error).toBe('');
  });
});
