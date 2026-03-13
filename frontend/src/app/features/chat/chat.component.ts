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
import { Subject, takeUntil } from 'rxjs';
import { ChatService, ConnectionStatus, MessageDto, SyncStatus } from '../../core/services/chat.service';

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

  private readonly destroy$ = new Subject<void>();

  constructor(private readonly chatService: ChatService) {}

  ngOnInit(): void {
    // Intentionally left blank, waiting for user to click Join
  }

  async join(): Promise<void> {
    const name = this.usernameInput().trim();
    if (!name) return;

    this.username.set(name);

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
