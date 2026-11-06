import type { ScreenshotMeta } from './coords.js';

export type ErrorCode =
  | 'denied'
  | 'paused'
  | 'blocked'
  | 'timeout'
  | 'not_found'
  | 'stale_element'
  | 'app_not_responding'
  | 'elevated_target'
  | 'secure_desktop'
  | 'user_busy'
  | 'interrupted_by_user'
  | 'unsupported_platform'
  | 'bad_request'
  | 'failed';

export interface ImagePart {
  data: string; // base64 (no data: prefix)
  mimeType: string;
}

export interface ActionVerification {
  via?: 'uia' | 'message' | 'input' | 'none';
  focusRestored?: boolean;
  cursorRestored?: boolean;
  activeWindowChanged?: boolean;
  titleChanged?: boolean;
  newWindows?: Array<{ wid: string; title: string; app: string }>;
  closedWindows?: string[];
  summary?: string;
}

export interface ToolResult {
  /** Human/LLM readable text. */
  text?: string;
  /** Marks text as coming from screen/window/file/command (prompt-injection protection). */
  untrusted?: boolean;
  /** Structured data payload. */
  data?: unknown;
  /** Attached images (e.g. screenshot). */
  images?: ImagePart[];
  /** Screenshot coordinate metadata if this call produced a screenshot. */
  screenshotMeta?: ScreenshotMeta;
  /** Post-action verification summary. */
  verification?: ActionVerification;
}

export interface ToolError {
  code: ErrorCode;
  message: string;
}

// ---- Named Pipe JSON-RPC 2.0 between TS Server and C# Native Helper ----
export interface RpcRequest {
  jsonrpc: '2.0';
  id: string | number;
  method: string;
  params?: Record<string, unknown>;
}

export interface RpcResponse {
  jsonrpc: '2.0';
  id: string | number;
  result?: ToolResult;
  error?: {
    code: number;
    message: string;
    data?: {
      errorCode?: ErrorCode;
      details?: unknown;
    };
  };
}
