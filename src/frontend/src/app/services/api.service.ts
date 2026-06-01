import { HttpClient } from '@angular/common/http';
import { Injectable } from '@angular/core';
import { Observable } from 'rxjs';
import { environment } from '../../environments/environment';

export interface Project {
  id: number;
  name: string;
  owner: string;
  createdAt: string;
}

export interface Order {
  id: number;
  projectId: number;
  description: string;
  amount: number;
  status: string;
  createdAt: string;
}

export interface Notification {
  id: string;
  order_id: string;
  project_id: string;
  message: string;
  status: string;
  created_at: string;
  /** traceId links the notification to the originating order.created trace. */
  trace_id?: string;
}

/**
 * ApiService is the single HTTP boundary between the Angular SPA and gateway-api.
 *
 * All calls go to `environment.apiBaseUrl` (default: same origin via the
 * Traefik ingress in k3d, or http://localhost:8080 in local dev).
 *
 * Faro's `FetchInstrumentation` wraps every fetch automatically, injecting a
 * W3C `traceparent` header so browser spans are linked to backend server spans
 * in Grafana Tempo.  No manual header management is required here.
 */
@Injectable({ providedIn: 'root' })
export class ApiService {
  private base = environment.apiBaseUrl;

  constructor(private http: HttpClient) {}

  /** Returns all projects owned by this gateway instance. */
  getProjects(): Observable<Project[]> {
    return this.http.get<Project[]>(`${this.base}/projects`);
  }

  /** Returns a single project by its numeric ID. Throws 404 if not found. */
  getProject(id: number): Observable<Project> {
    return this.http.get<Project>(`${this.base}/projects/${id}`);
  }

  /** Creates a project. `name` and `owner` are required non-empty strings. */
  createProject(data: { name: string; owner: string }): Observable<Project> {
    return this.http.post<Project>(`${this.base}/projects`, data);
  }

  /** Deletes a project by ID. Returns 404 if the project does not exist. */
  deleteProject(id: number): Observable<void> {
    return this.http.delete<void>(`${this.base}/projects/${id}`);
  }

  /**
   * Returns all orders for a project, proxied via gateway-api's gRPC
   * server-streaming call to order-api.  The gateway buffers the stream
   * and returns the full array as a single JSON response.
   */
  getOrdersByProject(projectId: number): Observable<Order[]> {
    return this.http.get<Order[]>(`${this.base}/projects/${projectId}/orders`);
  }

  /**
   * Creates an order.  Validated by gateway-api (projectId > 0, amount in
   * (0, 999_999.99], description 1–500 chars) before forwarding to order-api
   * via gRPC.  Returns 400 on validation failure, 502 on gRPC/downstream error.
   */
  createOrder(data: { projectId: number; description: string; amount: number }): Observable<{ id: number; status: string }> {
    return this.http.post<{ id: number; status: string }>(`${this.base}/orders`, data);
  }

  /**
   * Returns the most recent 100 notifications proxied from notification-svc.
   * Returns 502 if notification-svc is unavailable.
   */
  getNotifications(): Observable<Notification[]> {
    return this.http.get<Notification[]>(`${this.base}/notifications`);
  }

  /**
   * Triggers the intentional /api/error endpoint on gateway-api.
   * Used to validate that unhandled exception spans appear in Jaeger with
   * exception.type, exception.message, and exception.stacktrace attributes.
   */
  triggerError(): Observable<unknown> {
    return this.http.get(`${this.base}/error`);
  }
}
