import { Injectable } from '@angular/core';
import * as signalR from '@microsoft/signalr';
import { Subject } from 'rxjs';
import { AuthService } from './auth.service';
import { infiniteRetry } from './signalr-retry';
import type {
  BankStatus,
  TransactionSubmitResult,
  TransactionStatusChanged,
} from '../models';

export type { BankStatus, TransactionSubmitResult, TransactionStatusChanged };

/**
 * Manages one `HubConnection` per tenant to the `/hubs/aura` endpoint for
 * bank transaction submission and status tracking.
 *
 * **Global bank model**: the bank is a single shared resource. `BankStatus` events
 * are not scoped to a tenant — every connected UI client receives the same snapshot.
 *
 * Listens for:
 * - `TransactionStatusChanged` — lifecycle event (processing → completed | failed).
 * - `BankStatus`               — full snapshot (isBankBusy, currentTransaction, history).
 */
@Injectable({ providedIn: 'root' })
export class TransactionService {
  // ── Private state ──────────────────────────────────────────────────────
  private connections    = new Map<string, signalR.HubConnection>();
  private statusSubjects = new Map<string, Subject<string>>();

  // ── Event subjects ─────────────────────────────────────────────────────
  private readonly _statusChanged$ = new Subject<{ tenantId: string } & TransactionStatusChanged>();

  /**
   * Global bank status — NOT scoped to a tenant.
   * Subscribers should not filter by tenantId.
   */
  private readonly _bankStatus$ = new Subject<BankStatus>();

  // ── Public observables ─────────────────────────────────────────────────
  public readonly statusChanged$ = this._statusChanged$.asObservable();
  public readonly bankStatus$    = this._bankStatus$.asObservable();

  constructor(private auth: AuthService) {}

  // ── Connection management ──────────────────────────────────────────────

  /** Returns a stream of connection state strings for the given tenant. */
  getStatusStream(tenantId: string) {
    return this.getOrCreateStatusSubject(tenantId).asObservable();
  }

  /**
   * Establishes a hub connection for `tenantId` if not already connected.
   * No-ops when the connection is already active.
   */
  async connect(tenantId: string, userName: string): Promise<void> {
    const existing = this.connections.get(tenantId);
    if (existing && existing.state !== signalR.HubConnectionState.Disconnected) return;

    const statusSubject = this.getOrCreateStatusSubject(tenantId);
    statusSubject.next('connecting');

    const hub = new signalR.HubConnectionBuilder()
      .withUrl('/hubs/aura', {
        accessTokenFactory: () => this.auth.getToken(tenantId, 'ui', userName)
          .then(p => p.accessToken),
      })
      .withAutomaticReconnect(infiniteRetry)
      .withKeepAliveInterval(2_000_000_000)
      .withServerTimeout(24 * 60 * 60 * 1_000)
      .build();

    hub.onreconnecting(() => statusSubject.next('reconnecting'));
    hub.onreconnected(() => statusSubject.next('connected'));
    hub.onclose(() => statusSubject.next('disconnected'));

    // Lifecycle events carry tenantId so subscribers can filter if needed
    hub.on('TransactionStatusChanged', (data: TransactionStatusChanged) =>
      this._statusChanged$.next({ tenantId, ...data }));

    // BankStatus is global — no tenantId wrapping needed
    hub.on('BankStatus', (data: BankStatus) =>
      this._bankStatus$.next(data));

    try {
      await hub.start();
      this.connections.set(tenantId, hub);
      statusSubject.next('connected');
    } catch (err) {
      statusSubject.next('disconnected');
      throw err;
    }
  }

  /** Stops and removes the hub connection for `tenantId`. */
  async disconnect(tenantId: string): Promise<void> {
    const hub = this.connections.get(tenantId);
    if (!hub) return;
    await hub.stop();
    this.connections.delete(tenantId);
    this.getOrCreateStatusSubject(tenantId).next('disconnected');
  }

  // ── Hub methods ────────────────────────────────────────────────────────

  /**
   * Invokes `SubmitTransaction` on AuraHub.
   * Returns immediately with `accepted` or `rejected` — does not wait for processing.
   */
  async submitTransaction(
    tenantId: string,
    description: string,
    amount: number,
    currency = 'VND'
  ): Promise<TransactionSubmitResult> {
    const hub = this.connections.get(tenantId);
    if (!hub) throw new Error(`Not connected for tenant ${tenantId}`);
    return hub.invoke<TransactionSubmitResult>('SubmitTransaction', { description, amount, currency });
  }

  // ── Private ────────────────────────────────────────────────────────────

  private getOrCreateStatusSubject(tenantId: string): Subject<string> {
    if (!this.statusSubjects.has(tenantId)) {
      this.statusSubjects.set(tenantId, new Subject<string>());
    }
    return this.statusSubjects.get(tenantId)!;
  }
}
