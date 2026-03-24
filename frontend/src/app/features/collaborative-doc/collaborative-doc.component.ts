import {
  Component, OnInit, OnDestroy, signal
} from '@angular/core';
import { CommonModule, DatePipe } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { Subscription } from 'rxjs';
import { DocumentService, FieldLockInfo } from '../../core/services/document.service';

interface DocField {
  id: string;
  label: string;
  type: 'text' | 'textarea' | 'select';
  value: string;
  options?: string[];
}

interface FieldState {
  lockedBy: string | null;
  lockedByName: string | null;
  isMyLock: boolean;
  expiresAt: Date | null;
}

const DOC_ID = 'contract-001';

@Component({
  selector: 'app-collaborative-doc',
  standalone: true,
  imports: [CommonModule, FormsModule, DatePipe],
  template: `
<div class="page">
  <div class="page-header">
    <h2>Problem 3: Collaborative Document — Field-Level Pessimistic Lock</h2>
    <p class="desc">
      Mở trang này trên <strong>nhiều tab/browser</strong> với tên khác nhau.
      Click vào field để lock — người khác thấy field đó bị khóa và không edit được.
      Lock TTL: <strong>30 giây</strong> (heartbeat mỗi 8s để gia hạn).
      Ngắt kết nối → tất cả lock tự release.
    </p>
    <div class="arch-note">
      <strong>Cơ chế:</strong> ConcurrentDictionary &lt;docId:fieldId, FieldLockEntry&gt;
      + TTL + background cleanup timer 5s + OnDisconnected auto-release
    </div>
  </div>

  <!-- Login bar -->
  <div class="identity-bar">
    @if (!connected()) {
      <input [(ngModel)]="userName" placeholder="Tên của bạn (vd: Alice)" class="input name-input"
        (keydown.enter)="connect()" />
      <button class="btn btn-primary" (click)="connect()">Tham gia</button>
      <span class="hint-text">Mở tab khác với tên khác để test multi-user</span>
    } @else {
      <div class="you-info">
        <span class="you-badge">{{ userName }}</span>
        <code class="uid">{{ userId() }}</code>
      </div>
      <div class="conn-status">
        <span class="dot" [class]="'dot-' + connStatus()"></span>
        <span>{{ connStatus() }}</span>
      </div>
      <button class="btn btn-danger btn-sm" (click)="disconnect()">Rời khỏi</button>
    }
  </div>

  @if (connected()) {
    <div class="doc-layout">
      <!-- Document form -->
      <div class="doc-card">
        <div class="doc-title">
          <span>Hợp đồng #2024-001</span>
          <span class="doc-sub">Đang có {{ activeLocks().length }} field bị lock</span>
        </div>

        @for (field of fields(); track field.id) {
          <div class="field-row">
            <label class="field-label">{{ field.label }}</label>
            <div class="field-wrapper">
              <div class="field-status-bar">
                @if (isMyLock(field.id)) {
                  <span class="status-mine">✎ Bạn đang chỉnh sửa</span>
                } @else if (isLockedByOther(field.id)) {
                  <span class="status-locked">
                    🔒 {{ getLockState(field.id)?.lockedByName }} đang chỉnh sửa...
                  </span>
                }
              </div>

              @if (field.type === 'textarea') {
                <textarea
                  class="field-input"
                  [class.input-mine]="isMyLock(field.id)"
                  [class.input-locked]="isLockedByOther(field.id)"
                  [disabled]="isLockedByOther(field.id)"
                  [(ngModel)]="field.value"
                  (focus)="onFocus(field.id)"
                  (blur)="onBlur(field.id)"
                  (ngModelChange)="onValueChange(field.id, $event)"
                  rows="3"
                  [placeholder]="isLockedByOther(field.id) ? 'Field đang bị khóa' : 'Nhập...'">
                </textarea>
              } @else if (field.type === 'select') {
                <select
                  class="field-input"
                  [class.input-mine]="isMyLock(field.id)"
                  [class.input-locked]="isLockedByOther(field.id)"
                  [disabled]="isLockedByOther(field.id)"
                  [(ngModel)]="field.value"
                  (focus)="onFocus(field.id)"
                  (blur)="onBlur(field.id)"
                  (ngModelChange)="onValueChange(field.id, $event)">
                  @for (opt of field.options; track opt) {
                    <option [value]="opt">{{ opt }}</option>
                  }
                </select>
              } @else {
                <input type="text"
                  class="field-input"
                  [class.input-mine]="isMyLock(field.id)"
                  [class.input-locked]="isLockedByOther(field.id)"
                  [disabled]="isLockedByOther(field.id)"
                  [(ngModel)]="field.value"
                  (focus)="onFocus(field.id)"
                  (blur)="onBlur(field.id)"
                  (ngModelChange)="onValueChange(field.id, $event)"
                  [placeholder]="isLockedByOther(field.id) ? 'Field đang bị khóa' : 'Nhập...'" />
              }
            </div>
          </div>
        }
      </div>

      <!-- Lock panel -->
      <div class="locks-panel">
        <h4>Active Locks</h4>
        @if (activeLocks().length === 0) {
          <p class="empty">Không có field nào bị lock</p>
        }
        @for (lock of activeLocks(); track lock.fieldId) {
          <div class="lock-item" [class.lock-mine]="lock.userId === userId()">
            <div class="lock-field-name">{{ getFieldLabel(lock.fieldId) }}</div>
            <div class="lock-user-name">
              {{ lock.userId === userId() ? '(bạn)' : lock.userName }}
            </div>
            <div class="lock-exp">HH {{ lock.expiresAt | date:'HH:mm:ss' }}</div>
          </div>
        }

        <div class="legend">
          <div class="legend-title">Chú thích</div>
          <div class="legend-item"><span class="sw sw-mine"></span> Lock của bạn</div>
          <div class="legend-item"><span class="sw sw-other"></span> Lock của người khác</div>
        </div>

        <div class="ttl-info">
          <strong>Lock TTL</strong>
          <ul>
            <li>Hết hạn sau <strong>30s</strong> không hoạt động</li>
            <li>Heartbeat mỗi <strong>8s</strong> khi đang edit</li>
            <li>Auto-release khi <strong>blur</strong> hoặc <strong>disconnect</strong></li>
          </ul>
        </div>
      </div>
    </div>
  }
</div>
  `,
  styles: [`
    .page { padding: 24px; font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', sans-serif; max-width: 1000px; margin: 0 auto; }
    .page-header { margin-bottom: 16px; }
    .page-header h2 { margin: 0 0 8px; font-size: 20px; color: #1a1a2e; }
    .desc { color: #555; font-size: 13px; margin: 0 0 8px; line-height: 1.6; }
    .arch-note { background: #e8f4f8; border-left: 3px solid #007bff; padding: 8px 12px; font-size: 12px; border-radius: 0 4px 4px 0; }
    .identity-bar { display: flex; align-items: center; gap: 10px; margin-bottom: 20px; padding: 10px 14px; background: #f8f9fa; border-radius: 6px; flex-wrap: wrap; }
    .name-input { padding: 7px 10px; border: 1px solid #ccc; border-radius: 4px; font-size: 13px; min-width: 160px; }
    .hint-text { font-size: 12px; color: #888; font-style: italic; }
    .you-info { display: flex; align-items: center; gap: 8px; flex: 1; }
    .you-badge { font-size: 14px; font-weight: 600; color: #1a1a2e; }
    .uid { font-size: 11px; color: #888; background: #eee; padding: 1px 6px; border-radius: 3px; }
    .conn-status { display: flex; align-items: center; gap: 5px; font-size: 12px; color: #666; }
    .dot { width: 8px; height: 8px; border-radius: 50%; flex-shrink: 0; }
    .dot-connected { background: #28a745; }
    .dot-connecting, .dot-reconnecting { background: #ffc107; }
    .dot-disconnected { background: #dc3545; }
    .btn { padding: 7px 14px; border: none; border-radius: 4px; cursor: pointer; font-size: 13px; }
    .btn-primary { background: #007bff; color: white; }
    .btn-danger { background: #dc3545; color: white; }
    .btn-sm { padding: 4px 10px; font-size: 12px; }
    .doc-layout { display: grid; grid-template-columns: 1fr 210px; gap: 20px; align-items: start; }
    .doc-card { background: white; border: 1px solid #ddd; border-radius: 8px; padding: 20px; box-shadow: 0 1px 3px rgba(0,0,0,0.05); }
    .doc-title { font-size: 17px; font-weight: 700; margin-bottom: 20px; color: #1a1a2e; border-bottom: 2px solid #007bff; padding-bottom: 8px; display: flex; justify-content: space-between; align-items: center; }
    .doc-sub { font-size: 11px; font-weight: 400; color: #888; }
    .field-row { display: grid; grid-template-columns: 130px 1fr; gap: 12px; align-items: start; margin-bottom: 12px; }
    .field-label { font-size: 13px; font-weight: 500; color: #555; padding-top: 8px; }
    .field-wrapper { position: relative; }
    .field-status-bar { height: 16px; margin-bottom: 2px; font-size: 11px; }
    .status-mine { color: #155724; font-weight: 500; }
    .status-locked { color: #dc3545; font-weight: 500; }
    .field-input { width: 100%; padding: 7px 10px; border: 2px solid #ddd; border-radius: 4px; font-size: 13px; box-sizing: border-box; transition: border-color 0.15s, background 0.15s; outline: none; background: white; resize: vertical; }
    .field-input:focus { border-color: #007bff; }
    .input-mine { border-color: #28a745 !important; background: #f8fff9 !important; }
    .input-locked { border-color: #dc3545 !important; background: #fff5f5 !important; cursor: not-allowed; }
    .field-input:disabled { opacity: 0.8; }
    .locks-panel { }
    .locks-panel h4 { margin: 0 0 10px; font-size: 13px; color: #333; text-transform: uppercase; letter-spacing: 0.5px; }
    .lock-item { border-radius: 6px; padding: 8px 10px; margin-bottom: 6px; font-size: 12px; }
    .lock-mine { background: #d4edda; border: 1px solid #c3e6cb; }
    .lock-item:not(.lock-mine) { background: #f8d7da; border: 1px solid #f5c6cb; }
    .lock-field-name { font-weight: 600; margin-bottom: 1px; }
    .lock-user-name { color: #555; }
    .lock-exp { font-size: 10px; color: #888; }
    .empty { color: #999; font-size: 12px; }
    .legend { margin-top: 16px; font-size: 12px; }
    .legend-title { font-weight: 600; color: #555; margin-bottom: 4px; }
    .legend-item { display: flex; align-items: center; gap: 6px; margin-bottom: 3px; color: #555; }
    .sw { width: 12px; height: 12px; border-radius: 2px; display: inline-block; flex-shrink: 0; }
    .sw-mine { background: #d4edda; border: 1px solid #c3e6cb; }
    .sw-other { background: #f8d7da; border: 1px solid #f5c6cb; }
    .ttl-info { margin-top: 16px; font-size: 12px; background: #f8f9fa; border-radius: 4px; padding: 8px 10px; }
    .ttl-info strong { display: block; margin-bottom: 4px; }
    .ttl-info ul { margin: 0; padding-left: 16px; }
    .ttl-info li { margin-bottom: 2px; color: #555; }
  `]
})
export class CollaborativeDocComponent implements OnInit, OnDestroy {
  userName = '';
  connected = signal(false);
  connStatus = signal('disconnected');
  userId = signal('');
  activeLocks = signal<FieldLockInfo[]>([]);

