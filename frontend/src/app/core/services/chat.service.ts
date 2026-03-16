import { Injectable, OnDestroy } from '@angular/core';
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

// Retry vô hạn: [0s, 2s, 5s, 10s, 10s, ...] — không bao giờ trả về null nên không bao giờ bỏ cuộc
const infiniteRetry: signalR.IRetryPolicy = {
  nextRetryDelayInMilliseconds(ctx: signalR.RetryContext): number {
    const delays = [0, 2000, 5000, 10000];
    return delays[Math.min(ctx.previousRetryCount, delays.length - 1)];
  }
};

@Injectable({
  providedIn: 'root'
})
export class ChatService implements OnDestroy {
  private hubConnection: signalR.HubConnection | undefined;
  private readonly apiUrl = '/api/messages';
  private readonly hubUrl = '/hubs/chat';

  // State
  private messagesSubject = new BehaviorSubject<MessageDto[]>([]);
  public messages$ = this.messagesSubject.asObservable();

  private readReceiptsSubject = new BehaviorSubject<{ staffId: number; messageId: number } | null>(null);
  public readReceipts$ = this.readReceiptsSubject.asObservable();

  private connectionStatusSubject = new BehaviorSubject<ConnectionStatus>('disconnected');
  public connectionStatus$ = this.connectionStatusSubject.asObservable();

  private syncStatusSubject = new BehaviorSubject<SyncStatus>({ syncing: false, count: 0 });
  public syncStatus$ = this.syncStatusSubject.asObservable();

  // Context để reconnect lại khi cần
  private currentGroupId: number | null = null;
  private currentStaffId: number | null = null;

  // Phân biệt: user chủ động ngắt vs. mất mạng
  private isIntentionallyStopped = false;

  private readonly onlineHandler = () => this.handleOnline();

  constructor(private http: HttpClient) {
    // Khi trình duyệt phát hiện có mạng lại — fallback nếu auto-reconnect đã dừng
    window.addEventListener('online', this.onlineHandler);
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
      // Trạng thái disconnected khi không phải do user → start lại connection
      await this.startConnection(this.currentGroupId, this.currentStaffId);
    }
    // Nếu đang 'reconnecting': infinite retry đang chạy, sẽ tự reconnect khi mạng ổn định
  }

  public async startConnection(groupId: number, staffId: number): Promise<void> {
    // Dừng connection cũ nếu còn sống
    if (this.hubConnection && this.hubConnection.state !== signalR.HubConnectionState.Disconnected) {
      try { await this.hubConnection.stop(); } catch { /* ignore */ }
    }

    this.isIntentionallyStopped = false;
    this.currentGroupId = groupId;
    this.currentStaffId = staffId;
    this.connectionStatusSubject.next('connecting');

    this.hubConnection = new signalR.HubConnectionBuilder()
      .withUrl(this.hubUrl)
      .withAutomaticReconnect(infiniteRetry)  // Retry vô hạn, không bao giờ bỏ cuộc
      // Tắt client-side ping (type:6 từ client → server)
      // KHÔNG dùng 0 — setTimeout(fn, 0) fire ngay lập tức, gây ping liên tục
      // Dùng giá trị rất lớn (~23 ngày) để không bao giờ fire trong thực tế
      // 2_000_000_000 ms < 2^31-1 (giới hạn an toàn của setTimeout 32-bit)
      .withKeepAliveInterval(2_000_000_000)
      .withServerTimeout(24 * 60 * 60 * 1000)
      .build();

    // Đang retry sau khi mất kết nối
    this.hubConnection.onreconnecting(() => {
      this.connectionStatusSubject.next('reconnecting');
    });

    // Reconnect thành công — fetch messages bị miss trong lúc offline
    this.hubConnection.onreconnected(async () => {
      this.connectionStatusSubject.next('connected');
      await this.fetchMissingMessages();
    });

    // onclose chỉ fire khi gọi stop() tường minh (vì infiniteRetry không bao giờ dừng)
    this.hubConnection.onclose(() => {
      this.connectionStatusSubject.next('disconnected');
    });

    this.hubConnection.on('NewMessageNotification', () => {
      this.fetchMissingMessages();
    });

    this.hubConnection.on('UserReadReceipt', (data: { staffId: number; messageId: number }) => {
      this.readReceiptsSubject.next(data);
    });

    try {
      await this.hubConnection.start();
      await this.hubConnection.invoke('JoinGroup', groupId);
      this.connectionStatusSubject.next('connected');
      await this.fetchMissingMessages();
    } catch (err) {
      this.connectionStatusSubject.next('disconnected');
      console.error('Error while starting SignalR connection:', err);
      throw err;
    }
  }

  // Dừng kết nối bình thường — user chủ động rời
  public async stopConnection(): Promise<void> {
    this.isIntentionallyStopped = true;
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

  // Mô phỏng mất mạng / token hết hạn — hub tự auto-leave qua OnDisconnectedAsync
  public async forceDisconnect(): Promise<void> {
    this.isIntentionallyStopped = true;
    if (this.hubConnection) {
      try {
        await this.hubConnection.stop();
      } catch (err) {
        console.error('Error during force disconnect:', err);
      }
    }
    this.connectionStatusSubject.next('disconnected');
  }

  // Reconnect thủ công sau forceDisconnect
  public async reconnect(): Promise<void> {
    if (this.currentGroupId === null || this.currentStaffId === null) return;
    await this.startConnection(this.currentGroupId, this.currentStaffId);
  }

  private async fetchMissingMessages(): Promise<void> {
    if (!this.hubConnection || this.currentGroupId === null || this.currentStaffId === null) return;

    try {
      // Lấy pointer từ HTTP (GET /receipt) — giữ nguyên
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

      // Lấy messages từ HTTP (GET /messages?afterMessageId) — giữ nguyên
      const newMessages = await firstValueFrom(this.http.get<MessageDto[]>(url));

      if (newMessages.length > 0) {
        this.syncStatusSubject.next({ syncing: true, count: newMessages.length });

        const current = this.messagesSubject.value;
        const existingIds = new Set(current.map(m => m.id));
        const unique = newMessages.filter(m => !existingIds.has(m.id));
        this.messagesSubject.next([...current, ...unique]);

        const maxId = Math.max(...newMessages.map(m => m.id));

        // Chỉ cái này đổi sang hub invoke (thay POST /api/messages/read)
        // Fire-and-forget: server cập nhật pointer + broadcast UserReadReceipt cho group
        this.hubConnection.invoke('MarkRead', this.currentGroupId, this.currentStaffId, maxId)
          .catch(err => console.error('Failed to update read pointer:', err));

        setTimeout(() => this.syncStatusSubject.next({ syncing: false, count: 0 }), 2000);
      }
    } catch (err) {
      console.error('Failed to fetch missing messages:', err);
    }
  }

  public sendMessage(request: SendMessageRequest): Observable<MessageDto> {
    return this.http.post<MessageDto>(this.apiUrl, request);
  }
}
