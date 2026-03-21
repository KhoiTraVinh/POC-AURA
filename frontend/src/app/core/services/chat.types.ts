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
