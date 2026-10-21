/**
 * 本アプリを置いたサブパス（例 `/idp`）。**サブパスが無ければ空文字。**
 *
 * **組み立て時に焼き込まない。** サーバが配信時に meta 要素へ埋める（`HtmlShell.cs`）。
 * 同じ成果物を `/` にもサブパスにも置けるようにするため。
 *
 * ⚠️ **サーバへの経路はすべてこの値から始める。** `/api/...` を直に書くと、
 * サブパス配下に置いたとき同じホストの別アプリ（Pleasanter）へ要求が飛ぶ。
 */
const BASE_PLACEHOLDER = '__IDP_BASE_PATH__';
const ADMIN_PLACEHOLDER = '__IDP_ADMIN_PATH__';
const DEFAULT_ADMIN_PATH = '/admin';

/** meta 要素の値を、使える形に直す。**おかしな値は無かったものとして扱う。** */
export function normalizeBasePath(value: string | null | undefined): string {
  // **印が差し替わっていないのは開発サーバ（vite）。** サブパスは無い
  if (!value || value === BASE_PLACEHOLDER) return '';

  const trimmed = value.replace(/\/+$/, '');
  // **自サイト内の経路だけを受け付ける。** `//example.com` のような書き方は外す
  if (trimmed === '' || !trimmed.startsWith('/') || trimmed.startsWith('//')) return '';
  return trimmed;
}

/**
 * 管理画面の入口（サブパス込み）を整える。**サーバがサブパスごと埋める。**
 * 印のまま・おかしな値のときは、サブパスに既定の `/admin` を足したものにする。
 */
export function normalizeAdminPath(value: string | null | undefined, base: string): string {
  if (!value || value === ADMIN_PLACEHOLDER) return `${base}${DEFAULT_ADMIN_PATH}`;

  const trimmed = value.replace(/\/+$/, '');
  if (trimmed === '' || !trimmed.startsWith('/') || trimmed.startsWith('//')) {
    return `${base}${DEFAULT_ADMIN_PATH}`;
  }
  return trimmed;
}

function readMeta(name: string): string | undefined {
  if (typeof document === 'undefined') return undefined;
  return document.querySelector<HTMLMetaElement>(`meta[name="${name}"]`)?.content;
}

/** サーバが配信時に埋め込んだサブパス。 */
export const basePath = normalizeBasePath(readMeta('idp-base-path'));

/** サーバが配信時に埋め込んだ管理画面の入口（サブパス込み）。 */
export const adminPath = normalizeAdminPath(readMeta('idp-admin-path'), basePath);

/** 本アプリの経路（`/api/...` や `/login`）を、サブパスから始まる URL にする。 */
export function appUrl(path: string, base = basePath): string {
  if (!path.startsWith('/')) {
    throw new Error('本アプリ内のパスは / で始めてください。');
  }
  return `${base}${path}`;
}

/**
 * 未ログインのとき遷移するログイン画面の URL。
 * **戻り先は管理画面の経路（`returnUrl`）。** ログイン画面はサーバが描く。
 */
export function loginUrl(returnUrl: string, base = basePath): string {
  return `${appUrl('/login', base)}?returnUrl=${encodeURIComponent(returnUrl)}`;
}
