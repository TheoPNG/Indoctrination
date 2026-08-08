#!/bin/bash
# Runs the alpha smoke test inside a real Unity process: opens the scene, loads
# the card database through Resources, and builds the whole board interface while
# driving a complete game through it.
#
# RulesCheck proves the rules and CompileCheck proves the types; only this can
# prove the game actually stands up. Close the Unity Editor before running - it
# holds a lock on the project.
set -e

UNITY_VERSION="6000.5.7f1"
UNITY="/Applications/Unity/Hub/Editor/${UNITY_VERSION}/Unity.app/Contents/MacOS/Unity"
PROJECT="$(cd "$(dirname "$0")/../.." && pwd)"
LOG="${PROJECT}/Tools/SmokeTest/last-run.log"

if [ ! -x "${UNITY}" ]; then
    echo "Could not find Unity ${UNITY_VERSION} at ${UNITY}" >&2
    exit 1
fi

if [ -f "${PROJECT}/Temp/UnityLockfile" ]; then
    echo "The Unity Editor has this project open. Close it and try again." >&2
    exit 1
fi

rm -f "${LOG}"

set +e
"${UNITY}" -batchmode -nographics -projectPath "${PROJECT}" \
    -executeMethod Indoctrination.EditorTools.AlphaSmokeTest.RunBatch \
    -logFile "${LOG}"
STATUS=$?
set -e

# Unity's log carries the whole editor startup; only the check output matters.
sed -n '/Scene wiring:/,/SMOKE TEST/p' "${LOG}" | grep -v '^$' || true

if grep -q "error CS" "${LOG}"; then
    echo ""
    echo "Compile errors:"
    grep "error CS" "${LOG}" | sort -u
    exit 1
fi

if [ ${STATUS} -ne 0 ]; then
    echo ""
    echo "Smoke test failed (exit ${STATUS}). Full log: ${LOG}"
    exit ${STATUS}
fi

exit 0
