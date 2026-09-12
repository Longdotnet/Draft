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

  if [ "$attempt_epoch" -lt "$runner_epoch" ]; then
    legacy_false_negatives=$((legacy_false_negatives + 1))
  fi
done

if [ "$legacy_false_negatives" -eq 0 ]; then
  echo "Expected the skew corpus to reproduce at least one legacy false-negative." >&2
  exit 1
fi

successful_runner_at='2026-09-12T09:10:11Z'
printf '{"accepted":true,"queued":true,"requestedAt":"not-a-time"}\n' > "$response_file"
IFS=$'\t' read -r source boundary_at boundary_epoch < <(
  scheduler_choose_freshness_boundary "$successful_runner_at" "$response_file"
)
[ "$source" = "runner" ]
[ "$boundary_at" = "$successful_runner_at" ]
[ "$boundary_epoch" -eq "$(date -u -d "$successful_runner_at" +%s)" ]

[ "$(scheduler_timestamp_is_fresh '' "$boundary_epoch")" = "0" ]
[ "$(scheduler_timestamp_is_fresh 'garbage' "$boundary_epoch")" = "0" ]
[ "$(scheduler_timestamp_is_fresh "$successful_runner_at" 0)" = "0" ]

[ "$(scheduler_verified_failed failed 'degraded:reminder' 1 1 1 1)" = "1" ]
[ "$(scheduler_verified_failed failed 'abandoned:leaseexpired' 0 1 0 1)" = "1" ]
[ "$(scheduler_verified_failed failed 'degraded:reminder' 1 0 1 0)" = "0" ]
[ "$(scheduler_verified_failed failed 'degraded:reminder' 1 1 0 1)" = "0" ]
[ "$(scheduler_verified_failed failed '' 0 1 0 1)" = "0" ]
[ "$(scheduler_verified_failed healthy 'degraded:reminder' 1 1 1 1)" = "0" ]

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

  expired_lease=$(iso_from_epoch "$observed_epoch")
  [ "$(scheduler_verified_in_progress 200 running 1 1 "$observed_at" "$expired_lease")" = "0" ]
  [ "$(scheduler_verified_in_progress 200 running 0 1 "$observed_at" "$lease_until")" = "0" ]
  [ "$(scheduler_verified_in_progress 200 running 1 0 "$observed_at" "$lease_until")" = "0" ]
  [ "$(scheduler_verified_in_progress 503 running 1 1 "$observed_at" "$lease_until")" = "0" ]
  [ "$(scheduler_verified_in_progress 200 failed 1 1 "$observed_at" "$lease_until")" = "0" ]
  [ "$(scheduler_verified_in_progress 200 running 1 1 garbage "$lease_until")" = "0" ]
done

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

  predecessor_failure_epoch=$((queue_epoch + 1 + seed % 7))
  predecessor_failure=$(iso_from_epoch "$predecessor_failure_epoch")
  predecessor_failure_fresh=$(scheduler_timestamp_is_fresh "$predecessor_failure" "$queue_epoch")
  [ "$predecessor_failure_fresh" = "1" ]
  [ "$(scheduler_verified_failed \
      failed 'degraded:reminder' 1 0 "$predecessor_failure_fresh" 0)" = "0" ]
  legacy_predecessor_failure_misattributions=$((legacy_predecessor_failure_misattributions + 1))

  successor_attempt_epoch=$((predecessor_failure_epoch + 1 + seed % 3))
  successor_failure_epoch=$((successor_attempt_epoch + 1 + seed % 5))
  successor_attempt=$(iso_from_epoch "$successor_attempt_epoch")
  successor_failure=$(iso_from_epoch "$successor_failure_epoch")
  successor_attempt_fresh=$(scheduler_timestamp_is_fresh "$successor_attempt" "$queue_epoch")
  successor_failure_fresh=$(scheduler_timestamp_is_fresh "$successor_failure" "$queue_epoch")
  [ "$(scheduler_verified_failed \
      failed 'degraded:reminder' 1 1 "$successor_failure_fresh" "$successor_attempt_fresh")" = "1" ]

  [ "$(scheduler_verified_failed \
      failed 'abandoned:leaseexpired' 0 1 0 "$successor_attempt_fresh")" = "1" ]

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

