# 認証と TOTP 運用手順書

IdP のサインイン（パスワード・TOTP）で起きることと、困ったときの確かめ方。
設計は [`アーキテクチャ方針.md`](アーキテクチャ方針.md) の「1. Pleasanter の DB を読む」と「2. TOTP」。

## ユーザーが入れる条件

IdP は Pleasanter の `Users` をそのまま使う。**次の全てを満たす人だけが入れる。**

1. ログイン ID とパスワードが合う（大文字小文字を区別しない。ログイン ID は Pleasanter の登録と同じ）
2. `Disabled`（無効）でない
3. `Lockout`（Pleasanter 側のロックアウト）でない
4. ログイン期限（`LoginExpirationLimit` / `LoginExpirationPeriod`）が切れていない
5. パスワードの有効期限（`PasswordExpirationTime`）が切れていない
6. TOTP を求められる人は、正しいコードを入れられる
7. IdP 側のロックアウト（下記）に掛かっていない

**パスワードが無い人（外部認証だけのユーザー）は、IdP のパスワードでは入れない。**

## TOTP の登録

**Pleasanter で登録する。** IdP には登録画面が無い（Pleasanter の DB へ書けないため）。

1. Pleasanter の管理者が、`Security.json` の `SecondaryAuthentication` で二次認証を有効にする
   （`Mode` を `DefaultEnable` か `DefaultDisable` にし、`NotificationType` を `Totp` にする）
2. 利用者が Pleasanter へログインすると、QR コードが出る。認証アプリで読み、確認のコードを入れる
3. 以降、**IdP でも同じアプリのコードで入れる**（IdP は `Users.SecretKey` で検証する）

### IdP がコードを求める人

| `IDP_TOTP_MODE` | 求める人 |
| --- | --- |
| `Auto`（既定） | **登録済みの人**（`EnableSecretKey` が有効で `SecretKey` がある）。Pleasanter の設定は見ない |
| `Pleasanter` | Pleasanter と同じ。`SecondaryAuthentication.Mode` が `DefaultEnable` なら、個別に無効にした人以外。`DefaultDisable` なら、個別に有効にした人だけ。**必須なのに未登録の人は入れない**（Pleasanter で先に登録してもらう） |

**どちらの人が求められるかは、管理画面の「ユーザー照会」で確かめられる**（`hasTotp` と `totpRequired`。
共有鍵そのものは見えない）。

⚠️ `Pleasanter` モードで IdP の `SecondaryAuthentication.Mode` が Pleasanter の設定と食い違うと、
Pleasanter では求められない人が IdP で求められる（その逆も）。写しなので、**変えるときは両方を揃える。**

### コードが通らないとき

| 原因 | 対処 |
| --- | --- |
| 端末の時計がずれている | 時刻を自動同期にする。IdP は前 1 枠（既定。`CountTolerances`）と後ろ 1 枠（各 30 秒）まで許す |
| 同じコードを 2 回使った | **30 秒の間でも、使ったコードは再利用できない。** 次のコードを待つ |
| Pleasanter で TOTP を登録し直した | 新しい共有鍵が `Users.SecretKey` に入る。IdP は都度読むので、登録し直した認証アプリのコードで入れる |
| 認証アプリを失くした | Pleasanter の管理者が、そのユーザーの TOTP を無効にする（`EnableSecretKey` を外す）。IdP に復旧コードは無い |

## IdP 側のロックアウト

- **パスワードと TOTP の失敗を合わせて**数え、`IDP_LOGIN_MAX_FAILURES`（既定 5 回）に達すると、
  `IDP_LOGIN_LOCKOUT_MINUTES`（既定 15 分）は、正しいパスワードでも入れない
- 失敗を数える期間は `IDP_LOGIN_FAILURE_WINDOW_MINUTES`（既定 15 分）。成功すると記録は消える
- **存在しないログイン ID も同じ回数で締め出す。** 画面の応答から、ID が実在するかを読み取らせない
- **これは Pleasanter の `Lockout` とは別。** Pleasanter の DB へは書かない。**解除は時間が経つのを待つ**
  （急ぐときは、IdP の DB の `LoginFailures` の該当行を消す）。Pleasanter 側で `Lockout` にされた人は、
  Pleasanter で解除する

## 画面のメッセージ

| 画面 | 原因 |
| --- | --- |
| ログイン ID またはパスワードが正しくありません | 誤り、または存在しない ID（区別しない） |
| 試行が続いたため、しばらくサインインできません | IdP 側のロックアウト |
| このアカウントではサインインできません | 無効、Pleasanter 側のロックアウト、パスワードが無い |
| ログインの有効期限が切れています | `LoginExpirationLimit` / `LoginExpirationPeriod` |
| パスワードの有効期限が切れています | Pleasanter でパスワードを変える（IdP は変更できない） |
| 二段階認証が必要ですが、Pleasanter で認証アプリが登録されていません | `Pleasanter` モードで必須だが未登録 |
| 操作の有効時間が切れました | パスワードから TOTP までが 5 分を超えた。最初からやり直す |

詳しい理由は、管理画面の「監査ログ」で `auth.login.failure` / `auth.login.denied` / `auth.totp.failure` の
`detail.reason` を見る（パスワードやコードは記録しない）。

## 初期管理者でログインする

Pleasanter の初期管理者 `Administrator` は、**初回のログインでパスワードを変えるまで、
パスワードの有効期限が作成時刻になっている**（Pleasanter の仕様。`UsersInitializer.cs`）。
IdP でも同じ扱いなので、**先に Pleasanter へログインしてパスワードを変える。**

また `Administrator` は `TenantManager` だけを持ち `ServiceManager` ではない。
IdP の管理画面へ入れる役割は既定で `TenantManager`（テナント管理者以上）なので、そのまま入れる
（絞り方は [`設定パラメータ一覧.md`](設定パラメータ一覧.md) の `Admin.json`）。
