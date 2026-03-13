export interface ChatMessage {
  user: string;
  message: string;
  timestamp: string;
}

export interface TypingEvent {
  user: string;
  isTyping: boolean;
}
