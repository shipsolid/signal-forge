import { ComponentFixture, TestBed } from '@angular/core/testing';
import { of, throwError } from 'rxjs';
import { NotificationsComponent } from './notifications.component';
import { ApiService, Notification } from '../../services/api.service';

const MOCK_NOTIFICATIONS: Notification[] = [
  { id: '1', order_id: '10', project_id: '5', message: 'Order 10 received', status: 'sent', created_at: '2026-01-01T00:00:00Z', trace_id: 'abc123' },
  { id: '2', order_id: '11', project_id: '5', message: 'Order 11 received', status: 'sent', created_at: '2026-01-02T00:00:00Z' },
];

describe('NotificationsComponent', () => {
  let fixture: ComponentFixture<NotificationsComponent>;
  let component: NotificationsComponent;
  let apiSpy: jest.Mocked<ApiService>;

  beforeEach(async () => {
    apiSpy = {
      getNotifications: jest.fn().mockReturnValue(of(MOCK_NOTIFICATIONS)),
    } as unknown as jest.Mocked<ApiService>;

    await TestBed.configureTestingModule({
      imports: [NotificationsComponent],
      providers: [{ provide: ApiService, useValue: apiSpy }],
    }).compileComponents();

    fixture = TestBed.createComponent(NotificationsComponent);
    component = fixture.componentInstance;
    fixture.detectChanges();
  });

  it('calls getNotifications on init', () => {
    expect(apiSpy.getNotifications).toHaveBeenCalledTimes(1);
  });

  it('renders notification messages after load', () => {
    const items: NodeListOf<HTMLElement> = fixture.nativeElement.querySelectorAll('ul li');
    expect(items.length).toBe(2);
    expect(items[0].textContent).toContain('Order 10 received');
    expect(items[1].textContent).toContain('Order 11 received');
  });

  it('renders trace_id when present', () => {
    const items: NodeListOf<HTMLElement> = fixture.nativeElement.querySelectorAll('ul li');
    expect(items[0].textContent).toContain('abc123');
  });

  it('clears loading flag after successful load', () => {
    expect(component.loading).toBe(false);
  });

  it('shows empty-state message when no notifications returned', () => {
    apiSpy.getNotifications.mockReturnValue(of([]));
    component.load();
    fixture.detectChanges();

    expect(fixture.nativeElement.textContent).toContain('No notifications yet');
  });

  it('shows error message when getNotifications fails', () => {
    apiSpy.getNotifications.mockReturnValue(
      throwError(() => new Error('Service unavailable'))
    );
    component.load();
    fixture.detectChanges();

    expect(component.error).toBe('Service unavailable');
    expect(fixture.nativeElement.textContent).toContain('Service unavailable');
  });

  it('clears loading flag even on error', () => {
    apiSpy.getNotifications.mockReturnValue(
      throwError(() => new Error('timeout'))
    );
    component.load();
    expect(component.loading).toBe(false);
  });

  it('refreshes list when load() is called again', () => {
    const fresh: Notification[] = [
      { id: '3', order_id: '12', project_id: '7', message: 'Order 12 received', status: 'sent', created_at: '2026-01-03T00:00:00Z' },
    ];
    apiSpy.getNotifications.mockReturnValue(of(fresh));
    component.load();
    fixture.detectChanges();

    expect(apiSpy.getNotifications).toHaveBeenCalledTimes(2);
    expect(component.notifications).toEqual(fresh);
  });
});
