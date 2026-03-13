import { Injectable, OnDestroy } from '@angular/core';
import {
  HubConnection,
  HubConnectionBuilder,
  HubConnectionState,
  LogLevel,
} from '@microsoft/signalr';
import { BehaviorSubject, Subject } from 'rxjs';
import { ChatMessage, TypingEvent } from '../models/chat-message.model';
import { environment } from '../../../environments/environment';

export type ConnectionStatus = 'disconnected' | 'connecting' | 'connected' | 'error';

@Injectable({ providedIn: 'root' })
export class SignalRService implements OnDestroy {
  private hubConnection!: HubConnection;

  readonly connectionStatus$ = new BehaviorSubject<ConnectionStatus>('disconnected');
  readonly connectionId$ = new BehaviorSubject<string | null>(null);
  readonly messages$ = new Subject<ChatMessage>();
  readonly messageHistory$ = new Subject<ChatMessage[]>();
  readonly userConnected$ = new Subject<string>();
  readonly userDisconnected$ = new Subject<string>();
  readonly userTyping$ = new Subject<TypingEvent>();

  get isConnected(): boolean {
    return this.hubConnection?.state === HubConnectionState.Connected;
  }

  async connect(): Promise<void> {
    this.hubConnection = new HubConnectionBuilder()
      .withUrl(`${environment.apiUrl}/hubs/chat`)
      .withAutomaticReconnect([0, 2000, 5000, 10000, 30000])
      .configureLogging(environment.production ? LogLevel.Warning : LogLevel.Information)
      .build();

    this.registerHandlers();

    this.connectionStatus$.next('connecting');
    try {
      await this.hubConnection.start();
      this.connectionStatus$.next('connected');
    } catch (err) {
      console.error('SignalR connection error:', err);
      this.connectionStatus$.next('error');
      throw err;
    }
  }

  async disconnect(): Promise<void> {
    if (this.hubConnection) {
      await this.hubConnection.stop();
      this.connectionStatus$.next('disconnected');
    }
  }

  async sendMessage(user: string, message: string): Promise<void> {
    if (!this.isConnected) throw new Error('Not connected to SignalR hub');
    await this.hubConnection.invoke('SendMessage', user, message);
  }

  async sendTyping(user: string, isTyping: boolean): Promise<void> {
    if (!this.isConnected) return;
    await this.hubConnection.invoke('SendTyping', user, isTyping);
  }

  private registerHandlers(): void {
    this.hubConnection.on('Connected', (connectionId: string) => {
      this.connectionId$.next(connectionId);
    });

    this.hubConnection.on('MessageHistory', (history: ChatMessage[]) => {
      this.messageHistory$.next(history);
    });

    this.hubConnection.on('ReceiveMessage', (message: ChatMessage) => {
      this.messages$.next(message);
    });

    this.hubConnection.on('UserConnected', (connectionId: string) => {
      this.userConnected$.next(connectionId);
    });

    this.hubConnection.on('UserDisconnected', (connectionId: string) => {
      this.userDisconnected$.next(connectionId);
    });

    this.hubConnection.on('UserTyping', (event: TypingEvent) => {
      this.userTyping$.next(event);
    });

    this.hubConnection.onreconnecting(() => {
      this.connectionStatus$.next('connecting');
    });

    this.hubConnection.onreconnected(() => {
      this.connectionStatus$.next('connected');
    });

    this.hubConnection.onclose(() => {
      this.connectionStatus$.next('disconnected');
      this.connectionId$.next(null);
    });
  }

  ngOnDestroy(): void {
    this.disconnect();
  }
}
