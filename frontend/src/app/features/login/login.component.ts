import { Component, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Router } from '@angular/router';
import { SessionService } from '../../core/services/session.service';

@Component({
  selector: 'app-login',
  standalone: true,
  imports: [FormsModule],
  template: `
<div class="login-wrap">
  <div class="login-card">
    <div class="logo">POC AURA</div>
    <p class="sub">Nhập tên để bắt đầu — hệ thống sẽ tự gán tenant cho bạn</p>

    <div class="form-field">
      <label>Tên của bạn</label>
      <input
        [(ngModel)]="name"
        placeholder="Vd: Alice, Bob, Charlie..."
        class="input"
        (keydown.enter)="submit()"
        autofocus />
    </div>

    @if (error()) {
      <div class="err">{{ error() }}</div>
    }

    <button class="btn-enter" (click)="submit()" [disabled]="loading()">
      @if (loading()) { Đang kết nối... } @else { Vào hệ thống → }
    </button>
  </div>
</div>
  `,
  styles: [`
    .login-wrap {
      min-height: 100vh; display: flex; align-items: center; justify-content: center;
      background: linear-gradient(135deg, #1a1a2e 0%, #16213e 50%, #0f3460 100%);
    }
    .login-card {
      background: white; border-radius: 16px; padding: 48px 40px;
      width: 360px; box-shadow: 0 20px 60px rgba(0,0,0,.4);
      display: flex; flex-direction: column; gap: 16px;
    }
    .logo {
      font-size: 26px; font-weight: 800; letter-spacing: 3px;
      color: #1a1a2e; text-align: center; margin-bottom: 4px;
    }
    .sub { font-size: 13px; color: #888; text-align: center; margin: 0; line-height: 1.5; }
    .form-field { display: flex; flex-direction: column; gap: 6px; }
    .form-field label { font-size: 12px; font-weight: 600; color: #555; }
    .input {
      padding: 11px 14px; border: 2px solid #ddd; border-radius: 8px;
      font-size: 14px; transition: border-color .15s; outline: none;
    }
    .input:focus { border-color: #007bff; }
    .err { background: #fde; color: #c00; font-size: 12px; padding: 8px 12px; border-radius: 6px; }
    .btn-enter {
      margin-top: 8px; padding: 12px; background: #007bff; color: white;
      border: none; border-radius: 8px; font-size: 15px; font-weight: 600;
      cursor: pointer; transition: background .15s;
    }
    .btn-enter:hover:not(:disabled) { background: #0056b3; }
    .btn-enter:disabled { background: #9ac; cursor: not-allowed; }
  `],
})
export class LoginComponent {
  name    = '';
  error   = signal('');
  loading = signal(false);

  constructor(private session: SessionService, private router: Router) {}

  async submit() {
    const trimmed = this.name.trim();
    if (!trimmed) { this.error.set('Vui lòng nhập tên của bạn'); return; }
    this.loading.set(true);
    this.error.set('');
    this.session.login(trimmed);
    await this.router.navigate(['/print']);
    this.loading.set(false);
  }
}
