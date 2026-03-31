import { Component, OnInit, OnDestroy, signal, inject } from '@angular/core';
import { CommonModule, DatePipe } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { Subscription } from 'rxjs';
import { TransactionService } from '../../core/services/transaction.service';
import type { BankStatus, TransactionSubmitResult } from '../../core/models';
import { SessionService } from '../../core/services/session.service';

@Component({
  selector: 'app-transaction-queue',
  standalone: true,
  imports: [CommonModule, FormsModule, DatePipe],
  template: `
<div class="page">
  <div class="page-header">
    <h2>🏦 Bank Transaction</h2>
    <p class="desc">
      Gửi lệnh thanh toán — <strong>Blazor SmartHub</strong>
      (<a href="http://localhost:5001/bank" target="_blank">localhost:5001/bank</a>)
      xử lý và báo lại qua <code>POST /api/transaction/complete</code>.
      Bank là tài nguyên chung: chỉ <strong>1 transaction tại một thời điểm</strong> cho tất cả tenant.
    </p>
    <div class="arch-note">
      <strong>Flow:</strong> Angular <code>SubmitTransaction</code> →
      global lock + lưu DB + route →
      SmartHub xử lý (3–7s) → <code>POST /api/transaction/complete</code> →
      Backend release lock + broadcast → Angular nhận kết quả
    </div>
  </div>

  <div class="layout">
    <!-- Left: submit -->
    <div class="panel">
      <h3>Gửi thanh toán</h3>

      <div class="form-group">
        <label>Mô tả</label>
        <input [(ngModel)]="description" placeholder="Vd: Payment Invoice #123" class="input" />
      </div>
      <div class="form-group">
        <label>Số tiền (VND)</label>
        <input [(ngModel)]="amount" type="number" placeholder="1000000" class="input" />
      </div>

      <div class="btn-row">
        <button class="btn btn-success btn-full"
          [disabled]="connStatus() !== 'connected'"
          (click)="submit()">Gửi thanh toán</button>
      </div>

      <p class="hint">
        Gửi nhanh nhiều lần để thấy reject khi bank đang bận.
      </p>

      @if (lastResult()) {
        <div class="result-box" [class.result-ok]="lastResult()!.status === 'accepted'"
             [class.result-fail]="lastResult()!.status === 'rejected'">
          <strong>{{ lastResult()!.status === 'accepted' ? '✓ Accepted' : '✗ Rejected' }}</strong>
          <p>{{ lastResult()!.message }}</p>
          @if (lastResult()!.currentlyProcessing) {
            <small>Đang xử lý: [{{ lastResult()!.currentlyProcessing!.id }}] {{ lastResult()!.currentlyProcessing!.description }}</small>
          }
        </div>
      }

      <div class="conn-row">
        <span class="dot" [class]="'dot-' + connStatus()"></span>
        <span>{{ connStatus() }}</span>
        <span class="tenant-badge">{{ tenantId }}</span>
      </div>
    </div>

    <!-- Middle: bank status -->
    <div class="panel">
      <h3>Bank Status [{{ tenantId }}]</h3>
      <div class="bank-card" [class.bank-busy]="bankStatus()?.isBankBusy" [class.bank-free]="!bankStatus()?.isBankBusy">
        <div class="bank-indicator">
          <div class="bank-light"></div>
          <span>{{ bankStatus()?.isBankBusy ? 'BUSY — Processing' : 'FREE — Ready' }}</span>
        </div>
        @if (bankStatus()?.currentTransaction; as cur) {
          <div class="current-tx">
            <div class="cur-id">[{{ cur.id }}]</div>
            <div class="cur-desc">{{ cur.description }}</div>
            <div class="cur-time">Since {{ cur.submittedAt | date:'HH:mm:ss' }}</div>
          </div>
        }
      </div>

      <div class="live-log">
        <div class="live-log-title">Live Events</div>
        @for (e of liveEvents().slice(-8); track $index) {
          <div class="live-entry" [class]="'le-' + e.state">
            <span class="le-time">{{ e.time }}</span>
            <span class="le-id">[{{ e.id }}]</span>
            {{ e.message }}
          </div>
        }
      </div>
    </div>

    <!-- Right: history -->
    <div class="panel">
      <h3>History</h3>
      @if (!bankStatus()?.history?.length) {
        <p class="empty">Chưa có giao dịch</p>
      }
      @for (tx of (bankStatus()?.history ?? []).slice().reverse(); track tx.id) {
        <div class="hist-item" [class.hist-ok]="tx.state === 'completed'" [class.hist-fail]="tx.state === 'failed'">
          <div class="hist-row">
            <span class="pill" [class]="'pill-' + tx.state">{{ tx.state }}</span>
            <span class="hist-id">#{{ tx.id }}</span>
          </div>
          <div class="hist-desc">{{ tx.description }}</div>
          @if (tx.result) { <div class="hist-result">{{ tx.result }}</div> }
          <div class="hist-time">{{ tx.submittedAt | date:'HH:mm:ss' }}
            @if (tx.finishedAt) { → {{ tx.finishedAt | date:'HH:mm:ss' }} }
          </div>
        </div>
      }
    </div>
  </div>
</div>
  `,
  styles: [`
    .page { padding: 24px; font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', sans-serif; max-width: 1100px; margin: 0 auto; }
    .page-header { margin-bottom: 20px; }
    .page-header h2 { margin: 0 0 8px; font-size: 20px; color: #1a1a2e; }
    .desc { color: #555; font-size: 13px; margin: 0 0 10px; }
    .desc a { color: #007bff; }
    code { background: #f0f0f0; padding: 1px 5px; border-radius: 3px; font-size: 11px; }
    .arch-note { background: #e8f4f8; border-left: 3px solid #007bff; padding: 8px 12px; font-size: 12px; border-radius: 0 4px 4px 0; line-height: 1.6; }
    .layout { display: grid; grid-template-columns: 250px 1fr 220px; gap: 16px; }
    .panel { border: 1px solid #ddd; border-radius: 8px; padding: 16px; background: #fafafa; }
    .panel h3 { margin: 0 0 12px; font-size: 14px; color: #333; }
    .form-group { margin-bottom: 10px; }
    .form-group label { display: block; font-size: 11px; color: #666; margin-bottom: 3px; }
    .input { width: 100%; padding: 7px 10px; border: 1px solid #ccc; border-radius: 4px; font-size: 13px; box-sizing: border-box; }
    .btn-row { margin-bottom: 8px; }
    .btn { padding: 7px 14px; border: none; border-radius: 4px; cursor: pointer; font-size: 13px; }
    .btn-success { background: #28a745; color: white; }
    .btn-success:disabled { background: #9ac; cursor: not-allowed; }
    .btn-full { width: 100%; }
    .hint { font-size: 11px; color: #888; margin: 4px 0 8px; font-style: italic; }
    .result-box { margin-top: 8px; padding: 10px; border-radius: 6px; font-size: 12px; }
    .result-box p { margin: 4px 0; }
    .result-box small { color: #666; font-size: 11px; }
    .result-ok   { background: #d4edda; border: 1px solid #c3e6cb; color: #155724; }
    .result-fail { background: #f8d7da; border: 1px solid #f5c6cb; color: #721c24; }
    .conn-row { margin-top: 8px; display: flex; align-items: center; gap: 6px; font-size: 12px; color: #666; }
    .dot { width: 8px; height: 8px; border-radius: 50%; }
    .dot-connected { background: #28a745; }
    .dot-connecting, .dot-reconnecting { background: #ffc107; }
    .dot-disconnected { background: #dc3545; }
    .tenant-badge { font-size: 11px; padding: 1px 7px; background: #e8f4f8; color: #0056b3; border-radius: 10px; font-weight: 600; }
    .bank-card { border-radius: 6px; padding: 12px; margin-bottom: 12px; }
    .bank-busy { background: #fff3cd; border: 2px solid #ffc107; }
    .bank-free { background: #d4edda; border: 2px solid #28a745; }
    .bank-indicator { display: flex; align-items: center; gap: 8px; margin-bottom: 8px; font-weight: 600; font-size: 13px; }
    .bank-light { width: 12px; height: 12px; border-radius: 50%; flex-shrink: 0; }
    .bank-busy .bank-light { background: #ffc107; animation: blink 1s infinite; }
    .bank-free .bank-light { background: #28a745; }
    @keyframes blink { 0%, 100% { opacity: 1; } 50% { opacity: 0.3; } }
    .current-tx { font-size: 12px; background: rgba(0,0,0,.05); padding: 8px; border-radius: 4px; }
    .cur-id   { font-size: 10px; color: #666; font-weight: 600; }
    .cur-desc { font-weight: 500; margin: 2px 0; }
    .cur-time { font-size: 11px; color: #666; }
    .live-log { font-size: 11px; }
    .live-log-title { font-weight: 600; color: #666; margin-bottom: 4px; text-transform: uppercase; font-size: 10px; }
    .live-entry { display: flex; gap: 6px; padding: 2px 0; border-bottom: 1px solid #eee; font-family: monospace; flex-wrap: wrap; }
    .le-time { color: #999; flex-shrink: 0; }
    .le-id   { font-weight: 600; }
    .le-processing { color: #856404; }
    .le-completed  { color: #155724; }
    .le-failed     { color: #721c24; }
    .empty { color: #999; font-size: 12px; }
    .hist-item { border-radius: 5px; padding: 8px; margin-bottom: 7px; font-size: 11px; }
    .hist-ok   { background: #d4edda; }
    .hist-fail { background: #f8d7da; }
    .hist-row  { display: flex; justify-content: space-between; align-items: center; margin-bottom: 3px; }
    .pill { padding: 1px 6px; border-radius: 10px; font-size: 10px; font-weight: 600; text-transform: uppercase; }
    .pill-completed { background: #c3e6cb; color: #155724; }
    .pill-failed    { background: #f5c6cb; color: #721c24; }
    .hist-id     { font-size: 10px; color: #666; }
    .hist-desc   { font-weight: 500; margin-bottom: 2px; }
    .hist-result { color: #555; font-style: italic; margin-bottom: 2px; word-break: break-word; }
    .hist-time   { color: #888; }
  `]
})
export class TransactionQueueComponent implements OnInit, OnDestroy {
  private sessionSvc = inject(SessionService);
  private tx         = inject(TransactionService);

