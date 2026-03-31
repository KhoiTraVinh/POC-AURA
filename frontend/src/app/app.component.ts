import { Component, inject } from '@angular/core';
import { RouterOutlet, RouterLink, RouterLinkActive, Router } from '@angular/router';
import { SessionService } from './core/services/session.service';

@Component({
  selector: 'app-root',
  standalone: true,
  imports: [RouterOutlet, RouterLink, RouterLinkActive],
  template: `
<div class="app-shell">
  <nav class="nav-bar">
    <div class="nav-brand">POC AURA</div>

    @if (session.isLoggedIn()) {
      <div class="nav-links">
        <a routerLink="/print"       routerLinkActive="active">🖨 Print</a>
        <a routerLink="/transaction" routerLinkActive="active">🏦 Bank Transaction</a>
        <a routerLink="/collab-doc"  routerLinkActive="active">📄 Collab Document</a>
      </div>

      <div class="nav-user">
        <span class="user-name">{{ session.session()!.name }}</span>
        <span class="tenant-badge">{{ session.session()!.tenantId }}</span>
        <button class="btn-logout" (click)="logout()">Thoát</button>
      </div>
    }
  </nav>
  <main>
    <router-outlet />
  </main>
</div>
  `,
  styles: [`
    .app-shell { display: flex; flex-direction: column; min-height: 100vh; }
    .nav-bar {
      display: flex; align-items: center; gap: 16px;
      padding: 0 20px; height: 50px;
      background: #1a1a2e; color: white;
      position: sticky; top: 0; z-index: 100;
      border-bottom: 2px solid #2d2d4e;
    }
    .nav-brand { font-weight: 800; font-size: 15px; letter-spacing: 2px; color: #7eb8f7; flex-shrink: 0; }
    .nav-links { display: flex; gap: 2px; flex: 1; }
    .nav-links a {
      color: #bbb; text-decoration: none; padding: 6px 14px;
      border-radius: 4px; font-size: 13px; transition: all 0.15s;
    }
    .nav-links a:hover { background: rgba(255,255,255,0.08); color: white; }
    .nav-links a.active { background: #007bff; color: white; }
    .nav-user { display: flex; align-items: center; gap: 8px; margin-left: auto; }
    .user-name { font-size: 13px; color: #eee; font-weight: 500; }
    .tenant-badge {
      font-size: 11px; padding: 2px 8px; border-radius: 10px;
      background: #0f3460; color: #7eb8f7; font-weight: 600; border: 1px solid #1e5080;
    }
    .btn-logout {
      padding: 4px 12px; background: transparent; border: 1px solid #555;
      color: #aaa; border-radius: 4px; cursor: pointer; font-size: 12px;
      transition: all .15s;
    }
    .btn-logout:hover { border-color: #dc3545; color: #ff6b6b; }
    main { flex: 1; }
  `],
})
export class AppComponent {
  session = inject(SessionService);
  private router = inject(Router);

  logout() {
    this.session.logout();
    this.router.navigate(['/login']);
  }
}
