# _reference（参考専用・AGPL）

`Implem.Pleasanter` は Pleasanter 本体（**GNU AGPL v3**）の **AI による事実確認専用ソース**。
Git では追跡せず、読む前に上流 `main` の先頭を必要時に取得する。CI では取得しない。
日次更新・ポインタ更新 PR は使わない。

- リポジトリ: <https://github.com/Implem/Implem.Pleasanter>
- 取得先: 既定は `_reference/Implem.Pleasanter/`、共有先は `PLEASANTER_REFERENCE_DIR` で指定する
- 再現性の根拠: **実際に確認した完全な SHA・ソースへの固定リンク・参照日**
- 過去の記録（2026-10-19）: 旧 gitlink は
  [`1b7b6e6c930cf6a40c89b0c8c692a63177289bef`](https://github.com/Implem/Implem.Pleasanter/tree/1b7b6e6c930cf6a40c89b0c8c692a63177289bef)。
  当時の文書は `Pleasanter_1.5.9.0` と記録していた。今回、その版の仕様・実機は再検証していない。
  新たな確認の根拠や現在の取得先を表すものではない

## 厳守事項

- **コードをコピー・流用・リンク・プロジェクト参照しない。** 本アプリは Pleasanter 本体を
  改造せず、**DB を読み取り専用で参照する別アプリ**として実装する
- ここは **DB スキーマ（`Users` など）・列定義・パスワードや `SecretKey` の扱いの事実確認** にのみ使う
- ビルド対象・配布物に含めない（ソリューションファイルにも入れない）
- 取得した参照ソースは編集しないこと。既定先・共有先とも非追跡であり、
  本リポジトリへコミットしない。説明用のこの README は追跡する

> 本アプリは Pleasanter の **DB を読み取り専用で参照するだけ**で、Pleasanter のコードを使わないので、本体の派生著作物には
> ならない。**本体のコードを取り込んだ瞬間にこの前提が崩れる。**
>
> **本製品はデュアルライセンス（AGPL-3.0-or-later OR 商用）で提供する**（[`../LICENSING.md`](../LICENSING.md)）。
> Pleasanter の著作権は当社に無いため、**取り込むと商用ライセンスでの提供ができなくなる。**
> 「どうせ AGPL 側があるのだから取り込んでよい」ではない。**デュアルの片方が壊れる。**

## 取得

```bash
# 読む前に毎回実行する（初回は shallow clone、以後は fetch して detached HEAD を更新）
bash scripts/sync-reference.sh
# 確認した版を記録する
git -C _reference/Implem.Pleasanter rev-parse HEAD
git -C _reference/Implem.Pleasanter log -1 --format='%H %cI'
```

Windows の PowerShell で共有先を指定する例:

```powershell
$env:PLEASANTER_REFERENCE_DIR = 'D:\references\Implem.Pleasanter'
bash scripts/sync-reference.sh
git -C $env:PLEASANTER_REFERENCE_DIR rev-parse HEAD
```

共有先は直接読み、ローカルへリンクを作らない。既存の変更・別 origin・取得不能などは失敗し、
既存ソースを上書きしない。失敗を無視せず、原因を解消してから参照する。
Windows の深い worktree では長パスによって初回取得が失敗する場合がある。
その場合は短い共有先を指定するか、実行プロセス内だけ Git の `core.longpaths=true` を指定する。
取得結果の `FullSHA`・`Retrieved`・`Version` を記録し、
`https://github.com/Implem/Implem.Pleasanter/blob/<完全なSHA>/<path>` の形で固定リンクを残す。
実機未検証ならその旨も明記する。

旧サブモジュールの取得済みソース・未コミット変更は移行時にも削除しない。
同期が拒否した場合も変更を保全し、強制更新や `.git/modules` の掃除は行わない。

## この参照ソースで確認できること

公開リポジトリには **Web アプリ本体のソースが含まれている**。IdP が見るのは次のあたり。
**パスと行番号は版によって変わるので、使うときに実物で確かめること。**

| 対象 | 確認できること |
|---|---|
| `Implem.Pleasanter/Models/Users/` | `Users` テーブルの列（`LoginId` / `Password` / `SecretKey` / `Disabled` など）の意味と扱い |
| `Implem.Pleasanter/Libraries/Security/` | パスワードのハッシュ方式、TOTP（`SecretKey`）の検証方式 |
| `Implem.Pleasanter/App_Data/Definitions/Definition_Column/*.json` | 列の型・桁 |
| `Implem.Pleasanter/Models/Groups/` `Models/Depts/` | 所属・グループのテーブルと関係（属性の払い出し元） |
| `global.json` / 各 `*.csproj` | **確認したコミットのランタイム版数** |

## 確認できないこと

- **実機の DB に入っている値の形。** ハッシュ・`SecretKey` の保存形式などは、
  **ソースの読みと実機の DB の両方で確認すること**
- 実運用のテナント設定・権限・拡張項目の有効化状態

## バージョン追随

- **読む前に同期する。** Pleasanter 更新時は差分
  （DB スキーマ・列定義・認証まわり・ランタイム版数）を確認し、必要な対応は
  **自前実装側に取り込む**（本体コードは持ち込まない）
- **`main` はリリース前の変更を含む。** 「どの版の事実か」は、**確認した完全な SHA・固定リンク・参照日と `<Version>`**
  （`Implem.Pleasanter/Implem.Pleasanter.csproj`）で言う。リリース版との対応は `Pleasanter_*` タグで確かめる

```bash
# どのリリースタグと同じコミットか
git ls-remote --tags https://github.com/Implem/Implem.Pleasanter.git 'refs/tags/Pleasanter_*^{}'
```

検証環境の `implem/pleasanter:1.5.9.0` は参照ソースの同期と独立に維持する。
DB スキーマ・認証・イメージ更新の妥当性は、別途実ソースと実機の両方で確認する。

本方式の出典（2026-10-27 参照）:
[Template の配布手順（承認済みコミット）](https://github.com/vehiclevisionjp/VehicleVision.PleasanterTools.Template/blob/3312f003fbe9827090e20bc643800824bdf30e22/_documents/参照ソースのオンデマンド取得への配布手順.md)。
