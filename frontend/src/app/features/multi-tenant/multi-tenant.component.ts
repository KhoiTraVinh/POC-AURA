import { Component, OnInit, OnDestroy, signal } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { Subscription } from 'rxjs';
import { PrintHubService, PrintJobResult } from '../../core/services/print-hub.service';

interface Toast {
  id: number;
  type: 'success' | 'error';
  title: string;
  message: string;
}

@Component({
  selector: 'app-multi-tenant',
  standalone: true,
  imports: [CommonModule, FormsModule],
  template: `
<div class="page">
  <div class="page-header">
    <h2>Print Job — Multi-Tenant</h2>
    <p class="desc">
      Mỗi browser tab chọn một tenant khác nhau. Angular chỉ gửi lệnh in —
      <strong>Blazor SmartHub</strong> (<a href="http://localhost:5001" target="_blank">localhost:5001</a>)
      nhận job, xử lý, rồi báo lại qua HTTP API. Backend dùng
      <code>IHubContext</code> bắn notification về đúng Angular tab.
    </p>
    <div class="arch-note">
      <strong>Flow:</strong> Angular <code>SubmitPrintJob</code> →
      <code>PrintHub</code> lưu DB + route → SmartHub nhận →
      SmartHub xử lý → <code>POST /api/print/complete</code> →
      Backend notify Angular → <strong>Popup</strong>
    </div>
  </div>

  <!-- Connection panel -->
  <div class="conn-card">
    <div class="conn-row">
      <div class="field">
        <label>Tenant</label>
        <select [(ngModel)]="selectedTenant" [disabled]="status() !== 'disconnected'" class="input">
          <option value="TenantA">TenantA</option>
          <option value="TenantB">TenantB</option>
          <option value="TenantC">TenantC</option>
        </select>
      </div>
      <div class="field">
        <label>Your Name</label>
        <input [(ngModel)]="userName" [disabled]="status() !== 'disconnected'"
          placeholder="e.g. Alice" class="input" />
      </div>
      <div class="field field-btn">
        @if (status() === 'disconnected') {
          <button class="btn btn-primary" (click)="connect()">Connect</button>
        } @else {
          <button class="btn btn-danger" (click)="disconnect()">Disconnect</button>
        }
      </div>
      <div class="status-chip" [class]="'chip-' + statusColor()">
        <span class="dot"></span> {{ status() }}
      </div>
    </div>
  </div>

  @if (status() === 'connected') {
    <!-- Submit form -->
    <div class="submit-card">
      <div class="submit-row">
        <input [(ngModel)]="docName" placeholder="Document name (e.g. Invoice-001)" class="input flex-2" />
        <input [(ngModel)]="docContent" placeholder="Content..." class="input flex-3" />
        <input [(ngModel)]="copies" type="number" min="1" max="10" class="input w80" />
        <button class="btn btn-success" (click)="submitJob()">
          🖨 Send Print Job
        </button>
      </div>
      <p class="hint">
        Mở 3 browser tab với tenant khác nhau để test tenant isolation.
        SmartHub phải đang kết nối tại <a href="http://localhost:5001" target="_blank">localhost:5001</a>
      </p>
    </div>

    <!-- Pending jobs -->
    @if (pendingJobs().length > 0) {
      <div class="section-card">
        <div class="section-title">Pending <span class="badge-count">{{ pendingJobs().length }}</span></div>
        @for (job of pendingJobs(); track job.id) {
          <div class="job-row job-pending">
            <span class="spinner">⏳</span>
            <span class="job-name">{{ job.documentName }}</span>
            <span class="job-id">#{{ job.id }}</span>
            <span class="job-copies">×{{ job.copies }}</span>
          </div>
        }
      </div>
    }

    <!-- Completed jobs -->
    @if (completedJobs().length > 0) {
      <div class="section-card">
        <div class="section-title">Completed (last {{ completedJobs().length }})</div>
        @for (r of completedJobs().slice(-10).reverse(); track r.jobId) {
          <div class="job-row" [class.job-ok]="r.success" [class.job-fail]="!r.success">
            {{ r.success ? '✓' : '✗' }}
            <span class="job-id">#{{ r.jobId }}</span>
            <span class="job-msg">{{ r.message }}</span>
          </div>
        }
      </div>
    }
  }

  <!-- Log -->
  <div class="log-card">
    <div class="log-title">Activity Log</div>
    @for (entry of logs().slice(-12); track $index) {
      <div class="log-line" [class]="'log-' + entry.type">
        <span class="log-t">{{ entry.time }}</span> {{ entry.msg }}
      </div>
    }
  </div>
</div>

<!-- Toast notifications -->
<div class="toast-area">
  @for (t of toasts(); track t.id) {
    <div class="toast" [class.toast-ok]="t.type === 'success'" [class.toast-fail]="t.type === 'error'">
      <div class="toast-title">{{ t.title }}</div>
      <div class="toast-msg">{{ t.message }}</div>
    </div>
  }
</div>
  `,
  styles: [`
    .page { padding: 24px; font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', sans-serif;
            max-width: 900px; margin: 0 auto; display: flex; flex-direction: column; gap: 14px; }
    .page-header h2 { margin: 0 0 8px; font-size: 20px; color: #1a1a2e; }
    .desc { color: #555; font-size: 13px; margin: 0 0 8px; line-height: 1.5; }
    .desc a { color: #007bff; }
    code { background: #f0f0f0; padding: 1px 5px; border-radius: 3px; font-size: 11px; }
    .arch-note { background: #e8f4f8; border-left: 3px solid #007bff; padding: 8px 12px; font-size: 12px; border-radius: 0 4px 4px 0; line-height: 1.6; }

    .conn-card, .submit-card, .section-card, .log-card {
      background: #fafafa; border: 1px solid #e0e0e0; border-radius: 8px; padding: 14px 16px;
    }
    .conn-row { display: flex; align-items: flex-end; gap: 10px; flex-wrap: wrap; }
    .field { display: flex; flex-direction: column; gap: 3px; }
    .field label { font-size: 11px; color: #666; font-weight: 500; }
    .field-btn { justify-content: flex-end; }

    .input { padding: 7px 10px; border: 1px solid #ccc; border-radius: 4px; font-size: 13px; background: white; }
    .input:disabled { background: #f5f5f5; color: #999; }
    .flex-2 { flex: 2; min-width: 140px; }
    .flex-3 { flex: 3; min-width: 160px; }
    .w80 { width: 70px; }

    .status-chip { display: flex; align-items: center; gap: 6px; padding: 5px 12px;
                   border-radius: 16px; font-size: 12px; font-weight: 500; }
    .chip-green { background: #d4edda; color: #155724; }
    .chip-yellow { background: #fff3cd; color: #856404; }
    .chip-gray { background: #e2e3e5; color: #383d41; }
    .dot { width: 7px; height: 7px; border-radius: 50%; background: currentColor; }

    .btn { padding: 7px 14px; border: none; border-radius: 4px; cursor: pointer; font-size: 13px; font-weight: 500; }
    .btn-primary { background: #007bff; color: white; }
    .btn-danger { background: #dc3545; color: white; }
    .btn-success { background: #28a745; color: white; }

    .submit-row { display: flex; gap: 8px; flex-wrap: wrap; align-items: center; }
    .hint { font-size: 11px; color: #888; margin: 8px 0 0; font-style: italic; }
    .hint a { color: #007bff; }

    .section-title { font-size: 12px; font-weight: 600; color: #555; text-transform: uppercase;
                     letter-spacing: 0.5px; margin-bottom: 8px; display: flex; align-items: center; gap: 6px; }
    .badge-count { background: #007bff; color: white; border-radius: 10px; padding: 1px 7px; font-size: 11px; }

    .job-row { display: flex; align-items: center; gap: 10px; padding: 6px 8px;
               border-radius: 4px; font-size: 12px; margin-bottom: 4px; }
    .job-pending { background: #fff3cd; }
    .job-ok { background: #d4edda; }
    .job-fail { background: #f8d7da; }
    .spinner { font-size: 14px; }
    .job-name { font-weight: 500; flex: 1; }
    .job-id { color: #888; font-size: 11px; font-family: monospace; }
    .job-copies { color: #666; font-size: 11px; }
    .job-msg { color: #555; flex: 1; }

    .log-card { background: #1a1a2e; border-color: #1a1a2e; }
    .log-title { font-size: 10px; color: #555; margin-bottom: 4px; text-transform: uppercase; letter-spacing: 0.5px; }
    .log-line { font-size: 11px; font-family: 'Consolas', monospace; margin-bottom: 2px; }
    .log-t { color: #555; }
    .log-info { color: #9cdcfe; }
    .log-success { color: #4ec9b0; }
    .log-error { color: #f44747; }
    .log-warn { color: #ce9178; }

    /* Toast notifications */
    .toast-area { position: fixed; bottom: 24px; right: 24px; display: flex; flex-direction: column;
                  gap: 10px; z-index: 9999; pointer-events: none; }
    .toast { background: white; border-radius: 8px; padding: 12px 16px; min-width: 260px;
             box-shadow: 0 4px 16px rgba(0,0,0,.15); border-left: 4px solid; animation: slideIn .3s ease; }
    .toast-ok { border-color: #28a745; }
    .toast-fail { border-color: #dc3545; }
    .toast-title { font-weight: 600; font-size: 13px; margin-bottom: 3px; }
    .toast-ok .toast-title { color: #155724; }
    .toast-fail .toast-title { color: #721c24; }
    .toast-msg { font-size: 12px; color: #555; }
    @keyframes slideIn { from { transform: translateX(110%); opacity: 0; } to { transform: translateX(0); opacity: 1; } }
  `]
})
export class MultiTenantComponent implements OnInit, OnDestroy {
  selectedTenant = 'TenantA';
  userName = 'Alice';
  docName = '';
  docContent = '';
  copies = 1;

