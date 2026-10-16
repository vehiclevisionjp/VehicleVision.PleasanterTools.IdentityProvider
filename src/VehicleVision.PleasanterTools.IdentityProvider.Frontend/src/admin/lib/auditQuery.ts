/**
 * 監査ログの絞り込み入力を、API の問い合わせへ直す。Svelte に依存させない。
 */
import type { AuditLogQuery } from './types';

/** 画面が持つ絞り込みの入力（入力欄の文字列のまま）。 */
export interface AuditFilter {
  /** `datetime-local` の値（`2026-10-17T09:00`）。端末のタイムゾーンで解釈する */
  from: string;
  to: string;
  actor: string;
  action: string;
  failedOnly: boolean;
}

export const AUDIT_PAGE_SIZE = 100;

export function emptyAuditFilter(): AuditFilter {
  return { from: '', to: '', actor: '', action: '', failedOnly: false };
}

/**
 * `datetime-local` の値を ISO 8601（UTC、`Z` 付き）へ。**空・読めない値は `undefined`。**
 * API の日時は UTC なので、端末の時刻として読んだものを UTC へ直して送る。
 */
export function localToIso(value: string): string | undefined {
  if (value.trim() === '') return undefined;
  const date = new Date(value);
  return Number.isNaN(date.getTime()) ? undefined : date.toISOString();
}

/** 絞り込みとページ位置から問い合わせを作る。**空の条件は含めない。** */
export function buildAuditQuery(filter: AuditFilter, page: number): AuditLogQuery {
  return {
    from: localToIso(filter.from),
    to: localToIso(filter.to),
    actor: filter.actor.trim() || undefined,
    action: filter.action.trim() || undefined,
    failedOnly: filter.failedOnly || undefined,
    limit: AUDIT_PAGE_SIZE,
    offset: Math.max(0, page) * AUDIT_PAGE_SIZE,
  };
}

/** 失敗の記録か。**ステータスコードが 400 以上、または action が `failure` で終わる。** */
export function isFailure(log: { statusCode: number | null; action: string }): boolean {
  return (log.statusCode !== null && log.statusCode >= 400) || /\.failure$/.test(log.action);
}
