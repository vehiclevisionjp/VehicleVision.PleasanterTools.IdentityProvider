import { adminPath, basePath, loginUrl } from './basePath';
import { apiUrl, buildHeaders, buildQuery, parseBody, parseError } from './http';
import { acceptLanguageHeader, t } from './i18n/state.svelte';
import type {
  AuditLog,
  AuditLogQuery,
  KeyPurpose,
  Me,
  OidcClient,
  OidcClientCreated,
  OidcClientInput,
  SamlProvider,
  SamlProviderInput,
  SigningKey,
  SsoSession,
  Status,
  UserLookup,
  AccessKind,
  DirectoryEntry,
} from './types';

/**
 * 呼び出しの結果。
 *
 * **例外を投げずに返す。** 画面は「通った / 通らなかった」とその理由を出し分ける必要があり、
 * 例外だと分岐が書きづらい。
 */
export type Result<T> =
  | { ok: true; value: T }
  | { ok: false; status: number; code: string; message: string };

let redirectingToLogin = false;

/** 未ログイン（401）のとき、ログイン画面へ遷移する。**戻り先は管理画面の入口。** */
function redirectToLogin(): void {
  if (redirectingToLogin || typeof window === 'undefined') return;
  redirectingToLogin = true;
  window.location.assign(loginUrl(adminPath, basePath));
}

interface CallInit {
  method?: 'GET' | 'POST' | 'PUT' | 'DELETE';
  json?: unknown;
  /** 応答を JSON ではなく文字列として受ける（証明書の PEM） */
  text?: boolean;
}

async function call<T>(path: string, init: CallInit = {}): Promise<Result<T>> {
  const method = init.method ?? 'GET';

  let response: Response;
  try {
    response = await fetch(apiUrl(path, basePath), {
      method,
      // **Cookie を必ず送る。** 認証は SSO セッションの Cookie
      credentials: 'same-origin',
      headers: buildHeaders(method, init.json !== undefined, acceptLanguageHeader()),
      body: init.json === undefined ? undefined : JSON.stringify(init.json),
    });
  } catch {
    return { ok: false, status: 0, code: 'network', message: t('app.networkError') };
  }

  if (response.status === 204) {
    return { ok: true, value: undefined as T };
  }

  const raw = await response.text();

  if (response.ok) {
    return { ok: true, value: (init.text ? raw : parseBody(raw)) as T };
  }

  if (response.status === 401) {
    redirectToLogin();
  }

  const error = parseError(
    response.status,
    parseBody(raw),
    t('app.requestFailed', { status: response.status }),
  );
  return { ok: false, status: error.status, code: error.code, message: error.message };
}

const segment = encodeURIComponent;

// ---- 自分・状態 -------------------------------------------------------------

export const getMe = () => call<Me>('/me');
export const logout = () => call<void>('/logout', { method: 'POST' });
export const getStatus = () => call<Status>('/status');

// ---- OIDC クライアント ------------------------------------------------------

export const listOidcClients = () => call<OidcClient[]>('/oidc-clients');
export const getOidcClient = (clientId: string) =>
  call<OidcClient>(`/oidc-clients/${segment(clientId)}`);
export const createOidcClient = (input: OidcClientInput) =>
  call<OidcClientCreated>('/oidc-clients', { method: 'POST', json: input });
export const updateOidcClient = (clientId: string, input: OidcClientInput) =>
  call<OidcClient>(`/oidc-clients/${segment(clientId)}`, { method: 'PUT', json: input });
export const reissueOidcClientSecret = (clientId: string) =>
  call<{ clientSecret: string }>(`/oidc-clients/${segment(clientId)}/secret`, { method: 'POST' });
export const deleteOidcClient = (clientId: string) =>
  call<void>(`/oidc-clients/${segment(clientId)}`, { method: 'DELETE' });

// ---- SAML サービスプロバイダ ------------------------------------------------

export const listSamlProviders = () => call<SamlProvider[]>('/saml-providers');
export const getSamlProvider = (providerId: string) =>
  call<SamlProvider>(`/saml-providers/${segment(providerId)}`);
export const createSamlProvider = (input: SamlProviderInput) =>
  call<SamlProvider>('/saml-providers', { method: 'POST', json: input });
export const updateSamlProvider = (providerId: string, input: SamlProviderInput) =>
  call<SamlProvider>(`/saml-providers/${segment(providerId)}`, { method: 'PUT', json: input });
export const deleteSamlProvider = (providerId: string) =>
  call<void>(`/saml-providers/${segment(providerId)}`, { method: 'DELETE' });
/** メタデータ XML を解釈する。**保存はされない。** */
export const importSamlMetadata = (xml: string) =>
  call<SamlProvider>('/saml-providers/import-metadata', { method: 'POST', json: { xml } });

// ---- 署名鍵 -----------------------------------------------------------------

export const listSigningKeys = () => call<SigningKey[]>('/signing-keys');
export const rotateSigningKey = (purpose: KeyPurpose) =>
  call<SigningKey>('/signing-keys/rotate', { method: 'POST', json: { purpose } });
/** SP へ渡す公開証明書（PEM）。 */
export const getSigningKeyCertificate = (keyId: string) =>
  call<string>(`/signing-keys/${segment(keyId)}/certificate`, { text: true });
export const deleteSigningKey = (keyId: string) =>
  call<void>(`/signing-keys/${segment(keyId)}`, { method: 'DELETE' });

// ---- SSO セッション ---------------------------------------------------------

export const listSessions = (query: { loginId?: string; limit?: number; offset?: number }) =>
  call<SsoSession[]>(`/sessions${buildQuery(query)}`);
export const revokeSession = (sessionId: string) =>
  call<void>(`/sessions/${segment(sessionId)}`, { method: 'DELETE' });

// ---- 監査ログ ---------------------------------------------------------------

export const listAuditLogs = (query: AuditLogQuery) =>
  call<AuditLog[]>(`/audit-logs${buildQuery({ ...query })}`);

// ---- Pleasanter ユーザの照会 ------------------------------------------------

/** アクセスのルールの対象を名前で探す（管理者自身のテナントだけ）。 */
export const searchDirectory = (kind: AccessKind, q: string) =>
  call<DirectoryEntry[]>(`/pleasanter/directory${buildQuery({ kind, q })}`);

export const lookupPleasanterUser = (loginId: string) =>
  call<UserLookup>(`/pleasanter/users/lookup${buildQuery({ loginId })}`);
