# .claude

## settings.json

Claude Code の**リポジトリ共有**設定。権限の許可（`allow`）と禁止（`deny`）のルールを置いている。
**すべてのリポジトリで同じ内容**にしている。差が出たら、どちらかが古い。

- **JSON であってコメントを書けない。** 書くと壊れ、中の許可ルールが黙って効かなくなる。
  説明が必要ならこのファイルへ書くこと（VehicleVision.Dealer で実際に踏んだ）
- **このファイルは追跡される。** `.gitignore` で無視しているのは
  `.claude/worktrees/`（並列作業の作業コピー）と `.claude/settings.local.json` だけ。
  ⚠️ **`.claude/` を丸ごと無視へ戻さないこと。** 共有設定が各人の手元にしか残らず、
  置いたつもりで誰にも届かない状態になる
- 個人のローカル上書きは `settings.local.json`。`.gitignore` 済みなのでコミットされない

## エージェント向けの規約はどこにあるか

- [`../.github/copilot-instructions.md`](../.github/copilot-instructions.md) — **規約の単一の参照元。**
  全文はここにあり、他はここを指すか写しを持つだけ
- [`../AGENTS.md`](../AGENTS.md) — AGENTS.md 標準を読むエージェント（Codex CLI など）向け。
  「必ず守る規約」の写しを持つ。**直接編集せず `scripts/sync-agents.sh` で再生成する。**
  食い違いは CI（`ci.yml` のライセンス検査ジョブ）が `--check` で落とす
- [`../CLAUDE.md`](../CLAUDE.md) — Claude Code 固有の補足（外部メモリの扱いなど）
- [`../_documents/ブランチ運用方針.md`](../_documents/ブランチ運用方針.md) — ブランチと作業ツリーの規則

## 許可と禁止の内容

**許可（allow）**

- 読み取り・検証: `git fetch`、`git worktree list / add`、`gh pr / issue / run / workflow / repo / label` の参照系、`npx markdownlint-cli2`、`bash -n`
- 作業の記録: `git add`、`git commit`（ローカルで取り消せる）
- ビルド・検査: `dotnet build / restore / test / pack / format / list / new / sln / add`、`npm run build / check / lint / typecheck`
  （`src/*` の Frontend は `npm --prefix src/* run build / check / test`）
- サブエージェント: `copilot`（Copilot CLI。意図した許可）
- 文書の取得: `WebFetch` は `docs.github.com` と `code.claude.com`
- **そのリポジトリに無いコマンド（たとえば dotnet が無いリポジトリの dotnet）の許可も、そのまま入っている。** 害はなく、同じ内容を保つことを優先した

**禁止（deny）**

- 破壊的: `git push --force`・`-f`・`--delete`、`git reset --hard`、`git clean -f`、`git commit --amend`、`rm -rf`、`Remove-Item -Recurse`
- 公開の取り消せない操作: `gh repo delete`、`gh secret *`、`gh variable set / delete`
- 保護ブランチへの直接 push（`main`・`develop`・`master`）。**作業ブランチ → PR で入れる**

**書き方と限界**

- ルールは `Bash(...)` と `PowerShell(...)` を対にして並べる。`git -C <パス> push` のような形でも deny が当たるよう、`git * push ...` の形も併記している
- ルールは、Claude Code が書く**典型の形**にしか当たらない。**安全の境界ではない。** 本当の防御は、GitHub 側のルールセットと、バイパス設定
## Copilot CLI

`Bash(copilot:*)` / `Bash(copilot *)` を許可してある。Claude Code から Copilot CLI へ
作業を外注するため（VehicleVision.Dealer と同じ 2 行）。

```bash
copilot --model gpt-5-mini --allow-all --no-color -p "指示"
```

- `--allow-all` が無いと確認プロンプトで止まる。`--no-color` が無いと出力が読めない
- **モデルは仕事に合わせて選ぶこと。** `gpt-5-mini` は `claude-opus-5` の約 45 分の 1
- **本番の作業へ出す前に `-p "reply with OK only"` で 1 回試すこと。**
  モデル名は増減し、通らない名前は即エラーで返る
- 導入は `winget install --id GitHub.Copilot --exact`、認証は `copilot login`

詳細と credits の実測値は外部メモリの `copilot-cli-invocation` /
`copilot-cli-model-choice` にある（[CLAUDE.md](../CLAUDE.md) の「外部メモリリポジトリ」）。
