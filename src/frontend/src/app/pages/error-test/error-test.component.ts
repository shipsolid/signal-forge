import { Component } from '@angular/core';
import { CommonModule } from '@angular/common';
import { ApiService } from '../../services/api.service';

@Component({
  selector: 'app-error-test',
  standalone: true,
  imports: [CommonModule],
  template: `
    <h1>Error Test Page</h1>
    <p>Use this page to trigger backend errors and frontend exceptions for OTel validation.</p>

    <button (click)="triggerBackendError()">Trigger Backend Error (500)</button>
    <button (click)="triggerFrontendError()">Trigger JS Exception</button>
    <button (click)="triggerUnhandledRejection()">Trigger Unhandled Promise Rejection</button>

    <div *ngIf="backendResult" class="result">
      Backend result: <pre>{{ backendResult }}</pre>
    </div>
    <div *ngIf="backendError" class="error">
      Backend error captured: {{ backendError }}
    </div>
    <div *ngIf="frontendMsg" class="info">{{ frontendMsg }}</div>
  `,
})
export class ErrorTestComponent {
  backendResult = '';
  backendError = '';
  frontendMsg = '';

  constructor(private api: ApiService) {}

  triggerBackendError(): void {
    this.backendError = '';
    this.backendResult = '';
    this.api.triggerError().subscribe({
      next: (r) => { this.backendResult = JSON.stringify(r); },
      error: (err) => { this.backendError = `${err.status}: ${err.message}`; },
    });
  }

  triggerFrontendError(): void {
    // Faro should capture this as a JS error with stack trace
    this.frontendMsg = 'JS exception thrown — check Faro / Grafana Cloud Frontend';
    throw new Error('Intentional frontend error for Faro validation');
  }

  triggerUnhandledRejection(): void {
    this.frontendMsg = 'Unhandled promise rejection triggered — check Faro';
    Promise.reject(new Error('Intentional unhandled promise rejection for Faro'));
  }
}
