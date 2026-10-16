/**
 * ハッシュルーティング（`#/clients/abc` など）。**管理画面のパスはサーバが決める**ので、
 * 画面の切り替えは `#` 以降だけで行う。
 */

export type Route =
  | { page: 'dashboard' }
  | { page: 'clients' }
  | { page: 'client'; clientId: string | null }
  | { page: 'saml' }
  | { page: 'samlProvider'; providerId: string | null }
  | { page: 'keys' }
  | { page: 'sessions' }
  | { page: 'audit' }
  | { page: 'lookup' };

/** ヘッダのナビに出す画面（一覧・入口）。 */
export type NavPage = 'dashboard' | 'clients' | 'saml' | 'keys' | 'sessions' | 'audit' | 'lookup';

export const NAV_PAGES: readonly NavPage[] = [
  'dashboard',
  'clients',
  'saml',
  'keys',
  'sessions',
  'audit',
  'lookup',
];

const HASH_BY_PAGE: Record<NavPage, string> = {
  dashboard: '#/',
  clients: '#/clients',
  saml: '#/saml',
  keys: '#/keys',
  sessions: '#/sessions',
  audit: '#/audit',
  lookup: '#/lookup',
};

/** `location.hash` を画面へ。**知らないものはダッシュボード。** */
export function parseRoute(hash: string): Route {
  const path = hash.replace(/^#/, '').split('?')[0] ?? '';
  const segments = path.split('/').filter((segment) => segment !== '');
  const [head, second] = segments;

  const id = (): string | null => {
    if (second === undefined || segments.length > 2) return null;
    try {
      return decodeURIComponent(second);
    } catch {
      return null;
    }
  };

  switch (head) {
    case 'clients':
      if (segments.length === 1) return { page: 'clients' };
      if (second === 'new' && segments.length === 2) return { page: 'client', clientId: null };
      return id() === null ? { page: 'clients' } : { page: 'client', clientId: id() };
    case 'saml':
      if (segments.length === 1) return { page: 'saml' };
      if (second === 'new' && segments.length === 2) return { page: 'samlProvider', providerId: null };
      return id() === null ? { page: 'saml' } : { page: 'samlProvider', providerId: id() };
    case 'keys':
      return { page: 'keys' };
    case 'sessions':
      return { page: 'sessions' };
    case 'audit':
      return { page: 'audit' };
    case 'lookup':
      return { page: 'lookup' };
    default:
      return { page: 'dashboard' };
  }
}

/** ナビの項目が指すハッシュ。 */
export function navHash(page: NavPage): string {
  return HASH_BY_PAGE[page];
}

export function clientHash(clientId: string | null): string {
  return clientId === null ? '#/clients/new' : `#/clients/${encodeURIComponent(clientId)}`;
}

export function samlProviderHash(providerId: string | null): string {
  return providerId === null ? '#/saml/new' : `#/saml/${encodeURIComponent(providerId)}`;
}

/** 今の画面が、ナビのどの項目に属するか。 */
export function navPageOf(route: Route): NavPage {
  switch (route.page) {
    case 'client':
      return 'clients';
    case 'samlProvider':
      return 'saml';
    default:
      return route.page;
  }
}
