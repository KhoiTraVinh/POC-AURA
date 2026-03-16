import {
  Component,
  OnInit,
  OnDestroy,
  ViewChild,
  ElementRef,
  signal,
  computed,
} from '@angular/core';
import { FormsModule } from '@angular/forms';
import { CommonModule } from '@angular/common';
import { HttpClient } from '@angular/common/http';
import { Subject, takeUntil } from 'rxjs';
import { ChatService, ConnectionStatus, MessageDto, SyncStatus } from '../../core/services/chat.service';

export interface CacheEntry {
  key: string;
  value: unknown;
}

export interface CacheSnapshot {
  count: number;
  entries: CacheEntry[];
  error?: string;
}

@Component({
  selector: 'app-chat',
  standalone: true,
  imports: [CommonModule, FormsModule],
  templateUrl: './chat.component.html',
  styleUrl: './chat.component.scss',
})
export class ChatComponent implements OnInit, OnDestroy {
  @ViewChild('messagesContainer') messagesContainer!: ElementRef<HTMLDivElement>;

  readonly username = signal('');
  readonly staffIdInput = signal<number>(1);
  readonly groupId = signal<number>(1);
  readonly messageInput = signal('');
  readonly messages = signal<MessageDto[]>([]);
  readonly connectionStatus = signal<ConnectionStatus>('disconnected');
  readonly syncStatus = signal<SyncStatus>({ syncing: false, count: 0 });
  readonly isJoined = signal(false);
  readonly usernameInput = signal('');

  // Track read receipts
  private latestReadReceiptsMap = new Map<number, number>();
  readonly latestReadReceptId = signal<number | null>(null);

  readonly isConnected = computed(() => this.connectionStatus() === 'connected');

  // Cache Inspector
  readonly showCachePanel = signal(false);
  readonly cacheSnapshot = signal<CacheSnapshot | null>(null);
  readonly cacheLoading = signal(false);

  private readonly destroy$ = new Subject<void>();

  constructor(
    private readonly chatService: ChatService,
    private readonly http: HttpClient,
  ) {}

  ngOnInit(): void {
    // Subscriptions đặt ở đây — chỉ subscribe 1 lần duy nhất trong vòng đời component
    // Không đặt trong join() để tránh stack nhiều subscription mỗi lần join() được gọi

    this.chatService.messages$
      .pipe(takeUntil(this.destroy$))
      .subscribe((history) => {
        this.messages.set(history);
        this.scrollToBottom();
      });

    this.chatService.readReceipts$
      .pipe(takeUntil(this.destroy$))
      .subscribe((receipt) => {
        if (receipt) {
          this.latestReadReceiptsMap.set(receipt.messageId, receipt.staffId);
          this.latestReadReceptId.set(receipt.messageId);
        }
      });

    this.chatService.connectionStatus$
      .pipe(takeUntil(this.destroy$))
      .subscribe((status) => {
        this.connectionStatus.set(status);
      });

    this.chatService.syncStatus$
      .pipe(takeUntil(this.destroy$))
      .subscribe((sync) => {
        this.syncStatus.set(sync);
      });
  }

  async join(): Promise<void> {
    // Guard: không cho join lại khi đang connected / đang trong quá trình join
    if (this.isJoined()) return;

    const name = this.usernameInput().trim();
    if (!name) return;

    this.username.set(name);

    try {
      await this.chatService.startConnection(this.groupId(), this.staffIdInput());
      this.isJoined.set(true);
    } catch {
      // connectionStatus$ sẽ tự emit 'disconnected'
    }
  }

  send(): void {
    const msg = this.messageInput().trim();
    if (!msg || !this.isConnected()) return;

    this.chatService.sendMessage({
      groupId: this.groupId(),
      type: 'Text',
      ref: msg
    }).subscribe({
      next: () => {
        this.messageInput.set('');
      },
      error: (err) => console.error('Could not send message', err)
    });
  }

  // ── Cache Inspector ────────────────────────────────────────────
  toggleCachePanel(): void {
    const next = !this.showCachePanel();
    this.showCachePanel.set(next);
    if (next) this.refreshCache();
  }

  refreshCache(): void {
    this.cacheLoading.set(true);
    this.http.get<CacheSnapshot>('/api/cache').subscribe({
      next: (data) => {
        this.cacheSnapshot.set(data);
        this.cacheLoading.set(false);
      },
      error: (err) => {
        console.error('Failed to load cache entries', err);
        this.cacheSnapshot.set({ count: 0, entries: [], error: 'Request failed' });
        this.cacheLoading.set(false);
      },
    });
  }

  formatCacheValue(value: unknown): string {
    if (value === null || value === undefined) return 'null';
    return JSON.stringify(value);
  }
  // ── End Cache Inspector ─────────────────────────────────────────

  // Mô phỏng token hết hạn / mất mạng — hub tự auto-leave group
  async disconnect(): Promise<void> {
    await this.chatService.forceDisconnect();
  }

  // Kết nối lại — tự động fetch messages bị miss bằng pointer từ server
  async reconnect(): Promise<void> {
    await this.chatService.reconnect();
  }

  onKeyDown(event: KeyboardEvent): void {
    if (event.key === 'Enter' && !event.shiftKey) {
      event.preventDefault();
      this.send();
    }
  }

  formatTime(timestamp: string): string {
    return new Date(timestamp).toLocaleTimeString('vi-VN', {
      hour: '2-digit',
      minute: '2-digit',
    });
  }

  connectionStatusLabel(): string {
    switch (this.connectionStatus()) {
      case 'connected': return 'Connected';
      case 'connecting': return 'Connecting...';
      case 'reconnecting': return 'Reconnecting...';
      default: return 'Disconnected';
    }
  }

  private scrollToBottom(): void {
    setTimeout(() => {
      if (this.messagesContainer?.nativeElement) {
        const el = this.messagesContainer.nativeElement;
        el.scrollTop = el.scrollHeight;
      }
    }, 50);
  }

  ngOnDestroy(): void {
    this.destroy$.next();
    this.destroy$.complete();
    this.chatService.stopConnection();
  }
}
