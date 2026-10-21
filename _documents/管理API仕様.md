# 管理 API 仕様

管理画面（Svelte の SPA）が呼ぶ API。**サーバは `Web/Endpoints/Admin*Endpoints.cs`、
画面は `Frontend/src/admin/` が受け持つ。** このファイルが両者の契約で、変えるときは両方を直す。

## 共通

- ベースは `{basePath}/api/admin`。`basePath` は `IDP_PATH_BASE`（既定 `/`）。画面は配信時に
  HTML へ埋め込まれる印（`__IDP_BASE_PATH__`）から取る
- JSON は camelCase、日時は ISO 8601（UTC、`Z` 付き）
- 認証は SSO セッションの Cookie。**管理者は Pleasanter のユーザ**（既定は `ServiceManager`）。
  画面は未ログインを `401` で知り、`{basePath}/login?returnUrl={管理画面のパス}` へ遷移する
  （ログイン画面はサーバが描く HTML。SPA には含めない）
- **更新系（POST / PUT / DELETE）は `X-IdP-Admin: 1` ヘッダが要る。** 付けないと `403`（CSRF 対策）
- 失敗の本体は `{ "error": "コード", "message": "日本語の説明" }`
    - `401 unauthorized`（未ログイン・失効）、`403 forbidden`（権限なし・ヘッダなし）、
      `404 not_found`、`409 conflict`（重複）、`400 validation`（`message` に原因）
- **秘密値は返さない。** クライアントシークレットは作成時・再発行時の 1 回だけ返す。
  Pleasanter の `SecretKey`・パスワードハッシュ・署名鍵の秘密鍵は一切返さない

## 自分

| メソッド・パス | 本体 | 応答 |
| --- | --- | --- |
| `GET /me` | — | `Me` |
| `POST /logout` | — | `204`（SSO セッションを閉じる） |

```text
Me { userId: number, tenantId: number, loginId: string, name: string,
     role: "ServiceManager" | "TenantManager", language: string,
     authMethods: string[],   // "pwd" / "otp"
     authTime: string }
```

## 状態

`GET /status` → `Status`

```text
Status {
  issuer: string, version: string,
  endpoints: { discovery, jwks, authorize, token, userinfo, endSession, revocation,
               samlMetadata, samlSso, samlSlo: string },
  database: { provider: string, appliedVersion: number|null, latestVersion: number, pendingCount: number },
  pleasanter: { connected: boolean, provider: string, error: string|null, userCount: number|null },
  totp: { mode: "Auto" | "Pleasanter", countTolerances: number },
  session: { lifetimeMinutes: number, idleMinutes: number }
}
```

## OIDC クライアント `/oidc-clients`

```text
OidcClient {
  clientId: string, displayName: string,
  clientType: "confidential" | "public",
  redirectUris: string[], postLogoutRedirectUris: string[],
  allowedScopes: string[],           // openid profile email groups offline_access のうち
  allowedTenantIds: number[],        // 空なら全テナント
  accessRules: AccessRule[],         // 空なら絞らない（下記）
  accessTokenLifetimeSeconds: number | null,   // null は既定
  refreshTokenLifetimeSeconds: number | null,
  enabled: boolean, hasSecret: boolean,
  createdAt: string, updatedAt: string }
```

| メソッド・パス | 本体 | 応答 |
| --- | --- | --- |
| `GET /oidc-clients` | — | `OidcClient[]` |
| `GET /oidc-clients/{clientId}` | — | `OidcClient` |
| `POST /oidc-clients` | `OidcClient`（`clientId` 省略可。省略すると生成） | `201` `{ client: OidcClient, clientSecret: string \| null }` |
| `PUT /oidc-clients/{clientId}` | `OidcClient`（`clientId` と種別は変えられない） | `OidcClient` |
| `POST /oidc-clients/{clientId}/secret` | — | `{ clientSecret: string }`（旧シークレットは即失効） |
| `DELETE /oidc-clients/{clientId}` | — | `204` |

- `public` は PKCE 必須でシークレットを持たない。`confidential` は作成時にシークレットを返す
- リダイレクト URI は完全一致で照合する。ワイルドカードは認めない

## SAML サービスプロバイダ `/saml-providers`

```text
SamlProvider {
  providerId: string,                // GUID
  entityId: string, displayName: string,
  acsUrl: string, sloUrl: string | null,
  signingCertificate: string | null, // SP の署名用証明書（PEM または Base64）
  requireSignedRequests: boolean,
  nameIdFormat: "emailAddress" | "persistent" | "unspecified" | "transient",
  nameIdSource: "loginId" | "email" | "userId" | "globalId",
  attributeMappings: { attributeName: string,
                       source: "loginId" | "email" | "name" | "userId" | "userCode"
                             | "dept" | "groups" | "tenantId" | "language" }[],
  signAssertion: boolean, signResponse: boolean,
  allowedTenantIds: number[],
  accessRules: AccessRule[],         // 空なら絞らない（下記）
  enabled: boolean, createdAt: string, updatedAt: string }
```