  fields = signal<DocField[]>([
    { id: 'buyer_name', label: 'Tên bên mua', type: 'text', value: '' },
    { id: 'buyer_tax', label: 'Mã số thuế', type: 'text', value: '' },
    { id: 'buyer_address', label: 'Địa chỉ', type: 'textarea', value: '' },
    { id: 'contract_value', label: 'Giá trị HĐ', type: 'text', value: '' },
    { id: 'payment_method', label: 'Hình thức TT', type: 'select', value: 'Chuyển khoản',
      options: ['Chuyển khoản', 'Tiền mặt', 'Thẻ ngân hàng', 'Ví điện tử'] },
    { id: 'delivery_date', label: 'Ngày giao hàng', type: 'text', value: '' },
    { id: 'notes', label: 'Ghi chú', type: 'textarea', value: '' },
  ]);

  private lockStates = new Map<string, FieldState>();
  private subs: Subscription[] = [];

  constructor(private docService: DocumentService) {}

  ngOnInit() {
    this.subs.push(
      this.docService.connectionStatus$.subscribe(s => {
        this.connStatus.set(s);
        if (s === 'disconnected') this.connected.set(false);
      }),
      this.docService.lockSnapshot$.subscribe(locks => {
        this.lockStates.clear();
        locks.forEach(lock => this.setLock(lock.fieldId, lock.userId, lock.userName, lock.expiresAt));
        this.syncActiveLocks();
      }),
      this.docService.fieldLocked$.subscribe(info => {
        this.setLock(info.fieldId, info.userId, info.userName, info.expiresAt);
        this.syncActiveLocks();
      }),
      this.docService.fieldUnlocked$.subscribe(({ fieldId }) => {
        this.lockStates.delete(fieldId);
        this.syncActiveLocks();
      }),
      this.docService.fieldsExpired$.subscribe(locks => {
        locks.forEach(l => this.lockStates.delete(l.fieldId));
        this.syncActiveLocks();
      }),
      this.docService.fieldValueChanged$.subscribe(({ fieldId, value }) => {
        this.fields.update(fs => fs.map(f => f.id === fieldId ? { ...f, value } : f));
      })
    );
  }

