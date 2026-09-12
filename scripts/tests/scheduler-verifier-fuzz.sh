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
  [ "$boundary_epoch" -eq $((server_epoch - 1)) ]
  attempt_fresh=$(scheduler_timestamp_is_fresh "$attempt_at" "$boundary_epoch")
  success_fresh=$(scheduler_timestamp_is_fresh "$success_at" "$boundary_epoch")
  [ "$attempt_fresh" = "1" ]
  [ "$success_fresh" = "1" ]
  [ "$(scheduler_verified_healthy 200 healthy 1 1 "$attempt_fresh" "$success_fresh")" = "1" ]

  stale_at=$(iso_from_epoch $((server_epoch - 2)))
  [ "$(scheduler_timestamp_is_fresh "$stale_at" "$boundary_epoch")" = "0" ]
  [ "$(scheduler_verified_healthy 200 healthy 1 0 1 1)" = "0" ]
  [ "$(scheduler_verified_healthy 200 healthy 1 1 0 1)" = "0" ]

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

# Preserve both grounded failed-cycle shapes, but only after the accepted cycle's
# own durable attempt advanced. A predecessor failure that lands after the queue
# receipt is not the queued successor's terminal result.
[ "$(scheduler_verified_failed failed 'degraded:reminder' 1 1 1 1)" = "1" ]
[ "$(scheduler_verified_failed failed 'abandoned:leaseexpired' 0 1 0 1)" = "1" ]
[ "$(scheduler_verified_failed failed 'degraded:reminder' 1 0 1 0)" = "0" ]
[ "$(scheduler_verified_failed failed 'degraded:reminder' 1 1 0 1)" = "0" ]
[ "$(scheduler_verified_failed failed '' 0 1 0 1)" = "0" ]
[ "$(scheduler_verified_failed healthy 'degraded:reminder' 1 1 1 1)" = "0" ]

# Production incident #241 is a permanent seed. The verifier exhausted its fixed
# 24-poll window while the API still reported a fresh running attempt with a live
# durable lease extending another ~13 minutes. The historical endpoint also
# stamped requestedAt 8ms after the worker had already persisted LastAttemptAt;
# the one-second server-clock receipt margin preserves that causal wake.
printf '{"accepted":true,"queued":true,"requestedAt":"2026-09-12T05:51:34.4179435Z"}\n' > "$response_file"
IFS=$'\t' read -r source boundary_at production_boundary < <(
  scheduler_choose_freshness_boundary '2026-09-12T05:51:34Z' "$response_file"
)
[ "$source" = "server" ]
production_attempt='2026-09-12T05:51:34.409815Z'
production_attempt_fresh=$(scheduler_timestamp_is_fresh "$production_attempt" "$production_boundary")
[ "$production_attempt_fresh" = "1" ]
[ "$(scheduler_verified_in_progress \
  200 running 1 "$production_attempt_fresh" \
  '2026-09-12T05:53:39.8710278Z' \
  '2026-09-12T06:06:34.409815Z')" = "1" ]

# Stateful running-cycle corpus: mutate verification-window exhaustion, lease
# runway and stale ownership. A fresh running attempt under a lease that remains
# live at the API observation is never a timeout failure.
running_seed_count=256
running_base=$(date -u -d '2026-09-12T10:00:00Z' +%s)
for seed in $(seq 1 "$running_seed_count"); do
  accepted_epoch=$((running_base + seed * 11))
  attempt_epoch=$((accepted_epoch + seed % 3))
  observed_epoch=$((attempt_epoch + 120 + seed % 91))
  lease_runway=$((1 + (seed * 29) % 901))
  lease_epoch=$((observed_epoch + lease_runway))

  attempt_at=$(iso_from_epoch "$attempt_epoch")
  observed_at=$(iso_from_epoch "$observed_epoch")
  lease_until=$(iso_from_epoch "$lease_epoch")
  attempt_fresh=$(scheduler_timestamp_is_fresh "$attempt_at" "$accepted_epoch")
  [ "$attempt_fresh" = "1" ]
  [ "$(scheduler_verified_in_progress 200 running 1 "$attempt_fresh" "$observed_at" "$lease_until")" = "1" ]

  # Expired lease, stale/unadvanced attempt, failed state, malformed clocks and a
  # non-200 health response must never be accepted as authoritative in-progress.
  expired_lease=$(iso_from_epoch "$observed_epoch")
  [ "$(scheduler_verified_in_progress 200 running 1 1 "$observed_at" "$expired_lease")" = "0" ]
  [ "$(scheduler_verified_in_progress 200 running 0 1 "$observed_at" "$lease_until")" = "0" ]
  [ "$(scheduler_verified_in_progress 200 running 1 0 "$observed_at" "$lease_until")" = "0" ]
  [ "$(scheduler_verified_in_progress 503 running 1 1 "$observed_at" "$lease_until")" = "0" ]
  [ "$(scheduler_verified_in_progress 200 failed 1 1 "$observed_at" "$lease_until")" = "0" ]
  [ "$(scheduler_verified_in_progress 200 running 1 1 garbage "$lease_until")" = "0" ]
