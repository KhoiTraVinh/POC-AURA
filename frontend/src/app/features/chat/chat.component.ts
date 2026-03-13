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
import { ChatService, MessageDto } from '../../core/services/chat.service';

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
  readonly groupId = signal<number>(1); // Hardcoded to 1 for POC
  readonly messageInput = signal('');
  readonly messages = signal<MessageDto[]>([]);
  readonly connectionStatus = signal<'disconnected' | 'connecting' | 'connected' | 'error'>('disconnected');
  readonly isJoined = signal(false);
  readonly usernameInput = signal('');
  
  // Track read receipts mapping messageId -> user
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
        this.markLatestAsRead();
      });

    this.chatService.readReceipts$
      .pipe(takeUntil(this.destroy$))
      .subscribe((receipt) => {
          if(receipt) {
             this.latestReadReceiptsMap.set(receipt.messageId, receipt.staffId);
             this.latestReadReceptId.set(receipt.messageId); 
          }
      });

    try {
      await this.chatService.startConnection(this.groupId());
      this.isJoined.set(true);
      this.connectionStatus.set('connected');
    } catch {
      this.connectionStatus.set('error');
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
      next: (sentMsg) => {
        this.messageInput.set('');
        // The message will be updated via SignalR notification -> GET API roundtrip.
        // We can optionally add it proactively, but listening to the pipe is safer for consistency.
      },
      error: (err) => console.error("Could not send message", err)
    });
  }

  markLatestAsRead(): void {
      const msgs = this.messages();
      if(msgs.length === 0) return;
      
      const lastMsgId = msgs[msgs.length - 1].id;
      
      // Simulate a Staff ID lookup based on string username for POC
      const staffIdMock = this.username().length; 

      this.chatService.markAsRead({
          groupId: this.groupId(),
          lastReadMessageId: lastMsgId,
          staffId: staffIdMock
      }).subscribe();
  }

  onKeyDown(event: KeyboardEvent): void {
    if (event.key === 'Enter' && !event.shiftKey) {
      event.preventDefault();
      this.send();
    }
  }

  isOwnMessage(msg: MessageDto): boolean {
    // Basic mock logic: In a real app we'd compare StaffIds. Here we check text for demonstration if possible
    // Using Ref is tricky, let's just pretend all messages aren't 'Own' for now, or match on string matching 'type' if we hacked it.
    // For this POC let's just return false unless we persist UserNames to the DB.
    return false; 
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
    this.chatService.stopConnection(this.groupId());
  }
}
