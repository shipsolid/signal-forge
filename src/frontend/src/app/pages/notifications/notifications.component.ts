import { Component, OnInit } from '@angular/core';
import { CommonModule } from '@angular/common';
import { ApiService, Notification } from '../../services/api.service';

@Component({
  selector: 'app-notifications',
  standalone: true,
  imports: [CommonModule],
  template: `
    <h1>Notifications</h1>
    <div *ngIf="loading">Loading...</div>
    <div *ngIf="error" class="error">{{ error }}</div>
    <ul *ngIf="notifications.length">
      <li *ngFor="let n of notifications">
        <strong>{{ n.message }}</strong>
        <span class="status">{{ n.status }}</span>
        <small>{{ n.created_at }}</small>
        <small *ngIf="n.trace_id">TraceID: {{ n.trace_id }}</small>
      </li>
    </ul>
    <p *ngIf="!loading && notifications.length === 0">No notifications yet.</p>
    <button (click)="load()">Refresh</button>
  `,
})
export class NotificationsComponent implements OnInit {
  notifications: Notification[] = [];
  loading = false;
  error = '';

  constructor(private api: ApiService) {}

  ngOnInit(): void {
    this.load();
  }

  load(): void {
    this.loading = true;
    this.api.getNotifications().subscribe({
      next: (data) => {
        this.notifications = data;
        this.loading = false;
      },
      error: (err) => {
        this.error = err.message;
        this.loading = false;
      },
    });
  }
}
