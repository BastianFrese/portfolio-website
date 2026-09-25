#!/bin/bash
# CPU-/Verlustmessung für den flow-meter auf einem Knoten.
# Läuft im Vordergrund, tastet /proc/<pid>/stat von Meter und tcpdump ab.
# Kein Eingriff: startet nur den Meter lesend und beendet ihn wieder.
set -u

DIR=/root/flow-meter
DURATION=${1:-900}
INTERVAL=${2:-30}
LOG=/tmp/flow-meter-cpu.log

cd "$DIR" || exit 1
HZ=$(getconf CLK_TCK)

echo "=== vmbr0 vorher"
ip -s link show vmbr0 | tail -4

python3 meter.py --dump >"$LOG" 2>&1 &
MPID=$!
echo "meter pid $MPID"

ticks() {
    local total=0 p
    for p in "$MPID" $(pgrep -P "$MPID" 2>/dev/null) $(pgrep -x tcpdump 2>/dev/null); do
        [ -r "/proc/$p/stat" ] || continue
        local t
        t=$(awk '{ sub(/.*\) /, ""); print $12 + $13 }' "/proc/$p/stat" 2>/dev/null)
        [ -n "$t" ] && total=$((total + t))
    done
    echo "$total"
}

T0=$(ticks)
S0=$(date +%s)
echo "=== messung: ${DURATION}s, abtastung alle ${INTERVAL}s"
for _ in $(seq 1 $((DURATION / INTERVAL))); do
    sleep "$INTERVAL"
    echo "  t=$(( $(date +%s) - S0 ))s  jiffies=$(ticks)"
done
T1=$(ticks)
S1=$(date +%s)

ELAPSED=$((S1 - S0))
DELTA=$((T1 - T0))
PCT=$(awk -v d="$DELTA" -v h="$HZ" -v e="$ELAPSED" 'BEGIN { printf "%.3f", (d / h / e) * 100 }')

kill -TERM "$MPID" 2>/dev/null
wait "$MPID" 2>/dev/null

echo "=== ergebnis"
echo "meter+tcpdump: ${DELTA} jiffies in ${ELAPSED}s  →  ${PCT} % eines kerns"
echo "=== vmbr0 nachher"
ip -s link show vmbr0 | tail -4
echo "=== fenster im log: $(grep -c '^fenster' "$LOG")"
echo "=== warnungen im log:"
grep -c WARNUNG "$LOG" || true
grep WARNUNG "$LOG" | sort | uniq -c | head
echo "=== verluste laut meter (zeilen mit 'verworfen')"
grep -o '[0-9]* vom kernel verworfen' "$LOG" | sort | uniq -c | head
echo "=== letztes fenster"
awk '/^fenster/{buf=""} {buf=buf $0 "\n"} END{print buf}' "$LOG" | head -30
