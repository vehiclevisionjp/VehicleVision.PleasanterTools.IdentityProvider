#!/usr/bin/env bash
# フロントエンド（管理画面など）を組み立てて .Web/wwwroot へ出す（Issue #421）。
#
# ⚠️ **wwwroot は git で追跡していない。** `git pull` しても画面は新しくならない。
# これを知らずに `dotnet run` だけを繰り返すと、**古い画面を見続けることになる**
# （実際に 1 週間気付かなかった。#421）。
#
# **依存は無いときだけ入れる。** 毎回 `npm ci` を回すと、デバッグの開始が遅くなる。
#
# コンソールへ出す文字列は英語（規約の例外。Azure の Kudu で日本語が化ける）。
set -euo pipefail

cd "$(dirname "${BASH_SOURCE[0]}")/../src/VehicleVision.PleasanterTools.IdentityProvider.Frontend"

if [ ! -d node_modules ]; then
    echo "Installing frontend dependencies (node_modules is missing)."
    npm ci
fi

npm run build

echo "OK   Frontend assets are up to date."
