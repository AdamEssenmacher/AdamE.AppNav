#!/usr/bin/env bash

set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
package_directory="${1:-artifacts/packages}"
expected_package_version="${2:-}"
if [[ -z "$expected_package_version" ]]; then
  echo 'Usage: verify-package-assets.sh <package-directory> <expected-package-version>' >&2
  exit 2
fi

shopt -s nullglob
core_candidates=("${package_directory}"/AdamE.AppNav.[0-9]*.nupkg)
maui_candidates=("${package_directory}"/AdamE.AppNav.Maui.[0-9]*.nupkg)
core_symbol_candidates=("${package_directory}"/AdamE.AppNav.[0-9]*.snupkg)
maui_symbol_candidates=("${package_directory}"/AdamE.AppNav.Maui.[0-9]*.snupkg)

if [[ ${#core_candidates[@]} -ne 1 ]]; then
  echo "Expected exactly one AdamE.AppNav package in ${package_directory}." >&2
  exit 1
fi

if [[ ${#maui_candidates[@]} -ne 1 ]]; then
  echo "Expected exactly one AdamE.AppNav.Maui package in ${package_directory}." >&2
  exit 1
fi

if [[ ${#core_symbol_candidates[@]} -ne 1 ]]; then
  echo "Expected exactly one AdamE.AppNav symbol package in ${package_directory}." >&2
  exit 1
fi

if [[ ${#maui_symbol_candidates[@]} -ne 1 ]]; then
  echo "Expected exactly one AdamE.AppNav.Maui symbol package in ${package_directory}." >&2
  exit 1
fi

core_entries="$(unzip -Z1 "${core_candidates[0]}")"
maui_entries="$(unzip -Z1 "${maui_candidates[0]}")"
core_symbol_entries="$(unzip -Z1 "${core_symbol_candidates[0]}")"
maui_symbol_entries="$(unzip -Z1 "${maui_symbol_candidates[0]}")"
maui_nuspec="$(unzip -p "${maui_candidates[0]}" AdamE.AppNav.Maui.nuspec)"
core_nuspec="$(unzip -p "${core_candidates[0]}" AdamE.AppNav.nuspec)"

dotnet run \
  --file "$ROOT/eng/verify-package-versions.cs" \
  -- \
  "$expected_package_version" \
  "${core_candidates[0]}" \
  "${maui_candidates[0]}"

require_asset() {
  local entries="$1"
  local pattern="$2"
  local description="$3"

  if ! grep -Eq "${pattern}" <<<"${entries}"; then
    echo "Package is missing ${description}." >&2
    exit 1
  fi
}

reject_asset() {
  local entries="$1"
  local pattern="$2"
  local description="$3"

  if grep -Eq "${pattern}" <<<"${entries}"; then
    echo "Package unexpectedly contains ${description}." >&2
    exit 1
  fi
}

verify_symbol_archive() {
  local package="$1"
  local entries="$2"
  local pdb_pattern="$3"
  local expected_source="$4"
  local description="$5"

  if ! unzip -tq "${package}" >/dev/null; then
    echo "${description} symbol package is not a readable ZIP archive." >&2
    exit 1
  fi

  local pdb_count
  pdb_count="$(grep -Ec "${pdb_pattern}" <<<"${entries}")"
  if [[ "${pdb_count}" -eq 0 ]]; then
    echo "${description} symbol package contains no expected portable PDB entries." >&2
    exit 1
  fi

  local pdb_entry
  while IFS= read -r pdb_entry; do
    [[ -n "${pdb_entry}" ]] || continue

    local signature
    signature="$(set +o pipefail; unzip -p "${package}" "${pdb_entry}" | dd bs=4 count=1 2>/dev/null)"
    if [[ "${signature}" != "BSJB" ]]; then
      echo "${description} symbol entry '${pdb_entry}' is not a portable PDB." >&2
      exit 1
    fi

    if ! unzip -p "${package}" "${pdb_entry}" | strings | grep -F "${expected_source}" >/dev/null; then
      echo "${description} symbol entry '${pdb_entry}' does not describe '${expected_source}'." >&2
      exit 1
    fi
  done < <(grep -E "${pdb_pattern}" <<<"${entries}")
}

require_asset "${core_entries}" '^lib/net10\.0/AdamE\.AppNav\.dll$' 'the core net10.0 assembly'
require_asset "${core_entries}" '^analyzers/dotnet/cs/AdamE\.AppNav\.Generators\.dll$' 'the route source generator'
reject_asset "${core_entries}" '^analyzers/dotnet/cs/AdamE\.AppNav\.Maui\.Generators\.dll$' 'the MAUI page source generator'
require_asset "${core_symbol_entries}" '^lib/net10\.0/AdamE\.AppNav\.pdb$' 'the core portable PDB'

require_asset "${maui_entries}" '^lib/net10\.0/AdamE\.AppNav\.Maui\.dll$' 'the MAUI net10.0 assembly'
require_asset "${maui_entries}" '^lib/net10\.0-android[^/]*/AdamE\.AppNav\.Maui\.dll$' 'the MAUI net10.0 Android assembly'
require_asset "${maui_entries}" '^lib/net10\.0-ios[^/]*/AdamE\.AppNav\.Maui\.dll$' 'the MAUI net10.0 iOS assembly'
require_asset "${maui_entries}" '^lib/net10\.0-maccatalyst[^/]*/AdamE\.AppNav\.Maui\.dll$' 'the MAUI net10.0 Mac Catalyst assembly'
require_asset "${maui_entries}" '^analyzers/dotnet/cs/AdamE\.AppNav\.Maui\.Generators\.dll$' 'the MAUI page source generator'
reject_asset "${maui_entries}" '^analyzers/dotnet/cs/AdamE\.AppNav\.Generators\.dll$' 'the route source generator directly'
require_asset "${maui_symbol_entries}" '^lib/net10\.0/AdamE\.AppNav\.Maui\.pdb$' 'the MAUI net10.0 portable PDB'
require_asset "${maui_symbol_entries}" '^lib/net10\.0-android[^/]*/AdamE\.AppNav\.Maui\.pdb$' 'the MAUI Android portable PDB'
require_asset "${maui_symbol_entries}" '^lib/net10\.0-ios[^/]*/AdamE\.AppNav\.Maui\.pdb$' 'the MAUI iOS portable PDB'
require_asset "${maui_symbol_entries}" '^lib/net10\.0-maccatalyst[^/]*/AdamE\.AppNav\.Maui\.pdb$' 'the MAUI Mac Catalyst portable PDB'
require_asset "${maui_nuspec}" '<group targetFramework="net10\.0">' 'the MAUI net10.0 dependency group'
require_asset "${maui_nuspec}" '<dependency id="Microsoft\.Maui\.Controls" version="10\.0\.90"' 'the MAUI SR9 dependency for net10.0 assets'
require_asset "${core_nuspec}" '<repository type="git" url="https://github\.com/AdamEssenmacher/AdamE\.AppNav\.git"' 'the AppNav repository metadata'
if [[ "${AppNavStableRelease:-false}" == "true" ]]; then
  require_asset "${core_nuspec}" '<version>[0-9]+\.[0-9]+\.[0-9]+</version>' 'a semantic package version'
else
  require_asset "${core_nuspec}" '<version>[0-9]+\.[0-9]+\.[0-9]+-[^<]+</version>' 'a prerelease package version'
fi

if grep -Eq '^lib/(net8|netstandard)' <<<"${core_entries}"$'\n'"${maui_entries}"; then
  echo 'Packages contain an unsupported net8 or netstandard library asset.' >&2
  exit 1
fi

verify_symbol_archive \
  "${core_symbol_candidates[0]}" \
  "${core_symbol_entries}" \
  '^lib/net10\.0/AdamE\.AppNav\.pdb$' \
  'RouterNavigator.cs' \
  'Core'
verify_symbol_archive \
  "${maui_symbol_candidates[0]}" \
  "${maui_symbol_entries}" \
  '^lib/net10\.0(-android[^/]*|-ios[^/]*|-maccatalyst[^/]*)?/AdamE\.AppNav\.Maui\.pdb$' \
  'MauiNavigationPresenter.cs' \
  'MAUI'

echo 'Package assets match the supported .NET 10 public-preview contract.'
