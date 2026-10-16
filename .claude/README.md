# .claude

## settings.json

Claude Code の**リポジトリ共有**設定。権限（`permissions.allow`）の許可ルールを置いている。

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
