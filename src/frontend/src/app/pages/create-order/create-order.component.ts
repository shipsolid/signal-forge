import { Component, OnInit } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { ApiService } from '../../services/api.service';

@Component({
  selector: 'app-create-order',
  standalone: true,
  imports: [CommonModule, FormsModule, RouterLink],
  template: `
    <a routerLink="/">← Back</a>
    <h1>Create Order</h1>
    <div *ngIf="success" class="success">Order created! ID: {{ createdId }}</div>
    <div *ngIf="error" class="error">{{ error }}</div>
    <form (ngSubmit)="submit()" #f="ngForm">
      <label>
        Project ID:
        <input type="number" [(ngModel)]="projectId" name="projectId" required />
      </label>
      <label>
        Description:
        <input [(ngModel)]="description" name="description" placeholder="What are you ordering?" required />
      </label>
      <label>
        Amount ($):
        <input type="number" step="0.01" [(ngModel)]="amount" name="amount" required />
      </label>
      <button type="submit" [disabled]="submitting">
        {{ submitting ? 'Creating...' : 'Create Order' }}
      </button>
    </form>
  `,
})
export class CreateOrderComponent implements OnInit {
  projectId = 0;
  description = '';
  amount = 0;
  submitting = false;
  success = false;
  createdId: number | null = null;
  error = '';

  constructor(
    private api: ApiService,
    private route: ActivatedRoute,
    private router: Router
  ) {}

  ngOnInit(): void {
    const pid = this.route.snapshot.queryParamMap.get('projectId');
    if (pid !== null) {
      const parsed = parseInt(pid, 10);
      if (!isNaN(parsed) && parsed > 0) this.projectId = parsed;
    }
  }

  submit(): void {
    this.submitting = true;
    this.error = '';
    this.api
      .createOrder({ projectId: this.projectId, description: this.description, amount: this.amount })
      .subscribe({
        next: (res) => {
          this.createdId = res.id;
          this.success = true;
          this.submitting = false;
          setTimeout(() => this.router.navigate(['/projects', this.projectId]), 1500);
        },
        error: (err) => { this.error = err.message; this.submitting = false; },
      });
  }
}