  status = signal<string>('disconnected');
  pendingJobs = signal<Array<{ id: string; documentName: string; copies: number }>>([]);
  completedJobs = signal<PrintJobResult[]>([]);
  logs = signal<Array<{ time: string; type: string; msg: string }>>([]);
  toasts = signal<Toast[]>([]);

  private subs: Subscription[] = [];
  private toastCounter = 0;

  constructor(private printHub: PrintHubService) {}

  ngOnInit() {
    this.subs.push(
      this.printHub.onPrintJobQueued$.subscribe(({ tenantId, job }) => {
        if (tenantId !== this.selectedTenant) return;
        this.pendingJobs.update(list => [...list, { id: job.id, documentName: job.documentName, copies: job.copies }]);
        this.addLog('info', `Queued #${job.id} "${job.documentName}" → SmartHub`);
      }),
      this.printHub.onPrintJobComplete$.subscribe(({ tenantId, result }) => {
        if (tenantId !== this.selectedTenant) return;
        this.pendingJobs.update(list => list.filter(j => j.id !== result.jobId));
        this.completedJobs.update(list => [...list, result]);
        this.addLog(result.success ? 'success' : 'error',
          `#${result.jobId} ${result.success ? 'Done ✓' : 'Failed ✗'}: ${result.message}`);
        this.showToast(result);
      }),
      this.printHub.onPrintJobStatusUpdate$.subscribe(({ tenantId, result }) => {
        if (tenantId !== this.selectedTenant) return;
        this.pendingJobs.update(list => list.filter(j => j.id !== result.jobId));
        this.addLog(result.success ? 'success' : 'error',
          `[StatusUpdate] #${result.jobId} ${result.success ? 'done' : 'failed'} (another UI)`);
      }),
      this.printHub.onClientConnected$.subscribe(info => {
        if (info.tenantId === this.selectedTenant)
          this.addLog('info', `Connected: ${info.clientType} (${info.userName})`);
      }),
      this.printHub.onClientDisconnected$.subscribe(info => {
        if (info.tenantId === this.selectedTenant)
          this.addLog('warn', `Disconnected: ${info.clientType}`);
      })
    );
  }

