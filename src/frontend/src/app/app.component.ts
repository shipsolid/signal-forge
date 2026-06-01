import { Component } from '@angular/core';
import { RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';

@Component({
  selector: 'app-root',
  standalone: true,
  imports: [RouterOutlet, RouterLink, RouterLinkActive],
  template: `
    <nav>
      <a routerLink="/" routerLinkActive="active" [routerLinkActiveOptions]="{ exact: true }">Dashboard</a>
      <a routerLink="/orders/new" routerLinkActive="active">New Order</a>
      <a routerLink="/notifications" routerLinkActive="active">Notifications</a>
      <a routerLink="/error-test" routerLinkActive="active">Error Test</a>
    </nav>
    <main>
      <router-outlet />
    </main>
  `,
  styles: [`
    nav { display: flex; gap: 1rem; padding: 1rem; background: #1a1a2e; }
    nav a { color: #e0e0e0; text-decoration: none; }
    nav a.active { color: #00d4ff; font-weight: bold; }
    main { padding: 1rem; }
  `],
})
export class AppComponent {}
