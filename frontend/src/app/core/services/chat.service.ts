import { Injectable, OnDestroy } from '@angular/core';
import { BehaviorSubject, firstValueFrom, Observable } from 'rxjs';
import { MessageDto, ConnectionStatus, SendMessageRequest } from './chat.types';
import { ChatHttpService } from './chat-http.service';
import { ChatSignalrService } from './chat-signalr.service';

@Injectable({
  providedIn: 'root'
})
export class ChatService implements OnDestroy {
  // State
  private messagesSubject = new BehaviorSubject<MessageDto[]>([]);
  public messages$ = this.messagesSubject.asObservable();

  private readReceiptsSubject = new BehaviorSubject<{ staffId: number; messageId: number } | null>(null);
  public readReceipts$ = this.readReceiptsSubject.asObservable();

  private connectionStatusSubject = new BehaviorSubject<ConnectionStatus>('disconnected');
  public connectionStatus$ = this.connectionStatusSubject.asObservable();

  // Context for reconnecting when needed
  private currentGroupId: number | null = null;
  private currentStaffId: number | null = null;

  // Differentiate: user intentionally stopped vs network loss
  private isIntentionallyStopped = false;

  private readonly onlineHandler = () => this.handleOnline();

  constructor(
    private chatHttp: ChatHttpService,
    private chatSignalr: ChatSignalrService
  ) {
    // When browser detects network is back — fallback if auto-reconnect stopped
    window.addEventListener('online', this.onlineHandler);

    // Subscribe to SignalR events
    this.chatSignalr.connectionStatus$.subscribe(status => {
      this.connectionStatusSubject.next(status);
      if (status === 'connected') {
        this.fetchLatestMessages();
      }
    });

    this.chatSignalr.newMessage$.subscribe(() => {
      this.fetchLatestMessages();
    });

    this.chatSignalr.userReadReceipt$.subscribe(receipt => {
      this.readReceiptsSubject.next(receipt);
    });
  }

  ngOnDestroy(): void {
    window.removeEventListener('online', this.onlineHandler);
  }

  private async handleOnline(): Promise<void> {
    const status = this.connectionStatusSubject.value;
    if (
      !this.isIntentionallyStopped &&
      this.currentGroupId !== null &&
      this.currentStaffId !== null &&
      status === 'disconnected'
    ) {
      // Disconnected state when not caused by user -> restart connection
      await this.startConnection(this.currentGroupId, this.currentStaffId);
    }
  }

  public async startConnection(groupId: number, staffId: number): Promise<void> {
    this.isIntentionallyStopped = false;
    this.currentGroupId = groupId;
    this.currentStaffId = staffId;
    
    await this.chatSignalr.startConnection(groupId);
    // Note: The 'connected' status from SignalR service will trigger fetchLatestMessages
  }

  // Normal connection stop — user explicitly leaves
  public async stopConnection(): Promise<void> {
    this.isIntentionallyStopped = true;
    await this.chatSignalr.stopConnection(this.currentGroupId);
    
    this.messagesSubject.next([]);
    this.connectionStatusSubject.next('disconnected');
    this.currentGroupId = null;
    this.currentStaffId = null;
  }

  // Simulate network loss / token expiration
  public async forceDisconnect(): Promise<void> {
    this.isIntentionallyStopped = true;
    await this.chatSignalr.forceDisconnect();
  }

  // Manual reconnect after forceDisconnect
  public async reconnect(): Promise<void> {
    if (this.currentGroupId === null || this.currentStaffId === null) return;
    await this.startConnection(this.currentGroupId, this.currentStaffId);
  }

  private async fetchLatestMessages(): Promise<void> {
    if (this.currentGroupId === null || this.currentStaffId === null) return;

    try {
      // Get pointer from HTTP (GET /receipt)
      const receipt = await firstValueFrom(
        this.chatHttp.getReceipt(this.currentGroupId, this.currentStaffId)
      );

      const pointer = receipt.lastReadMessageId;

      // Get messages from HTTP (GET /messages?afterMessageId)
      const newMessages = await firstValueFrom(
        this.chatHttp.getMessages(this.currentGroupId, pointer)
      );

      if (newMessages.length > 0) {
        const current = this.messagesSubject.value;
        const existingIds = new Set(current.map(m => m.id));
        const unique = newMessages.filter(m => !existingIds.has(m.id));
        this.messagesSubject.next([...current, ...unique]);

        const maxId = Math.max(...newMessages.map(m => m.id));

        // Let server update pointer + broadcast UserReadReceipt to group
        this.chatSignalr.updateReadPointer(this.currentGroupId, this.currentStaffId, maxId);
      }
    } catch (err) {
      console.error('Failed to fetch latest messages:', err);
    }
  }

  public sendMessage(request: SendMessageRequest): Observable<MessageDto> {
    return this.chatHttp.sendMessage(request);
  }
}
