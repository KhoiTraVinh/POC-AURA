import { Component, OnInit, OnDestroy, signal, inject } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { Subscription } from 'rxjs';
import { BatchImportService, BatchJob, ImportedRecord, RecordsPage } from '../../core/services/batch-import.service';
import { SessionService } from '../../core/services/session.service';

type Tab = 'import' | 'data';

@Component({
  selector: 'app-batch-import',
  standalone: true,
  imports: [CommonModule, FormsModule],
  template: `
<div class="page">
  <div class="page-header">
    <h2>📦 Batch Import</h2>
    <p class="desc">
      Upload CSV lớn → <strong>Hangfire</strong> chia chunk → <strong>SqlBulkCopy</strong> song song →
      realtime % qua <strong>SignalR</strong>.
    </p>
    <div class="arch-note">
      <strong>Flow:</strong> Angular upload →
      <code>POST /api/batch/upload</code> →
      Hangfire enqueue →
      4 threads × SqlBulkCopy →
      SignalR BatchProgress → Progress bar
    </div>
  </div>

  <!-- Tabs -->
  <div class="tabs">
    <button class="tab" [class.active]="tab() === 'import'" (click)="tab.set('import')">⬆ Import</button>
    <button class="tab" [class.active]="tab() === 'data'"   (click)="switchToData()">🗄 Xem Data</button>
  </div>

  <!-- ═══════════════════ IMPORT TAB ═══════════════════ -->
  @if (tab() === 'import') {

    <!-- Generate + Upload -->
    <div class="submit-card">
      <div class="gen-row">
        <span class="label">Tạo test data:</span>
        @for (n of [10_000, 50_000, 200_000, 500_000]; track n) {
          <button class="btn btn-outline btn-sm" (click)="generateCsv(n)" [disabled]="uploading()">
            {{ n.toLocaleString() }} rows (~{{ sizeMb(n) }} MB)
          </button>
        }
      </div>

      <div class="upload-row">
        <label class="file-label">
          <input type="file" accept=".csv" (change)="onFileSelected($event)" [disabled]="uploading()" />
          <span>{{ selectedFile()?.name ?? 'Chọn file CSV...' }}</span>
        </label>
        <button class="btn btn-primary"
                (click)="upload()"
                [disabled]="!selectedFile() || uploading()">
          @if (uploading()) { <span class="spinner"></span> }
          Bắt đầu import
        </button>
      </div>

      @if (uploadError()) {
        <div class="alert alert-error">{{ uploadError() }}</div>
      }
    </div>

    <!-- Active job progress -->
    @if (activeJob()) {
      <div class="progress-card">
        <div class="progress-header">
          <span class="filename">{{ activeJob()!.fileName }}</span>
          <span class="status-badge" [class]="'status-' + activeJob()!.status">
            {{ activeJob()!.status }}
          </span>
          @if (activeJob()!.status === 'running' || activeJob()!.status === 'queued') {
            <button class="btn btn-danger btn-sm" (click)="cancelJob(activeJob()!.id)">
              ✕ Hủy
            </button>
          }
          @if (activeJob()!.status === 'completed') {
            <button class="btn btn-outline btn-sm" (click)="viewJobData(activeJob()!)">
              🗄 Xem data →
            </button>
          }
        </div>

        <div class="progress-bar-wrap">
          <div class="progress-bar" [style.width.%]="activeJob()!.percent">
            {{ activeJob()!.percent }}%
          </div>
        </div>

        <div class="progress-stats">
          <span>{{ activeJob()!.processedRows.toLocaleString() }} / {{ activeJob()!.totalRows.toLocaleString() }} rows</span>
          @if (rps() > 0) {
            <span class="rps">{{ rps().toLocaleString() }} rows/s</span>
          }
          @if (activeJob()!.status === 'completed') {
            <span class="done">✓ Hoàn thành!</span>
          }
          @if (activeJob()!.status === 'failed') {
            <span class="fail">✗ Thất bại — Hangfire đang retry...</span>
          }
        </div>

        <div class="hangfire-link">
          Xem chi tiết job: <a href="/hangfire" target="_blank">Hangfire Dashboard →</a>
        </div>
      </div>
    }

    <!-- Recent jobs history -->
    @if (history().length > 0) {
      <div class="history-card">
        <h3>Lịch sử import gần đây</h3>
        <table class="history-table">
          <thead>
            <tr><th>File</th><th>Rows</th><th>Size</th><th>Status</th><th>Thời gian</th><th></th></tr>
          </thead>
          <tbody>
            @for (j of history(); track j.id) {
              <tr class="history-row" (click)="selectHistory(j)">
                <td>{{ j.fileName }}</td>
                <td>{{ j.totalRows.toLocaleString() }}</td>
                <td>{{ (j.fileSizeBytes / 1048576).toFixed(1) }} MB</td>
                <td><span class="status-badge" [class]="'status-' + j.status">{{ j.status }}</span></td>
                <td>{{ j.createdAt | date:'HH:mm:ss' }}</td>
                <td>
                  @if (j.status === 'completed') {
                    <button class="btn btn-outline btn-sm" (click)="viewJobData(j); $event.stopPropagation()">🗄 Data</button>
                  }
                </td>
              </tr>
            }
          </tbody>
        </table>
      </div>
    }
  }

  <!-- ═══════════════════ DATA TAB ═══════════════════ -->
  @if (tab() === 'data') {
    <div class="data-card">

      <!-- Job selector + filters -->
      <div class="data-toolbar">
        <div class="toolbar-left">
          <select class="select" [(ngModel)]="selectedBatchId" (ngModelChange)="onBatchChange($event)">
            <option value="">— Chọn batch job —</option>
            @for (j of completedJobs(); track j.id) {
              <option [value]="j.id">{{ j.fileName }} ({{ j.totalRows.toLocaleString() }} rows · {{ j.createdAt | date:'HH:mm dd/MM' }})</option>
            }
          </select>
        </div>
        <div class="toolbar-right">
          <input class="input" type="text" placeholder="Tìm Name..." [(ngModel)]="searchText" (keyup.enter)="loadRecords(1)" />
          <select class="select select-sm" [(ngModel)]="filterCategory" (ngModelChange)="loadRecords(1)">
            <option value="">Tất cả Category</option>
            @for (c of categories; track c) { <option [value]="c">{{ c }}</option> }
          </select>
          <button class="btn btn-primary btn-sm" (click)="loadRecords(1)">Tìm</button>
          <button class="btn btn-outline btn-sm" (click)="clearFilters()">Reset</button>
        </div>
      </div>

      <!-- Stats bar -->
      @if (recordsPage()) {
        <div class="stats-bar">
          <span>Tổng: <strong>{{ recordsPage()!.total.toLocaleString() }}</strong> records</span>
          <span>Trang {{ recordsPage()!.page }} / {{ recordsPage()!.totalPages }}</span>
          <span class="rps">Hiển thị {{ recordsPage()!.items.length }} rows</span>
        </div>
      }

      <!-- Table -->
      @if (recordsLoading()) {
        <div class="loading">
          <span class="spinner spinner-lg"></span>
          <span>Đang tải dữ liệu...</span>
        </div>
      } @else if (recordsPage()?.items?.length) {
        <div class="table-wrap">
          <table class="data-table">
            <thead>
              <tr>
                <th>#</th>
                <th>Name</th>
                <th>Category</th>
                <th class="num">Value</th>
                <th>Timestamp</th>
                <th>ImportedAt</th>
              </tr>
            </thead>
            <tbody>
              @for (r of recordsPage()!.items; track r.id) {
                <tr>
                  <td class="muted">{{ r.id }}</td>
                  <td>{{ r.name }}</td>
                  <td><span class="cat-badge">{{ r.category }}</span></td>
                  <td class="num">{{ r.value | number:'1.0-0' }}</td>
                  <td class="muted">{{ r.timestamp | date:'dd/MM/yyyy' }}</td>
                  <td class="muted">{{ r.importedAt | date:'HH:mm:ss' }}</td>
                </tr>
              }
            </tbody>
          </table>
        </div>

        <!-- Pagination -->
        <div class="pagination">
          <button class="btn btn-outline btn-sm" [disabled]="recordsPage()!.page <= 1" (click)="loadRecords(recordsPage()!.page - 1)">← Trước</button>
          @for (p of pageRange(); track p) {
            <button class="btn btn-sm"
                    [class.btn-primary]="p === recordsPage()!.page"
                    [class.btn-outline]="p !== recordsPage()!.page"
                    (click)="loadRecords(p)">{{ p }}</button>
          }
          <button class="btn btn-outline btn-sm" [disabled]="recordsPage()!.page >= recordsPage()!.totalPages" (click)="loadRecords(recordsPage()!.page + 1)">Sau →</button>
        </div>

      } @else if (selectedBatchId && !recordsLoading()) {
        <div class="empty">Không có dữ liệu. Thử thay đổi filter.</div>
      } @else {
        <div class="empty">Chọn một batch job ở trên để xem data đã import.</div>
      }
    </div>
  }
</div>
  `,
  styles: [`
    .page         { padding: 24px; max-width: 1100px; margin: 0 auto; }
    .page-header  { margin-bottom: 20px; }
    .page-header h2 { font-size: 22px; margin: 0 0 6px; }
    .desc         { color: #888; margin: 0 0 10px; }
    .arch-note    { background: #1e293b; border-radius: 8px; padding: 10px 14px; font-size: 13px; color: #94a3b8; }

    /* Tabs */
    .tabs         { display: flex; gap: 4px; margin-bottom: 20px; border-bottom: 1px solid #1e293b; }
    .tab          { padding: 8px 20px; border: none; background: transparent; color: #64748b; cursor: pointer; font-size: 14px; font-weight: 500; border-bottom: 2px solid transparent; margin-bottom: -1px; }
    .tab.active   { color: #3b82f6; border-bottom-color: #3b82f6; }
    .tab:hover:not(.active) { color: #94a3b8; }

    .submit-card, .progress-card, .history-card, .data-card {
      background: #0f172a; border: 1px solid #1e293b; border-radius: 12px;
      padding: 20px; margin-bottom: 20px;
    }
    .gen-row      { display: flex; align-items: center; gap: 8px; flex-wrap: wrap; margin-bottom: 14px; }
    .label        { color: #94a3b8; font-size: 13px; }
    .upload-row   { display: flex; gap: 10px; align-items: center; }
    .file-label   { flex: 1; cursor: pointer; }
    .file-label input { display: none; }
    .file-label span  {
      display: block; padding: 8px 14px; background: #1e293b;
      border-radius: 8px; color: #94a3b8; font-size: 14px; white-space: nowrap; overflow: hidden; text-overflow: ellipsis;
    }

    .btn          { padding: 8px 16px; border-radius: 8px; border: none; cursor: pointer; font-size: 14px; font-weight: 500; transition: opacity .15s; }
    .btn-primary  { background: #3b82f6; color: #fff; }
    .btn-outline  { background: transparent; border: 1px solid #334155; color: #94a3b8; }
    .btn-danger   { background: #ef4444; color: #fff; }
    .btn-sm       { padding: 5px 10px; font-size: 12px; }
    .btn:disabled { opacity: 0.4; cursor: not-allowed; }

    .spinner      { display: inline-block; width: 12px; height: 12px; border: 2px solid #fff; border-top-color: transparent; border-radius: 50%; animation: spin .6s linear infinite; margin-right: 6px; }
    .spinner-lg   { width: 20px; height: 20px; border-color: #3b82f6; border-top-color: transparent; margin-right: 10px; }
    @keyframes spin { to { transform: rotate(360deg); } }

    .alert-error  { background: #450a0a; color: #fca5a5; border-radius: 8px; padding: 10px 14px; margin-top: 12px; font-size: 13px; }

    .progress-header { display: flex; align-items: center; gap: 10px; margin-bottom: 14px; }
    .filename     { flex: 1; font-weight: 500; font-size: 15px; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }

    .progress-bar-wrap { background: #1e293b; border-radius: 6px; height: 28px; overflow: hidden; margin-bottom: 10px; }
    .progress-bar {
      height: 100%; background: linear-gradient(90deg, #3b82f6, #06b6d4);
      transition: width .3s ease; display: flex; align-items: center; justify-content: center;
      font-size: 13px; font-weight: 600; color: #fff; min-width: 40px;
    }

    .progress-stats { display: flex; gap: 20px; font-size: 13px; color: #94a3b8; }
    .rps   { color: #06b6d4; }
    .done  { color: #22c55e; font-weight: 600; }
    .fail  { color: #ef4444; }

    .hangfire-link { margin-top: 12px; font-size: 12px; color: #64748b; }
    .hangfire-link a { color: #3b82f6; }

    .status-badge { padding: 2px 8px; border-radius: 4px; font-size: 11px; font-weight: 600; }
    .status-queued    { background: #1e3a5f; color: #60a5fa; }
    .status-running   { background: #1a3a1a; color: #4ade80; }
    .status-completed { background: #14532d; color: #86efac; }
    .status-failed    { background: #450a0a; color: #fca5a5; }
    .status-cancelled { background: #292524; color: #a8a29e; }

    .history-card h3 { font-size: 14px; color: #94a3b8; margin: 0 0 12px; text-transform: uppercase; letter-spacing: .05em; }
    .history-table { width: 100%; border-collapse: collapse; font-size: 13px; }
    .history-table th { text-align: left; padding: 6px 10px; color: #64748b; border-bottom: 1px solid #1e293b; }
    .history-table td { padding: 8px 10px; border-bottom: 1px solid #0f172a; }
    .history-row:hover { background: #1e293b; cursor: pointer; }

    /* Data tab */
    .data-toolbar  { display: flex; justify-content: space-between; align-items: center; gap: 10px; margin-bottom: 16px; flex-wrap: wrap; }
    .toolbar-left  { flex: 1; min-width: 260px; }
    .toolbar-right { display: flex; gap: 8px; align-items: center; flex-wrap: wrap; }

    .select  { background: #1e293b; border: 1px solid #334155; color: #e2e8f0; padding: 7px 10px; border-radius: 8px; font-size: 13px; width: 100%; }
    .select-sm { width: auto; }
    .input   { background: #1e293b; border: 1px solid #334155; color: #e2e8f0; padding: 7px 10px; border-radius: 8px; font-size: 13px; }
    .input::placeholder { color: #475569; }

    .stats-bar { display: flex; gap: 24px; font-size: 13px; color: #64748b; margin-bottom: 12px; padding: 8px 0; border-bottom: 1px solid #1e293b; }
    .stats-bar strong { color: #e2e8f0; }

    .loading { display: flex; align-items: center; padding: 40px 0; color: #64748b; font-size: 14px; justify-content: center; }
    .empty   { text-align: center; padding: 40px 0; color: #475569; font-size: 14px; }

    .table-wrap { overflow-x: auto; }
    .data-table  { width: 100%; border-collapse: collapse; font-size: 13px; min-width: 700px; }
    .data-table th { text-align: left; padding: 8px 12px; color: #64748b; border-bottom: 1px solid #1e293b; font-weight: 500; white-space: nowrap; }
    .data-table td { padding: 7px 12px; border-bottom: 1px solid #0f172a; }
    .data-table tr:hover td { background: #1e293b; }
    .num  { text-align: right; font-variant-numeric: tabular-nums; }
    .muted { color: #64748b; }

    .cat-badge { padding: 2px 7px; border-radius: 4px; background: #1e3a5f; color: #60a5fa; font-size: 11px; font-weight: 600; }

    .pagination { display: flex; gap: 6px; align-items: center; justify-content: center; margin-top: 16px; flex-wrap: wrap; }
  `]
})
export class BatchImportComponent implements OnInit, OnDestroy {
  private svc     = inject(BatchImportService);
  private session = inject(SessionService);
  private subs    = new Subscription();

