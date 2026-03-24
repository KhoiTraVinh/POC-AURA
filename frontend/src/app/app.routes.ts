import { Routes } from '@angular/router';

export const routes: Routes = [
  { path: '', redirectTo: 'chat', pathMatch: 'full' },
  {
    path: 'chat',
    loadComponent: () =>
      import('./features/chat/chat.component').then((m) => m.ChatComponent),
  },
  {
    path: 'multi-tenant',
    loadComponent: () =>
      import('./features/multi-tenant/multi-tenant.component').then((m) => m.MultiTenantComponent),
  },
  {
    path: 'transaction',
    loadComponent: () =>
      import('./features/transaction-queue/transaction-queue.component').then((m) => m.TransactionQueueComponent),
  },
  {
    path: 'collab-doc',
    loadComponent: () =>
      import('./features/collaborative-doc/collaborative-doc.component').then((m) => m.CollaborativeDocComponent),
  },
  { path: '**', redirectTo: 'chat' },
];
