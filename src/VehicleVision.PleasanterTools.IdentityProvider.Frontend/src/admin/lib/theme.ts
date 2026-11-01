/**
 * ライト / ダークの切り替え（DesignReference の「明示型」）。
 * **既定はライト。OS の設定（`prefers-color-scheme`）には自動で従わない。**
 * 利用者が「OS に合わせる」を**明示的に選んだときだけ** OS を見る。
 * 画面は解決した値を `<html data-theme>` へ当てる（配色は CSS 変数、`design/colors-explicit.css`）。
 */

export type ThemePreference = 'light' | 'dark' | 'auto';
export type ResolvedTheme = 'light' | 'dark';

export const THEME_PREFERENCES: readonly ThemePreference[] = ['light', 'dark', 'auto'];

/** 保存のキー（DesignReference の規約 `vv-<アプリ名>-theme`） */
export const THEME_STORAGE_KEY = 'vv-idp-admin-theme';

/** 旧版のキー。**保存された選択は尊重する**ので、新しいキーが無いときだけ読む */
export const LEGACY_THEME_STORAGE_KEY = 'idp.admin.theme';

/**
 * 保存されていた文字列を解釈する。**おかしな値・保存が無いときは `light`。**
 * 旧版の `system`（OS に合わせる）は、利用者が選んだ値なので `auto` として引き継ぐ。
 */
export function parseThemePreference(value: string | null | undefined): ThemePreference {
  if (value === 'dark' || value === 'auto') {return value;}
  if (value === 'system') {return 'auto';}
  return 'light';
}

/** 選択を、実際に当てる配色へ解決する。`auto` のときだけ OS の設定を使う。 */
export function resolveTheme(preference: ThemePreference, systemDark: boolean): ResolvedTheme {
  if (preference === 'auto') {return systemDark ? 'dark' : 'light';}
  return preference;
}

/** 保存値を読む。**保存先が使えなくても動く（その場合はライト）。** */
export function readThemePreference(): ThemePreference {
  try {
    return parseThemePreference(
      localStorage.getItem(THEME_STORAGE_KEY) ?? localStorage.getItem(LEGACY_THEME_STORAGE_KEY),
    );
  } catch {
    return 'light';
  }
}

export function saveThemePreference(preference: ThemePreference): void {
  try {
    localStorage.setItem(THEME_STORAGE_KEY, preference);
  } catch {
    // 保存できなくても今の画面には効く
  }
}

/** OS がダークか。`auto` を選んだときだけ使う。 */
export function systemPrefersDark(): boolean {
  try {
    return window.matchMedia('(prefers-color-scheme: dark)').matches;
  } catch {
    return false;
  }
}
