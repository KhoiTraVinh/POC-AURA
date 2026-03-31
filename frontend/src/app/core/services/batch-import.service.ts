import { Injectable, inject } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import * as signalR from '@microsoft/signalr';
import { Subject, Observable, from, switchMap } from 'rxjs';
import { AuthService } from './auth.service';

export interface BatchProgress {
  batchId:       string;
  processedRows: number;
  totalRows:     number;
  percent:       number;
  rowsPerSecond: number;
}

export interface BatchCompleted {
  batchId:       string;
  processedRows: number;
  durationMs:    number;
  rowsPerSecond: number;
}

export interface ImportedRecord {
  id:          number;
  name:        string;
  category:    string;
  value:       number;
  timestamp:   string;
  importedAt:  string;
}

export interface RecordsPage {
  total:      number;
  page:       number;
  pageSize:   number;
  totalPages: number;
  items:      ImportedRecord[];
}

export interface BatchJob {
  id:            string;
  fileName:      string;
  fileSizeBytes: number;
  totalRows:     number;
  processedRows: number;
  percent:       number;
  status:        'queued' | 'running' | 'completed' | 'failed' | 'cancelled';
  createdAt:     string;
  completedAt?:  string;
}

@Injectable({ providedIn: 'root' })
export class BatchImportService {
  private http = inject(HttpClient);
  private auth = inject(AuthService);

  private hub?: signalR.HubConnection;
  private tenantId = '';
  private userName = '';

  private _progress$   = new Subject<BatchProgress>();
  private _completed$  = new Subject<BatchCompleted>();
  private _failed$     = new Subject<{ batchId: string; error: string }>();
  private _cancelled$  = new Subject<{ batchId: string }>();

  readonly progress$   = this._progress$.asObservable();
  readonly completed$  = this._completed$.asObservable();
  readonly failed$     = this._failed$.asObservable();
  readonly cancelled$  = this._cancelled$.asObservable();

  // ── SignalR ──────────────────────────────────────────────────────────────

  async connect(tenantId: string, userName: string): Promise<void> {
    this.tenantId = tenantId;
    this.userName = userName;

    if (this.hub?.state === signalR.HubConnectionState.Connected) return;

    this.hub = new signalR.HubConnectionBuilder()
      .withUrl('/hubs/aura', {
        accessTokenFactory: () =>
          this.auth.getToken(tenantId, 'ui', userName).then(p => p.accessToken),
      })
      .withAutomaticReconnect()
      .build();

    this.hub.on('BatchProgress',  (d: BatchProgress)  => this._progress$.next(d));
    this.hub.on('BatchCompleted', (d: BatchCompleted)  => this._completed$.next(d));
    this.hub.on('BatchFailed',    (d: any)             => this._failed$.next(d));
    this.hub.on('BatchCancelled', (d: any)             => this._cancelled$.next(d));

    await this.hub.start();
  }

  // ── HTTP ─────────────────────────────────────────────────────────────────

  upload(file: File): Observable<{ batchId: string; totalRows: number; fileName: string }> {
    const form = new FormData();
    form.append('file', file);
    return from(this.auth.getToken(this.tenantId, 'ui', this.userName)).pipe(
      switchMap(token => {
        const headers = { Authorization: `Bearer ${token.accessToken}` };
        return this.http.post<any>('/api/batch/upload', form, { headers });
      })
    );
  }

  cancel(batchId: string): Observable<void> {
    return from(this.auth.getToken(this.tenantId, 'ui', this.userName)).pipe(
      switchMap(token => {
        const headers = { Authorization: `Bearer ${token.accessToken}` };
        return this.http.delete<void>(`/api/batch/${batchId}`, { headers });
      })
    );
  }

  getStatus(batchId: string): Observable<BatchJob> {
    return from(this.auth.getToken(this.tenantId, 'ui', this.userName)).pipe(
      switchMap(token => {
        const headers = { Authorization: `Bearer ${token.accessToken}` };
        return this.http.get<BatchJob>(`/api/batch/${batchId}`, { headers });
      })
    );
  }

  listJobs(): Observable<BatchJob[]> {
    return from(this.auth.getToken(this.tenantId, 'ui', this.userName)).pipe(
      switchMap(token => {
        const headers = { Authorization: `Bearer ${token.accessToken}` };
        return this.http.get<BatchJob[]>('/api/batch', { headers });
      })
    );
  }

  getRecords(batchId: string, page = 1, pageSize = 100, search = '', category = ''): Observable<RecordsPage> {
    return from(this.auth.getToken(this.tenantId, 'ui', this.userName)).pipe(
      switchMap(token => {
        const headers = { Authorization: `Bearer ${token.accessToken}` };
        const params: Record<string, string> = { page: String(page), pageSize: String(pageSize) };
        if (search)   params['search']   = search;
        if (category) params['category'] = category;
        return this.http.get<RecordsPage>(`/api/batch/${batchId}/records`, { headers, params });
      })
    );
  }

  // ── CSV generator ────────────────────────────────────────────────────────

  generateTestCsv(rowCount: number): void {
    const cats = ['Electronics', 'Clothing', 'Food', 'Books', 'Sports', 'Health', 'Auto'];
    const rows = ['Name,Category,Value,Timestamp'];

    for (let i = 0; i < rowCount; i++) {
      const name     = `Product-${String(i).padStart(7, '0')}`;
      const category = cats[i % cats.length];
      const value    = (Math.random() * 10_000_000).toFixed(0);
      const days     = Math.floor(Math.random() * 730);
      const ts       = new Date(Date.now() - days * 86_400_000).toISOString().slice(0, 10);
      rows.push(`${name},${category},${value},${ts}`);
    }

    const blob = new Blob([rows.join('\n')], { type: 'text/csv' });
    const url  = URL.createObjectURL(blob);
    const a    = document.createElement('a');
    a.href     = url;
    a.download = `test-data-${rowCount}.csv`;
    a.click();
    URL.revokeObjectURL(url);
  }
}
