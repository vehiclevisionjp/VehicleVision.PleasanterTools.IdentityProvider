# OIDC 連携 運用手順書

連携先のアプリ（RP）から、この IdP を OpenID Connect の OP として使うための手順。
設計の要点は [`アーキテクチャ方針.md`](アーキテクチャ方針.md) の「4. OIDC」。

## エンドポイント

`{Issuer}` は `IDP_ISSUER`（例 `https://idp.example.com`）。**管理画面のダッシュボードに、そのままコピーできる形で出る。**

| 用途 | URL |
| --- | --- |
| ディスカバリ | `{Issuer}/.well-known/openid-configuration` |
| 認可 | `{Issuer}/oidc/authorize` |
| トークン | `{Issuer}/oidc/token` |
| UserInfo | `{Issuer}/oidc/userinfo` |
| 公開鍵（JWKS） | `{Issuer}/oidc/jwks` |
| ログアウト | `{Issuer}/oidc/logout` |
| 失効（RFC 7009） | `{Issuer}/oidc/revocation` |

多くの RP は、ディスカバリの URL を 1 つ設定すれば残りを自動で読む。

## 1. クライアントを登録する

管理画面（`{Issuer}/admin`）の「OIDC クライアント」で作る。**入れるのは Pleasanter の管理者
（既定は `ServiceManager`。[`設定パラメータ一覧.md`](設定パラメータ一覧.md)）。**

| 項目 | 内容 |
| --- | --- |
| クライアント ID | 英数字と `- _ .`。空にすると生成する |
| 種別 | `confidential`（サーバ側のアプリ。シークレットを持てる）/ `public`（SPA・ネイティブアプリ。**PKCE 必須・シークレット無し**） |
| リダイレクト URI | **完全一致。** ワイルドカード・前方一致は無い。`https` のみ（`localhost` とループバックは `http` も可）。公開クライアントは逆ドメイン形式のカスタムスキーム（`com.example.app:/cb`）も可 |
| ログアウト後の戻り先 | RP-Initiated Logout の `post_logout_redirect_uri`。完全一致 |
| スコープ | `openid`（必須）・`profile`・`email`・`groups`・`offline_access`。**許したものだけ**付与する |
| 許可するテナント | 空なら全テナント。入れると、そのテナントのユーザーだけに発行する（Pleasanter のテナント ID） |
| ユーザー・組織・グループで絞る | **許可（Include）と拒否（Exclude）のルール。** 下の「アクセスのルール」 |
| 有効期間 | アクセストークン・リフレッシュトークンの個別の指定（空なら既定） |

- 機密クライアントは作成時に**シークレットを 1 度だけ**表示する。**控えること。** 失ったら「再発行」する
  （旧シークレットは即座に使えなくなる）
- クライアント認証は `client_secret_basic`（既定）と `client_secret_post`。公開クライアントは `none`

### アクセスのルール（誰に使わせるか）

クライアントごとに、ユーザー・組織・グループで使える人を絞れる（管理画面の編集画面の「ユーザー・組織・グループで絞る」）。

- **拒否（Exclude）に当たる人は使えない。** 許可（Include）に当たっていても同じ
- **許可（Include）が 1 件でもあれば、どれかに当たる人だけが使える。** 許可が無ければ（拒否だけなら）、拒否に当たらない全員が使える
- ルールが空なら絞らない。「許可するテナント」とは**両方を満たす**必要がある
- **グループは、本人が直接のメンバーか、所属組織がメンバーのもの。** グループの入れ子は辿らない
- **トークンを出すたびに判定する。** ルールを足す・人がグループから外れると、**発行済みのリフレッシュトークンも次の更新で止まる**
  （すでに出たアクセストークン・ID トークンは、期限まで有効）
- 対象は名前で探して選べる（自分のテナントの、無効でないものだけ）。ID を直接入れることもできる

## 2. RP 側の設定

| RP の項目 | 値 |
| --- | --- |
| Issuer / Authority | `{Issuer}` |
| Client ID / Secret | 1 で登録したもの |
| Redirect URI | 1 で登録したもの |
| スコープ | `openid profile email`（必要なら `groups offline_access`） |
| PKCE | `S256`（公開クライアントは必須。機密でも推奨） |
| 署名アルゴリズム | RS256 |

## 3. 返るクレーム

| スコープ | クレーム |
| --- | --- |
| `openid` | `sub`（Pleasanter の `UserId`）、`iss` `aud` `exp` `iat` `auth_time` `nonce` `amr` `acr` `sid` `at_hash` `azp` |
| `profile` | `name` `preferred_username`（ログイン ID）`locale` `zoneinfo` `dept` `tenant_id` `user_code` `tenant_manager` `service_manager` |
| `email` | `email`（Pleasanter で最初に登録されたメールアドレス）、`email_verified`（常に `false`） |
| `groups` | `groups`（所属するグループ名の配列） |

- **`sub` はログイン ID ではなく `UserId`。** ログイン ID が変わっても同じ人として扱える。
  RP のアカウントの突き合わせには `sub` を使い、`preferred_username` は表示用にする
