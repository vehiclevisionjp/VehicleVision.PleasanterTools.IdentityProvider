#!/usr/bin/env bash
# VS Code のタスク定義を検査する（Issue #413）。
#
# **見るのは 2 つ。**
#   1. args に ${workspaceFolder} が混ざっていないか（Issue #411）
#      既定シェルが Git Bash だと、展開された D:\repos\... のバックスラッシュを
#      bash が食べて MSB1009 になる
#   2. args のパスが、そのタスクの cwd から解決できるか（Issue #413）
#      ${workspaceFolder} を外したとき、cwd を別ディレクトリへ移しているタスクだけ
#      取り違えた。**1 を直して 2 を作り込む、という壊し方をした**
#
# ⚠️ **ビルド成果物は見ない。** 未ビルドの作業ツリーで必ず落ちるため。
#
# コンソールへ出す文字列は英語（規約の例外。Azure の Kudu で日本語が化ける）。
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
tasks="$root/.vscode/tasks.json"

if [ ! -f "$tasks" ]; then
    echo "NG   .vscode/tasks.json not found" >&2
    exit 1
fi

python - "$root" "$tasks" <<'PY'
import json
import os
import re
import sys

root, path = sys.argv[1], sys.argv[2]
with open(path, encoding='utf-8') as handle:
    source = handle.read()

# JSONC なので行コメントを落としてから読む
document = json.loads(re.sub(r'^\s*//.*$', '', source, flags=re.M))

problems = []
for task in document.get('tasks', []):
    label = task.get('label', '(no label)')
    options = task.get('options') or {}
    cwd = options.get('cwd', '${workspaceFolder}').replace('${workspaceFolder}', root)
    for argument in task.get('args', []):
        if '${workspaceFolder}' in argument:
            problems.append(
                f"{label}: args must not contain ${{workspaceFolder}} ({argument})")
            continue
        if argument.startswith('-') or '/' not in argument:
            continue
        # ビルド成果物は未ビルドだと存在しないので見ない。
        # ⚠️ '/bin/' の部分一致で書くと bin/ で始まるパスを拾えない（実際に踏んだ）。
        # **区切りごとに見る**
        if {'bin', 'obj'} & set(argument.split('/')):
            continue
        target = os.path.join(cwd, argument)
        if not os.path.exists(target):
            problems.append(f"{label}: path does not resolve from cwd ({argument})")

for problem in problems:
    print(f"NG   {problem}", file=sys.stderr)

if problems:
    sys.exit(1)

print(f"OK   {len(document.get('tasks', []))} VS Code tasks resolve correctly")
PY
