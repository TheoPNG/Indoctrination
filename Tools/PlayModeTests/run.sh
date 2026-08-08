#!/bin/bash
# Runs the PlayMode tests: a real NetworkManager hosting a real game, with RPCs
# going over the wire. This is the only check that exercises the networking
# itself rather than the rules engine underneath it.
#
# Close the Unity Editor before running - it holds a lock on the project.
set -e

UNITY_VERSION="6000.5.7f1"
UNITY="/Applications/Unity/Hub/Editor/${UNITY_VERSION}/Unity.app/Contents/MacOS/Unity"
PROJECT="$(cd "$(dirname "$0")/../.." && pwd)"
LOG="${PROJECT}/Tools/PlayModeTests/last-run.log"
RESULTS="${PROJECT}/Tools/PlayModeTests/results.xml"

if [ ! -x "${UNITY}" ]; then
    echo "Could not find Unity ${UNITY_VERSION} at ${UNITY}" >&2
    exit 1
fi

if [ -f "${PROJECT}/Temp/UnityLockfile" ]; then
    echo "The Unity Editor has this project open. Close it and try again." >&2
    exit 1
fi

rm -f "${LOG}" "${RESULTS}"

set +e
"${UNITY}" -batchmode -nographics -projectPath "${PROJECT}" \
    -runTests -testPlatform PlayMode \
    -testResults "${RESULTS}" \
    -logFile "${LOG}"
STATUS=$?
set -e

if grep -q "error CS" "${LOG}"; then
    echo "Compile errors:"
    grep "error CS" "${LOG}" | sort -u
    exit 1
fi

if [ ! -f "${RESULTS}" ]; then
    echo "No results were produced. Full log: ${LOG}" >&2
    tail -40 "${LOG}" >&2
    exit 1
fi

# One line per test case, from the NUnit XML the runner writes.
python3 - "${RESULTS}" <<'PY'
import sys, xml.etree.ElementTree as ET

root = ET.parse(sys.argv[1]).getroot()
cases = list(root.iter("test-case"))
failed = 0

if not cases:
    print("NO PLAYMODE TESTS WERE EXECUTED")
    sys.exit(1)

for case in cases:
    name = case.get("name")
    result = case.get("result")
    if result == "Passed":
        print(f"  PASS  {name}")
    else:
        failed += 1
        print(f"  FAIL  {name}")
        message = case.find(".//message")
        if message is not None and message.text:
            for line in message.text.strip().splitlines():
                print(f"          {line}")

print("")
print("ALL PLAYMODE TESTS PASSED" if failed == 0 else f"{failed} PLAYMODE TEST(S) FAILED")
sys.exit(1 if failed else 0)
PY
