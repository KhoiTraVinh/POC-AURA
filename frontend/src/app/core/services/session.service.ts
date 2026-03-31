import { Injectable, signal, computed } from '@angular/core';

export interface UserSession {
  name: string;
  tenantId: string;
}

@Injectable({ providedIn: 'root' })
export class SessionService {
  private readonly TENANTS = ['TenantA', 'TenantB', 'TenantC'];
  private readonly KEY = 'aura_session';

  private _session = signal<UserSession | null>(null);

  readonly session = this._session.asReadonly();
  readonly isLoggedIn = computed(() => this._session() !== null);

  constructor() {
    try {
      const stored = sessionStorage.getItem(this.KEY);
      if (stored) this._session.set(JSON.parse(stored));
    } catch { /* ignore */ }
  }

  login(name: string): UserSession {
    const tenantId = this.TENANTS[Math.floor(Math.random() * this.TENANTS.length)];
    const s: UserSession = { name: name.trim(), tenantId };
    this._session.set(s);
    sessionStorage.setItem(this.KEY, JSON.stringify(s));
    return s;
  }

  logout(): void {
    this._session.set(null);
    sessionStorage.removeItem(this.KEY);
  }
}
