# _reference（参考専用・AGPL）

`Implem.Pleasanter` は Pleasanter 本体（**GNU AGPL v3**）を **参考目的でのみ** サブモジュールとして
配置したもの。固定コミットで参照する。

- リポジトリ: <https://github.com/Implem/Implem.Pleasanter>
- 固定位置: `Pleasanter_1.5.9.0`（`1b7b6e6c`）

## 厳守事項

- **コードをコピー・流用・リンク・プロジェクト参照しない。** 本アプリは Pleasanter 本体を
  改造せず、**DB を読み取り専用で参照する別アプリ**として実装する
- ここは **DB スキーマ（`Users` など）・列定義・パスワードや `SecretKey` の扱いの事実確認** にのみ使う
- ビルド対象・配布物に含めない（ソリューションファイルにも入れない）
- `_reference/` 配下を編集しないこと。サブモジュールの作業ツリーであり、本リポジトリの
  コミットには乗らない

> 本アプリは Pleasanter の **DB を読み取り専用で参照するだけ**で、Pleasanter のコードを使わないので、本体の派生著作物には
> ならない。**本体のコードを取り込んだ瞬間にこの前提が崩れる。**
>
> **本製品はデュアルライセンス（AGPL-3.0-or-later OR 商用）で提供する**（[`../LICENSING.md`](../LICENSING.md)）。
> Pleasanter の著作権は当社に無いため、**取り込むと商用ライセンスでの提供ができなくなる。**
> 「どうせ AGPL 側があるのだから取り込んでよい」ではない。**デュアルの片方が壊れる。**

## 取得

```bash
git submodule update --init --recursive
```

## この submodule で確認できること

公開リポジトリには **Web アプリ本体のソースが含まれている**。IdP が見るのは次のあたり。
**パスと行番号は版によって変わるので、使うときに実物で確かめること。**

| 対象 | 確認できること |
|---|---|
| `Implem.Pleasanter/Models/Users/` | `Users` テーブルの列（`LoginId` / `Password` / `SecretKey` / `Disabled` など）の意味と扱い |
| `Implem.Pleasanter/Libraries/Security/` | パスワードのハッシュ方式、TOTP（`SecretKey`）の検証方式 |
| `Implem.Pleasanter/App_Data/Definitions/Definition_Column/*.json` | 列の型・桁 |
| `Implem.Pleasanter/Models/Groups/` `Models/Depts/` | 所属・グループのテーブルと関係（属性の払い出し元） |
| `global.json` / 各 `*.csproj` | **本体のランタイム版数**（SDK 10.0.100 / net10.0） |

## 確認できないこと

- **実機の DB に入っている値の形。** ハッシュ・`SecretKey` の保存形式などは、
  **ソースの読みと実機の DB の両方で確認すること**
- 実運用のテナント設定・権限・拡張項目の有効化状態

## バージョン追随

- 固定コミットで参照する。Pleasanter 更新時は差分（DB スキーマ・列定義・認証まわり・
  ランタイム版数）を確認し、必要な対応は**自前実装側に取り込む**（本体コードは持ち込まない）

```bash
git -C _reference/Implem.Pleasanter fetch --tags origin
git -C _reference/Implem.Pleasanter tag --sort=-creatordate | head -5
```
