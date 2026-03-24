import { Injectable } from '@angular/core';
import * as signalR from '@microsoft/signalr';
import { Subject } from 'rxjs';
import { AuthService } from './auth.service';

export interface TransactionSubmitResult {
  transactionId: string | null;
  status: 'accepted' | 'rejected';
  message: string;
  currentlyProcessing: TransactionStatus | null;
}

export interface TransactionStatus {
  id: string;
  state: 'processing' | 'completed' | 'failed' | 'rejected';
  description: string;
  result: string | null;
  submittedAt: string;
  finishedAt: string | null;
}

export interface BankStatus {
  tenantId: string;
  isBankBusy: boolean;
  currentTransaction: TransactionStatus | null;
  history: TransactionStatus[];
}

export interface TransactionStatusChanged {
  id: string;
  state: string;
  message: string;
}

const infiniteRetry: signalR.IRetryPolicy = {
  nextRetryDelayInMilliseconds(ctx: signalR.RetryContext): number {
    const delays = [0, 2000, 5000, 10000];
    return delays[Math.min(ctx.previousRetryCount, delays.length - 1)];
  },
};

@Injectable({ providedIn: 'root' })
export class TransactionService {
  private connections = new Map<string, signalR.HubConnection>(); // key = tenantId
  private connectionStatus$ = new Map<string, Subject<string>>();

  private statusChangedSubject = new Subject<{ tenantId: string } & TransactionStatusChanged>();
  private bankStatusSubject = new Subject<{ tenantId: string } & BankStatus>();

  public statusChanged$ = this.statusChangedSubject.asObservable();
  public bankStatus$ = this.bankStatusSubject.asObservable();

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
      .withUrl('/hubs/transaction', {
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

    hub.on('TransactionStatusChanged', (data: TransactionStatusChanged) =>
      this.statusChangedSubject.next({ tenantId, ...data }));
    hub.on('BankStatus', (data: Omit<BankStatus, 'tenantId'>) =>
      this.bankStatusSubject.next({ tenantId, ...data }));

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

  async submitTransaction(tenantId: string, description: string, amount: number, currency = 'VND'): Promise<TransactionSubmitResult> {
    const hub = this.connections.get(tenantId);
    if (!hub) throw new Error(`Not connected for tenant ${tenantId}`);
    return await hub.invoke<TransactionSubmitResult>('SubmitTransaction', { description, amount, currency });
  }

  private getStatusSubject(tenantId: string): Subject<string> {
    if (!this.connectionStatus$.has(tenantId)) {
      this.connectionStatus$.set(tenantId, new Subject<string>());
    }
    return this.connectionStatus$.get(tenantId)!;
  }
}
