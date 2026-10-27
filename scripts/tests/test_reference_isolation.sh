#!/usr/bin/env bash
# IdP 固有の参照元コメント・DB 名を許可し、実際の取り込みは拒否する。
set -euo pipefail
repo_root="$(cd "$(dirname "$0")/../.." && pwd -P)"
mkdir -p "$repo_root/temp"
test_root="$(mktemp -d "$repo_root/temp/reference-isolation-test.XXXXXX")"
trap 'rm -rf -- "$test_root"' EXIT
mkdir -p "$test_root/scripts" "$test_root/src"
cp "$repo_root/scripts/reference-isolation-check.sh" "$test_root/scripts/"
export PLEASANTER_REFERENCE_DIR="$test_root/shared"
cat > "$test_root/Directory.Build.props" <<'XML'
<!--
  参照元は _reference/Implem.Pleasanter。実装には取り込まない。
-->
<Project><!-- Implem.Pleasanter の版を説明するコメント --></Project>
XML
cat > "$test_root/src/test.cs" <<'CS'
/// <summary>参照元は _reference/Implem.Pleasanter。</summary>
class DatabaseName { public const string Value = "Implem.Pleasanter"; }
CS
check() {
    bash "$test_root/scripts/reference-isolation-check.sh" > "$test_root/output" 2>&1
}
expect_failure() {
    if check; then
        echo 'Expected reference isolation failure.' >&2
        exit 1
    fi
    grep -q 'must not be' "$test_root/output"
}
check
for reference in '_reference/Implem.Pleasanter' "$PLEASANTER_REFERENCE_DIR"; do
    cat > "$test_root/Directory.Build.props" <<XML
<Project>
  <!-- 直後の Compile は取り込みなので拒否する。 -->
  <ItemGroup><Compile
    Include="$reference/source.cs" /></ItemGroup>
</Project>
XML
    expect_failure
done
printf '<Project><!-- コメント --><Import Project="_reference/build.props" /></Project>\n' \
    > "$test_root/Directory.Build.props"
expect_failure
printf '<Project />\n' > "$test_root/Directory.Build.props"
printf 'using Implem.Pleasanter;\n' > "$test_root/src/test.cs"
expect_failure
echo 'IdP reference isolation tests passed: comments/DB names allowed; multiline, inline and shared imports rejected.'
