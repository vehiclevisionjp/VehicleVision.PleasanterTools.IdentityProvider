// **最初の描画の前に `data-theme` を付ける。** ダークを選んだ利用者に、ライトが一瞬見えるのを避ける。
// CSP（`script-src 'self'`）でインラインのスクリプトは使えないので、`main.ts` の先頭で読み込む。
import { readThemePreference, resolveTheme, systemPrefersDark } from './theme';

document.documentElement.dataset.theme = resolveTheme(readThemePreference(), systemPrefersDark());
