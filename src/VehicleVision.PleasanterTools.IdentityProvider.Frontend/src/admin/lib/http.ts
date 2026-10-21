/**
 * 管理 API 呼び出しの純粋な部分（URL の組み立て・ヘッダ・エラーの解釈）。
 * **Svelte に依存させない**（vitest で試すため）。
 */
import { appUrl } from './basePath';

/** 管理 API の URL（`{basePath}/api/admin{path}`）。 */
export function apiUrl(path: string, base: string): string {
  if (!path.startsWith('/')) {
    throw new Error('API のパスは / で始めてください。');
  }
  return appUrl(`/api/admin${path}`, base);
}

/** 更新系か。**更新系だけ `X-IdP-Admin: 1` が要る**（CSRF 対策）。 */
export function isMutating(method: string | undefined): boolean {
  const upper = (method ?? 'GET').toUpperCase();
  return upper === 'POST' || upper === 'PUT' || upper === 'DELETE' || upper === 'PATCH';
}

/** 送るヘッダ。 */
export function buildHeaders(
  method: string | undefined,
  hasJsonBody: boolean,
  language: string,
): Record<string, string> {
  return {
    'Accept-Language': language,
    ...(hasJsonBody ? { 'Content-Type': 'application/json' } : {}),
    ...(isMutating(method) ? { 'X-IdP-Admin': '1' } : {}),
  };
}

/** 値の無いものを除いてクエリ文字列を作る。先頭の `?` を含む。無ければ空文字。 */
export function buildQuery(
  params: Record<string, string | number | boolean | null | undefined>,
): string {
  const search = new URLSearchParams();
  for (const [key, value] of Object.entries(params)) {
    if (value === undefined || value === null || value === '' || value === false) {continue;}
    search.set(key, String(value));
  }
  const text = search.toString();
  return text === '' ? '' : `?${text}`;
}

/** 失敗の解釈結果。 */
export interface ApiError {
  status: number;
  /** サーバのエラーコード（`unauthorized` など）。本体が読めなければ空 */
  code: string;
  message: string;
}

/**
 * 失敗応答を解釈する。**本体は `{ error, message }`**（仕様の「共通」）。
 * 読めないときは `fallbackMessage` を使う。
 */
export function parseError(status: number, body: unknown, fallbackMessage: string): ApiError {
  if (typeof body === 'object' && body !== null) {
    const record = body as { error?: unknown; message?: unknown };
    const message =
      typeof record.message === 'string' && record.message !== '' ? record.message : fallbackMessage;
    return { status, code: typeof record.error === 'string' ? record.error : '', message };
  }
  return { status, code: '', message: fallbackMessage };
}

/** 応答本体の文字列を JSON として読む。空は `undefined`、JSON でなければ文字列のまま。 */
export function parseBody(raw: string): unknown {
  if (raw === '') {return undefined;}
  try {
    return JSON.parse(raw);
  } catch {
    return raw;
  }
}
