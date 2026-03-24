import { Injectable } from '@angular/core';
import * as signalR from '@microsoft/signalr';
import { Subject } from 'rxjs';
import { AuthService } from './auth.service';

export interface PrintJob {
  id: string;
  tenantId: string;
  documentName: string;
  content: string;
  copies: number;
  requestorConnectionId: string;
  createdAt: string;
}

export interface PrintJobResult {
  jobId: string;
  tenantId: string;
  requestorConnectionId: string;
  success: boolean;
  message: string;
  completedAt: string;
}

const infiniteRetry: signalR.IRetryPolicy = {
  nextRetryDelayInMilliseconds(ctx: signalR.RetryContext): number {
    const delays = [0, 2000, 5000, 10000];
    return delays[Math.min(ctx.previousRetryCount, delays.length - 1)];
  },
};

@Injectable({ providedIn: 'root' })
export class PrintHubService {
  private connections = new Map<string, signalR.HubConnection>(); // key = tenantId
  private connectionStatus$ = new Map<string, Subject<string>>();

  private printJobQueued$ = new Subject<{ tenantId: string; job: PrintJob }>();
  private printJobComplete$ = new Subject<{ tenantId: string; result: PrintJobResult }>();
  private printJobStatusUpdate$ = new Subject<{ tenantId: string; result: PrintJobResult }>();
  private clientConnected$ = new Subject<any>();
  private clientDisconnected$ = new Subject<any>();

  public onPrintJobQueued$ = this.printJobQueued$.asObservable();
  public onPrintJobComplete$ = this.printJobComplete$.asObservable();
  public onPrintJobStatusUpdate$ = this.printJobStatusUpdate$.asObservable();
  public onClientConnected$ = this.clientConnected$.asObservable();
  public onClientDisconnected$ = this.clientDisconnected$.asObservable();

  constructor(private auth: AuthService) {}

  getStatusStream(tenantId: string) {
    if (!this.connectionStatus$.has(tenantId)) {
      this.connectionStatus$.set(tenantId, new Subject<string>());
    }
    return this.connectionStatus$.get(tenantId)!.asObservable();
  }

  async connect(tenantId: string, userName: string): Promise<void> {
    const conn = this.connections.get(tenantId);
    if (conn && conn.state !== signalR.HubConnectionState.Disconnected) return;

    const statusSubject = this.getStatusSubject(tenantId);
    statusSubject.next('connecting');

    const hub = new signalR.HubConnectionBuilder()
      .withUrl('/hubs/print', {
        accessTokenFactory: async () => {
          const pair = await this.auth.getToken(tenantId, 'ui', userName);
          return pair.accessToken;
        },
      })
      .withAutomaticReconnect(infiniteRetry)
      .withKeepAliveInterval(2000000000)
      .withServerTimeout(24 * 60 * 60 * 1000)
      .build();

    hub.onreconnecting(() => statusSubject.next('reconnecting'));
    hub.onreconnected(() => statusSubject.next('connected'));
    hub.onclose(() => statusSubject.next('disconnected'));

    hub.on('PrintJobQueued', (job: PrintJob) =>
      this.printJobQueued$.next({ tenantId, job }));
    hub.on('PrintJobComplete', (result: PrintJobResult) =>
      this.printJobComplete$.next({ tenantId, result }));
    hub.on('PrintJobStatusUpdate', (result: PrintJobResult) =>
      this.printJobStatusUpdate$.next({ tenantId, result }));
    hub.on('ClientConnected', (info: any) =>
      this.clientConnected$.next({ tenantId, ...info }));
    hub.on('ClientDisconnected', (info: any) =>
      this.clientDisconnected$.next({ tenantId, ...info }));

    try {
      await hub.start();
      this.connections.set(tenantId, hub);
      statusSubject.next('connected');
    } catch (err) {
      statusSubject.next('disconnected');
      throw err;
    }
  }

  async disconnect(tenantId: string): Promise<void> {
    const hub = this.connections.get(tenantId);
    if (hub) {
      await hub.stop();
      this.connections.delete(tenantId);
      this.getStatusSubject(tenantId).next('disconnected');
    }
  }

  async submitPrintJob(tenantId: string, documentName: string, content: string, copies = 1): Promise<void> {
    const hub = this.connections.get(tenantId);
    if (!hub) throw new Error(`Not connected for tenant ${tenantId}`);
    await hub.invoke('SubmitPrintJob', { documentName, content, copies });
  }

  getConnectionId(tenantId: string): string | null {
    return this.connections.get(tenantId)?.connectionId ?? null;
  }

  private getStatusSubject(tenantId: string): Subject<string> {
    if (!this.connectionStatus$.has(tenantId)) {
      this.connectionStatus$.set(tenantId, new Subject<string>());
    }
    return this.connectionStatus$.get(tenantId)!;
  }
}
