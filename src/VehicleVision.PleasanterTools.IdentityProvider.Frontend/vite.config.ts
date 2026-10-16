import { resolve } from 'node:path';
import { svelte } from '@sveltejs/vite-plugin-svelte';
// **`vite` ではなく `vitest/config` から取る。** `test` の設定を書けるのはこちら
import { defineConfig } from 'vitest/config';
import type { Plugin } from 'vite';

// **書体は data URI にしない。**
// CSP に `font-src` が無く `default-src 'self'` で判定されるため、
// インライン化された書体はブラウザに拒否される。別ファイルとして出す
const fontFile = /\.(woff2?|ttf|otf)$/i;

// **書体が data URI で紛れ込んだら組み立てを失敗させる。**
function forbidInlineFonts(): Plugin {
  return {
    name: 'idp:forbid-inline-fonts',
    apply: 'build',
    generateBundle(_options, bundle) {
      for (const item of Object.values(bundle)) {
        if (item.type !== 'asset' || !item.fileName.endsWith('.css')) continue;
        const css = typeof item.source === 'string' ? item.source : new TextDecoder().decode(item.source);
        if (css.includes('data:font/')) {
          this.error(`Inlined font (data:font/) found in ${item.fileName}. The CSP rejects it; keep fonts as separate files.`);
        }
      }
    },
  };
}

// ビルド成果物は .Web の wwwroot へ出す。**単一のアプリとして配信するため**
export default defineConfig({
  plugins: [svelte(), forbidInlineFonts()],
  // **配置先のサブパスを組み立て時に焼き込まない。**
  //   - JS と CSS からの参照は自分の位置からの相対にする
  //   - HTML からの参照だけは埋め込み先の印にし、サーバが配信時に差し替える（HtmlShell.cs）
  base: './',
  experimental: {
    renderBuiltUrl(filename, { hostType }) {
      return hostType === 'html' ? `__IDP_BASE_PATH__/${filename}` : { relative: true };
    },
  },
  build: {
    outDir: '../VehicleVision.PleasanterTools.IdentityProvider.Web/wwwroot',
    emptyOutDir: true,
    assetsInlineLimit: (filePath) => (fontFile.test(filePath) ? false : undefined),
    rollupOptions: {
      // 入口は管理画面の 1 つだけ
      input: {
        admin: resolve(import.meta.dirname, 'admin.html'),
      },
    },
  },
  server: {
    // 開発時は IdP 本体（launchSettings の http プロファイル）へ中継する
    proxy: {
      '/api': 'http://localhost:5067',
    },
  },
  test: {
    // **`lib/` の純粋な関数だけを見る。**
    include: ['src/**/*.test.ts'],
    environment: 'node',
  },
});
