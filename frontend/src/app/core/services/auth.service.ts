import { Injectable, signal } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { firstValueFrom } from 'rxjs';

export interface TokenPair {
  accessToken: string;
  refreshToken: string;
  expiresAt: number; // unix seconds
}

@Injectable({ providedIn: 'root' })
export class AuthService {
  private tokens = new Map<string, TokenPair>(); // key = `${tenantId}_${clientType}`

  constructor(private http: HttpClient) {}

  async getToken(tenantId: string, clientType: 'ui' | 'smarthub' | 'bank', userName?: string): Promise<TokenPair> {
    const key = `${tenantId}_${clientType}`;
    const existing = this.tokens.get(key);

    // Return cached if still valid (more than 60s remaining)
    if (existing && existing.expiresAt - Date.now() / 1000 > 60) {
      return existing;
    }

    // Refresh if we have refresh token
    if (existing?.refreshToken) {
      try {
        const refreshed = await firstValueFrom(
          this.http.post<TokenPair>('/api/auth/refresh', { refreshToken: existing.refreshToken })
        );
        this.tokens.set(key, refreshed);
        return refreshed;
      } catch {
        // Refresh failed, get new token
      }
    }

    // Get new token
    const pair = await firstValueFrom(
      this.http.post<TokenPair>('/api/auth/token', { tenantId, clientType, userName })
    );
    this.tokens.set(key, pair);
    return pair;
  }

  getAccessToken(tenantId: string, clientType: 'ui' | 'smarthub' | 'bank'): string | undefined {
    return this.tokens.get(`${tenantId}_${clientType}`)?.accessToken;
  }

  clearToken(tenantId: string, clientType: 'ui' | 'smarthub' | 'bank'): void {
    this.tokens.delete(`${tenantId}_${clientType}`);
  }
}
