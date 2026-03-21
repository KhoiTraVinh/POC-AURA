import { Injectable } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Observable } from 'rxjs';
import { MessageDto, SendMessageRequest, ReadReceiptDto } from './chat.types';

@Injectable({
  providedIn: 'root'
})
export class ChatHttpService {
  private readonly apiUrl = '/api/messages';

  constructor(private http: HttpClient) {}

  getReceipt(groupId: number, staffId: number): Observable<ReadReceiptDto> {
    return this.http.get<ReadReceiptDto>(`${this.apiUrl}/receipt?groupId=${groupId}&staffId=${staffId}`);
  }

  getMessages(groupId: number, afterMessageId?: number | null): Observable<MessageDto[]> {
    let url = `${this.apiUrl}/${groupId}`;
    if (afterMessageId !== null && afterMessageId !== undefined) {
      url += `?afterMessageId=${afterMessageId}`;
    }
    return this.http.get<MessageDto[]>(url);
  }

  sendMessage(request: SendMessageRequest): Observable<MessageDto> {
    return this.http.post<MessageDto>(this.apiUrl, request);
  }
}
