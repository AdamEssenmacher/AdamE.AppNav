#!/usr/bin/env bash

set -euo pipefail

package_directory="${1:-artifacts/packages}"

shopt -s nullglob
core_candidates=("${package_directory}"/AdamE.AppNav.[0-9]*.nupkg)
maui_candidates=("${package_directory}"/AdamE.AppNav.Maui.[0-9]*.nupkg)

if [[ ${#core_candidates[@]} -ne 1 ]]; then
  echo "Expected exactly one AdamE.AppNav package in ${package_directory}." >&2
  exit 1
fi

if [[ ${#maui_candidates[@]} -ne 1 ]]; then
  echo "Expected exactly one AdamE.AppNav.Maui package in ${package_directory}." >&2
  exit 1
fi

core_entries="$(unzip -Z1 "${core_candidates[0]}")"
maui_entries="$(unzip -Z1 "${maui_candidates[0]}")"
maui_nuspec="$(unzip -p "${maui_candidates[0]}" AdamE.AppNav.Maui.nuspec)"

require_asset() {
  local entries="$1"
  local pattern="$2"
  local description="$3"

  if ! grep -Eq "${pattern}" <<<"${entries}"; then
    echo "Package is missing ${description}." >&2
    exit 1
  fi
}

require_asset "${core_entries}" '^lib/net10\.0/AdamE\.AppNav\.dll$' 'the core net10.0 assembly'
require_asset "${core_entries}" '^analyzers/dotnet/cs/AdamE\.AppNav\.Generators\.dll$' 'the route source generator'

require_asset "${maui_entries}" '^lib/net10\.0/AdamE\.AppNav\.Maui\.dll$' 'the MAUI net10.0 assembly'
require_asset "${maui_entries}" '^lib/net10\.0-android[^/]*/AdamE\.AppNav\.Maui\.dll$' 'the MAUI net10.0 Android assembly'
require_asset "${maui_entries}" '^lib/net10\.0-ios[^/]*/AdamE\.AppNav\.Maui\.dll$' 'the MAUI net10.0 iOS assembly'
require_asset "${maui_entries}" '^lib/net10\.0-maccatalyst[^/]*/AdamE\.AppNav\.Maui\.dll$' 'the MAUI net10.0 Mac Catalyst assembly'
require_asset "${maui_nuspec}" '<group targetFramework="net10\.0">' 'the MAUI net10.0 dependency group'
require_asset "${maui_nuspec}" '<dependency id="Microsoft\.Maui\.Controls" version="10\.0\.90"' 'the MAUI SR9 dependency for net10.0 assets'

if grep -Eq '^lib/(net8|netstandard)' <<<"${core_entries}"$'\n'"${maui_entries}"; then
  echo 'Packages contain an unsupported net8 or netstandard library asset.' >&2
  exit 1
fi

echo 'Package assets match the supported .NET 9 and .NET 10 contract.'
