/**
 * フォームの検証と、入力文字列の解釈。**Svelte に依存させない。**
 *
 * **サーバの検証の代わりではない。** 打ち間違いをその場で知らせるためのもので、
 * 最終的な判断はサーバがする（`400 validation`）。
 * 失敗は文言の鍵で返す。画面が今の言語で書く。
 */
import type { MessageKey } from './i18n/messages';
import {
  ATTRIBUTE_SOURCES,
  OIDC_SCOPES,
  type AccessEffect,
  type AccessRule,
  type AttributeMapping,
  type AttributeSource,
  type ClientType,
  type NameIdFormat,
  type NameIdSource,
  type OidcClient,
  type OidcClientInput,
  type SamlProvider,
  type SamlProviderInput,
} from './types';

/** 検証の失敗。`params` は文言への差し込み。 */
export interface FieldError {
  key: MessageKey;
  params?: Record<string, string | number>;
}

export type Parsed<T> = { ok: true; value: T } | { ok: false; error: FieldError };

/** 1 行 1 件の入力を配列にする。**空行は捨て、前後の空白を除き、重複は 1 つにする。** */
export function parseLines(text: string): string[] {
  const seen = new Set<string>();
  const lines: string[] = [];
  for (const raw of text.split(/\r?\n/)) {
    const line = raw.trim();
    if (line !== '' && !seen.has(line)) {
      seen.add(line);
      lines.push(line);
    }
  }
  return lines;
}

/**
 * テナント ID の入力（`1, 2 3` のようにカンマ・空白・改行区切り）を数値の配列にする。
 * **空は「全テナント」を意味する空配列。** 正の整数以外が 1 つでもあれば失敗。重複は除き昇順にする。
 */
export function parseTenantIds(text: string): Parsed<number[]> {
  const tokens = text.split(/[\s,、，]+/).filter((token) => token !== '');
  const ids = new Set<number>();
  for (const token of tokens) {
    if (!/^[0-9]+$/.test(token)) {
      return { ok: false, error: { key: 'validation.tenantIds', params: { value: token } } };
    }
    const id = Number(token);
    if (!Number.isSafeInteger(id) || id <= 0) {
      return { ok: false, error: { key: 'validation.tenantIds', params: { value: token } } };
    }
    ids.add(id);
  }
  return { ok: true, value: [...ids].sort((a, b) => a - b) };
}

/** テナント ID を入力欄の文字列へ戻す。 */
export function formatTenantIds(ids: readonly number[]): string {
  return ids.join(', ');
}

/** ループバックのホストか。 */
function isLoopbackHost(hostname: string): boolean {
  return hostname === 'localhost' || hostname === '127.0.0.1' || hostname === '[::1]';
}

/**
 * リダイレクト URI（ログイン後・ログアウト後の戻り先）を調べる。
 *
 * - 絶対 URI で、`https`。**`http` はループバック（localhost など）だけ**
 * - 公開クライアントだけ、逆ドメイン形式のカスタムスキーム（`com.example.app:/cb`）も可（ネイティブアプリ）
 * - **ワイルドカードは認めない**（サーバは完全一致で照合する）
 * - フラグメント（`#`）は持てない（RFC 6749 3.1.2）
 */
export function validateRedirectUri(uri: string, isPublicClient = false): FieldError | null {
  if (uri.includes('*')) return { key: 'validation.redirectUri.wildcard', params: { value: uri } };

  let url: URL;
  try {
    url = new URL(uri);
  } catch {
    return { key: 'validation.redirectUri.invalid', params: { value: uri } };
  }

  const isHttp = url.protocol === 'https:' || url.protocol === 'http:';
  const isNativeScheme = isPublicClient && url.protocol.slice(0, -1).includes('.');
  if (!isHttp && !isNativeScheme) {
    return { key: 'validation.redirectUri.scheme', params: { value: uri } };
  }
  if (url.protocol === 'http:' && !isLoopbackHost(url.hostname)) {
    return { key: 'validation.redirectUri.insecure', params: { value: uri } };
  }
  if (uri.includes('#')) return { key: 'validation.redirectUri.fragment', params: { value: uri } };
  return null;
}

/** 行ごとに検証し、最初の失敗を返す。 */
export function validateRedirectUris(uris: readonly string[], isPublicClient = false): FieldError | null {
  for (const uri of uris) {
    const error = validateRedirectUri(uri, isPublicClient);
    if (error) return error;
  }
  return null;
}

/**
 * スコープの選択を検証して並べ直す（`OIDC_SCOPES` の順）。
 * **`openid` が要る。** 知らないスコープは失敗。
 */
