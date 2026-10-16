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
| `IDP_SECRET_KEY` | IdP が保存する秘密（署名鍵など）を守る鍵。**失うと保存済みの秘密が全て使えなくなる。** 本番では Key Vault などへ置き、控えを取ること。`openssl rand -base64 32` で作れる |
| `IDP_ISSUER` | IdP の外から見える URL。SAML の EntityID と OIDC の `iss` になる。**公開 URL と必ず一致させること**（検証環境の既定 `http://localhost:8081`） |

- 上の表は **最小の起動に要るもの**。設定を増やしたときは、この表と
  [`_documents/開発環境.md`](../../_documents/開発環境.md) の環境変数の節へ追記すること
- **環境変数の接頭辞は `IDP_`。** 階層は `_` 区切りで、小文字・大文字は区別しない

## ファイル

**このフォルダへ置く JSON（`Service.json` など）は、設定の実装と一緒に足す。**
足すときは次を守ること。

- **コミットするのは既定値だけ。** 資格情報は `null` にしておき、環境変数か Key Vault で与える
- **JSON のキーが環境変数と名前が違うときは、読み込み側で写す**ことになるので、
  その対応表をこの節へ書く
- ⚠️ **値が `null` や空のキーは無視する。** 写すと、環境変数で与えた値を空で塗り潰してしまう

| ファイル | 内容 | 読まれているか |
|---|---|---|
| （未作成） | | |

## 検証環境の値

`compose.yaml` と `.vscode/launch.json` に入っているのは **検証環境用の固定値だけ**で、
本番の資格情報ではない。**本番へ持ち込まないこと。**