done

# Cross-run predecessor corpus. A scheduled/manual wake can be accepted while the
# baseline cycle still owns the lease. The successor cannot advance LastAttemptAt
# until that predecessor terminates, so verifier exhaustion must preserve this as
# grounded in-progress work rather than manufacture scheduler_cycle_timeout.
predecessor_seed_count=192
predecessor_base=$(date -u -d '2026-09-12T12:00:00Z' +%s)
legacy_predecessor_failure_misattributions=0
for seed in $(seq 1 "$predecessor_seed_count"); do
  predecessor_attempt_epoch=$((predecessor_base + seed * 17))
  queue_epoch=$((predecessor_attempt_epoch + 5 + seed % 31))
  observed_epoch=$((queue_epoch + 120 + seed % 61))
  lease_epoch=$((observed_epoch + 1 + (seed * 43) % 901))

  predecessor_attempt=$(iso_from_epoch "$predecessor_attempt_epoch")
  observed_at=$(iso_from_epoch "$observed_epoch")
  lease_until=$(iso_from_epoch "$lease_epoch")

  [ "$(scheduler_verified_predecessor_in_progress \
      200 running 0 "$predecessor_attempt" "$predecessor_attempt" \
      "$observed_at" "$lease_until")" = "1" ]

  # The same predecessor can fail after the new queue receipt. Its failure marker
  # is fresh by wall-clock order but it is still not the queued successor's result.
  predecessor_failure_epoch=$((queue_epoch + 1 + seed % 7))
  predecessor_failure=$(iso_from_epoch "$predecessor_failure_epoch")
  predecessor_failure_fresh=$(scheduler_timestamp_is_fresh "$predecessor_failure" "$queue_epoch")
  [ "$predecessor_failure_fresh" = "1" ]
  [ "$(scheduler_verified_failed \
      failed 'degraded:reminder' 1 0 "$predecessor_failure_fresh" 0)" = "0" ]
  legacy_predecessor_failure_misattributions=$((legacy_predecessor_failure_misattributions + 1))

  # Once the queued successor actually starts, authority transfers to its attempt.
  successor_attempt_epoch=$((predecessor_failure_epoch + 1 + seed % 3))
  successor_failure_epoch=$((successor_attempt_epoch + 1 + seed % 5))
  successor_attempt=$(iso_from_epoch "$successor_attempt_epoch")
  successor_failure=$(iso_from_epoch "$successor_failure_epoch")
  successor_attempt_fresh=$(scheduler_timestamp_is_fresh "$successor_attempt" "$queue_epoch")
  successor_failure_fresh=$(scheduler_timestamp_is_fresh "$successor_failure" "$queue_epoch")
  [ "$(scheduler_verified_failed \
      failed 'degraded:reminder' 1 1 "$successor_failure_fresh" "$successor_attempt_fresh")" = "1" ]

  # An abandoned successor is also authoritative only after its own attempt starts.
  [ "$(scheduler_verified_failed \
      failed 'abandoned:leaseexpired' 0 1 0 "$successor_attempt_fresh")" = "1" ]

  # Negative predecessor shapes: different attempt, expired lease, empty baseline,
  # terminal state, or non-200 health cannot hide a real verification timeout.
  changed_attempt=$(iso_from_epoch $((predecessor_attempt_epoch + 1)))
  expired_lease=$(iso_from_epoch "$observed_epoch")
  [ "$(scheduler_verified_predecessor_in_progress 200 running 0 "$predecessor_attempt" "$changed_attempt" "$observed_at" "$lease_until")" = "0" ]
  [ "$(scheduler_verified_predecessor_in_progress 200 running 0 "$predecessor_attempt" "$predecessor_attempt" "$observed_at" "$expired_lease")" = "0" ]
  [ "$(scheduler_verified_predecessor_in_progress 200 running 0 '' "$predecessor_attempt" "$observed_at" "$lease_until")" = "0" ]
  [ "$(scheduler_verified_predecessor_in_progress 200 healthy 0 "$predecessor_attempt" "$predecessor_attempt" "$observed_at" "$lease_until")" = "0" ]
  [ "$(scheduler_verified_predecessor_in_progress 503 running 0 "$predecessor_attempt" "$predecessor_attempt" "$observed_at" "$lease_until")" = "0" ]
done

if [ "$legacy_predecessor_failure_misattributions" -eq 0 ]; then
  echo "Expected predecessor transition corpus to exercise failure attribution." >&2
  exit 1
fi

echo "scheduler verifier fuzz: clock-skew=$seed_count running-window=$running_seed_count predecessor-transition=$predecessor_seed_count seeds passed; legacy clock false-negatives=$legacy_false_negatives predecessor failure-attribution cases=$legacy_predecessor_failure_misattributions"