  // Import tab
  selectedFile = signal<File | null>(null);
  uploading    = signal(false);
  uploadError  = signal<string | null>(null);
  activeJob    = signal<BatchJob | null>(null);
  history      = signal<BatchJob[]>([]);
  rps          = signal(0);
  tab          = signal<Tab>('import');

  // Data tab
  selectedBatchId = '';
  searchText      = '';
  filterCategory  = '';
  recordsPage     = signal<RecordsPage | null>(null);
  recordsLoading  = signal(false);

  readonly categories = ['Electronics', 'Clothing', 'Food', 'Books', 'Sports', 'Health', 'Auto'];

  completedJobs = signal<BatchJob[]>([]);

  async ngOnInit() {
    const s = this.session.session();
    if (!s) return;

    await this.svc.connect(s.tenantId, s.name);

    this.subs.add(this.svc.progress$.subscribe(p => {
      if (this.activeJob()?.id !== p.batchId) return;
      this.activeJob.update(j => j ? { ...j, processedRows: p.processedRows, percent: p.percent, status: 'running' } : j);
      this.rps.set(p.rowsPerSecond);
    }));

    this.subs.add(this.svc.completed$.subscribe(c => {
      if (this.activeJob()?.id !== c.batchId) return;
      this.activeJob.update(j => j ? { ...j, percent: 100, processedRows: j.totalRows, status: 'completed' } : j);
      this.rps.set(c.rowsPerSecond);
      this.loadHistory();
    }));

    this.subs.add(this.svc.failed$.subscribe(f => {
      if (this.activeJob()?.id !== f.batchId) return;
      this.activeJob.update(j => j ? { ...j, status: 'failed' } : j);
      this.loadHistory();
    }));

    this.subs.add(this.svc.cancelled$.subscribe(c => {
      if (this.activeJob()?.id !== c.batchId) return;
      this.activeJob.update(j => j ? { ...j, status: 'cancelled' } : j);
      this.loadHistory();
    }));

    this.loadHistory();
  }

