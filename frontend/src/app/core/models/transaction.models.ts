/**
 * Snapshot of a single bank transaction (in-flight or historical).
 */
export interface TransactionStatus {
  id: string;
  state: 'processing' | 'completed' | 'failed' | 'rejected';
  description: string;
  result: string | null;
  submittedAt: string;
  finishedAt: string | null;
}

/**
 * Immediate response from `SubmitTransaction` hub method.
 * - `accepted` → follow progress via `TransactionStatusChanged` events.
 * - `rejected` → bank is busy; caller should retry.
 */
export interface TransactionSubmitResult {
  transactionId: string | null;
  status: 'accepted' | 'rejected';
  message: string;
  currentlyProcessing: TransactionStatus | null;
}

/**
 * Global bank state snapshot pushed via `BankStatus` SignalR event.
 * All UI clients across every tenant receive the same snapshot.
 */
export interface BankStatus {
  isBankBusy: boolean;
  currentTransaction: TransactionStatus | null;
  history: TransactionStatus[];
}

/**
 * Lifecycle event pushed via `TransactionStatusChanged`.
 */
export interface TransactionStatusChanged {
  id: string;
  state: string;
  message: string;
}
