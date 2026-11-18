#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")"
: "${OAHU_FEED:?Set OAHU_FEED to the OAHU NuGet feed}"
sources=(--source "$OAHU_FEED" --source https://api.nuget.org/v3/index.json)
if [[ -n "${ANDROID_HOME:-}" ]]; then
  sdk=(-p:AndroidSdkDirectory="$ANDROID_HOME")
else
  sdk=()
fi
if [[ -n "${JAVA_HOME:-}" ]]; then
  sdk+=(-p:JavaSdkDirectory="$JAVA_HOME")
fi

project=Host/Host.csproj
dotnet restore "$project" "${sources[@]}" "${sdk[@]}" -p:NuGetAudit=false
dotnet build "$project" -c Release --no-restore "${sdk[@]}"
