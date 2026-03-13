import { Injectable } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import * as signalR from '@microsoft/signalr';
import { BehaviorSubject, firstValueFrom, Observable } from 'rxjs';

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

export interface ReadReceiptDto {
  groupId: number;
  staffId: number;
  lastReadMessageId: number | null;
}

export type ConnectionStatus = 'disconnected' | 'connecting' | 'connected' | 'reconnecting';

export interface SyncStatus {
  syncing: boolean;
  count: number;
}

@Injectable({
  providedIn: 'root'
})
export class ChatService {
  private hubConnection: signalR.HubConnection | undefined;
  private readonly apiUrl = '/api/messages';
  private readonly hubUrl = '/hubs/chat';

  // State
  private messagesSubject = new BehaviorSubject<MessageDto[]>([]);
  public messages$ = this.messagesSubject.asObservable();

  private readReceiptsSubject = new BehaviorSubject<{ staffId: number; messageId: number } | null>(null);
  public readReceipts$ = this.readReceiptsSubject.asObservable();

  // Connection status để UI hiển thị trạng thái kết nối
  private connectionStatusSubject = new BehaviorSubject<ConnectionStatus>('disconnected');
  public connectionStatus$ = this.connectionStatusSubject.asObservable();

  // Sync status khi đang fetch messages bị miss sau reconnect
  private syncStatusSubject = new BehaviorSubject<SyncStatus>({ syncing: false, count: 0 });
  public syncStatus$ = this.syncStatusSubject.asObservable();

  // Context hiện tại — dùng lại khi reconnect thủ công
  private currentGroupId: number | null = null;
  private currentStaffId: number | null = null;

  constructor(private http: HttpClient) {}

  public async startConnection(groupId: number, staffId: number): Promise<void> {
    this.currentGroupId = groupId;
    this.currentStaffId = staffId;
    this.connectionStatusSubject.next('connecting');

    this.hubConnection = new signalR.HubConnectionBuilder()
      .withUrl(this.hubUrl)
      .withAutomaticReconnect()
      .build();

    // Đang reconnect tự động (do mất mạng tạm thời)
    this.hubConnection.onreconnecting(() => {
      this.connectionStatusSubject.next('reconnecting');
    });

    // Reconnect tự động thành công — dùng pointer từ server để fetch messages bị miss
    this.hubConnection.onreconnected(async () => {
      this.connectionStatusSubject.next('connected');
      await this.fetchMissingMessages();
    });

    // Connection đóng hẳn
    this.hubConnection.onclose(() => {
      this.connectionStatusSubject.next('disconnected');
    });

    // Nhận tín hiệu "có message mới" → gọi API lấy data (không dùng payload của signal)
    this.hubConnection.on('NewMessageNotification', () => {
      this.fetchMissingMessages();
    });

    // Nhận read receipt từ client khác trong cùng group
    this.hubConnection.on('UserReadReceipt', (data: { staffId: number; messageId: number }) => {
      this.readReceiptsSubject.next(data);
    });

    try {
      await this.hubConnection.start();
      await this.hubConnection.invoke('JoinGroup', groupId);
      this.connectionStatusSubject.next('connected');

      // Load messages từ pointer trên server (bao gồm messages bị miss trước đó)
      await this.fetchMissingMessages();
    } catch (err) {
      this.connectionStatusSubject.next('disconnected');
      console.error('Error while starting SignalR connection:', err);
      throw err;
    }
  }

  // Dừng kết nối bình thường — gọi LeaveGroup trước
  public async stopConnection(): Promise<void> {
    if (this.hubConnection) {
      try {
        if (this.currentGroupId !== null) {
          await this.hubConnection.invoke('LeaveGroup', this.currentGroupId);
        }
        await this.hubConnection.stop();
      } catch (err) {
        console.error('Error stopping connection:', err);
      } finally {
        this.connectionStatusSubject.next('disconnected');
        this.messagesSubject.next([]);
        this.currentGroupId = null;
        this.currentStaffId = null;
      }
    }
  }

  // Ngắt kết nối thủ công — mô phỏng token hết hạn / mất mạng
  // Hub sẽ tự auto-leave qua OnDisconnectedAsync, không cần gọi LeaveGroup
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

  // Kết nối lại sau khi bị ngắt thủ công
  public async reconnect(): Promise<void> {
    if (this.currentGroupId === null || this.currentStaffId === null) return;
    await this.startConnection(this.currentGroupId, this.currentStaffId);
  }

  // Lấy pointer bền vững từ server (ReadReceipt.LastReadMessageId) rồi fetch messages mới hơn
  // Không dùng in-memory vì sẽ bị mất khi token refresh / component destroy
  private async fetchMissingMessages(): Promise<void> {
    if (this.currentGroupId === null || this.currentStaffId === null) return;

    try {
      const receipt = await firstValueFrom(
        this.http.get<ReadReceiptDto>(
          `${this.apiUrl}/receipt?groupId=${this.currentGroupId}&staffId=${this.currentStaffId}`
        )
      );

      const pointer = receipt.lastReadMessageId;
      let url = `${this.apiUrl}/${this.currentGroupId}`;
      if (pointer !== null && pointer !== undefined) {
        url += `?afterMessageId=${pointer}`;
      }

      const newMessages = await firstValueFrom(
        this.http.get<MessageDto[]>(url)
      );

      if (newMessages.length > 0) {
        this.syncStatusSubject.next({ syncing: true, count: newMessages.length });

        // Merge với messages hiện có, loại trùng lặp
        const current = this.messagesSubject.value;
        const existingIds = new Set(current.map(m => m.id));
        const unique = newMessages.filter(m => !existingIds.has(m.id));
        this.messagesSubject.next([...current, ...unique]);

        // Cập nhật pointer lên message mới nhất
        const maxId = Math.max(...newMessages.map(m => m.id));
        this.markAsRead({
          groupId: this.currentGroupId!,
          staffId: this.currentStaffId!,
          lastReadMessageId: maxId
        }).subscribe({
          error: (err) => console.error('Failed to update pointer:', err)
        });

        // Ẩn sync banner sau 2 giây
        setTimeout(() => this.syncStatusSubject.next({ syncing: false, count: 0 }), 2000);
      }
    } catch (err) {
      console.error('Failed to fetch missing messages:', err);
    }
  }

  public sendMessage(request: SendMessageRequest): Observable<MessageDto> {
    return this.http.post<MessageDto>(this.apiUrl, request);
  }

  public markAsRead(request: MarkReadRequest): Observable<void> {
    return this.http.post<void>(`${this.apiUrl}/read`, request);
  }
}