export function validateScopes(selected: readonly string[]): Parsed<string[]> {
  const known = new Set<string>(OIDC_SCOPES);
  for (const scope of selected) {
    if (!known.has(scope)) {
      return { ok: false, error: { key: 'validation.scope.unknown', params: { value: scope } } };
    }
  }
  if (!selected.includes('openid')) {
    return { ok: false, error: { key: 'validation.scope.openid' } };
  }
  return { ok: true, value: OIDC_SCOPES.filter((scope) => selected.includes(scope)) };
}

/** スペース区切りのスコープ文字列（`openid profile`）を配列にして検証する。 */
export function parseScopeInput(text: string): Parsed<string[]> {
  const tokens = text.split(/\s+/).filter((token) => token !== '');
  return validateScopes([...new Set(tokens)]);
}

/** 有効期間（秒）の最大。1 年。 */
export const MAX_LIFETIME_SECONDS = 31_536_000;

/** 有効期間（秒）の入力。**空は「既定」を意味する `null`。** 正の整数だけ。 */
export function parseOptionalSeconds(text: string): Parsed<number | null> {
  const trimmed = text.trim();
  if (trimmed === '') return { ok: true, value: null };
  if (!/^[0-9]+$/.test(trimmed)) {
    return { ok: false, error: { key: 'validation.seconds' } };
  }
  const seconds = Number(trimmed);
  if (seconds <= 0 || seconds > MAX_LIFETIME_SECONDS) {
    return { ok: false, error: { key: 'validation.seconds' } };
  }
  return { ok: true, value: seconds };
}

/** アクセスのルールの上限（サーバと同じ）。 */
export const MAX_ACCESS_RULES = 200;

/**
 * アクセスのルールを検証する。**同じ対象を include と exclude の両方へ入れられない**（サーバと同じ）。
 */
export function validateAccessRules(rules: readonly AccessRule[]): FieldError | null {
  if (rules.length > MAX_ACCESS_RULES) {
    return { key: 'validation.accessRules.tooMany', params: { max: MAX_ACCESS_RULES } };
  }
  const effects = new Map<string, AccessEffect>();
  for (const rule of rules) {
    const key = `${rule.kind}:${rule.targetId}`;
    const previous = effects.get(key);
    if (previous !== undefined && previous !== rule.effect) {
      return { key: 'validation.accessRules.conflict', params: { target: rule.label ?? key } };
    }
    effects.set(key, rule.effect);
  }
  return null;
}

/** クライアント ID に使える文字（URL とヘッダに載せても壊れないもの）。 */
const CLIENT_ID_PATTERN = /^[A-Za-z0-9._~-]{1,100}$/;

// ---- OIDC クライアントのフォーム --------------------------------------------

/** 画面が持つ入力の状態。**配列や数値も入力欄の文字列で持つ。** */
export interface OidcFormState {
  clientId: string;
  displayName: string;
  clientType: ClientType;
  redirectUris: string;
  postLogoutRedirectUris: string;
  scopes: string[];
  tenantIds: string;
  accessRules: AccessRule[];
  accessTokenLifetimeSeconds: string;
  refreshTokenLifetimeSeconds: string;
  enabled: boolean;
}

export function emptyOidcForm(): OidcFormState {
  return {
    clientId: '',
    displayName: '',
    clientType: 'confidential',
    redirectUris: '',
    postLogoutRedirectUris: '',
    scopes: ['openid', 'profile', 'email'],
    tenantIds: '',
    accessRules: [],
    accessTokenLifetimeSeconds: '',
    refreshTokenLifetimeSeconds: '',
    enabled: true,
  };
}

export function oidcFormFromClient(client: OidcClient): OidcFormState {
  return {
    clientId: client.clientId,
    displayName: client.displayName,
    clientType: client.clientType,
    redirectUris: client.redirectUris.join('\n'),
    postLogoutRedirectUris: client.postLogoutRedirectUris.join('\n'),
    scopes: [...client.allowedScopes],
    tenantIds: formatTenantIds(client.allowedTenantIds),
    accessRules: client.accessRules.map((rule) => ({ ...rule })),
    accessTokenLifetimeSeconds: client.accessTokenLifetimeSeconds?.toString() ?? '',
    refreshTokenLifetimeSeconds: client.refreshTokenLifetimeSeconds?.toString() ?? '',
    enabled: client.enabled,
  };
}

export type OidcFormField =
  | 'clientId'
  | 'displayName'
  | 'redirectUris'
  | 'postLogoutRedirectUris'
  | 'scopes'
  | 'tenantIds'
  | 'accessRules'
  | 'accessTokenLifetimeSeconds'
  | 'refreshTokenLifetimeSeconds';