| メソッド・パス | 本体 | 応答 |
| --- | --- | --- |
| `GET /saml-providers` | — | `SamlProvider[]` |
| `GET /saml-providers/{providerId}` | — | `SamlProvider` |
| `POST /saml-providers` | `SamlProvider`（`providerId` 省略） | `201` `SamlProvider` |
| `PUT /saml-providers/{providerId}` | `SamlProvider` | `SamlProvider` |
| `DELETE /saml-providers/{providerId}` | — | `204` |
| `POST /saml-providers/import-metadata` | `{ xml: string }` | `SamlProvider`（保存はしない。`entityId` `displayName` `acsUrl` `sloUrl` `signingCertificate` だけ埋める） |

## 署名鍵 `/signing-keys`

```text
SigningKey { keyId: string, purpose: "oidc" | "saml", algorithm: string,
             status: "active" | "retired", thumbprint: string,
             notBefore: string, notAfter: string, createdAt: string }
```

| メソッド・パス | 本体 | 応答 |
| --- | --- | --- |
| `GET /signing-keys` | — | `SigningKey[]` |
| `POST /signing-keys/rotate` | `{ purpose: "oidc" \| "saml" }` | `201` `SigningKey`（新しい鍵が active、旧鍵は retired） |
| `GET /signing-keys/{keyId}/certificate` | — | `text/plain` の PEM（SP へ渡す公開証明書） |
| `DELETE /signing-keys/{keyId}` | — | `204`（retired だけ。active は `409`） |

## SSO セッション `/sessions`

```text
SsoSession { sessionId: string, tenantId: number, userId: number, loginId: string,
             name: string, authTime: string, lastSeenAt: string, expiresAt: string,
             ipAddress: string | null, userAgent: string | null,
             authMethods: string[], current: boolean }
```

| メソッド・パス | 応答 |
| --- | --- |
| `GET /sessions?loginId=&limit=&offset=` | `SsoSession[]`（有効なものだけ。`limit` 既定 50・最大 200） |
| `DELETE /sessions/{sessionId}` | `204`（そのセッションで発行済みのトークンも以降は使えない） |

## 監査ログ `/audit-logs`

`GET /audit-logs?from=&to=&actor=&failedOnly=&action=&limit=&offset=` → `AuditLog[]`

```text
AuditLog { occurredAt: string, actorUserKey: string | null, actorLoginId: string | null,
           action: string, statusCode: number | null,
           targetType: string | null, targetId: string | null,
           detail: object | null, ipAddress: string | null }
```

- `limit` 既定 100・最大 200。新しい順
- `action` は `auth.login.success` `auth.login.failure` `oidc.token.issued` `saml.response.issued`
  `admin.oidc-client.create` のようなドット区切り。部分一致で絞る

## アクセスのルール `AccessRule`

`OidcClient` と `SamlProvider` の `accessRules` に入れる。**ユーザー・組織・グループで、使える人を絞る。**

```text
AccessRule {
  effect: "include" | "exclude",
  kind: "user" | "dept" | "group",
  targetId: number,        // Pleasanter の UserId / DeptId / GroupId（1 以上）
  label: string | null }   // 画面に出す名前（登録時のもの。判定には使わない）
```

- **`exclude` に当たれば拒否。`include` が 1 件でもあれば、どれかに当たる人だけ。** 空なら絞らない。
  `allowedTenantIds` とは両方を満たす必要がある
- 保存時に `effect` / `kind` は小文字へ直し、同じ（`effect`・`kind`・`targetId`）は 1 件へ畳む
- 同じ対象を `include` と `exclude` の両方へ入れることはできない。上限は 200 件。違反は `400`
- 評価は、トークンを出すたび（認可・コード交換・リフレッシュ）と SAML の SSO のたびに行う

## Pleasanter の対象の検索

`GET /pleasanter/directory?kind=user|dept|group&q=` → `{ id: number, name: string, detail: string | null }[]`

アクセスのルールの対象を、名前で探す（部分一致・大文字小文字は区別しない。ユーザーはログイン ID も対象）。
**管理者自身のテナントの、無効でないものだけを、最大 20 件**返す。`kind` が違えば `400`。

## Pleasanter ユーザの照会（診断用）

`GET /pleasanter/users/lookup?loginId=` → `UserLookup`

```text
UserLookup { found: boolean, tenantId: number|null, userId: number|null, loginId: string|null,
             name: string|null, disabled: boolean, lockout: boolean,
             tenantManager: boolean, serviceManager: boolean,
             hasTotp: boolean,          // Pleasanter 側で TOTP が登録・有効
             totpRequired: boolean,     // この IdP がこの人に TOTP を求めるか
             mailAddresses: string[], groups: string[], dept: string|null }
```

**秘密鍵・パスワードは返さない。** 「なぜこの人は TOTP を求められる / 求められないのか」を運用者が確かめるための画面。
