#!/usr/bin/env bash
# Proves: a Workflow Core instance that a step error terminated can be rewound
# from a second host process. Only the failed step runs again. Completed steps
# do not repeat their side effects.
#   bash demo.sh           all checks must PASS, exit 0
#   bash demo.sh --break   rewind re-arms every pointer, not only failed ones, a check must FAIL, exit 1
set -euo pipefail
cd "$(dirname "$0")"
[[ -d "$HOME/.dotnet" ]] && export PATH="$HOME/.dotnet:$PATH"
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
command -v jq >/dev/null || { echo "jq is required"; exit 2; }

OUT="$PWD/.demo"
SRC=src
rm -rf "$OUT" && mkdir -p "$OUT"

sabotage() {  # sabotage <file> <exact text to remove>
  grep -qF -- "$2" "$1" || { echo "sabotage target not found in $1"; exit 2; }
  F="$1" T="$2" python3 -c 'import os; p=os.environ["F"]; s=open(p).read(); open(p,"w").write(s.replace(os.environ["T"], "", 1))'
}

case "${1:-}" in
  "") ;;
  --break)
    cp -r src "$OUT/src" && rm -rf "$OUT"/src/*/bin "$OUT"/src/*/obj && SRC="$OUT/src"
    sabotage "$SRC/WfRewind/WorkflowRewinder.cs" ".Where(p => p.Status == PointerStatus.Failed)"
    echo "BREAK MODE: rewind re-arms every execution pointer, not only the failed one. Expect a FAIL." ;;
  *) echo "usage: bash demo.sh [--break]"; exit 2 ;;
esac

FAILED=0
pass() { echo "CHECK PASS  $1"; }
fail() { echo "CHECK FAIL  $1"; FAILED=1; }

if dotnet build "$SRC/WfRewind.Demo" -c Release -o "$OUT/bin" -v quiet -nologo > "$OUT/build.log" 2>&1; then
  pass "1 build: WfRewind + demo compile against WorkflowCore 3.21.0 and WorkflowCore.Persistence.Sqlite 3.21.0"
else
  cat "$OUT/build.log"; fail "1 build"; echo "RESULT: FAIL"; exit 1
fi

export WF_DB="$OUT/wf.db" IDS_FILE="$OUT/ids.json" EFFECTS_FILE="$OUT/effects.jsonl"
PARTNER_DOWN=order-1 dotnet "$OUT/bin/WfRewind.Demo.dll" fail > "$OUT/fail.jsonl" 2> "$OUT/fail.log"
cp "$EFFECTS_FILE" "$OUT/effects-after-fail.jsonl"
dotnet "$OUT/bin/WfRewind.Demo.dll" rewind > "$OUT/rewind.jsonl" 2> "$OUT/rewind.log"

check() {  # check <label> <file> <scenario> <jq filter on that record that must print true>
  if [[ "$(jq -s "[.[] | select(.scenario == \"$3\")] | length == 1 and (.[0] | $4)" "$2")" == "true" ]]; then pass "$1"; else fail "$1"; fi
}
effects() {  # effects <file> <orderId> -> {"Reserve:ok":1,...}
  jq -sc --arg o "$2" '[.[] | select(.orderId == $o) | "\(.step):\(.result)"] | group_by(.) | map({key: .[0], value: length}) | from_entries' "$1"
}
expect_effects() {  # expect_effects <label> <file> <orderId> <expected json>
  local got; got="$(effects "$2" "$3")"
  if [[ "$(jq -n --argjson a "$got" --argjson b "$4" '$a == $b')" == "true" ]]; then pass "$1"; else fail "$1 (got $got)"; fi
}

check "2 process A: order-1 terminated by CallPartner error, plan shows exactly [CallPartner]" "$OUT/fail.jsonl" order-1 \
  '.status == "Terminated" and .failedSteps == ["CallPartner"]'
check "3 control: stock ResumeWorkflow on the terminated instance returns false, status stays Terminated" "$OUT/fail.jsonl" order-1 \
  '.resumeAccepted == false and .statusAfterResume == "Terminated"'
expect_effects "4 before rewind: order-1 side effects are Reserve once, one CallPartner error, no Ship" \
  "$OUT/effects-after-fail.jsonl" order-1 '{"Reserve:ok":1,"CallPartner:error":1}'
check "5 process B (fresh host, same SQLite db): Plan=Ready [CallPartner], Rewind=Rewound, instance reaches Complete" "$OUT/rewind.jsonl" order-1 \
  '.planVerdict == "Ready" and .planSteps == ["CallPartner"] and .verdict == "Rewound" and .rewound == ["CallPartner"] and .status == "Complete"'
expect_effects "6 no replay: order-1 side effects are Reserve once, CallPartner error then ok, Ship once" \
  "$EFFECTS_FILE" order-1 '{"Reserve:ok":1,"CallPartner:error":1,"CallPartner:ok":1,"Ship:ok":1}'
check "7 refuse: rewinding a Complete instance returns NotTerminated and it stays Complete" "$OUT/rewind.jsonl" order-2 \
  '.verdict == "NotTerminated" and .status == "Complete"'
check "8 refuse: a manually terminated instance has no failed step, returns NoFailedStep, stays Terminated" "$OUT/rewind.jsonl" hold-3 \
  '.verdict == "NoFailedStep" and .status == "Terminated"'
check "9 idempotent: a second rewind of order-1 after it completed returns NotTerminated" "$OUT/rewind.jsonl" order-1-again \
  '.verdict == "NotTerminated" and .status == "Complete"'
if diff <(jq -c 'select(.orderId != "order-1")' "$OUT/effects-after-fail.jsonl") <(jq -c 'select(.orderId != "order-1")' "$EFFECTS_FILE") > /dev/null; then
  pass "10 untouched: order-2 and hold-3 side effects did not change during process B"
else
  fail "10 untouched: order-2 and hold-3 side effects changed during process B"
fi

echo "--- results ---"
cat "$OUT/fail.jsonl" "$OUT/rewind.jsonl"
echo "--- side effects ledger (effects.jsonl) ---"
for o in order-1 order-2 hold-3; do echo "$o $(effects "$EFFECTS_FILE" "$o")"; done

if [[ $FAILED -ne 0 ]]; then echo "RESULT: FAIL (raw output in $OUT/)"; exit 1; fi
echo "RESULT: PASS"
