#!/bin/bash
# Usage: OAHU_FEED=/path/to/oahu-feed/nuget ./build.sh
set -euo pipefail
cd "$(dirname "$0")"
: "${OAHU_FEED:?Set OAHU_FEED to the local OAHU NuGet feed}"
dotnet restore Host.csproj --source "$OAHU_FEED" --source https://api.nuget.org/v3/index.json
export OAHU_RUNTIME_SWIFT="${NUGET_PACKAGES:-$HOME/.nuget/packages}/oahu.ios/0.1.0-preview.g7a56b3ba/swift/OahuRuntime"
dotnet build Host.csproj -c Release --no-restore