  ngOnDestroy() { this.subs.unsubscribe(); }

  onFileSelected(event: Event) {
    const f = (event.target as HTMLInputElement).files?.[0] ?? null;
    this.selectedFile.set(f);
    this.uploadError.set(null);
  }

  upload() {
    const file = this.selectedFile();
    if (!file) return;

    this.uploading.set(true);
    this.uploadError.set(null);

    this.svc.upload(file).subscribe({
      next: res => {
        this.uploading.set(false);
        this.activeJob.set({
          id: res.batchId, fileName: res.fileName, fileSizeBytes: file.size,
          totalRows: res.totalRows, processedRows: 0, percent: 0,
          status: 'queued', createdAt: new Date().toISOString()
        });
        this.selectedFile.set(null);
      },
      error: err => {
        this.uploading.set(false);
        this.uploadError.set(err?.error?.error ?? 'Upload thất bại');
      }
    });
  }

  cancelJob(batchId: string) {
    this.svc.cancel(batchId).subscribe({
      error: err => this.uploadError.set(err?.error?.error ?? 'Hủy thất bại')
    });
  }

  generateCsv(rows: number) { this.svc.generateTestCsv(rows); }

  sizeMb(rows: number): string {
    return ((rows * 45) / 1_048_576).toFixed(0);
  }