# Restart/deploy lost-wake corpus. HTTP 202 currently means an in-process bounded
# channel accepted a signal; a process death before consumption can leave durable
# scheduler state completely unchanged. After a sustained verifier grace period,
# that exact unchanged shape is eligible for one bounded replay. Any evidence that
# the accepted cycle started, or that the baseline predecessor is still live,
# fences the recovery replay to avoid manufacturing a duplicate cycle.
lost_wake_seed_count=192
lost_wake_base=$(date -u -d '2026-09-12T14:00:00Z' +%s)
for seed in $(seq 1 "$lost_wake_seed_count"); do
  baseline_attempt_epoch=$((lost_wake_base + seed * 19))
  queue_epoch=$((baseline_attempt_epoch + 10 + seed % 41))
  observed_epoch=$((queue_epoch + 55 + seed % 31))
  baseline_attempt=$(iso_from_epoch "$baseline_attempt_epoch")
  observed_at=$(iso_from_epoch "$observed_epoch")

  # Old healthy/failed terminal state with the exact same attempt is the minimized
  # restart-lost signal: the endpoint accepted the wake, but no durable attempt ever
  # advanced. Both 200 and 503 health payloads are reachable evidence, not transport loss.
  if [ $((seed % 2)) -eq 0 ]; then
    health_http=200
    health_state=healthy
  else
    health_http=503
    health_state=failed
  fi
  [ "$(scheduler_should_requeue_unstarted_wake \
      "$health_http" "$health_state" 0 "$baseline_attempt" "$baseline_attempt" \
      "$observed_at" '')" = "1" ]

  # Once a successor starts, even one millisecond later, replay is forbidden.
  successor_attempt=$(iso_from_epoch $((queue_epoch + 1 + seed % 3)))
  [ "$(scheduler_should_requeue_unstarted_wake \
      200 running 1 "$baseline_attempt" "$successor_attempt" "$observed_at" \
      "$(iso_from_epoch $((observed_epoch + 120)))")" = "0" ]

  # A live baseline predecessor also owns the delay; its queued successor must not
  # be multiplied by recovery replay merely because LastAttemptAt has not advanced.
  live_lease=$(iso_from_epoch $((observed_epoch + 1 + seed % 300)))
  [ "$(scheduler_should_requeue_unstarted_wake \
      200 running 0 "$baseline_attempt" "$baseline_attempt" "$observed_at" "$live_lease")" = "0" ]

  # Unreachable/malformed health evidence and a changed attempt fail closed.
  [ "$(scheduler_should_requeue_unstarted_wake \
      000 unknown 0 "$baseline_attempt" "$baseline_attempt" "$observed_at" '')" = "0" ]
  [ "$(scheduler_should_requeue_unstarted_wake \
      200 healthy 0 "$baseline_attempt" "$baseline_attempt" garbage '')" = "0" ]
  [ "$(scheduler_should_requeue_unstarted_wake \
      200 healthy 0 "$baseline_attempt" "$successor_attempt" "$observed_at" '')" = "0" ]
done

# Recovery replay amplification corpus. The previous workflow gated retries on
# whether a recovery POST reached HTTP 202. If that POST failed, the next poll
# could try again, turning one lost-wake rescue into repeated POST amplification.
# The authoritative bound is now "attempted once", not "accepted once".
recovery_replay_seed_count=256
for seed in $(seq 1 "$recovery_replay_seed_count"); do
  recovery_requeue_attempted=0
  recovery_requeue_accepted=0
  recovery_candidate_count=0
  recovery_post_count=0
  candidate_start=$((8 + seed % 3))

  for verification_attempt in $(seq 1 24); do
    if [ "$verification_attempt" -ge "$candidate_start" ]; then
      recovery_candidate=1
      recovery_candidate_count=$((recovery_candidate_count + 1))
    else
      recovery_candidate=0
      recovery_candidate_count=0
    fi

    if [ "$(scheduler_should_attempt_recovery_requeue \
        "$recovery_candidate" "$verification_attempt" "$recovery_candidate_count" \
        "$recovery_requeue_attempted")" = "1" ]; then
      recovery_requeue_attempted=1
      recovery_post_count=$((recovery_post_count + 1))

      # Mutate the recovery transport result. Even when the only recovery POST is
      # rejected or lost, later identical observations must not POST again.
      if [ $((seed % 4)) -eq 0 ]; then
        recovery_requeue_accepted=1
      fi
    fi
  done

  [ "$recovery_post_count" -eq 1 ]
  [ "$recovery_requeue_attempted" -eq 1 ]

  # A second restart/lost-channel observation in the same verifier run cannot
  # resurrect another recovery POST, whether the first POST was accepted or not.
  [ "$(scheduler_should_attempt_recovery_requeue 1 24 12 "$recovery_requeue_attempted")" = "0" ]

  if [ $((seed % 4)) -eq 0 ]; then
    [ "$recovery_requeue_accepted" -eq 1 ]
  else
    [ "$recovery_requeue_accepted" -eq 0 ]
  fi
done

# Negative boundary cases: too early, insufficient grounded observations, or an
# already-attempted recovery are never allowed to issue a recovery POST.
[ "$(scheduler_should_attempt_recovery_requeue 1 11 9 0)" = "0" ]
[ "$(scheduler_should_attempt_recovery_requeue 1 12 2 0)" = "0" ]
[ "$(scheduler_should_attempt_recovery_requeue 0 24 12 0)" = "0" ]
[ "$(scheduler_should_attempt_recovery_requeue 1 24 12 1)" = "0" ]

echo "scheduler verifier fuzz: clock-skew=$seed_count running-window=$running_seed_count predecessor-transition=$predecessor_seed_count lost-wake=$lost_wake_seed_count recovery-replay=$recovery_replay_seed_count seeds passed; legacy clock false-negatives=$legacy_false_negatives predecessor failure-attribution cases=$legacy_predecessor_failure_misattributions"
