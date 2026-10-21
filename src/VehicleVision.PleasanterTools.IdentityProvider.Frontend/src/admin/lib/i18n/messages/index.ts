import { interpolate, type Language } from '../language';
import { en } from './en';
import { ja } from './ja';

/**
 * 管理画面の文言。
 *
 * **日本語のカタログが鍵の一覧そのもの。** 英語は `Record<MessageKey, string>` として書くので、
 * **足し忘れると型検査（`npm run check` / `npm run build`）で落ちる。**
 */
export { en, ja };

/** 文言の鍵。**日本語のカタログが一覧そのもの。** */
export type MessageKey = keyof typeof ja;

const CATALOGS: Record<Language, Record<MessageKey, string>> = { ja, en };

/** 文言を引く関数。 */
export type Translate = (key: MessageKey, parameters?: Record<string, string | number>) => string;

/** その言語の文言を引く関数を作る。**無ければ日本語へ落ちる。** */
export function translator(language: Language): Translate {
  const catalog = CATALOGS[language];
  return (key, parameters) => interpolate(catalog[key] || ja[key], parameters, language);
}
