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
import { ChatService } from '../../core/services/chat.service';
import { ConnectionStatus, MessageDto } from '../../core/services/chat.types';

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
  readonly isJoined = signal(false);
  readonly usernameInput = signal('');

  // Track the minimum read receipt ID (for all group members)
  readonly latestReadReceptId = signal<number | null>(null);

  readonly isConnected = computed(() => this.connectionStatus() === 'connected');

  private readonly destroy$ = new Subject<void>();

  constructor(
    private readonly chatService: ChatService,
    private readonly http: HttpClient,
  ) {}

  ngOnInit(): void {
    // Subscriptions placed here — only subscribe once during the component lifecycle

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
          // The backend now sends the group's minimum read pointer instead of individual
          this.latestReadReceptId.set(receipt.messageId);
        }
      });

    this.chatService.connectionStatus$
      .pipe(takeUntil(this.destroy$))
      .subscribe((status) => {
        this.connectionStatus.set(status);
      });
  }

  async join(): Promise<void> {
    // Guard: block re-joining when connected or during the joining process
    if (this.isJoined()) return;

    const name = this.usernameInput().trim();
    if (!name) return;

    this.username.set(name);

    try {
      await this.chatService.startConnection(this.groupId(), this.staffIdInput());
      this.isJoined.set(true);
    } catch {
      // connectionStatus$ will automatically emit 'disconnected'
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
