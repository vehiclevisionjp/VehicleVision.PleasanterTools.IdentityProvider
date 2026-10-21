/**
 * ライト / ダークの切り替え。**保存値 → OS の設定**の順で決める。
 * 画面は解決した値を `<html data-theme>` へ当てる（配色は CSS 変数、`styles.scss`）。
 */

export type ThemePreference = 'system' | 'light' | 'dark';
export type ResolvedTheme = 'light' | 'dark';

export const THEME_PREFERENCES: readonly ThemePreference[] = ['system', 'light', 'dark'];
export const THEME_STORAGE_KEY = 'idp.admin.theme';

/** 保存されていた文字列を解釈する。**おかしな値は `system`。** */
export function parseThemePreference(value: string | null | undefined): ThemePreference {
  return value === 'light' || value === 'dark' ? value : 'system';
}

/** OS に従う指定を、実際に当てる配色へ解決する。 */
export function resolveTheme(preference: ThemePreference, systemDark: boolean): ResolvedTheme {
  if (preference === 'system') {return systemDark ? 'dark' : 'light';}
  return preference;
}

/** 保存値を読む。**保存先が使えなくても動く。** */
export function readThemePreference(): ThemePreference {
  try {
    return parseThemePreference(localStorage.getItem(THEME_STORAGE_KEY));
  } catch {
    return 'system';
  }
}

export function saveThemePreference(preference: ThemePreference): void {
  try {
    localStorage.setItem(THEME_STORAGE_KEY, preference);
  } catch {
    // 保存できなくても今の画面には効く
  }
}
