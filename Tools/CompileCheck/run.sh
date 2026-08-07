#!/bin/bash
# Compiles every game script, including the Unity- and Netcode-dependent ones,
# without opening the Editor. Catches compile errors in seconds.
#
# Needs Library/ScriptAssemblies to exist, which it does after Unity has opened
# the project at least once. Nothing is produced that the game uses.
set -e

UNITY_VERSION="6000.5.7f1"
DOTNET_ROOT="/Applications/Unity/Hub/Editor/${UNITY_VERSION}/Unity.app/Contents/Resources/Scripting/DotNetSdk"

if [ ! -x "${DOTNET_ROOT}/dotnet" ]; then
    echo "Could not find the .NET SDK that ships with Unity ${UNITY_VERSION}." >&2
    echo "Looked in: ${DOTNET_ROOT}" >&2
    echo "If you upgraded Unity, update UNITY_VERSION here and in CompileCheck.csproj." >&2
    exit 1
fi

export DOTNET_ROOT
export PATH="${DOTNET_ROOT}:${PATH}"
export DOTNET_CLI_TELEMETRY_OPTOUT=1
export DOTNET_NOLOGO=1

cd "$(dirname "$0")"

# Unity's own assemblies produce a wall of unrelated warnings, so only messages
# about the game's own files are shown.
set +e
output=$(dotnet build --nologo --verbosity quiet 2>&1)
status=$?
set -e

echo "$output" | grep -E "Assets/Scripts|error " || true

if [ ${status} -eq 0 ]; then
    echo "Compiles clean."
fi

exit ${status}
