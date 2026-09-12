#!/usr/bin/env bash
set -euo pipefail

repo_root=$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)
# shellcheck source=../scheduler-verifier.sh
source "$repo_root/scripts/scheduler-verifier.sh"

work_dir=$(mktemp -d)
trap 'rm -rf "$work_dir"' EXIT
response_file="$work_dir/response.json"

iso_from_epoch() {
  date -u -d "@$1" +'%Y-%m-%dT%H:%M:%SZ'
}

legacy_false_negatives=0
seed_count=256
base_epoch=$(date -u -d '2026-09-12T08:00:00Z' +%s)

for seed in $(seq 1 "$seed_count"); do
  # Mutate cross-host clock skew over +/-120 seconds while preserving one real
  # server-owned causal order: accepted tick -> attempt -> terminal result.
  runner_epoch=$((base_epoch + seed * 7))
  skew=$(((seed * 37) % 241 - 120))
  server_epoch=$((runner_epoch + skew))
  attempt_epoch=$((server_epoch + 1 + seed % 4))
  success_epoch=$((attempt_epoch + 1 + seed % 5))

  runner_requested_at=$(iso_from_epoch "$runner_epoch")
  server_requested_at=$(iso_from_epoch "$server_epoch")
  attempt_at=$(iso_from_epoch "$attempt_epoch")
  success_at=$(iso_from_epoch "$success_epoch")

  printf '{"accepted":true,"queued":true,"requestedAt":"%s"}\n' "$server_requested_at" > "$response_file"
  IFS=$'\t' read -r source boundary_at boundary_epoch < <(
    scheduler_choose_freshness_boundary "$runner_requested_at" "$response_file"
  )

  [ "$source" = "server" ]
  [ "$boundary_at" = "$server_requested_at" ]
  [ "$boundary_epoch" -eq "$server_epoch" ]
  [ "$(scheduler_timestamp_is_fresh "$attempt_at" "$boundary_epoch")" = "1" ]
  [ "$(scheduler_timestamp_is_fresh "$success_at" "$boundary_epoch")" = "1" ]

  stale_at=$(iso_from_epoch $((server_epoch - 1)))
  [ "$(scheduler_timestamp_is_fresh "$stale_at" "$boundary_epoch")" = "0" ]

  # This is the minimized failure class in the old workflow: a perfectly fresh
  # server attempt is rejected solely because the runner clock is ahead.
  if [ "$attempt_epoch" -lt "$runner_epoch" ]; then
    legacy_false_negatives=$((legacy_false_negatives + 1))
  fi
done

if [ "$legacy_false_negatives" -eq 0 ]; then
  echo "Expected the skew corpus to reproduce at least one legacy false-negative." >&2
  exit 1
fi

# Malformed/missing server receipt must fail safely to the timestamp captured for
# the successful HTTP attempt, never to a stale timestamp from an earlier retry.
successful_runner_at='2026-09-12T09:10:11Z'
printf '{"accepted":true,"queued":true,"requestedAt":"not-a-time"}\n' > "$response_file"
IFS=$'\t' read -r source boundary_at boundary_epoch < <(
  scheduler_choose_freshness_boundary "$successful_runner_at" "$response_file"
)
[ "$source" = "runner" ]
[ "$boundary_at" = "$successful_runner_at" ]
[ "$boundary_epoch" -eq "$(date -u -d "$successful_runner_at" +%s)" ]

# Empty/malformed health timestamps can never become fresh by accident.
[ "$(scheduler_timestamp_is_fresh '' "$boundary_epoch")" = "0" ]
[ "$(scheduler_timestamp_is_fresh 'garbage' "$boundary_epoch")" = "0" ]
[ "$(scheduler_timestamp_is_fresh "$successful_runner_at" 0)" = "0" ]

echo "scheduler verifier fuzz: $seed_count seeds passed; legacy false-negatives reproduced=$legacy_false_negatives"
