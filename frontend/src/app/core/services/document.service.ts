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
  // Tracks fields this client has acquired { key → { docId, fieldId } }.
  // Used to re-acquire locks transparently after a reconnect.
  private claimedFields = new Map<string, { docId: string; fieldId: string }>();

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
    this.hub.onreconnected(async () => {
      this._connectionStatus$.next('connected');
      // The server assigns a new connectionId on reconnect. The old connection's
      // OnDisconnectedAsync will soon fire and call ReleaseAllByConnection(oldId),
      // which releases any lock still registered under the old connectionId.
      // Re-acquire all claimed fields now so the lock is updated to the new connectionId
      // before that cleanup fires — preventing a phantom lock/unlock cycle on all clients.
      await this.reacquireClaimedFields();
    });
    this.hub.onclose(() => this._connectionStatus$.next('disconnected'));

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
    this.claimedFields.clear();
    await this.hub?.stop();
    this._connectionStatus$.next('disconnected');
  }

  // ── Hub invocations ─────────────────────────────────────────────────────

  /** Tries to acquire exclusive edit rights on a document field. */
  async acquireFieldLock(docId: string, fieldId: string): Promise<LockAcquireResult> {
    if (!this.hub) throw new Error('DocumentService: not connected');
    const result = await this.hub.invoke<LockAcquireResult>('AcquireFieldLock', docId, fieldId);
    if (result.acquired) {
      this.claimedFields.set(`${docId}:${fieldId}`, { docId, fieldId });
    }
    return result;
  }

  /** Releases a previously acquired field lock. */
  async releaseFieldLock(docId: string, fieldId: string): Promise<void> {
    if (!this.hub) return;
    this.stopHeartbeat(docId, fieldId);
    this.claimedFields.delete(`${docId}:${fieldId}`);
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

  /**
   * Called after every reconnect. Re-invokes AcquireFieldLock for each field
   * this client held before the disconnect, then restarts the heartbeat timer.
   *
   * Why this is needed: SignalR assigns a NEW connectionId on reconnect. The old
   * connection's OnDisconnectedAsync will fire shortly after and call
   * ReleaseAllByConnection(oldConnectionId). If we don't re-acquire first, that
   * cleanup releases the lock (connectionId still points to the old connection)
   * and broadcasts FieldUnlocked to every client — causing the spam lock/unlock
   * loop visible in the UI.
   *
   * If another user grabbed the lock during the disconnect window, re-acquire
   * fails gracefully: we drop the claim and stop the heartbeat.
   */
  private async reacquireClaimedFields(): Promise<void> {
    for (const [key, { docId, fieldId }] of Array.from(this.claimedFields)) {
      try {
        const result = await this.hub!.invoke<LockAcquireResult>('AcquireFieldLock', docId, fieldId);
        if (result.acquired) {
          this.startHeartbeat(docId, fieldId);
        } else {
          // Someone else acquired the lock while we were disconnected — give it up.
          this.claimedFields.delete(key);
        }
      } catch {
        this.claimedFields.delete(key);
      }
    }
  }
}
