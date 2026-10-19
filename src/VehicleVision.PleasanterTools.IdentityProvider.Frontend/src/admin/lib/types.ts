/**
 * 管理 API の型。**`_documents/管理API仕様.md` を写したもの。**
 * 仕様に無いものは足さない。変えるときは仕様と両方を直す。
 */

export type AdminRole = 'ServiceManager' | 'TenantManager';

export interface Me {
  userId: number;
  tenantId: number;
  loginId: string;
  name: string;
  role: AdminRole;
  language: string;
  /** `pwd` / `otp` */
  authMethods: string[];
  authTime: string;
}

export interface StatusEndpoints {
  discovery: string;
  jwks: string;
  authorize: string;
  token: string;
  userinfo: string;
  endSession: string;
  revocation: string;
  samlMetadata: string;
  samlSso: string;
  samlSlo: string;
}

export type TotpMode = 'Auto' | 'Pleasanter';

export interface Status {
  issuer: string;
  version: string;
  endpoints: StatusEndpoints;
  database: {
    provider: string;
    appliedVersion: number | null;
    latestVersion: number;
    pendingCount: number;
  };
  pleasanter: {
    connected: boolean;
    provider: string;
    error: string | null;
    userCount: number | null;
  };
  totp: { mode: TotpMode; countTolerances: number };
  session: { lifetimeMinutes: number; idleMinutes: number };
}

// ---- アクセスのルール --------------------------------------------------------

export const ACCESS_EFFECTS = ['include', 'exclude'] as const;
export type AccessEffect = (typeof ACCESS_EFFECTS)[number];

export const ACCESS_KINDS = ['user', 'dept', 'group'] as const;
export type AccessKind = (typeof ACCESS_KINDS)[number];

/** 連携先へ入れる人を、ユーザー・組織・グループで絞るルール 1 件。 */
export interface AccessRule {
  effect: AccessEffect;
  kind: AccessKind;
  /** Pleasanter の UserId / DeptId / GroupId */
  targetId: number;
  /** 画面に出す名前（登録時のもの）。判定には使わない。 */
  label: string | null;
}

/** ルールの対象の候補（Pleasanter のユーザー・組織・グループ）。 */
export interface DirectoryEntry {
  id: number;
  name: string;
  /** ユーザーのログイン ID */
  detail: string | null;
}

// ---- OIDC クライアント -------------------------------------------------------

export const OIDC_SCOPES = ['openid', 'profile', 'email', 'groups', 'offline_access'] as const;
export type OidcScope = (typeof OIDC_SCOPES)[number];

export type ClientType = 'confidential' | 'public';

export interface OidcClient {
  clientId: string;
  displayName: string;
  clientType: ClientType;
  redirectUris: string[];
  postLogoutRedirectUris: string[];
  allowedScopes: string[];
  allowedTenantIds: number[];
  accessRules: AccessRule[];
  accessTokenLifetimeSeconds: number | null;
  refreshTokenLifetimeSeconds: number | null;
  enabled: boolean;
  hasSecret: boolean;
  createdAt: string;
  updatedAt: string;
}

/** 作成・更新で送る本体。**`clientId` は作成時だけ省ける。** サーバが持つ日時・`hasSecret` は送らない。 */
export type OidcClientInput = Omit<OidcClient, 'clientId' | 'hasSecret' | 'createdAt' | 'updatedAt'> & {
  clientId?: string;
};

export interface OidcClientCreated {
  client: OidcClient;
  clientSecret: string | null;
}

// ---- SAML サービスプロバイダ -------------------------------------------------

export const NAME_ID_FORMATS = ['emailAddress', 'persistent', 'unspecified', 'transient'] as const;
export type NameIdFormat = (typeof NAME_ID_FORMATS)[number];

export const NAME_ID_SOURCES = ['loginId', 'email', 'userId', 'globalId'] as const;
export type NameIdSource = (typeof NAME_ID_SOURCES)[number];

export const ATTRIBUTE_SOURCES = [
  'loginId',
  'email',
  'name',
  'userId',
  'userCode',
  'dept',
  'groups',
  'tenantId',
  'language',
] as const;
export type AttributeSource = (typeof ATTRIBUTE_SOURCES)[number];

export interface AttributeMapping {
  attributeName: string;
  source: AttributeSource;
}

export interface SamlProvider {
  providerId: string;
  entityId: string;
  displayName: string;
  acsUrl: string;
  sloUrl: string | null;
  signingCertificate: string | null;
  requireSignedRequests: boolean;
  nameIdFormat: NameIdFormat;
  nameIdSource: NameIdSource;
  attributeMappings: AttributeMapping[];
  signAssertion: boolean;
  signResponse: boolean;
  allowedTenantIds: number[];
  accessRules: AccessRule[];
  enabled: boolean;
  createdAt: string;
  updatedAt: string;
}

/** 作成・更新で送る本体。`providerId` は作成時は省く。 */
export type SamlProviderInput = Omit<SamlProvider, 'providerId' | 'createdAt' | 'updatedAt'> & {
  providerId?: string;
};

// ---- 署名鍵 -----------------------------------------------------------------

export type KeyPurpose = 'oidc' | 'saml';

export interface SigningKey {
  keyId: string;
  purpose: KeyPurpose;
  algorithm: string;
  status: 'active' | 'retired';
  thumbprint: string;
  notBefore: string;
  notAfter: string;
  createdAt: string;
}

// ---- SSO セッション ---------------------------------------------------------

export interface SsoSession {
  sessionId: string;
  tenantId: number;
  userId: number;
  loginId: string;
  name: string;
  authTime: string;
  lastSeenAt: string;
  expiresAt: string;
  ipAddress: string | null;
  userAgent: string | null;
  authMethods: string[];
  current: boolean;
}

// ---- 監査ログ ---------------------------------------------------------------

export interface AuditLog {
  occurredAt: string;
  actorUserKey: string | null;
  actorLoginId: string | null;
  action: string;
  statusCode: number | null;
  targetType: string | null;
  targetId: string | null;
  detail: Record<string, unknown> | null;
  ipAddress: string | null;
}

export interface AuditLogQuery {
  from?: string;
  to?: string;
  actor?: string;
  action?: string;
  failedOnly?: boolean;
  limit?: number;
  offset?: number;
}

// ---- Pleasanter ユーザ照会 --------------------------------------------------

export interface UserLookup {
  found: boolean;
  tenantId: number | null;
  userId: number | null;
  loginId: string | null;
  name: string | null;
  disabled: boolean;
  lockout: boolean;
  tenantManager: boolean;
  serviceManager: boolean;
  hasTotp: boolean;
  totpRequired: boolean;
  mailAddresses: string[];
  groups: string[];
  dept: string | null;
}