  selectHistory(job: BatchJob) { this.activeJob.set(job); }

  viewJobData(job: BatchJob) {
    this.selectedBatchId = job.id;
    this.searchText      = '';
    this.filterCategory  = '';
    this.tab.set('data');
    this.loadRecords(1);
  }

  switchToData() {
    this.tab.set('data');
    if (this.selectedBatchId) this.loadRecords(1);
  }

  onBatchChange(id: string) {
    this.selectedBatchId = id;
    this.recordsPage.set(null);
    if (id) this.loadRecords(1);
  }

  clearFilters() {
    this.searchText     = '';
    this.filterCategory = '';
    if (this.selectedBatchId) this.loadRecords(1);
  }

  loadRecords(page: number) {
    if (!this.selectedBatchId) return;
    this.recordsLoading.set(true);
    this.svc.getRecords(this.selectedBatchId, page, 100, this.searchText, this.filterCategory)
      .subscribe({
        next: data => { this.recordsPage.set(data); this.recordsLoading.set(false); },
        error: ()   => this.recordsLoading.set(false)
      });
  }

  pageRange(): number[] {
    const p = this.recordsPage();
    if (!p) return [];
    const cur = p.page, last = p.totalPages;
    const start = Math.max(1, cur - 2);
    const end   = Math.min(last, cur + 2);
    return Array.from({ length: end - start + 1 }, (_, i) => start + i);
  }

  private loadHistory() {
    this.svc.listJobs().subscribe(list => {
      this.history.set(list);
      this.completedJobs.set(list.filter(j => j.status === 'completed'));
    });
  }
}
