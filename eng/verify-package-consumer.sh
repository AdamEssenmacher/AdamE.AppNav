#!/usr/bin/env bash

set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
package_directory_input="${1:-$ROOT/artifacts/packages}"
if [[ ! -d "$package_directory_input" ]]; then
  echo "Package directory does not exist: $package_directory_input" >&2
  exit 1
fi
PACKAGE_DIRECTORY="$(cd "$package_directory_input" && pwd)"
FIXTURE="$ROOT/tests/PackageConsumer.Maui"

shopt -s nullglob
maui_packages=("$PACKAGE_DIRECTORY"/AdamE.AppNav.Maui.[0-9]*.nupkg)
if [[ ${#maui_packages[@]} -ne 1 ]]; then
  echo "Expected exactly one AdamE.AppNav.Maui package in $PACKAGE_DIRECTORY." >&2
  exit 1
fi

package_version="$(
  unzip -p "${maui_packages[0]}" AdamE.AppNav.Maui.nuspec |
    sed -n 's:.*<version>\([^<]*\)</version>.*:\1:p' |
    head -n 1
)"
if [[ -z "$package_version" ]]; then
  echo "Could not read the package version from ${maui_packages[0]}." >&2
  exit 1
fi

consumer_root="$(mktemp -d "${TMPDIR:-/tmp}/appnav-package-consumer.XXXXXX")"
cleanup() {
  rm -rf "$consumer_root"
}
trap cleanup EXIT

cp -R "$FIXTURE/." "$consumer_root/project"
cp "$ROOT/NuGet.config" "$consumer_root/NuGet.config"
dotnet nuget add source "$PACKAGE_DIRECTORY" \
  --name appnav-local \
  --configfile "$consumer_root/NuGet.config"

export NUGET_PACKAGES="$consumer_root/packages"
export NUGET_HTTP_CACHE_PATH="$consumer_root/http-cache"
export DOTNET_CLI_HOME="$consumer_root/dotnet-home"
export DOTNET_CLI_TELEMETRY_OPTOUT=1
export DOTNET_NOLOGO=1

dotnet restore "$consumer_root/project/PackageConsumer.Maui.csproj" \
  --configfile "$consumer_root/NuGet.config" \
  -p:AppNavPackageVersion="$package_version" \
  --no-cache
dotnet build "$consumer_root/project/PackageConsumer.Maui.csproj" \
  -c Release \
  --no-restore \
  -p:AppNavPackageVersion="$package_version" \
  -warnaserror

generated_root="$consumer_root/project/obj/Release/net10.0/generated"
if [[ -d "$generated_root" ]]; then
  if ! find "$generated_root" -type f -name '*.cs' -print0 |
    xargs -0 grep -q 'class AppNavGenerated'; then
    echo "The isolated consumer did not emit AppNavGenerated source." >&2
    exit 1
  fi
fi

echo "Isolated MAUI package consumer built with AdamE.AppNav.Maui $package_version."