export type FormResult<TInput, TField extends string> =
  | { ok: true; value: TInput }
  | { ok: false; errors: Partial<Record<TField, FieldError>> };

/** OIDC クライアントのフォームを検証し、送る本体にする。 */
export function validateOidcForm(
  form: OidcFormState,
  mode: 'create' | 'edit',
): FormResult<OidcClientInput, OidcFormField> {
  const errors: Partial<Record<OidcFormField, FieldError>> = {};

  const clientId = form.clientId.trim();
  if (mode === 'create' && clientId !== '' && !CLIENT_ID_PATTERN.test(clientId)) {
    errors.clientId = { key: 'validation.clientId' };
  }

  const displayName = form.displayName.trim();
  if (displayName === '') errors.displayName = { key: 'validation.required' };

  const redirectUris = parseLines(form.redirectUris);
  if (redirectUris.length === 0) {
    errors.redirectUris = { key: 'validation.redirectUri.required' };
  } else {
    const error = validateRedirectUris(redirectUris, form.clientType === 'public');
    if (error) errors.redirectUris = error;
  }

  const postLogoutRedirectUris = parseLines(form.postLogoutRedirectUris);
  const postLogoutError = validateRedirectUris(postLogoutRedirectUris, form.clientType === 'public');
  if (postLogoutError) errors.postLogoutRedirectUris = postLogoutError;

  const scopes = validateScopes(form.scopes);
  if (!scopes.ok) errors.scopes = scopes.error;

  const tenantIds = parseTenantIds(form.tenantIds);
  if (!tenantIds.ok) errors.tenantIds = tenantIds.error;

  const rulesError = validateAccessRules(form.accessRules);
  if (rulesError) errors.accessRules = rulesError;

  const access = parseOptionalSeconds(form.accessTokenLifetimeSeconds);
  if (!access.ok) errors.accessTokenLifetimeSeconds = access.error;

  const refresh = parseOptionalSeconds(form.refreshTokenLifetimeSeconds);
  if (!refresh.ok) errors.refreshTokenLifetimeSeconds = refresh.error;

  if (Object.keys(errors).length > 0 || !scopes.ok || !tenantIds.ok || !access.ok || !refresh.ok) {
    return { ok: false, errors };
  }

  return {
    ok: true,
    value: {
      ...(mode === 'create' && clientId !== '' ? { clientId } : {}),
      displayName,
      clientType: form.clientType,
      redirectUris,
      postLogoutRedirectUris,
      allowedScopes: scopes.value,
      allowedTenantIds: tenantIds.value,
      accessRules: form.accessRules.map((rule) => ({ ...rule })),
      accessTokenLifetimeSeconds: access.value,
      refreshTokenLifetimeSeconds: refresh.value,
      enabled: form.enabled,
    },
  };
}

// ---- SAML サービスプロバイダのフォーム --------------------------------------

export interface SamlFormState {
  entityId: string;
  displayName: string;
  acsUrl: string;
  sloUrl: string;
  signingCertificate: string;
  requireSignedRequests: boolean;
  nameIdFormat: NameIdFormat;
  nameIdSource: NameIdSource;
  attributeMappings: AttributeMapping[];
  signAssertion: boolean;
  signResponse: boolean;
  tenantIds: string;
  accessRules: AccessRule[];
  enabled: boolean;
}

export function emptySamlForm(): SamlFormState {
  return {
    entityId: '',
    displayName: '',
    acsUrl: '',
    sloUrl: '',
    signingCertificate: '',
    requireSignedRequests: false,
    nameIdFormat: 'emailAddress',
    nameIdSource: 'email',
    attributeMappings: [],
    signAssertion: true,
    signResponse: true,
    tenantIds: '',
    accessRules: [],
    enabled: true,
  };
}

export function samlFormFromProvider(provider: SamlProvider): SamlFormState {
  return {
    entityId: provider.entityId,
    displayName: provider.displayName,
    acsUrl: provider.acsUrl,
    sloUrl: provider.sloUrl ?? '',
    signingCertificate: provider.signingCertificate ?? '',
    requireSignedRequests: provider.requireSignedRequests,
    nameIdFormat: provider.nameIdFormat,
    nameIdSource: provider.nameIdSource,
    attributeMappings: provider.attributeMappings.map((mapping) => ({ ...mapping })),
    signAssertion: provider.signAssertion,
    signResponse: provider.signResponse,
    tenantIds: formatTenantIds(provider.allowedTenantIds),
    accessRules: provider.accessRules.map((rule) => ({ ...rule })),
    enabled: provider.enabled,
  };
}

