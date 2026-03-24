import { Injectable } from '@angular/core';
import * as signalR from '@microsoft/signalr';
import { Subject } from 'rxjs';

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

const infiniteRetry: signalR.IRetryPolicy = {
  nextRetryDelayInMilliseconds(ctx: signalR.RetryContext): number {
    const delays = [0, 2000, 5000, 10000];
    return delays[Math.min(ctx.previousRetryCount, delays.length - 1)];
  },
};

@Injectable({ providedIn: 'root' })
export class DocumentService {
  private hub: signalR.HubConnection | undefined;
  private heartbeatTimers = new Map<string, ReturnType<typeof setInterval>>();

  private fieldLockedSubject = new Subject<FieldLockInfo & { docId: string; fieldId: string }>();
  private fieldUnlockedSubject = new Subject<{ docId: string; fieldId: string }>();
  private fieldValueChangedSubject = new Subject<FieldValueChanged>();
  private fieldsExpiredSubject = new Subject<FieldLockInfo[]>();
  private connectionStatusSubject = new Subject<string>();
  private lockSnapshotSubject = new Subject<FieldLockInfo[]>();

  public fieldLocked$ = this.fieldLockedSubject.asObservable();
  public fieldUnlocked$ = this.fieldUnlockedSubject.asObservable();
  public fieldValueChanged$ = this.fieldValueChangedSubject.asObservable();
  public fieldsExpired$ = this.fieldsExpiredSubject.asObservable();
  public connectionStatus$ = this.connectionStatusSubject.asObservable();
  public lockSnapshot$ = this.lockSnapshotSubject.asObservable();

  async connect(userId: string, userName: string): Promise<void> {
    if (this.hub && this.hub.state !== signalR.HubConnectionState.Disconnected) return;

    this.connectionStatusSubject.next('connecting');

    this.hub = new signalR.HubConnectionBuilder()
      .withUrl(`/hubs/document?userId=${encodeURIComponent(userId)}&userName=${encodeURIComponent(userName)}`)
      .withAutomaticReconnect(infiniteRetry)
      .withKeepAliveInterval(2000000000)
      .withServerTimeout(24 * 60 * 60 * 1000)
      .build();

    this.hub.onreconnecting(() => this.connectionStatusSubject.next('reconnecting'));
    this.hub.onreconnected(() => this.connectionStatusSubject.next('connected'));
    this.hub.onclose(() => this.connectionStatusSubject.next('disconnected'));

    this.hub.on('LockSnapshot', (locks: FieldLockInfo[]) => this.lockSnapshotSubject.next(locks));
    this.hub.on('FieldLocked', (info: FieldLockInfo & { docId: string; fieldId: string }) => this.fieldLockedSubject.next(info));
    this.hub.on('FieldUnlocked', (info: { docId: string; fieldId: string }) => this.fieldUnlockedSubject.next(info));
    this.hub.on('FieldValueChanged', (data: FieldValueChanged) => this.fieldValueChangedSubject.next(data));
    this.hub.on('FieldsExpiredUnlocked', (locks: FieldLockInfo[]) => this.fieldsExpiredSubject.next(locks));

    try {
      await this.hub.start();
      this.connectionStatusSubject.next('connected');
    } catch (err) {
      this.connectionStatusSubject.next('disconnected');
      throw err;
    }
  }

  async disconnect(): Promise<void> {
    this.stopAllHeartbeats();
    await this.hub?.stop();
    this.connectionStatusSubject.next('disconnected');
  }

  async acquireFieldLock(docId: string, fieldId: string): Promise<LockAcquireResult> {
    if (!this.hub) throw new Error('Not connected');
    return await this.hub.invoke<LockAcquireResult>('AcquireFieldLock', docId, fieldId);
  }

  async releaseFieldLock(docId: string, fieldId: string): Promise<void> {
    if (!this.hub) return;
    this.stopHeartbeat(docId, fieldId);
    await this.hub.invoke('ReleaseFieldLock', docId, fieldId);
  }

  startHeartbeat(docId: string, fieldId: string): void {
    const key = `${docId}:${fieldId}`;
    this.stopHeartbeat(docId, fieldId);
    // Send heartbeat every 8 seconds (lock TTL is 30s)
    const timer = setInterval(() => {
      this.hub?.invoke('HeartbeatFieldLock', docId, fieldId).catch(() => {});
    }, 8000);
    this.heartbeatTimers.set(key, timer);
  }

  stopHeartbeat(docId: string, fieldId: string): void {
    const key = `${docId}:${fieldId}`;
    const timer = this.heartbeatTimers.get(key);
    if (timer) {
      clearInterval(timer);
      this.heartbeatTimers.delete(key);
    }
  }

  async updateFieldValue(docId: string, fieldId: string, value: string): Promise<void> {
    if (!this.hub) return;
    await this.hub.invoke('UpdateFieldValue', docId, fieldId, value);
  }

  private stopAllHeartbeats(): void {
    this.heartbeatTimers.forEach(timer => clearInterval(timer));
    this.heartbeatTimers.clear();
  }
}
