import { Injectable } from '@angular/core';
import * as signalR from '@microsoft/signalr';
import { Subject } from 'rxjs';
import { AuthService } from './auth.service';
import { infiniteRetry } from './signalr-retry';

export interface FieldLockInfo {
  docId: string;
  fieldId: string;
  userId: string;
  userName: string;
  expiresAt: string;
}

export interface LockAcquireResult {
  acquired: boolean;
  expiresAt: string | null;
  currentHolder: FieldLockInfo | null;
}

export interface FieldValueChanged {
  docId: string;
  fieldId: string;
  value: string;
  userId: string;
  userName: string;
}

/**
 * Manages the SignalR connection for collaborative document editing.
 *
 * Connects to the unified `/hubs/aura` endpoint (same hub as print/bank)
 * using a standard JWT bearer token.
 *
 * Listens for:
 * - `LockSnapshot`          — full lock state sent on connect.
 * - `FieldLocked`           — another user acquired a field lock.
 * - `FieldUnlocked`         — a field lock was released or expired.
 * - `FieldValueChanged`     — the lock holder changed a field value.
 * - `FieldsExpiredUnlocked` — one or more locks expired due to missed heartbeats.
 */
@Injectable({ providedIn: 'root' })
export class DocumentService {
  private hub: signalR.HubConnection | undefined;
  private heartbeatTimers = new Map<string, ReturnType<typeof setInterval>>();

  // ── Event subjects ──────────────────────────────────────────────────────
  private readonly _lockSnapshot$       = new Subject<FieldLockInfo[]>();
  private readonly _fieldLocked$        = new Subject<FieldLockInfo>();
  private readonly _fieldUnlocked$      = new Subject<{ docId: string; fieldId: string }>();
  private readonly _fieldValueChanged$  = new Subject<FieldValueChanged>();
  private readonly _fieldsExpired$      = new Subject<FieldLockInfo[]>();
  private readonly _connectionStatus$   = new Subject<string>();

  // ── Public observables ──────────────────────────────────────────────────
  public readonly lockSnapshot$      = this._lockSnapshot$.asObservable();
  public readonly fieldLocked$       = this._fieldLocked$.asObservable();
  public readonly fieldUnlocked$     = this._fieldUnlocked$.asObservable();
  public readonly fieldValueChanged$ = this._fieldValueChanged$.asObservable();
  public readonly fieldsExpired$     = this._fieldsExpired$.asObservable();
  public readonly connectionStatus$  = this._connectionStatus$.asObservable();

  constructor(private auth: AuthService) {}

  // ── Connection management ───────────────────────────────────────────────

  /**
   * Establishes a connection to `/hubs/aura` for the given tenant/user.
   * No-ops when already connected.
   */
  async connect(tenantId: string, userName: string): Promise<void> {
    if (this.hub && this.hub.state !== signalR.HubConnectionState.Disconnected) return;

    this._connectionStatus$.next('connecting');

    this.hub = new signalR.HubConnectionBuilder()
      .withUrl('/hubs/aura', {
        accessTokenFactory: () =>
          this.auth.getToken(tenantId, 'ui', userName).then(p => p.accessToken),
      })
      .withAutomaticReconnect(infiniteRetry)
      .withKeepAliveInterval(2_000_000_000)
      .withServerTimeout(24 * 60 * 60 * 1_000)
      .build();

    this.hub.onreconnecting(() => {
      // Stop all heartbeats immediately so the server does not receive
      // HeartbeatFieldLock calls via the new connection after the old
      // connection's OnDisconnectedAsync has already released the lock.
      // Without this, Heartbeat() on the server would re-create a "zombie"
      // lock entry with an empty ConnectionId that can never be released.
      this.stopAllHeartbeats();
      this._connectionStatus$.next('reconnecting');
    });
    this.hub.onreconnected(() => this._connectionStatus$.next('connected'));
    this.hub.onclose(()      => this._connectionStatus$.next('disconnected'));

    this.hub.on('LockSnapshot',          (locks: FieldLockInfo[])                    => this._lockSnapshot$.next(locks));
    this.hub.on('FieldLocked',           (info: FieldLockInfo)                        => this._fieldLocked$.next(info));
    this.hub.on('FieldUnlocked',         (info: { docId: string; fieldId: string })   => this._fieldUnlocked$.next(info));
    this.hub.on('FieldValueChanged',     (data: FieldValueChanged)                    => this._fieldValueChanged$.next(data));
    this.hub.on('FieldsExpiredUnlocked', (locks: FieldLockInfo[])                    => this._fieldsExpired$.next(locks));

    try {
      await this.hub.start();
      this._connectionStatus$.next('connected');
    } catch (err) {
      this._connectionStatus$.next('disconnected');
      throw err;
    }
  }

  async disconnect(): Promise<void> {
    this.stopAllHeartbeats();
    await this.hub?.stop();
    this._connectionStatus$.next('disconnected');
  }

  // ── Hub invocations ─────────────────────────────────────────────────────

  /** Tries to acquire exclusive edit rights on a document field. */
  async acquireFieldLock(docId: string, fieldId: string): Promise<LockAcquireResult> {
    if (!this.hub) throw new Error('DocumentService: not connected');
    return this.hub.invoke<LockAcquireResult>('AcquireFieldLock', docId, fieldId);
  }

  /** Releases a previously acquired field lock. */
  async releaseFieldLock(docId: string, fieldId: string): Promise<void> {
    if (!this.hub) return;
    this.stopHeartbeat(docId, fieldId);
    await this.hub.invoke('ReleaseFieldLock', docId, fieldId);
  }

  /**
   * Starts sending heartbeats every 8 s to keep the lock alive.
   * Must be called after a successful `acquireFieldLock`; lock TTL is 30 s.
   */
  startHeartbeat(docId: string, fieldId: string): void {
    this.stopHeartbeat(docId, fieldId); // clear any existing timer first
    const key   = `${docId}:${fieldId}`;
    const timer = setInterval(() => {
      this.hub?.invoke('HeartbeatFieldLock', docId, fieldId).catch(() => {});
    }, 8_000);
    this.heartbeatTimers.set(key, timer);
  }

  stopHeartbeat(docId: string, fieldId: string): void {
    const key   = `${docId}:${fieldId}`;
    const timer = this.heartbeatTimers.get(key);
    if (timer) {
      clearInterval(timer);
      this.heartbeatTimers.delete(key);
    }
  }

  /** Broadcasts a field value change in real-time. Caller must hold the lock. */
  async updateFieldValue(docId: string, fieldId: string, value: string): Promise<void> {
    if (!this.hub) return;
    await this.hub.invoke('UpdateFieldValue', docId, fieldId, value);
  }

  // ── Private helpers ─────────────────────────────────────────────────────

  private stopAllHeartbeats(): void {
    this.heartbeatTimers.forEach(timer => clearInterval(timer));
    this.heartbeatTimers.clear();
  }
}