/**
 * メタデータの取り込み結果（`entityId` `displayName` `acsUrl` `sloUrl` `signingCertificate` だけ埋まる）を、
 * 今の入力へ重ねる。**取り込みが返さない項目（NameID・属性・署名の設定など）は今の入力を残す。**
 */
export function applyImportedMetadata(form: SamlFormState, imported: SamlProvider): SamlFormState {
  return {
    ...form,
    entityId: imported.entityId || form.entityId,
    displayName: imported.displayName || form.displayName,
    acsUrl: imported.acsUrl || form.acsUrl,
    sloUrl: imported.sloUrl ?? '',
    signingCertificate: imported.signingCertificate ?? '',
  };
}

export type SamlFormField =
  | 'accessRules'
  | 'entityId'
  | 'displayName'
  | 'acsUrl'
  | 'sloUrl'
  | 'signingCertificate'
  | 'attributeMappings'
  | 'tenantIds';

function isHttpUrl(value: string): boolean {
  try {
    const url = new URL(value);
    return url.protocol === 'https:' || url.protocol === 'http:';
  } catch {
    return false;
  }
}

/** 証明書として読めそうか。PEM（`BEGIN CERTIFICATE`）か、Base64 の本体だけ。 */
export function looksLikeCertificate(text: string): boolean {
  const body = text
    .replace(/-----BEGIN CERTIFICATE-----/g, '')
    .replace(/-----END CERTIFICATE-----/g, '')
    .replace(/\s+/g, '');
  return body.length >= 16 && /^[A-Za-z0-9+/]+={0,2}$/.test(body);
}

/** 属性名の行を検証する。**空・重複は失敗。** */
export function validateAttributeMappings(mappings: readonly AttributeMapping[]): FieldError | null {
  const known = new Set<string>(ATTRIBUTE_SOURCES);
  const names = new Set<string>();
  for (const mapping of mappings) {
    const name = mapping.attributeName.trim();
    if (name === '') return { key: 'validation.attribute.empty' };
    if (!known.has(mapping.source)) return { key: 'validation.attribute.source', params: { value: mapping.source } };
    if (names.has(name)) return { key: 'validation.attribute.duplicate', params: { value: name } };
    names.add(name);
  }
  return null;
}

/** SAML サービスプロバイダのフォームを検証し、送る本体にする。 */
export function validateSamlForm(form: SamlFormState): FormResult<SamlProviderInput, SamlFormField> {
  const errors: Partial<Record<SamlFormField, FieldError>> = {};

  const entityId = form.entityId.trim();
  if (entityId === '') errors.entityId = { key: 'validation.required' };

  const displayName = form.displayName.trim();
  if (displayName === '') errors.displayName = { key: 'validation.required' };

  const acsUrl = form.acsUrl.trim();
  if (acsUrl === '') errors.acsUrl = { key: 'validation.required' };
  else if (!isHttpUrl(acsUrl)) errors.acsUrl = { key: 'validation.url' };

  const sloUrl = form.sloUrl.trim();
  if (sloUrl !== '' && !isHttpUrl(sloUrl)) errors.sloUrl = { key: 'validation.url' };

  const certificate = form.signingCertificate.trim();
  if (certificate !== '' && !looksLikeCertificate(certificate)) {
    errors.signingCertificate = { key: 'validation.certificate' };
  } else if (certificate === '' && form.requireSignedRequests) {
    errors.signingCertificate = { key: 'validation.certificate.required' };
  }

  const mappingError = validateAttributeMappings(form.attributeMappings);
  if (mappingError) errors.attributeMappings = mappingError;

  const tenantIds = parseTenantIds(form.tenantIds);
  if (!tenantIds.ok) errors.tenantIds = tenantIds.error;

  const rulesError = validateAccessRules(form.accessRules);
  if (rulesError) errors.accessRules = rulesError;

  if (Object.keys(errors).length > 0 || !tenantIds.ok) {
    return { ok: false, errors };
  }

  return {
    ok: true,
    value: {
      entityId,
      displayName,
      acsUrl,
      sloUrl: sloUrl === '' ? null : sloUrl,
      signingCertificate: certificate === '' ? null : certificate,
      requireSignedRequests: form.requireSignedRequests,
      nameIdFormat: form.nameIdFormat,
      nameIdSource: form.nameIdSource,
      attributeMappings: form.attributeMappings.map((mapping) => ({
        attributeName: mapping.attributeName.trim(),
        source: mapping.source as AttributeSource,
      })),
      signAssertion: form.signAssertion,
      signResponse: form.signResponse,
      allowedTenantIds: tenantIds.value,
      accessRules: form.accessRules.map((rule) => ({ ...rule })),
      enabled: form.enabled,
    },
  };
}
