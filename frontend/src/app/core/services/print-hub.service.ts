import { Injectable } from '@angular/core';
import * as signalR from '@microsoft/signalr';
import { Subject } from 'rxjs';
import { AuthService } from './auth.service';
import { infiniteRetry } from './signalr-retry';
import type { PrintJob, PrintJobResult } from '../models';

export type { PrintJob, PrintJobResult };

/**
 * Manages one `HubConnection` per tenant to the `/hubs/aura` endpoint.
 *
 * Listens for:
 * - `PrintJobQueued`      — confirmation that a job was accepted.
 * - `PrintJobComplete`    — job finished (sent to the original requestor).
 * - `PrintJobStatusUpdate`— broadcast to other UI clients in the same tenant.
 * - `ClientConnected` / `ClientDisconnected` — connection lifecycle events.
 *
 * All observables are per-tenant: components filter by `tenantId`.
 */
@Injectable({ providedIn: 'root' })
export class PrintHubService {
  // ── Private state ──────────────────────────────────────────────────────
  private connections     = new Map<string, signalR.HubConnection>();
  private statusSubjects  = new Map<string, Subject<string>>();

  // ── Event subjects ─────────────────────────────────────────────────────
  private readonly _jobQueued$      = new Subject<{ tenantId: string; job: PrintJob }>();
  private readonly _jobComplete$    = new Subject<{ tenantId: string; result: PrintJobResult }>();
  private readonly _jobStatusUpdate$= new Subject<{ tenantId: string; result: PrintJobResult }>();
  private readonly _clientConnected$    = new Subject<{ tenantId: string; clientType: string; userName: string; [k: string]: unknown }>();
  private readonly _clientDisconnected$ = new Subject<{ tenantId: string; clientType: string; [k: string]: unknown }>();
  /** Emits the tenantId each time that tenant's hub connection is re-established. */
  private readonly _reconnected$    = new Subject<string>();

  // ── Public observables ─────────────────────────────────────────────────
  /** Emits when a job is accepted and queued by AuraHub. */
  public readonly onPrintJobQueued$       = this._jobQueued$.asObservable();

  /** Emits when the original submitter's job is done/failed. */
  public readonly onPrintJobComplete$     = this._jobComplete$.asObservable();

  /** Emits for other UI clients when a job in the same tenant completes. */
  public readonly onPrintJobStatusUpdate$ = this._jobStatusUpdate$.asObservable();

  public readonly onClientConnected$    = this._clientConnected$.asObservable();
  public readonly onClientDisconnected$ = this._clientDisconnected$.asObservable();

  /**
   * Emits the tenantId when the hub connection for that tenant is re-established
   * after a drop. Components should call `syncJobs()` on this event to recover
   * completion status for any jobs that were processed while disconnected.
   */
  public readonly onReconnected$ = this._reconnected$.asObservable();

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
    hub.onreconnected(() => {
      statusSubject.next('connected');
      this._reconnected$.next(tenantId);
    });
    hub.onclose(() => statusSubject.next('disconnected'));

    hub.on('PrintJobQueued',       (job: PrintJob)         => this._jobQueued$.next({ tenantId, job }));
    hub.on('PrintJobComplete',     (result: PrintJobResult) => this._jobComplete$.next({ tenantId, result }));
    hub.on('PrintJobStatusUpdate', (result: PrintJobResult) => this._jobStatusUpdate$.next({ tenantId, result }));
    hub.on('ClientConnected',    (info: { clientType: string; userName: string }) =>
      this._clientConnected$.next({ tenantId, ...info }));
    hub.on('ClientDisconnected', (info: { clientType: string }) =>
      this._clientDisconnected$.next({ tenantId, ...info }));

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

  /** Invokes `SubmitPrintJob` on AuraHub. Throws if not connected for `tenantId`. */
  async submitPrintJob(
    tenantId: string, documentName: string, content: string, copies = 1
  ): Promise<void> {
    const hub = this.connections.get(tenantId);
    if (!hub) throw new Error(`Not connected for tenant ${tenantId}`);
    await hub.invoke('SubmitPrintJob', { documentName, content, copies });
  }

  /**
   * Invokes `SyncPrintJobs` on AuraHub for the given job IDs.
   * AuraHub pushes `PrintJobComplete` back to the caller for any job that is no
   * longer pending, so the component can update its local state without re-subscribing.
   * Silently no-ops when not connected or the list is empty.
   */
  async syncJobs(tenantId: string, jobIds: string[]): Promise<void> {
    if (!jobIds.length) return;
    const hub = this.connections.get(tenantId);
    if (!hub || hub.state !== signalR.HubConnectionState.Connected) return;
    await hub.invoke('SyncPrintJobs', jobIds);
  }

  /** Returns the current SignalR connection ID for a tenant (null if not connected). */
  getConnectionId(tenantId: string): string | null {
    return this.connections.get(tenantId)?.connectionId ?? null;
  }

  // ── Private ────────────────────────────────────────────────────────────

  private getOrCreateStatusSubject(tenantId: string): Subject<string> {
    if (!this.statusSubjects.has(tenantId)) {
      this.statusSubjects.set(tenantId, new Subject<string>());
    }
    return this.statusSubjects.get(tenantId)!;
  }
}
