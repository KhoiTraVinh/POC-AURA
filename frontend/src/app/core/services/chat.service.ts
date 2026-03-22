import { Injectable, OnDestroy } from "@angular/core";
import { BehaviorSubject, firstValueFrom, Observable } from "rxjs";
import { MessageDto, ConnectionStatus, SendMessageRequest } from "./chat.types";
import { ChatHttpService } from "./chat-http.service";
import { ChatSignalrService } from "./chat-signalr.service";

@Injectable({
  providedIn: "root",
})
export class ChatService implements OnDestroy {
  // State
  private messagesSubject = new BehaviorSubject<MessageDto[]>([]);
  public messages$ = this.messagesSubject.asObservable();

  private readReceiptsSubject = new BehaviorSubject<{
    staffId: number;
    messageId: number;
  } | null>(null);
  public readReceipts$ = this.readReceiptsSubject.asObservable();

  private connectionStatusSubject = new BehaviorSubject<ConnectionStatus>(
    "disconnected",
  );
  public connectionStatus$ = this.connectionStatusSubject.asObservable();

  // Context for reconnecting when needed
  private currentGroupId: number | null = null;
  private currentStaffId: number | null = null;
  private localLatestReadPointer: number | null = null;

  // Differentiate: user intentionally stopped vs network loss
  private isIntentionallyStopped = false;

  private readonly onlineHandler = () => this.handleOnline();
  private readonly offlineHandler = () => this.handleOffline();

  constructor(
    private chatHttp: ChatHttpService,
    private chatSignalr: ChatSignalrService,
  ) {
    // When browser detects network is back — fallback if auto-reconnect stopped
    window.addEventListener("online", this.onlineHandler);
    window.addEventListener("offline", this.offlineHandler);

    // Subscribe to SignalR events
    this.chatSignalr.connectionStatus$.subscribe((status) => {
      // Force reconnecting state if explicitly offline
      if (!navigator.onLine && !this.isIntentionallyStopped) {
        this.connectionStatusSubject.next("reconnecting");
      } else {
        this.connectionStatusSubject.next(status);
      }

      if (status === "connected") {
        console.log("aaaaaaaaaaaaaaa");
        this.fetchLatestMessages();
      }
    });

    this.chatSignalr.newMessage$.subscribe(() => {
      this.fetchLatestMessages();
    });

    this.chatSignalr.userReadReceipt$.subscribe((receipt) => {
      this.readReceiptsSubject.next(receipt);
    });
  }

  ngOnDestroy(): void {
    window.removeEventListener("online", this.onlineHandler);
    window.removeEventListener("offline", this.offlineHandler);
  }

  private async handleOffline(): Promise<void> {
    if (!this.isIntentionallyStopped) {
      if (!navigator.onLine) {
        this.connectionStatusSubject.next("reconnecting");
      }
      // Kill the zombied SignalR connection immediately
      await this.chatSignalr.forceClientStop();
    }
  }

  private async handleOnline(): Promise<void> {
    const status = this.connectionStatusSubject.value;
    if (
      !this.isIntentionallyStopped &&
      this.currentGroupId !== null &&
      this.currentStaffId !== null
    ) {
      // Browser says online -> force start connection to bypass 24hr idle timeout
      await this.startConnection(this.currentGroupId, this.currentStaffId);
    }
  }

  public async startConnection(
    groupId: number,
    staffId: number,
  ): Promise<void> {
    this.isIntentionallyStopped = false;
    this.currentGroupId = groupId;
    this.currentStaffId = staffId;
    this.localLatestReadPointer = null;

    await this.chatSignalr.startConnection(groupId);
    // Note: The 'connected' status from SignalR service will trigger fetchLatestMessages
  }

  // Normal connection stop — user explicitly leaves
  public async stopConnection(): Promise<void> {
    this.isIntentionallyStopped = true;
    await this.chatSignalr.stopConnection(this.currentGroupId);

    this.messagesSubject.next([]);
    this.connectionStatusSubject.next("disconnected");
    this.currentGroupId = null;
    this.currentStaffId = null;
    this.localLatestReadPointer = null;
  }

  private async fetchLatestMessages(): Promise<void> {
    if (this.currentGroupId === null || this.currentStaffId === null) return;

    try {
      let pointer = this.localLatestReadPointer;

      // Hit DB only once when local memory is cold (first load)
      if (pointer === null) {
        const receipt = await firstValueFrom(
          this.chatHttp.getReceipt(this.currentGroupId, this.currentStaffId),
        );
        pointer = receipt.lastReadMessageId ?? 0;
      }

      // Get messages from HTTP (GET /messages?afterMessageId)
      const newMessages = await firstValueFrom(
        this.chatHttp.getMessages(this.currentGroupId, pointer),
      );

      if (newMessages.length > 0) {
        const current = this.messagesSubject.value;
        const existingIds = new Set(current.map((m) => m.id));
        const unique = newMessages.filter((m) => !existingIds.has(m.id));
        this.messagesSubject.next([...current, ...unique]);

        const maxId = Math.max(...newMessages.map((m) => m.id));

        // Caching locally to prevent future DB hits
        this.localLatestReadPointer = maxId;

        // Let server update pointer + broadcast UserReadReceipt to group
        this.chatSignalr.updateReadPointer(
          this.currentGroupId,
          this.currentStaffId,
          maxId,
        );
      } else {
        // Even if no new messages were found, initialize the local pointer so it doesn't hit DB next time
        if (this.localLatestReadPointer === null) {
          this.localLatestReadPointer = pointer;
        }
      }
    } catch (err) {
      console.error("Failed to fetch latest messages:", err);
    }
  }

  public sendMessage(request: SendMessageRequest): Observable<MessageDto> {
    return this.chatHttp.sendMessage(request);
  }
}
