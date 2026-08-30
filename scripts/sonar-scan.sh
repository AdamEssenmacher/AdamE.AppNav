#!/usr/bin/env bash
# ===========================================================================
# Local SonarQube analysis for AdamE.AppNav.
#
#   ./scripts/sonar-scan.sh              portable scan with host-test coverage
#   ./scripts/sonar-scan.sh --no-tests   portable scan without tests or coverage
#
# This is opt-in developer tooling for the shared local SonarQube stack. It is
# intentionally not a CI, pull-request, or release gate. Do not invoke this
# script from GitHub Actions or any other hosted CI workflow.
#
# The scan targets the portable net10.0 production graph and the four
# host-runnable test suites. Native MAUI tests, samples, benchmarks, and package
# consumer fixtures are outside this local baseline.
# ===========================================================================
set -uo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
PORTABLE_PROJECT="src/AdamE.AppNav.Maui/AdamE.AppNav.Maui.csproj"
PROJECT_KEY="adame-appnav"
PROJECT_NAME="AdamE.AppNav"
COVERAGE_DIR="$REPO_ROOT/.coverage"
STACK_DIR="$HOME/sonarqube-local"

TEST_PROJECTS=(
  "tests/AdamE.AppNav.Tests/AdamE.AppNav.Tests.csproj"
  "tests/AdamE.AppNav.AdapterContract.Tests/AdamE.AppNav.AdapterContract.Tests.csproj"
  "tests/AdamE.AppNav.Generators.Tests/AdamE.AppNav.Generators.Tests.csproj"
  "tests/AdamE.AppNav.Maui.Generators.Tests/AdamE.AppNav.Maui.Generators.Tests.csproj"
)

PORTABLE_DEPENDENCY_PROJECTS=(
  "src/AdamE.AppNav.Generators/AdamE.AppNav.Generators.csproj"
  "src/AdamE.AppNav/AdamE.AppNav.csproj"
  "src/AdamE.AppNav.Maui.Generators/AdamE.AppNav.Maui.Generators.csproj"
)

bold() { printf '\033[1m%s\033[0m\n' "$*"; }
ok()   { printf '  \033[32m✓\033[0m %s\n' "$*"; }
warn() { printf '  \033[33m!\033[0m %s\n' "$*"; }
die()  { printf '  \033[31m✗\033[0m %s\n' "$*"; exit 1; }

usage() {
  cat <<'EOF'
Usage: ./scripts/sonar-scan.sh [--no-tests]

Runs the portable AdamE.AppNav analysis against the local SonarQube instance.
This developer command is intentionally not part of GitHub Actions or CI.
EOF
}

RUN_TESTS=1
[ "$#" -le 1 ] || {
  usage >&2
  die "expected at most one argument"
}
case "${1:-}" in
  "") ;;
  --no-tests) RUN_TESTS=0 ;;
  -h|--help)
    usage
    exit 0
    ;;
  *)
    usage >&2
    die "unknown argument: $1"
    ;;
esac

cd "$REPO_ROOT" || die "cannot cd to $REPO_ROOT"

# --- configuration ---------------------------------------------------------
[ -f "$STACK_DIR/.env" ] && { set -a; . "$STACK_DIR/.env"; set +a; }
SONAR_HOST_URL="${SONAR_HOST_URL:-http://localhost:9000}"

[ -n "${SONAR_TOKEN:-}" ] || die "SONAR_TOKEN not set.
    Generate one at $SONAR_HOST_URL (My Account > Security), then add it to:
      $STACK_DIR/.env"

command -v curl >/dev/null 2>&1 || die "curl not found on PATH"
curl -sf "$SONAR_HOST_URL/api/system/status" >/dev/null 2>&1 \
  || die "SonarQube is not answering at $SONAR_HOST_URL. Start it with:
      launchctl kickstart -p gui/\$(id -u)/com.adam.sonarqube"

command -v dotnet >/dev/null 2>&1 || die "dotnet SDK not found on PATH"
SDK_VERSION="$(dotnet --version 2>/dev/null)" \
  || die "the .NET SDK selected by global.json is not available"

# The scanner is a global dotnet tool. Recent versions provision their own JRE,
# so Java does not need to be installed separately.
export PATH="$PATH:$HOME/.dotnet/tools"
if ! command -v dotnet-sonarscanner >/dev/null 2>&1; then
  bold "Installing dotnet-sonarscanner (one time)"
  dotnet tool install --global dotnet-sonarscanner || die "scanner install failed"
  export PATH="$PATH:$HOME/.dotnet/tools"
fi

VERSION="$(git rev-parse --short HEAD 2>/dev/null || date +%Y%m%d%H%M)"

bold "Preparing the portable AdamE.AppNav scan"
echo "  project  : $PORTABLE_PROJECT"
echo "  version  : $VERSION"
echo "  sdk      : $SDK_VERSION"
echo "  coverage : $([ "$RUN_TESTS" -eq 1 ] && echo 'four host suites' || echo 'skipped (--no-tests)')"
if [ -n "$(git status --porcelain 2>/dev/null)" ]; then
  warn "working tree is dirty -- SonarQube will analyze the files on disk"
