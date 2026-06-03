import { ComponentFixture, TestBed } from '@angular/core/testing';
import { RouterTestingModule } from '@angular/router/testing';
import { ActivatedRoute } from '@angular/router';
import { of, throwError } from 'rxjs';
import { ProjectDetailComponent } from './project-detail.component';
import { ApiService, Order, Project } from '../../services/api.service';

const MOCK_PROJECT: Project = {
  id: 3,
  name: 'Infrastructure Upgrade',
  owner: 'Alice',
  createdAt: '2026-01-10T00:00:00Z',
};

const MOCK_ORDERS: Order[] = [
  { id: 100, projectId: 3, description: 'Server rack', amount: 4500, status: 'Created', createdAt: '2026-01-11T00:00:00Z' },
  { id: 101, projectId: 3, description: 'Network switch', amount: 1200, status: 'Created', createdAt: '2026-01-12T00:00:00Z' },
];

describe('ProjectDetailComponent', () => {
  let fixture: ComponentFixture<ProjectDetailComponent>;
  let component: ProjectDetailComponent;
  let apiSpy: jest.Mocked<ApiService>;

  const mockRoute = {
    snapshot: {
      paramMap: { get: jest.fn().mockReturnValue('3') },
    },
  };

  beforeEach(async () => {
    apiSpy = {
      getProject: jest.fn().mockReturnValue(of(MOCK_PROJECT)),
      getOrdersByProject: jest.fn().mockReturnValue(of(MOCK_ORDERS)),
    } as unknown as jest.Mocked<ApiService>;

    await TestBed.configureTestingModule({
      imports: [ProjectDetailComponent, RouterTestingModule],
      providers: [
        { provide: ApiService, useValue: apiSpy },
        { provide: ActivatedRoute, useValue: mockRoute },
      ],
    }).compileComponents();

    fixture = TestBed.createComponent(ProjectDetailComponent);
    component = fixture.componentInstance;
    fixture.detectChanges();
  });

  // ── Data fetching ──────────────────────────────────────────────────────────

  it('fetches project and orders in parallel using the route id', () => {
    expect(apiSpy.getProject).toHaveBeenCalledWith(3);
    expect(apiSpy.getOrdersByProject).toHaveBeenCalledWith(3);
  });

  it('clears loading flag after successful fetch', () => {
    expect(component.loading).toBe(false);
  });

  // ── Rendering ─────────────────────────────────────────────────────────────

  it('renders the project name', () => {
    expect(fixture.nativeElement.textContent).toContain('Infrastructure Upgrade');
  });

  it('renders the project owner', () => {
    expect(fixture.nativeElement.textContent).toContain('Alice');
  });

  it('renders all orders', () => {
    const items: NodeListOf<HTMLElement> = fixture.nativeElement.querySelectorAll('ul li');
    expect(items.length).toBe(2);
    expect(items[0].textContent).toContain('Server rack');
    expect(items[1].textContent).toContain('Network switch');
  });

  it('shows empty-state message when the project has no orders', () => {
    apiSpy.getProject.mockReturnValue(of(MOCK_PROJECT));
    apiSpy.getOrdersByProject.mockReturnValue(of([]));

    fixture = TestBed.createComponent(ProjectDetailComponent);
    component = fixture.componentInstance;
    fixture.detectChanges();

    expect(fixture.nativeElement.textContent).toContain('No orders for this project');
  });

  // ── Error handling ────────────────────────────────────────────────────────

  it('shows error message when the forkJoin fails', () => {
    apiSpy.getProject.mockReturnValue(
      throwError(() => new Error('Not found'))
    );
    apiSpy.getOrdersByProject.mockReturnValue(of([]));

    fixture = TestBed.createComponent(ProjectDetailComponent);
    component = fixture.componentInstance;
    fixture.detectChanges();

    expect(component.error).toBe('Not found');
    expect(fixture.nativeElement.textContent).toContain('Not found');
  });

  it('clears loading flag even on error', () => {
    apiSpy.getProject.mockReturnValue(
      throwError(() => new Error('timeout'))
    );
    apiSpy.getOrdersByProject.mockReturnValue(of([]));

    fixture = TestBed.createComponent(ProjectDetailComponent);
    component = fixture.componentInstance;
    fixture.detectChanges();

    expect(component.loading).toBe(false);
  });
});
