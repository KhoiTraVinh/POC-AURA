import { Injectable } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import * as signalR from '@microsoft/signalr';
import { BehaviorSubject, Observable } from 'rxjs';

export interface MessageDto {
  id: number;
  groupId: number;
  type: string;
  ref: string;
  createdAt: string;
}

export interface SendMessageRequest {
  groupId: number;
  type: string;
  ref: string;
}

export interface MarkReadRequest {
  groupId: number;
  lastReadMessageId: number;
  staffId: number;
}

@Injectable({
  providedIn: 'root'
})
export class ChatService {
  private hubConnection: signalR.HubConnection | undefined;
  private readonly apiUrl = 'http://localhost:5000/api/messages'; // Adjust base URL as needed for dev
  private readonly hubUrl = 'http://localhost:5000/hubs/chat';

  // State
  private messagesSubject = new BehaviorSubject<MessageDto[]>([]);
  public messages$ = this.messagesSubject.asObservable();

  private readReceiptsSubject = new BehaviorSubject<{staffId: number, messageId: number} | null>(null);
  public readReceipts$ = this.readReceiptsSubject.asObservable();

  constructor(private http: HttpClient) { }

  public async startConnection(groupId: number): Promise<void> {
    this.hubConnection = new signalR.HubConnectionBuilder()
      .withUrl(this.hubUrl)
      .withAutomaticReconnect()
      .build();

    // Reconnect logic: Fetch missing messages when reconnected
    this.hubConnection.onreconnected(() => {
      console.log('SignalR Reconnected.');
      this.fetchMissingMessages(groupId);
    });

    // Listen to new messages
    this.hubConnection.on('NewMessageNotification', (messageId: number) => {
      console.log('New message received via SignalR:', messageId);
      this.fetchMissingMessages(groupId);
    });

    // Listen to read receipts
    this.hubConnection.on('UserReadReceipt', (data: { staffId: number, messageId: number }) => {
      console.log('Read receipt received:', data);
      this.readReceiptsSubject.next(data);
    });

    try {
      await this.hubConnection.start();
      console.log('SignalR Connected.');
      await this.hubConnection.invoke('JoinGroup', groupId);
      
      // Load initial messages
      await this.loadInitialMessages(groupId);
    } catch (err) {
      console.error('Error while starting SignalR connection: ' + err);
    }
  }

  public async stopConnection(groupId: number): Promise<void> {
    if (this.hubConnection) {
      try {
        await this.hubConnection.invoke('LeaveGroup', groupId);
        await this.hubConnection.stop();
        this.messagesSubject.next([]); // Clear messages state
      } catch (err) {
        console.error(err);
      }
    }
  }

  // HTTP API Calls
  private async loadInitialMessages(groupId: number): Promise<void> {
    this.http.get<MessageDto[]>(`${this.apiUrl}/${groupId}`).subscribe({
      next: (messages) => this.messagesSubject.next(messages),
      error: (err) => console.error('Failed to load initial messages', err)
    });
  }

  private fetchMissingMessages(groupId: number): void {
    const currentMessages = this.messagesSubject.value;
    let afterMessageId = undefined;
    
    if (currentMessages.length > 0) {
      afterMessageId = currentMessages[currentMessages.length - 1].id;
    }

    let url = `${this.apiUrl}/${groupId}`;
    if (afterMessageId) {
      url += `?afterMessageId=${afterMessageId}`;
    }

    this.http.get<MessageDto[]>(url).subscribe({
      next: (newMessages) => {
        if (newMessages.length > 0) {
          this.messagesSubject.next([...currentMessages, ...newMessages]);
        }
      },
      error: (err) => console.error('Failed to fetch missing messages', err)
    });
  }

  public sendMessage(request: SendMessageRequest): Observable<MessageDto> {
    return this.http.post<MessageDto>(this.apiUrl, request);
  }

  public markAsRead(request: MarkReadRequest): Observable<void> {
    return this.http.post<void>(`${this.apiUrl}/read`, request);
  }
}
