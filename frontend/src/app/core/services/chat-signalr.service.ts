import { Injectable } from '@angular/core';
import * as signalR from '@microsoft/signalr';
import { Subject } from 'rxjs';

// Infinite retry strategy: never returns null, so it never gives up
const infiniteRetry: signalR.IRetryPolicy = {
  nextRetryDelayInMilliseconds(ctx: signalR.RetryContext): number {
    const delays = [0, 2000, 5000, 10000];
    return delays[Math.min(ctx.previousRetryCount, delays.length - 1)];
  }
};

@Injectable({
  providedIn: 'root'
})
export class ChatSignalrService {
  private hubConnection: signalR.HubConnection | undefined;
  private readonly hubUrl = '/hubs/chat';

  // Events
  private connectionStatusSubject = new Subject<'connecting' | 'connected' | 'reconnecting' | 'disconnected'>();
  public connectionStatus$ = this.connectionStatusSubject.asObservable();

  private newMessageSubject = new Subject<void>();
  public newMessage$ = this.newMessageSubject.asObservable();

  private userReadReceiptSubject = new Subject<{ staffId: number; messageId: number }>();
  public userReadReceipt$ = this.userReadReceiptSubject.asObservable();

  public async startConnection(groupId: number): Promise<void> {
    if (this.hubConnection && this.hubConnection.state !== signalR.HubConnectionState.Disconnected) {
      try { await this.hubConnection.stop(); } catch { /* ignore */ }
    }

    this.connectionStatusSubject.next('connecting');

    this.hubConnection = new signalR.HubConnectionBuilder()
      .withUrl(this.hubUrl)
      .withAutomaticReconnect(infiniteRetry) // Infinite retry, never give up
      // Disable client-side ping (type:6 from client -> server)
      // DO NOT use 0 — setTimeout(fn, 0) fires immediately, causing constant pings
      // Use a very large value (~23 days) so it never fires in practice
      .withKeepAliveInterval(2_000_000_000)
      .withServerTimeout(24 * 60 * 60 * 1000)
      .build();

    this.hubConnection.onreconnecting(() => this.connectionStatusSubject.next('reconnecting'));
    this.hubConnection.onreconnected(() => this.connectionStatusSubject.next('connected'));
    this.hubConnection.onclose(() => this.connectionStatusSubject.next('disconnected'));

    this.hubConnection.on('NewMessageNotification', () => this.newMessageSubject.next());
    this.hubConnection.on('UserReadReceipt', (data: { staffId: number; messageId: number }) => this.userReadReceiptSubject.next(data));

    try {
      await this.hubConnection.start();
      await this.hubConnection.invoke('JoinGroup', groupId);
      this.connectionStatusSubject.next('connected');
    } catch (err) {
      this.connectionStatusSubject.next('disconnected');
      console.error('Error starting SignalR connection:', err);
      throw err;
    }
  }

  public async stopConnection(groupId: number | null): Promise<void> {
    if (this.hubConnection) {
      try {
        if (groupId !== null) {
          await this.hubConnection.invoke('LeaveGroup', groupId);
        }
        await this.hubConnection.stop();
      } catch (err) {
        console.error('Error stopping SignalR connection:', err);
      }
    }
    this.connectionStatusSubject.next('disconnected');
  }

  public async forceDisconnect(): Promise<void> {
    if (this.hubConnection) {
      try {
        await this.hubConnection.stop();
      } catch (err) {
        console.error('Error during force disconnect:', err);
      }
    }
    this.connectionStatusSubject.next('disconnected');
  }

  public updateReadPointer(groupId: number, staffId: number, maxId: number): void {
    if (!this.hubConnection) return;
    // Fire-and-forget: server updates pointer + broadcasts UserReadReceipt to group
    this.hubConnection.invoke('MarkRead', groupId, staffId, maxId)
      .catch(err => console.error('Failed to update read pointer via Hub:', err));
  }
}