  ngOnDestroy() {
    this.subs.forEach(s => s.unsubscribe());
    this.docService.disconnect();
  }

  async connect() {
    if (!this.userName.trim()) { alert('Vui lòng nhập tên'); return; }
    const uid = `${this.userName.trim().toLowerCase().replace(/\s+/g, '_')}_${Date.now().toString().slice(-4)}`;
    this.userId.set(uid);
    try {
      await this.docService.connect(uid, this.userName.trim());
      this.connected.set(true);
    } catch (err) { console.error('Connect failed:', err); }
  }

  async disconnect() {
    for (const [fieldId, state] of this.lockStates) {
      if (state.isMyLock) {
        await this.docService.releaseFieldLock(DOC_ID, fieldId).catch(() => {});
      }
    }
    await this.docService.disconnect();
    this.connected.set(false);
    this.lockStates.clear();
    this.syncActiveLocks();
  }

  async onFocus(fieldId: string) {
    if (!this.connected()) return;
    try {
      const result = await this.docService.acquireFieldLock(DOC_ID, fieldId);
      if (result.acquired) {
        this.setLock(fieldId, this.userId(), this.userName, result.expiresAt ?? '');
        this.docService.startHeartbeat(DOC_ID, fieldId);
        this.syncActiveLocks();
      }
    } catch (err) { console.error('Lock acquire failed:', err); }
  }

