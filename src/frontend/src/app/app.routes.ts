import { Routes } from '@angular/router';

export const routes: Routes = [
  {
    path: '',
    loadComponent: () =>
      import('./pages/dashboard/dashboard.component').then((m) => m.DashboardComponent),
  },
  {
    path: 'projects/:id',
    loadComponent: () =>
      import('./pages/project-detail/project-detail.component').then(
        (m) => m.ProjectDetailComponent,
      ),
  },
  {
    path: 'orders/new',
    loadComponent: () =>
      import('./pages/create-order/create-order.component').then((m) => m.CreateOrderComponent),
  },
  {
    path: 'notifications',
    loadComponent: () =>
      import('./pages/notifications/notifications.component').then((m) => m.NotificationsComponent),
  },
  {
    path: 'error-test',
    loadComponent: () =>
      import('./pages/error-test/error-test.component').then((m) => m.ErrorTestComponent),
  },
];
