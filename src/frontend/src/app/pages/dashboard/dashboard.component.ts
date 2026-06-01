import { Component, OnInit } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { ApiService, Project } from '../../services/api.service';

@Component({
  selector: 'app-dashboard',
  standalone: true,
  imports: [CommonModule, FormsModule, RouterLink],
  template: `
    <h1>Projects Dashboard</h1>
    <div *ngIf="loading">Loading...</div>
    <div *ngIf="error" class="error">{{ error }}</div>
    <ul *ngIf="projects.length">
      <li *ngFor="let p of projects">
        <a [routerLink]="['/projects', p.id]">{{ p.name }}</a>
        — Owner: {{ p.owner }}
        <small>{{ p.createdAt | date:'short' }}</small>
      </li>
    </ul>
    <p *ngIf="!loading && projects.length === 0">No projects yet.</p>
    <form (ngSubmit)="createProject()" #f="ngForm">
      <input [(ngModel)]="newName" name="name" placeholder="Project name" required />
      <input [(ngModel)]="newOwner" name="owner" placeholder="Owner" required />
      <button type="submit">Create Project</button>
    </form>
  `,
})
export class DashboardComponent implements OnInit {
  projects: Project[] = [];
  loading = false;
  error = '';
  newName = '';
  newOwner = '';

  constructor(private api: ApiService) {}

  ngOnInit(): void {
    this.load();
  }

  load(): void {
    this.loading = true;
    this.api.getProjects().subscribe({
      next: (data) => { this.projects = data; this.loading = false; },
      error: (err) => { this.error = err.message; this.loading = false; },
    });
  }

  createProject(): void {
    if (!this.newName || !this.newOwner) return;
    this.api.createProject({ name: this.newName, owner: this.newOwner }).subscribe({
      next: () => { this.newName = ''; this.newOwner = ''; this.load(); },
      error: (err) => { this.error = err.message; },
    });
  }
}
