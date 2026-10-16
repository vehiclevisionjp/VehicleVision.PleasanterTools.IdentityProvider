#!/usr/bin/env bash
# 起動しているアプリへ HTTP で当てて、配信と応答の形を確かめる。
#
# **単体テストでは見えない部分**（静的ファイルの配信・SPA のフォールバック・
# セキュリティヘッダ・OIDC の発見文書）を見る。
#
#   docker compose --profile sqlserver up -d --wait
#   ./scripts/smoke.sh
#
# コンソールへ出す文字列は英語（規約の例外。Azure の Kudu で日本語が化ける）。
set -uo pipefail

BASE="${IDP_BASE_URL:-http://localhost:8081}"
failed=0

check_status() {
    local path="$1" expected="$2" label="$3"
    local actual
    actual=$(curl -s -o /dev/null -w "%{http_code}" "${BASE}${path}")
    if [ "$actual" = "$expected" ]; then
        printf 'OK   %-46s %s\n' "$label" "$actual"
    else
        printf 'NG   %-46s expected %s / actual %s\n' "$label" "$expected" "$actual" >&2
        failed=1
    fi
}

check_header() {
    local path="$1" header="$2" label="$3"
    # **ヘッダを先に変数へ取る。** `curl | grep -q` だと、grep が先に終わって curl が SIGPIPE で落ち、
    # `pipefail` で「ヘッダが無い」と誤判定することがある（応答が小さいリダイレクトで実際に起きた）
    local headers
    headers=$(curl -s -D - -o /dev/null "${BASE}${path}")
    if printf '%s' "$headers" | grep -qi "^${header}:"; then
        printf 'OK   %-46s\n' "$label"
    else
        printf 'NG   %-46s missing header: %s\n' "$label" "$header" >&2
        failed=1
    fi
}

echo "== Serving =="
check_status "/healthz" 200 "liveness"
check_status "/ready" 200 "readiness (includes DB)"
check_status "/" 302 "front page (redirects to the admin console)"
check_status "/admin" 200 "admin console (SPA entry)"
check_status "/login" 200 "sign-in page"
check_status "/idp-assets/login.css" 200 "sign-in page stylesheet"
check_status "/api/admin/me" 401 "admin API requires sign-in"

echo
echo "== OIDC discovery =="
check_status "/.well-known/openid-configuration" 200 "OpenID Provider configuration"
check_status "/oidc/jwks" 200 "JWKS"

echo
echo "== SAML =="
check_status "/saml/metadata" 200 "IdP metadata"

echo
echo "== Security headers =="
check_header "/" "content-security-policy" "Content-Security-Policy"
check_header "/" "x-content-type-options" "X-Content-Type-Options"
check_header "/" "referrer-policy" "Referrer-Policy"
check_header "/" "permissions-policy" "Permissions-Policy"

echo
if [ "$failed" -eq 0 ]; then
    echo "All checks passed."
else
    echo "Some checks failed." >&2
fi
exit "$failed"
