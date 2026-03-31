import { Routes } from '@angular/router';
import { inject } from '@angular/core';
import { Router } from '@angular/router';
import { SessionService } from './core/services/session.service';

const authGuard = () => {
  const session = inject(SessionService);
  if (session.isLoggedIn()) return true;
  return inject(Router).createUrlTree(['/login']);
};

export const routes: Routes = [
  { path: '', redirectTo: 'print', pathMatch: 'full' },
  {
    path: 'login',
    loadComponent: () =>
      import('./features/login/login.component').then((m) => m.LoginComponent),
  },
  {
    path: 'print',
    canActivate: [authGuard],
    loadComponent: () =>
      import('./features/multi-tenant/multi-tenant.component').then((m) => m.MultiTenantComponent),
  },
  {
    path: 'transaction',
    canActivate: [authGuard],
    loadComponent: () =>
      import('./features/transaction-queue/transaction-queue.component').then((m) => m.TransactionQueueComponent),
  },
  {
    path: 'collab-doc',
    canActivate: [authGuard],
    loadComponent: () =>
      import('./features/collaborative-doc/collaborative-doc.component').then((m) => m.CollaborativeDocComponent),
  },
  {
    path: 'batch-import',
    canActivate: [authGuard],
    loadComponent: () =>
      import('./features/batch-import/batch-import.component').then((m) => m.BatchImportComponent),
  },
  { path: '**', redirectTo: 'login' },
];
