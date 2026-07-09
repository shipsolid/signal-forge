import { Component, OnInit } from '@angular/core';
import { CommonModule } from '@angular/common';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { forkJoin } from 'rxjs';
import { ApiService, Order, Project } from '../../services/api.service';

@Component({
  selector: 'app-project-detail',
  standalone: true,
  imports: [CommonModule, RouterLink],
  template: `
    <a routerLink="/">← Back</a>
    <div *ngIf="loading">Loading...</div>
    <div *ngIf="error" class="error">{{ error }}</div>
    <ng-container *ngIf="project">
      <h1>{{ project.name }}</h1>
      <p>Owner: {{ project.owner }} | Created: {{ project.createdAt | date: 'medium' }}</p>
      <h2>Orders</h2>
      <ul *ngIf="orders.length">
        <li *ngFor="let o of orders">
          #{{ o.id }} — {{ o.description }} (\${{ o.amount | number: '1.2-2' }}) —
          <strong>{{ o.status }}</strong>
        </li>
      </ul>
      <p *ngIf="!orders.length">No orders for this project.</p>
      <a [routerLink]="['/orders/new']" [queryParams]="{ projectId: project.id }">+ Create Order</a>
    </ng-container>
  `,
})
export class ProjectDetailComponent implements OnInit {
  project: Project | null = null;
  orders: Order[] = [];
  loading = false;
  error = '';

  constructor(
    private route: ActivatedRoute,
    private api: ApiService,
  ) {}

  ngOnInit(): void {
    const id = Number(this.route.snapshot.paramMap.get('id'));
    this.loading = true;
    forkJoin({
      project: this.api.getProject(id),
      orders: this.api.getOrdersByProject(id),
    }).subscribe({
      next: ({ project, orders }) => {
        this.project = project;
        this.orders = orders;
        this.loading = false;
      },
      error: (err) => {
        this.error = err.message;
        this.loading = false;
      },
    });
  }
}
