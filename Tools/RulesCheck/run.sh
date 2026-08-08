#!/bin/bash
# Runs the game's rules logic outside Unity and reports pass/fail for each rule.
# Uses the .NET SDK bundled with the Unity editor, so nothing extra to install.
set -e

UNITY_VERSION="6000.5.7f1"
DOTNET_ROOT="/Applications/Unity/Hub/Editor/${UNITY_VERSION}/Unity.app/Contents/Resources/Scripting/DotNetSdk"

if [ ! -x "${DOTNET_ROOT}/dotnet" ]; then
    echo "Could not find the .NET SDK that ships with Unity ${UNITY_VERSION}." >&2
    echo "Looked in: ${DOTNET_ROOT}" >&2
    echo "If you upgraded Unity, update UNITY_VERSION at the top of this script." >&2
    exit 1
fi

export DOTNET_ROOT
export PATH="${DOTNET_ROOT}:${PATH}"
export DOTNET_CLI_TELEMETRY_OPTOUT=1
export DOTNET_NOLOGO=1

cd "$(dirname "$0")"
dotnet run --verbosity quiet -- "$@"
