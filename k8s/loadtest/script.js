import http from 'k6/http';
import { check, sleep } from 'k6';

export const options = {
  stages: [
    { duration: '30s', target: 5 },
    { duration: '2m',  target: 20 },
    { duration: '30s', target: 0 },
  ],
};

const BASE = 'http://gateway-api.otel-lab.svc.cluster.local:5000';

export default function () {
  // Happy path: create project → create order → check notifications
  const project = http.post(`${BASE}/api/projects`, JSON.stringify({
    name: `Project-${Date.now()}`,
    owner: 'k6-user',
  }), { headers: { 'Content-Type': 'application/json' } });
  check(project, { 'project created': (r) => r.status === 201 });

  const projectId = JSON.parse(project.body).id;

  const order = http.post(`${BASE}/api/orders`, JSON.stringify({
    projectId: projectId,
    description: 'Load test order',
    amount: Math.random() * 10000,
  }), { headers: { 'Content-Type': 'application/json' } });
  check(order, { 'order created': (r) => r.status === 201 });

  sleep(1); // Let async processing happen

  const notifications = http.get(`${BASE}/api/notifications`);
  check(notifications, { 'notifications ok': (r) => r.status === 200 });

  // Read paths
  http.get(`${BASE}/api/projects`);
  http.get(`${BASE}/api/projects/${projectId}`);
  http.get(`${BASE}/api/projects/${projectId}/orders`);

  // Edge cases (10% slow, 5% error)
  if (Math.random() < 0.1) {
    http.get(`${BASE}/api/slow`);
  }
  if (Math.random() < 0.05) {
    http.get(`${BASE}/api/error`);
  }

  sleep(0.5);
}
