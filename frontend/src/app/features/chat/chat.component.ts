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
import { Subject, takeUntil, debounceTime, distinctUntilChanged } from 'rxjs';
import { SignalRService, ConnectionStatus } from '../../core/services/signalr.service';
import { ChatMessage } from '../../core/models/chat-message.model';

@Component({
  selector: 'app-chat',
  imports: [CommonModule, FormsModule],
  templateUrl: './chat.component.html',
  styleUrl: './chat.component.scss',
})
export class ChatComponent implements OnInit, OnDestroy {
  @ViewChild('messagesContainer') messagesContainer!: ElementRef<HTMLDivElement>;

  readonly username = signal('');
  readonly messageInput = signal('');
  readonly messages = signal<ChatMessage[]>([]);
  readonly typingUsers = signal<Set<string>>(new Set());
  readonly connectionStatus = signal<ConnectionStatus>('disconnected');
  readonly connectionId = signal<string | null>(null);
  readonly isJoined = signal(false);
  readonly usernameInput = signal('');

  readonly typingUsersArray = computed(() => [...this.typingUsers()]);
  readonly isConnected = computed(() => this.connectionStatus() === 'connected');

  private readonly destroy$ = new Subject<void>();
  private readonly typingSubject$ = new Subject<boolean>();

  constructor(private readonly signalR: SignalRService) {}

  ngOnInit(): void {
    this.typingSubject$
      .pipe(debounceTime(500), distinctUntilChanged(), takeUntil(this.destroy$))
      .subscribe((isTyping) => {
        if (this.isJoined()) {
          this.signalR.sendTyping(this.username(), isTyping);
        }
      });
  }

  async join(): Promise<void> {
    const name = this.usernameInput().trim();
    if (!name) return;

    this.username.set(name);

    this.signalR.connectionStatus$
      .pipe(takeUntil(this.destroy$))
      .subscribe((status) => this.connectionStatus.set(status));

    this.signalR.connectionId$
      .pipe(takeUntil(this.destroy$))
      .subscribe((id) => this.connectionId.set(id));

    this.signalR.messageHistory$
      .pipe(takeUntil(this.destroy$))
      .subscribe((history) => {
        this.messages.set(history);
        this.scrollToBottom();
      });

    this.signalR.messages$
      .pipe(takeUntil(this.destroy$))
      .subscribe((msg) => {
        this.messages.update((msgs) => [...msgs, msg]);
        this.scrollToBottom();
      });

    this.signalR.userTyping$
      .pipe(takeUntil(this.destroy$))
      .subscribe(({ user, isTyping }) => {
        this.typingUsers.update((set) => {
          const updated = new Set(set);
          if (isTyping) updated.add(user);
          else updated.delete(user);
          return updated;
        });
      });

    try {
      await this.signalR.connect();
      this.isJoined.set(true);
    } catch {
      this.connectionStatus.set('error');
    }
  }

  async send(): Promise<void> {
    const msg = this.messageInput().trim();
    if (!msg || !this.isConnected()) return;

    await this.signalR.sendMessage(this.username(), msg);
    this.messageInput.set('');
    this.typingSubject$.next(false);
  }

  onTyping(): void {
    this.typingSubject$.next(true);
    setTimeout(() => this.typingSubject$.next(false), 2000);
  }

  onKeyDown(event: KeyboardEvent): void {
    if (event.key === 'Enter' && !event.shiftKey) {
      event.preventDefault();
      this.send();
    }
  }

  isOwnMessage(msg: ChatMessage): boolean {
    return msg.user === this.username();
  }

  formatTime(timestamp: string): string {
    return new Date(timestamp).toLocaleTimeString('vi-VN', {
      hour: '2-digit',
      minute: '2-digit',
    });
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
    this.signalR.disconnect();
  }
}