  ngOnDestroy() {
    this.subs.forEach(s => s.unsubscribe());
    this.printHub.disconnect(this.selectedTenant);
  }

  async connect() {
    try {
      this.status.set('connecting');
      await this.printHub.connect(this.selectedTenant, this.userName);
      this.subs.push(
        this.printHub.getStatusStream(this.selectedTenant).subscribe(s => this.status.set(s))
      );
      this.status.set('connected');
      this.addLog('success', `Connected as ${this.userName} [${this.selectedTenant}]`);
    } catch (err) {
      this.status.set('disconnected');
      this.addLog('error', `Connection failed: ${err}`);
    }
  }

  async disconnect() {
    await this.printHub.disconnect(this.selectedTenant);
    this.status.set('disconnected');
    this.addLog('warn', 'Disconnected');
  }

  async submitJob() {
    const name = this.docName.trim() || 'Invoice-001';
    const content = this.docContent.trim() || 'Sample content';
    try {
      await this.printHub.submitPrintJob(this.selectedTenant, name, content, this.copies);
    } catch (err) {
      this.addLog('error', `Submit failed: ${err}`);
    }
  }

  statusColor(): string {
    if (this.status() === 'connected') return 'green';
    if (this.status() === 'connecting' || this.status() === 'reconnecting') return 'yellow';
    return 'gray';
  }

  private showToast(result: PrintJobResult) {
    const id = ++this.toastCounter;
    const toast: Toast = {
      id,
      type: result.success ? 'success' : 'error',
      title: result.success ? '🖨 Print Complete!' : '🖨 Print Failed',
      message: `#${result.jobId}: ${result.message}`
    };
    this.toasts.update(list => [...list, toast]);
    setTimeout(() => this.toasts.update(list => list.filter(t => t.id !== id)), 5000);
  }

  private addLog(type: string, msg: string) {
    const time = new Date().toLocaleTimeString('vi-VN', { hour12: false });
    this.logs.update(list => [...list, { time, type, msg }]);
  }
}