  async onBlur(fieldId: string) {
    if (!this.connected()) return;
    const state = this.lockStates.get(fieldId);
    if (state?.isMyLock) {
      await this.docService.releaseFieldLock(DOC_ID, fieldId).catch(() => {});
      this.lockStates.delete(fieldId);
      this.syncActiveLocks();
    }
  }

  async onValueChange(fieldId: string, value: string) {
    if (!this.lockStates.get(fieldId)?.isMyLock) return;
    await this.docService.updateFieldValue(DOC_ID, fieldId, value).catch(() => {});
  }

  getLockState(fieldId: string): FieldState | undefined { return this.lockStates.get(fieldId); }
  isLockedByOther(fieldId: string): boolean { const s = this.lockStates.get(fieldId); return !!s?.lockedBy && !s.isMyLock; }
  isMyLock(fieldId: string): boolean { return this.lockStates.get(fieldId)?.isMyLock ?? false; }
  getFieldLabel(fieldId: string): string { return this.fields().find(f => f.id === fieldId)?.label ?? fieldId; }

  private setLock(fieldId: string, userId: string, userName: string, expiresAt: string) {
    this.lockStates.set(fieldId, {
      lockedBy: userId, lockedByName: userName,
      isMyLock: userId === this.userId(),
      expiresAt: expiresAt ? new Date(expiresAt) : null,
    });
  }

  private syncActiveLocks() {
    const locks: FieldLockInfo[] = [];
    this.lockStates.forEach((state, fieldId) => {
      if (state.lockedBy) {
        locks.push({ docId: DOC_ID, fieldId, userId: state.lockedBy,
          userName: state.lockedByName ?? state.lockedBy,
          expiresAt: state.expiresAt?.toISOString() ?? '' });
      }
    });
    this.activeLocks.set(locks);
  }
}
