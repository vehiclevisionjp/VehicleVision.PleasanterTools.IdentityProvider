# Claude向けリポジトリ指示

このリポジトリで作業する場合は、共通のリポジトリ指示に従ってください。

@.github/copilot-instructions.md

パスに紐づく詳細な規約は `.github/instructions/*.instructions.md` に分かれています。
**そのパスのファイルを触る前に、該当するものを読んでください。**
Claude Code は `applyTo` を自動で解釈しないため、自分で開く必要があります。

- [ドキュメント](.github/instructions/ドキュメント.instructions.md) — `_documents/**`

これらの詳細ルールは上記Copilot指示および各 instructions ファイルを単一の参照元とし、
このファイルへ複製しないでください。

## このリポジトリ固有の注意

- **`_reference/Implem.Pleasanter` は必要時に取得する Pleasanter 本体（AGPL v3）の非追跡ソースです。**
  事実確認のための参照専用で、コードの取り込みは禁止です。
  読む前に `bash scripts/sync-reference.sh` で同期し、`PLEASANTER_REFERENCE_DIR` の指定時は
  その取得先を直接読みます。確認した完全な SHA・固定リンク・参照日を残し、実機未検証は明記します。
  詳細は [`_reference/README.md`](_reference/README.md) を参照してください。
- **Pleasanter の DB へは読み取り専用で接続します。** 書き込まない設計で、
  Pleasanter の `Users`（`SecretKey` を含む）を認証元として読むだけです。
  **Pleasanter の DB 資格情報と IdP の署名鍵は、サーバ側だけで保持し、ブラウザへ渡さないでください**
  （規約 8）。API キーは使いません。
- **IdP は 2 つの DB を使います。** Pleasanter の DB（`IDP_PLEASANTER_DB_PROVIDER` /
  `IDP_PLEASANTER_DB_CONNECTIONSTRING`）と、IdP 自身の DB（`IDP_DB_PROVIDER` /
  `IDP_DB_CONNECTIONSTRING`）です。同じ RDBMS サーバ内の別データベースでも構いません。
- **TOTP には Pleasanter の `Users.SecretKey` をそのまま使います。**
  列の定義と扱いは `_reference/Implem.Pleasanter` の実ソースで確認してください。

## 共有メモリリポジトリ

このリポジトリで作業して得た知見は、
[VehicleVision.PleasanterTools.IdentityProvider.ClaudeMemory](https://github.com/vehiclevisionjp/VehicleVision.PleasanterTools.IdentityProvider.ClaudeMemory)
（internal）で共有します。作業ツリーはハーネスのメモリディレクトリそのものです。

```text
~/.claude/projects/D--repos-VehicleVision-PleasanterTools-IdentityProvider/memory/
```

- **別途 clone を作らないこと。** 2 つあると必ず片方が古くなる
- セッション開始時に `git -C <memory> pull --rebase` で他の人の追記を取り込むこと
- メモリを追加・更新したら `MEMORY.md` の索引も更新し、コミットして push すること
  （本体リポジトリと違い、push まで指示を待たなくてよい）

なお共有メモリは正式な規約の置き場所ではありません。このファイルと
`.github/copilot-instructions.md` が常に優先され、食い違ったらメモリ側を直します。

### 本リポジトリの規約を変えたとき

**共有メモリに古くなった記述が無いか必ず確認すること。** 規約変更はメモリを黙って矛盾させます。
