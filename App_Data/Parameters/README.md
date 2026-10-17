# Parameters（設定ファイル）

**Pleasanter 本体と同じ方式**で、設定は `App_Data/Parameters/*.json` に置く
（参照: `_reference/Implem.Pleasanter/Implem.Pleasanter/App_Data/Parameters/`、
`_reference/Implem.Pleasanter/Implem.ParameterAccessor/Parameters.cs`）。

## 規約

- **キーの説明は、同じ階層に `"//キー名"` の項目を置いて書く。** Pleasanter 本体と同じ書き方
- **資格情報（DB の接続文字列・署名鍵・`IDP_SECRET_KEY`）の実値をこのフォルダへコミットしないこと。**
  実値は次のいずれかで与える
    1. 環境変数（Azure App Service のアプリケーション設定）
    2. Azure Key Vault
    3. `{名前}.local.json`（`.gitignore` 済み。ローカル開発用）
- 上書きの優先順位は **`{名前}.local.json` ＞ 環境変数 ＞ `{名前}.json`** とする
- **Pleasanter の DB 資格情報と IdP の署名鍵は、サーバ側だけで保持する。**
  ブラウザへ渡す画面・API・ログを作らないこと（[`規約 8`](../../.github/copilot-instructions.md)）

## 2 つの DB

**IdP は 2 つの DB を使う。** どちらも「種類（`PROVIDER`）」と「接続文字列」の組で指定する。

| 対象 | 種類 | 接続文字列 | 扱い |
|---|---|---|---|
| Pleasanter の DB | `IDP_PLEASANTER_DB_PROVIDER` | `IDP_PLEASANTER_DB_CONNECTIONSTRING` | **読み取り専用。** ユーザー（`Users`）などを参照するだけで、書き込まない |
| IdP 自身の DB | `IDP_DB_PROVIDER` | `IDP_DB_CONNECTIONSTRING` | 署名鍵・クライアント登録・セッションなど、IdP の状態を保持する |

- 種類は `SqlServer` / `PostgreSql` / `MySql`
- **同じ RDBMS サーバ内の別データベースでよい。** ただし 2 つの DB は **別のデータベース**にすること
  （Pleasanter の DB へ IdP のテーブルを足さない）
- **本番では Pleasanter の DB へ読み取り専用の資格情報を渡すこと。**
  アプリが書き込まない設計でも、DB 側の権限で書けなくしておく

## 環境変数

| 環境変数 | 内容 |
|---|---|
| `IDP_PLEASANTER_DB_PROVIDER` | Pleasanter の DB の種類 |
| `IDP_PLEASANTER_DB_CONNECTIONSTRING` | Pleasanter の DB への接続文字列。**資格情報を含む** |
| `IDP_DB_PROVIDER` | IdP 自身の DB の種類 |
| `IDP_DB_CONNECTIONSTRING` | IdP 自身の DB への接続文字列。**資格情報を含む** |
| `IDP_SECRET_KEY` | IdP が保存する秘密（署名鍵など）を守る鍵。**無ければ、起動時に作って `Service.local.json` へ保存する**（単一インスタンス向け。控えを取ること）。**失うと保存済みの秘密が全て使えなくなる。** 本番では Key Vault などへ置き、控えを取ること。`openssl rand -base64 32` で作れる |
| `IDP_ISSUER` | IdP の外から見える URL。SAML の EntityID と OIDC の `iss` になる。**公開 URL と必ず一致させること**（検証環境の既定 `http://localhost:8081`） |

- **配置先ごとの与え方（systemd・IIS・Windows・Docker・Kubernetes）は、[`_documents/導入-更新運用手順書.md`](../../_documents/導入-更新運用手順書.md) の「設定の与え方（配置先ごと）」。**
- 上の表は **最小の起動に要るもの**。設定を増やしたときは、この表と
  [`_documents/開発環境.md`](../../_documents/開発環境.md) の環境変数の節へ追記すること
- **ここにある環境変数は、`Rds.json`（DB）と `Service.json`（`SecretKey` など）でも指定できる**（下の「ファイル」の表）。
  環境変数が `.json` より強く、`.local.json` が一番強い
- **環境変数の接頭辞は `IDP_`。** 階層は `_` 区切りで、小文字・大文字は区別しない

## ファイル

**このフォルダへ置く JSON（`Service.json` など）は、設定の実装と一緒に足す。**
足すときは次を守ること。

- **コミットするのは既定値だけ。** 資格情報は `null` にしておき、環境変数か Key Vault で与える
- **JSON のキーが環境変数と名前が違うときは、読み込み側で写す**ことになるので、
  その対応表をこの節へ書く
- ⚠️ **値が `null` や空のキーは無視する。** 写すと、環境変数で与えた値を空で塗り潰してしまう

**Pleasanter 本体と同じく、話題ごとにファイルを分け、階層で書く**（`SecondaryAuthentication` のような入れ子、配列）。
`Security.json` 以外は、ファイル名がキーの先頭に付く（`Admin.json` の `Include.Users` は `Admin:Include:Users`）ので、
別のファイルのキーとぶつからない。`"//キー名"` の説明項目は設定として読まない。

| ファイル | 内容 | 読まれているか |
|---|---|---|
| `Service.json` | `Issuer`・`SecretKey`・`AllowInsecure`・`PathBase`・`ForwardedNetworks`・`HealthNetworks` | ✅ |
| `Rds.json` | DB（`Pleasanter` と `Idp`。種類は `Dbms`、接続文字列、暗号化の検査、起動時のマイグレーション） | ✅ |
| `Session.json` | SSO セッションの有効時間・無操作の許容時間 | ✅ |
| `Oidc.json` | 認可コード・アクセストークン・ID トークン・リフレッシュトークンの有効時間 | ✅ |
| `Saml.json` | アサーションの有効時間 | ✅ |
| `RateLimit.json` | ログイン・トークンの IP ごとの上限（`Login.PerMinute` / `Token.PerMinute`） | ✅ |
| `Admin.json` | 管理画面の入口（`Path`）と、入れる人（`Role` / `LoginIds` / `Include` / `Exclude`） | ✅ |
| `Audit.json` | 監査ログの保持期間 | ✅ |
| `Security.json` | ログイン失敗の締め出し・TOTP（`SecondaryAuthentication` は Pleasanter と同じ形） | ✅ |

- **配列は `.local.json` と添字ごとに重なる**（Pleasanter 本体と同じ JSON の読み方）。`.local.json` の配列が基より短いと、
  基の残りの要素が残る。配列を差し替えるときは、基と同じ要素数で書くか、環境変数（`IDP_ADMIN_INCLUDE` など）を使う
- **環境変数で指定できるものは、すべてこれらのファイルでも指定できる。** 接続文字列や `SecretKey` の実値は、コミットする `.json` ではなく
  `{名前}.local.json`（git 管理外）か環境変数で与える。配列（`ForwardedNetworks` など）は環境変数ではカンマ区切りの 1 つの値
- 一覧と環境変数の対応は [`_documents/設定パラメータ一覧.md`](../../_documents/設定パラメータ一覧.md)

## 検証環境の値

`compose.yaml` と `.vscode/launch.json` に入っているのは **検証環境用の固定値だけ**で、
本番の資格情報ではない。**本番へ持ち込まないこと。**
