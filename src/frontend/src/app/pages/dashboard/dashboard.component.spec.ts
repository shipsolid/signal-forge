import { ComponentFixture, TestBed } from '@angular/core/testing';
import { RouterTestingModule } from '@angular/router/testing';
import { of, throwError } from 'rxjs';
import { DashboardComponent } from './dashboard.component';
import { ApiService, Project } from '../../services/api.service';

const MOCK_PROJECTS: Project[] = [
  { id: 1, name: 'Alpha', owner: 'Alice', createdAt: '2026-01-01T00:00:00Z' },
  { id: 2, name: 'Beta', owner: 'Bob', createdAt: '2026-01-02T00:00:00Z' },
];

describe('DashboardComponent', () => {
  let fixture: ComponentFixture<DashboardComponent>;
  let component: DashboardComponent;
  let apiSpy: jest.Mocked<ApiService>;

  beforeEach(async () => {
    apiSpy = {
      getProjects: jest.fn().mockReturnValue(of(MOCK_PROJECTS)),
      createProject: jest.fn(),
      getProject: jest.fn(),
      deleteProject: jest.fn(),
      getOrdersByProject: jest.fn(),
      createOrder: jest.fn(),
      getNotifications: jest.fn(),
    } as unknown as jest.Mocked<ApiService>;

    await TestBed.configureTestingModule({
      imports: [DashboardComponent, RouterTestingModule],
      providers: [{ provide: ApiService, useValue: apiSpy }],
    }).compileComponents();

    fixture = TestBed.createComponent(DashboardComponent);
    component = fixture.componentInstance;
    fixture.detectChanges();
  });

  // ── Initial load ──────────────────────────────────────────────────────────

  it('calls getProjects on init', () => {
    expect(apiSpy.getProjects).toHaveBeenCalledTimes(1);
  });

  it('renders project list after successful load', () => {
    const items: NodeListOf<HTMLLIElement> = fixture.nativeElement.querySelectorAll('ul li');
    expect(items.length).toBe(2);
    expect(items[0].textContent).toContain('Alpha');
    expect(items[1].textContent).toContain('Beta');
  });

  it('clears loading flag after successful load', () => {
    expect(component.loading).toBe(false);
  });

  it('does not show error on successful load', () => {
    const errorEl = fixture.nativeElement.querySelector('.error');
    expect(errorEl).toBeNull();
  });

  it('shows empty-state message when no projects returned', () => {
    apiSpy.getProjects.mockReturnValue(of([]));
    component.load();
    fixture.detectChanges();

    const el: HTMLElement = fixture.nativeElement;
    expect(el.textContent).toContain('No projects yet');
  });

  // ── Error handling ────────────────────────────────────────────────────────

  it('shows error message when getProjects fails', () => {
    apiSpy.getProjects.mockReturnValue(throwError(() => new Error('Network error')));
    component.load();
    fixture.detectChanges();

    const el: HTMLElement = fixture.nativeElement;
    expect(component.error).toBe('Network error');
    expect(el.textContent).toContain('Network error');
  });

  it('clears loading flag even on error', () => {
    apiSpy.getProjects.mockReturnValue(throwError(() => new Error('timeout')));
    component.load();
    expect(component.loading).toBe(false);
  });

  // ── createProject ─────────────────────────────────────────────────────────

  it('createProject() calls api.createProject with name and owner', () => {
    const newProject: Project = { id: 3, name: 'Gamma', owner: 'Carol', createdAt: '' };
    apiSpy.createProject.mockReturnValue(of(newProject));
    apiSpy.getProjects.mockReturnValue(of([...MOCK_PROJECTS, newProject]));

    component.newName = 'Gamma';
    component.newOwner = 'Carol';
    component.createProject();

    expect(apiSpy.createProject).toHaveBeenCalledWith({
      name: 'Gamma',
      owner: 'Carol',
    });
  });

  it('createProject() reloads project list on success', () => {
    const newProject: Project = { id: 3, name: 'Gamma', owner: 'Carol', createdAt: '' };
    apiSpy.createProject.mockReturnValue(of(newProject));
    apiSpy.getProjects.mockReturnValue(of([...MOCK_PROJECTS, newProject]));

    component.newName = 'Gamma';
    component.newOwner = 'Carol';
    component.createProject();
    fixture.detectChanges();

    expect(apiSpy.getProjects).toHaveBeenCalledTimes(2);
  });
});
