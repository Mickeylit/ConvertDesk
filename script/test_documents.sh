#!/usr/bin/env bash
# Focused checks for ConvertDesk's product boundaries, rather than removed upstream features.
set -euo pipefail
TASK_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
TASK_DOTNET="${CONVERTDESK_DOTNET:-}"
if [[ -z "$TASK_DOTNET" ]]; then
  if command -v dotnet >/dev/null 2>&1; then TASK_DOTNET="$(command -v dotnet)";
  elif [[ -x "$TASK_ROOT/../dotnet/dotnet" ]]; then TASK_DOTNET="$TASK_ROOT/../dotnet/dotnet";
  else echo 'Install .NET 10 SDK or set CONVERTDESK_DOTNET.' >&2; exit 1; fi
fi
export DOTNET_CLI_TELEMETRY_OPTOUT=1
# Integration tests discover engines in the repository, not inside the packaged app.
# A source archive intentionally excludes downloaded binaries and prepares them here.
if [[ "$(uname -s)" == Darwin ]]; then
  TASK_RUNTIME="osx-$(uname -m)"
  [[ "$TASK_RUNTIME" != osx-x86_64 ]] || TASK_RUNTIME=osx-x64
  for TASK_ENGINE in rhwp pandoc; do
    if [[ ! -x "$TASK_ROOT/engines/$TASK_ENGINE/$TASK_ENGINE" && ! -x "$TASK_ROOT/engines/$TASK_ENGINE/bin/$TASK_ENGINE" ]]; then
      python3 "$TASK_ROOT/script/fetch_document_engine.py" --runtime "$TASK_RUNTIME" --engine "$TASK_ENGINE" --destination "$TASK_ROOT/engines"
    fi
  done
fi
"$TASK_DOTNET" test "$TASK_ROOT/tests/Filee.Documents.Tests/Filee.Documents.Tests.csproj" -c Release \
  -p:ConvertDeskDocumentsOnly=true --artifacts-path "$TASK_ROOT/artifacts/document-tests"
"$TASK_DOTNET" test "$TASK_ROOT/tests/Filee.App.Tests/Filee.App.Tests.csproj" -c Release \
  --filter 'FullyQualifiedName~DocumentConversionPlannerTests|FullyQualifiedName~DocumentWorkspaceTests|FullyQualifiedName~DocumentShellLifecycleTests|FullyQualifiedName~ProductRuntimeBoundaryTests|FullyQualifiedName~RadialControllerTests|FullyQualifiedName~RadialProductBoundaryTests|FullyQualifiedName~ConvertDeskUpdatePolicyTests|FullyQualifiedName~TrayServiceTests|FullyQualifiedName~MainWindowReleaseTests|FullyQualifiedName~SingleMainWindowTests|FullyQualifiedName~Document_shell_displays_its_version'
"$TASK_DOTNET" test "$TASK_ROOT/tests/Filee.Cli.Tests/Filee.Cli.Tests.csproj" -c Release --filter 'FullyQualifiedName~ProductFormatBoundaryTests'
"$TASK_DOTNET" test "$TASK_ROOT/tests/Filee.Core.Tests/Filee.Core.Tests.csproj" -c Release \
  --filter 'FullyQualifiedName~ProductFormatPolicyTests|FullyQualifiedName~DataDirectoryTests|FullyQualifiedName~RoutePlannerTests|FullyQualifiedName~JobQueueTests'
