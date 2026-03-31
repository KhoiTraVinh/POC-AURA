/**
 * Represents a print job as received from AuraHub (`PrintJobQueued` / `ExecutePrintJob`).
 */
export interface PrintJob {
  id: string;
  tenantId: string;
  documentName: string;
  content: string;
  copies: number;
  requestorConnectionId: string;
  createdAt: string;
}

/**
 * Result emitted by AuraHub after the SmartHub reports job completion
 * (`PrintJobComplete` / `PrintJobStatusUpdate`).
 */
export interface PrintJobResult {
  jobId: string;
  tenantId: string;
  requestorConnectionId: string;
  success: boolean;
  message: string;
  completedAt: string;
}
