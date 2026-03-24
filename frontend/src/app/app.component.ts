import { Component } from '@angular/core';
import { RouterOutlet, RouterLink, RouterLinkActive } from '@angular/router';

@Component({
  selector: 'app-root',
  standalone: true,
  imports: [RouterOutlet, RouterLink, RouterLinkActive],
  template: `
<div class="app-shell">
  <nav class="nav-bar">
    <div class="nav-brand">POC AURA</div>
    <div class="nav-links">
      <a routerLink="/chat" routerLinkActive="active">Chat</a>
      <a routerLink="/multi-tenant" routerLinkActive="active">Multi-Tenant Print</a>
      <a routerLink="/transaction" routerLinkActive="active">Bank Transaction</a>
      <a routerLink="/collab-doc" routerLinkActive="active">Collab Document</a>
    </div>
  </nav>
  <main>
    <router-outlet />
  </main>
</div>
  `,
  styles: [`
    .app-shell { display: flex; flex-direction: column; min-height: 100vh; }
    .nav-bar {
      display: flex; align-items: center; gap: 24px;
      padding: 0 24px; height: 50px;
      background: #1a1a2e; color: white;
      position: sticky; top: 0; z-index: 100;
      border-bottom: 2px solid #2d2d4e;
    }
    .nav-brand { font-weight: 800; font-size: 15px; letter-spacing: 2px; color: #7eb8f7; }
    .nav-links { display: flex; gap: 2px; }
    .nav-links a {
      color: #bbb; text-decoration: none; padding: 6px 14px;
      border-radius: 4px; font-size: 13px; transition: all 0.15s;
    }
    .nav-links a:hover { background: rgba(255,255,255,0.08); color: white; }
    .nav-links a.active { background: #007bff; color: white; }
    main { flex: 1; }
  `],
})
export class AppComponent {}