fi
echo

# Restore before begin so a feed or graph failure cannot strand an analysis.
bold "Restoring the portable production graph"
for project in "${PORTABLE_DEPENDENCY_PROJECTS[@]}"; do
  dotnet restore "$project" \
    || die "restore failed: $project"
done

# Restore the multi-targeted MAUI root without forwarding the net10.0 override
# into its netstandard2.0 generator dependencies, which were restored above.
dotnet restore "$PORTABLE_PROJECT" \
  --no-dependencies \
  -p:TargetFrameworks=net10.0 \
  || die "portable production restore failed"

if [ "$RUN_TESTS" -eq 1 ]; then
  for project in "${TEST_PROJECTS[@]}"; do
    dotnet restore "$project" \
      || die "restore failed: $project"
  done
fi

rm -rf "$COVERAGE_DIR"

# --- begin -----------------------------------------------------------------
bold "1/4  sonarscanner begin"
BEGIN_ARGS=(
  "/k:$PROJECT_KEY"
  "/n:$PROJECT_NAME"
  "/v:$VERSION"
  "/d:sonar.host.url=$SONAR_HOST_URL"
  "/d:sonar.token=$SONAR_TOKEN"
  "/d:sonar.exclusions=**/bin/**,**/obj/**,samples/**,benchmarks/**,tests/AdamE.AppNav.Maui.Tests/**,tests/PackageConsumer.Maui/**"
  "/d:sonar.inclusions=**/*.cs"
  "/d:sonar.scanner.scanAll=false"
)
[ "$RUN_TESTS" -eq 1 ] \
  && BEGIN_ARGS+=("/d:sonar.cs.opencover.reportsPaths=$COVERAGE_DIR/**/coverage.opencover.xml")

dotnet sonarscanner begin "${BEGIN_ARGS[@]}" || die "begin failed"

SCAN_ACTIVE=1
finish_incomplete_scan() {
  local exit_code=$?
  if [ "$SCAN_ACTIVE" -eq 1 ]; then
    warn "ending the incomplete SonarQube analysis"
    dotnet sonarscanner end "/d:sonar.token=$SONAR_TOKEN" >/dev/null 2>&1 || true
    SCAN_ACTIVE=0
  fi
  return "$exit_code"
}
trap finish_incomplete_scan EXIT

abort_scan() {
  warn "$1"
  if dotnet sonarscanner end "/d:sonar.token=$SONAR_TOKEN" >/dev/null 2>&1; then
    SCAN_ACTIVE=0
  fi
  die "$1"
}

# --- build -----------------------------------------------------------------
# --no-incremental is load-bearing: otherwise MSBuild can skip up-to-date
# projects, preventing the Roslyn analyzers from examining their source.
bold "2/4  build portable production and test projects"
dotnet build "$PORTABLE_PROJECT" \
  --no-restore \
  --no-incremental \
  --disable-build-servers \
  -m:1 \
  -f net10.0 \
  || abort_scan "portable production build failed"

if [ "$RUN_TESTS" -eq 1 ]; then
  for project in "${TEST_PROJECTS[@]}"; do
    dotnet build "$project" \
      --no-restore \
      --no-incremental \
      --disable-build-servers \
      -m:1 \
      -p:BuildProjectReferences=false \
      || abort_scan "test-project build failed: $project"
  done
fi

# --- test ------------------------------------------------------------------
TESTS_FAILED=0
if [ "$RUN_TESTS" -eq 1 ]; then
  bold "3/4  four host suites + OpenCover"
  for project in "${TEST_PROJECTS[@]}"; do
    project_name="$(basename "$project" .csproj)"
    if ! dotnet test "$project" \
      --no-build \
      --no-restore \
      --collect:"XPlat Code Coverage;Format=opencover" \
      --results-directory "$COVERAGE_DIR/$project_name"; then
      warn "tests failed: $project"
      TESTS_FAILED=1
    fi
  done

  FOUND="$(find "$COVERAGE_DIR" -name 'coverage.opencover.xml' 2>/dev/null | wc -l | tr -d ' ')"
  if [ "$FOUND" -ne "${#TEST_PROJECTS[@]}" ]; then
    warn "expected ${#TEST_PROJECTS[@]} OpenCover reports, found $FOUND"
    TESTS_FAILED=1
  else
    ok "$FOUND OpenCover reports"
  fi
else
  bold "3/4  tests skipped"
fi

# --- end -------------------------------------------------------------------
bold "4/4  sonarscanner end (uploading)"
dotnet sonarscanner end "/d:sonar.token=$SONAR_TOKEN" || die "end failed"
SCAN_ACTIVE=0
trap - EXIT

echo
ok "Analysis uploaded"
echo "  $SONAR_HOST_URL/dashboard?id=$PROJECT_KEY"

if [ "$TESTS_FAILED" -ne 0 ]; then
  die "analysis uploaded, but tests or coverage failed"
fi
