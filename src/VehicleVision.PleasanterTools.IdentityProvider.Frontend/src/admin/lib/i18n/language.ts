/**
 * 画面に出す言語の決め方と、言語に合わせた書式。
 * **ja / en の 2 言語。既定は ja。**
 */

export type Language = 'ja' | 'en';

/** 選べる言語。**先頭が既定。** */
export const SUPPORTED_LANGUAGES: readonly Language[] = ['ja', 'en'];

/** 未指定のときに使う言語。 */
export const DEFAULT_LANGUAGE: Language = 'ja';

/** 画面に出す言語の名前。**その言語自身で書く**（読めない名前で選ばせない）。 */
export const LANGUAGE_NAMES: Record<Language, string> = {
  ja: '日本語',
  en: 'English',
};

const LOCALES: Record<Language, string> = {
  ja: 'ja-JP',
  en: 'en-US',
};

/** 言語タグを本アプリの言語コードへ寄せる。対応していなければ `null`。地域は見ない。 */
export function normalizeLanguage(languageTag: string | null | undefined): Language | null {
  if (!languageTag) return null;
  const primary = languageTag.trim().split(/[-_]/)[0]?.toLowerCase() ?? '';
  return SUPPORTED_LANGUAGES.find((language) => language === primary) ?? null;
}

/**
 * 使う言語を決める。**明示の指定 → ブラウザの言語設定 → 既定**の順。
 * 明示が対応外の値なら、無かったものとして次を見る。
 */
export function negotiateLanguage(
  requested: string | null | undefined,
  browserLanguages: readonly string[] = [],
): Language {
  const explicit = normalizeLanguage(requested);
  if (explicit) return explicit;

  for (const candidate of browserLanguages) {
    const language = normalizeLanguage(candidate);
    if (language) return language;
  }
  return DEFAULT_LANGUAGE;
}

/** ブラウザが申告している言語。 */
export function browserLanguages(): readonly string[] {
  if (typeof navigator === 'undefined') return [];
  return navigator.languages ?? (navigator.language ? [navigator.language] : []);
}

export const LANGUAGE_STORAGE_KEY = 'idp.admin.language';

/** 保存された言語の選択を読む。**保存先が使えなくても動く。** */
export function readStoredLanguage(): string | null {
  try {
    return localStorage.getItem(LANGUAGE_STORAGE_KEY);
  } catch {
    return null;
  }
}

export function saveLanguage(language: Language): void {
  try {
    localStorage.setItem(LANGUAGE_STORAGE_KEY, language);
  } catch {
    // 保存できなくても今の画面には効く
  }
}

/** 文書の言語を宣言する。**読み上げの声と行折り返しの規則がこれで変わる。** */
export function applyDocumentLanguage(language: Language): void {
  if (typeof document !== 'undefined') {
    document.documentElement.lang = language;
  }
}

/** 数値を言語に合わせて書く。 */
export function formatNumber(value: number, language: Language): string {
  return new Intl.NumberFormat(LOCALES[language]).format(value);
}

/** 日時を言語に合わせて書く。**端末のタイムゾーンで出す。** 読めない値はそのまま返す。 */
export function formatDateTime(value: string | Date | null | undefined, language: Language): string {
  if (value === null || value === undefined || value === '') return '—';
  const date = typeof value === 'string' ? new Date(value) : value;
  if (Number.isNaN(date.getTime())) return String(value);
  return new Intl.DateTimeFormat(LOCALES[language], {
    dateStyle: 'medium',
    timeStyle: 'medium',
  }).format(date);
}

/** 差し込みのある文言を組み立てる。`{name}` の所を置き換える。数値は言語に合わせて整形する。 */
export function interpolate(
  template: string,
  parameters: Record<string, string | number> | undefined,
  language: Language,
): string {
  if (!parameters) return template;

  return template.replace(/\{(\w+)\}/g, (match, name: string) => {
    const value = parameters[name];
    if (value === undefined) return match;
    return typeof value === 'number' ? formatNumber(value, language) : value;
  });
}
