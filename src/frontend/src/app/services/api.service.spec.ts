import { TestBed } from '@angular/core/testing';
import {
  HttpClientTestingModule,
  HttpTestingController,
} from '@angular/common/http/testing';
import { ApiService, Project, Order, Notification } from './api.service';
import { environment } from '../../environments/environment';

describe('ApiService', () => {
  let service: ApiService;
  let http: HttpTestingController;
  const base = environment.apiBaseUrl;

  beforeEach(() => {
    TestBed.configureTestingModule({
      imports: [HttpClientTestingModule],
      providers: [ApiService],
    });
    service = TestBed.inject(ApiService);
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => http.verify());

  // ── getProjects ────────────────────────────────────────────────────────────

  it('getProjects() calls GET /projects and returns project array', () => {
    const mockProjects: Project[] = [
      { id: 1, name: 'Alpha', owner: 'Alice', createdAt: '2026-01-01T00:00:00Z' },
    ];

    service.getProjects().subscribe((projects) => {
      expect(projects.length).toBe(1);
      expect(projects[0].name).toBe('Alpha');
    });

    const req = http.expectOne(`${base}/projects`);
    expect(req.request.method).toBe('GET');
    req.flush(mockProjects);
  });

  it('getProjects() propagates HTTP errors', () => {
    let errorCaught = false;
    service.getProjects().subscribe({
      error: () => (errorCaught = true),
    });

    http.expectOne(`${base}/projects`).flush('Server error', {
      status: 500,
      statusText: 'Internal Server Error',
    });

    expect(errorCaught).toBe(true);
  });

  // ── getProject ─────────────────────────────────────────────────────────────

  it('getProject(id) calls GET /projects/:id', () => {
    const mockProject: Project = { id: 7, name: 'Beta', owner: 'Bob', createdAt: '2026-01-01T00:00:00Z' };

    service.getProject(7).subscribe((p) => {
      expect(p.id).toBe(7);
      expect(p.name).toBe('Beta');
    });

    const req = http.expectOne(`${base}/projects/7`);
    expect(req.request.method).toBe('GET');
    req.flush(mockProject);
  });

  it('getProject returns 404 as an error', () => {
    let status = 0;
    service.getProject(999).subscribe({
      error: (e) => (status = e.status),
    });

    http.expectOne(`${base}/projects/999`).flush('Not Found', {
      status: 404,
      statusText: 'Not Found',
    });

    expect(status).toBe(404);
  });

  // ── createProject ──────────────────────────────────────────────────────────

  it('createProject() calls POST /projects with body', () => {
    const mockProject: Project = { id: 3, name: 'Gamma', owner: 'Carol', createdAt: '2026-01-01T00:00:00Z' };

    service.createProject({ name: 'Gamma', owner: 'Carol' }).subscribe((p) => {
      expect(p.id).toBe(3);
    });

    const req = http.expectOne(`${base}/projects`);
    expect(req.request.method).toBe('POST');
    expect(req.request.body).toEqual({ name: 'Gamma', owner: 'Carol' });
    req.flush(mockProject);
  });

  // ── deleteProject ──────────────────────────────────────────────────────────

  it('deleteProject() calls DELETE /projects/:id', () => {
    service.deleteProject(5).subscribe();
    const req = http.expectOne(`${base}/projects/5`);
    expect(req.request.method).toBe('DELETE');
    req.flush(null);
  });

  // ── getOrdersByProject ─────────────────────────────────────────────────────

  it('getOrdersByProject() calls GET /projects/:id/orders', () => {
    const mockOrders: Order[] = [
      { id: 1, projectId: 2, description: 'Widget', amount: 10, status: 'Created', createdAt: '' },
    ];

    service.getOrdersByProject(2).subscribe((orders) => {
      expect(orders.length).toBe(1);
      expect(orders[0].description).toBe('Widget');
    });

    const req = http.expectOne(`${base}/projects/2/orders`);
    expect(req.request.method).toBe('GET');
    req.flush(mockOrders);
  });

  // ── createOrder ────────────────────────────────────────────────────────────

  it('createOrder() calls POST /orders with body', () => {
    service
      .createOrder({ projectId: 1, description: 'Gadget', amount: 49.99 })
      .subscribe((resp) => {
        expect(resp.id).toBe(42);
        expect(resp.status).toBe('Created');
      });

    const req = http.expectOne(`${base}/orders`);
    expect(req.request.method).toBe('POST');
    expect(req.request.body).toEqual({ projectId: 1, description: 'Gadget', amount: 49.99 });
    req.flush({ id: 42, status: 'Created' });
  });

  // ── getNotifications ───────────────────────────────────────────────────────

  it('getNotifications() calls GET /notifications', () => {
    const mockNotifications: Notification[] = [
      {
        id: 'notif-1', order_id: '1', project_id: '1',
        message: 'Order created', status: 'sent', created_at: '',
      },
    ];

    service.getNotifications().subscribe((n) => {
      expect(n.length).toBe(1);
      expect(n[0].id).toBe('notif-1');
    });

    const req = http.expectOne(`${base}/notifications`);
    expect(req.request.method).toBe('GET');
    req.flush(mockNotifications);
  });
});