  get tenantId() { return this.sessionSvc.session()!.tenantId; }
  get userName()  { return this.sessionSvc.session()!.name; }

  description = '';
  amount      = 1_500_000;

  connStatus  = signal<string>('disconnected');
  bankStatus  = signal<BankStatus | null>(null);
  lastResult  = signal<TransactionSubmitResult | null>(null);
  liveEvents  = signal<Array<{ id: string; state: string; message: string; time: string }>>([]);

  private subs: Subscription[] = [];

  ngOnInit() {
    this.subs.push(
      // Bank is a global resource — status applies to ALL tenants
      this.tx.bankStatus$.subscribe(data => this.bankStatus.set(data)),
      this.tx.statusChanged$.subscribe(change => {
        const time = new Date().toLocaleTimeString('vi-VN', { hour12: false });
        this.liveEvents.update(list => [...list, { ...change, time }]);
      })
    );

    // Auto-connect
    this.connect();
  }

  ngOnDestroy() {
    this.subs.forEach(s => s.unsubscribe());
    this.tx.disconnect(this.tenantId);
  }

  private async connect() {
    try {
      this.connStatus.set('connecting');
      await this.tx.connect(this.tenantId, this.userName);
      this.subs.push(
        this.tx.getStatusStream(this.tenantId).subscribe(s => this.connStatus.set(s))
      );
      this.connStatus.set('connected');
    } catch (err) {
      this.connStatus.set('disconnected');
      console.error('Connect failed:', err);
    }
  }

  async submit() {
    if (!this.description.trim()) { alert('Vui lòng nhập mô tả'); return; }
    try {
      const result = await this.tx.submitTransaction(this.tenantId, this.description.trim(), this.amount);
      this.lastResult.set(result);
    } catch (err) {
      console.error('Submit failed:', err);
    }
  }

}
