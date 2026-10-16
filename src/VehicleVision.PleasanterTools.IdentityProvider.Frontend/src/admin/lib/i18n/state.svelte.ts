import {
  applyDocumentLanguage,
  browserLanguages,
  DEFAULT_LANGUAGE,
  formatDateTime as formatDateTimeIn,
  negotiateLanguage,
  readStoredLanguage,
  saveLanguage,
  type Language,
} from './language';
import { translator, type MessageKey } from './messages';

/**
 * 管理画面が今出している言語。
 *
 * **画面の隅々まで props で配らない。** 管理画面は 1 つのアプリなので、言語は 1 つで足りる。
 * ここを読む所は自動で描き直る（`$state`）。
 *
 * **決め方は「画面で選んだ言語（保存）→ ブラウザの言語設定 → `ja`」。**
 */
let current = $state<Language>(DEFAULT_LANGUAGE);

/** 今の言語。 */
export function language(): Language {
  return current;
}

/** 起動時に言語を決める。 */
export function initLanguage(): void {
  setLanguage(negotiateLanguage(readStoredLanguage(), browserLanguages()), false);
}

/** 言語を切り替える。`persist` なら選択を覚える。 */
export function setLanguage(next: Language, persist = true): void {
  current = next;
  applyDocumentLanguage(next);
  if (persist) saveLanguage(next);
}

/** 文言を引く。**`$state` を読むので、呼んだ所が言語の切り替えで描き直る。** */
export function t(key: MessageKey, parameters?: Record<string, string | number>): string {
  return translator(current)(key, parameters);
}

/** 日時を今の言語で書く。 */
export function formatDateTime(value: string | Date | null | undefined): string {
  return formatDateTimeIn(value, current);
}

/** サーバへ「この言語で返してほしい」と伝えるヘッダ。 */
export function acceptLanguageHeader(): string {
  return current === DEFAULT_LANGUAGE ? `${current}, en;q=0.8` : `${current}, ${DEFAULT_LANGUAGE};q=0.8`;
}