- `amr` は `pwd`（パスワード）と、TOTP を通したときの `otp`。`acr` は `pwd` / `mfa`。
  **二段階認証を通ったかで RP が権限を変えたいときに使う**
- `email_verified` が常に `false` なのは、Pleasanter がメールアドレスの所有確認をしないため。
  RP が「確認済みのメールだけ」を要求する設定になっていると、ログインできない

## 4. 動作の確認（手で通す）

PKCE の検証値と、チャレンジを作る。

```bash
verifier=$(openssl rand -base64 48 | tr '+/' '-_' | tr -d '=\n')
challenge=$(printf '%s' "$verifier" | openssl dgst -sha256 -binary | openssl base64 -A | tr '+/' '-_' | tr -d '=')
```

ブラウザで認可要求を開く（ログインして、リダイレクト先の `code` を控える）。

```text
{Issuer}/oidc/authorize?response_type=code&client_id=CLIENT&redirect_uri=REDIRECT
  &scope=openid%20profile%20email&state=xyz&nonce=abc
  &code_challenge=CHALLENGE&code_challenge_method=S256
```

コードをトークンへ換える（機密クライアントの例）。

```bash
curl -s -u "CLIENT:SECRET" {Issuer}/oidc/token \
  -d grant_type=authorization_code -d code=CODE \
  -d redirect_uri=REDIRECT -d code_verifier="$verifier"
```

UserInfo を引く。

```bash
curl -s -H "Authorization: Bearer ACCESS_TOKEN" {Issuer}/oidc/userinfo
```

### ブラウザ上の RP（SPA）

トークン・UserInfo・失効のエンドポイントは、**登録したクライアントのリダイレクト URI と同じオリジン**からの
ブラウザの呼び出し（CORS）を許す。ディスカバリと JWKS は誰にでも読ませる。**リダイレクト URI を登録していない
オリジンの SPA は、トークンを取れない**（ブラウザが応答を読ませない）。クライアントを登録・変更した直後は、
最長 30 秒（知らないオリジンなら数秒）で反映される。

## 5. ログアウト

- RP から `{Issuer}/oidc/logout?id_token_hint=…&post_logout_redirect_uri=…&state=…` を開く。
  **`id_token_hint` を付けると確認なしでログアウトし、登録済みの戻り先へ返す。**
  付けないと確認画面を挟む（他所のページからの強制ログアウトを防ぐ）
- ログアウトすると、そのセッションから出た**アクセストークン（UserInfo）もリフレッシュトークンも使えなくなる**。
  ただし ID トークンは署名付きの JWT なので、RP がすでに持っているものは期限まで有効に見える。
  **RP 側でもセッションを閉じること**（Back-Channel / Front-Channel Logout には対応していない）
- 管理画面の「SSO セッション」から、特定のセッションを失効させることもできる

## 6. 署名鍵

初回起動で自動生成される（RSA 2048、RS256）。**管理画面の「署名鍵」から、いつでもローテーションできる。**

- ローテーションしても**旧鍵は公開し続ける**ので、発行済みのトークンの検証は止まらない。
  十分に時間が経ってから、退役した鍵を消す
- RP は JWKS を `kid` で引くので、通常は何もしなくてよい。JWKS を長くキャッシュする RP では、
  ローテーション直後に新しい鍵で署名したトークンが検証できないことがある
- 鍵のキャッシュは最長 30 秒。複数インスタンスでも、その間に追随する

## 7. うまくいかないとき

| 症状 | 原因と対処 |
| --- | --- |
| 認可で「要求を処理できません」の画面（リダイレクトしない） | `client_id` が無い・無効、または `redirect_uri` が登録と**完全に**一致しない。末尾の `/`・クエリ・大文字小文字を確かめる。**リダイレクト先が確定しないうちは、エラーをその URL へ返さない**（任意の URL へ誘導させないため） |
| `error=invalid_request`（リダイレクトで返る） | 公開クライアントで PKCE が無い、`code_challenge_method` が `S256` でない、同じパラメータが 2 回ある |
| `error=invalid_scope` | クライアントに許していないスコープを要求している |
| `error=access_denied` | ユーザーのテナントが「許可するテナント」に無い、または「ユーザー・組織・グループで絞る」のルールで許可されていない（監査ログの `oidc.authorize.denied` に `tenant-not-allowed` / `access-rule` が残る） |
| `invalid_grant`（トークン） | コードが期限切れ（60 秒）・使用済み・`redirect_uri` や `code_verifier` が違う。**使用済みのコードを再び送ると、そのコードから出たトークンが全て失効する** |
| `invalid_client` | クライアント ID・シークレットの誤り、無効にしたクライアント、公開クライアントがシークレットを送っている |
| ログインできない | [`SAML連携-運用手順書.md`](SAML連携-運用手順書.md) の末尾と、[`認証とTOTP-運用手順書.md`](認証とTOTP-運用手順書.md) |
| `iss` の不一致 | `IDP_ISSUER` が公開 URL と違う（リバースプロキシの外から見える URL を書く） |
