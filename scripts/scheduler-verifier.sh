#!/usr/bin/env bash

# Shared deterministic helpers for the production reminder-scheduler verifier.
# The freshness boundary must be owned by the API clock that also stamps durable
# scheduler attempts/results. Comparing those timestamps with a GitHub runner
# clock can falsely classify a fresh cycle as stale when the two hosts are skewed.

scheduler_parse_timestamp_epoch() {
  local value="${1:-}"
  if [ -z "$value" ]; then
    printf '0'
    return 0
  fi

  date -u -d "$value" +%s 2>/dev/null || printf '0'
}

scheduler_choose_freshness_boundary() {
  local runner_requested_at="${1:-}"
  local response_file="${2:-}"
  local server_requested_at=""
  local server_epoch=0
  local runner_epoch=0

  if [ -n "$response_file" ] && [ -s "$response_file" ]; then
    server_requested_at=$(jq -r '.requestedAt // empty' "$response_file" 2>/dev/null || true)
    server_epoch=$(scheduler_parse_timestamp_epoch "$server_requested_at")
  fi

  if [ "$server_epoch" -gt 0 ]; then
    # The current endpoint stamps requestedAt while forming the HTTP 202 response,
    # after TryTrigger() has already made the wake visible to the worker. A fast
    # worker can therefore persist LastAttemptAt a few milliseconds before this
    # receipt. Keep the API clock as authority but widen the response receipt by
    # one second. Baseline advancement is still required before any observation can
    # be certified, so this compatibility margin cannot authorize stale old state.
    printf 'server\t%s\t%s\n' "$server_requested_at" "$((server_epoch - 1))"
    return 0
  fi

  runner_epoch=$(scheduler_parse_timestamp_epoch "$runner_requested_at")
  printf 'runner\t%s\t%s\n' "$runner_requested_at" "$runner_epoch"
}

scheduler_timestamp_is_fresh() {
  local value="${1:-}"
  local boundary_epoch="${2:-0}"
  local value_epoch

  value_epoch=$(scheduler_parse_timestamp_epoch "$value")
  if [ "$value_epoch" -gt 0 ] && [ "$boundary_epoch" -gt 0 ] && [ "$value_epoch" -ge "$boundary_epoch" ]; then
    printf '1'
  else
    printf '0'
  fi
}

scheduler_verified_healthy() {
  local health_http="${1:-000}"
  local health_state="${2:-unknown}"
  local terminal_advanced="${3:-0}"
  local attempt_advanced="${4:-0}"
  local attempt_is_fresh="${5:-0}"
  local success_is_fresh="${6:-0}"

  if [ "$health_http" = "200" ] && [ "$health_state" = "healthy" ] && \
     [ "$terminal_advanced" -eq 1 ] && [ "$attempt_advanced" -eq 1 ] && \
     [ "$attempt_is_fresh" -eq 1 ] && [ "$success_is_fresh" -eq 1 ]; then
    printf '1'
  else
    printf '0'
  fi
}

scheduler_verified_failed() {
  local health_state="${1:-unknown}"
  local failure_code="${2:-}"
  local failure_advanced="${3:-0}"
  local attempt_advanced="${4:-0}"
  local failure_is_fresh="${5:-0}"
  local attempt_is_fresh="${6:-0}"

  if [ "$health_state" != "failed" ] || \
     [ "$attempt_advanced" -ne 1 ] || [ "$attempt_is_fresh" -ne 1 ]; then
    printf '0'
    return 0
  fi

  # Failure authority belongs to the accepted cycle only after that cycle's own
  # durable attempt advanced. A predecessor that was already running at baseline
  # can fail after this queue request and produce a fresh failure timestamp, but
  # that terminal marker must not be misattributed to the queued successor.
  #
  # Once the accepted attempt is grounded, two failure shapes are authoritative:
  # a fresh persisted failure marker or the health evaluator's explicit abandoned
  # lease code when the attempt lost its lease before writing a terminal marker.
  if { [ "$failure_advanced" -eq 1 ] && [ "$failure_is_fresh" -eq 1 ]; } || \
     [ "$failure_code" = "abandoned:leaseexpired" ]; then
    printf '1'
  else
    printf '0'
  fi
}

scheduler_verified_in_progress() {
  local health_http="${1:-000}"
  local health_state="${2:-unknown}"
  local attempt_advanced="${3:-0}"
  local attempt_is_fresh="${4:-0}"
  local observed_at="${5:-}"
  local lease_until="${6:-}"
  local observed_epoch
  local lease_epoch

  observed_epoch=$(scheduler_parse_timestamp_epoch "$observed_at")
  lease_epoch=$(scheduler_parse_timestamp_epoch "$lease_until")

  # A verifier timeout is not a scheduler failure while the API itself still
  # reports the accepted fresh attempt as running under a live durable lease.
  # One scheduler cycle can legitimately outlive the short GitHub polling window
  # while sequential bounded stages keep renewing ownership.
  if [ "$health_http" = "200" ] && [ "$health_state" = "running" ] && \
     [ "$attempt_advanced" -eq 1 ] && [ "$attempt_is_fresh" -eq 1 ] && \
     [ "$observed_epoch" -gt 0 ] && [ "$lease_epoch" -gt "$observed_epoch" ]; then
    printf '1'
  else
    printf '0'
  fi
}

scheduler_verified_predecessor_in_progress() {
  local health_http="${1:-000}"
  local health_state="${2:-unknown}"
  local attempt_advanced="${3:-0}"
  local baseline_attempt="${4:-}"
  local current_attempt="${5:-}"
  local observed_at="${6:-}"
  local lease_until="${7:-}"
  local observed_epoch
  local lease_epoch

  observed_epoch=$(scheduler_parse_timestamp_epoch "$observed_at")
  lease_epoch=$(scheduler_parse_timestamp_epoch "$lease_until")

  # A newly accepted external wake may arrive while a predecessor cycle is still
  # running. The bounded trigger coalesces that wake for the worker to consume when
  # the predecessor exits. Do not call the new wake timed out merely because its own
  # LastAttemptAt cannot advance until the live predecessor releases authority.
  #
  # This is intentionally narrow: the exact baseline attempt must still own a live
  # lease. Once it terminates, the verifier again requires the queued successor to
  # advance its own attempt before any healthy/failed result can be certified.
  if [ "$health_http" = "200" ] && [ "$health_state" = "running" ] && \
     [ "$attempt_advanced" -eq 0 ] && [ -n "$baseline_attempt" ] && \
     [ "$current_attempt" = "$baseline_attempt" ] && \
     [ "$observed_epoch" -gt 0 ] && [ "$lease_epoch" -gt "$observed_epoch" ]; then
    printf '1'
  else
    printf '0'
  fi
}

scheduler_should_requeue_unstarted_wake() {
  local health_http="${1:-000}"
  local health_state="${2:-unknown}"
  local attempt_advanced="${3:-0}"
  local baseline_attempt="${4:-}"
  local current_attempt="${5:-}"
  local observed_at="${6:-}"
  local lease_until="${7:-}"
  local observed_epoch

  observed_epoch=$(scheduler_parse_timestamp_epoch "$observed_at")

  # HTTP 202 only proves that the in-process bounded channel accepted a signal.
  # A deploy/restart can destroy that channel before the scheduler worker consumes
  # it. After a sustained grace period the workflow may replay the same logical wake
  # once, but only when durable state proves that no accepted-cycle attempt started
  # and no baseline predecessor still owns a live lease.
  if { [ "$health_http" != "200" ] && [ "$health_http" != "503" ]; } || \
     [ "$observed_epoch" -le 0 ] || [ "$attempt_advanced" -ne 0 ]; then
    printf '0'
    return 0
  fi

  if [ "$current_attempt" != "$baseline_attempt" ]; then
    printf '0'
    return 0
  fi

  if [ "$(scheduler_verified_predecessor_in_progress \
      "$health_http" "$health_state" "$attempt_advanced" \
      "$baseline_attempt" "$current_attempt" "$observed_at" "$lease_until")" = "1" ]; then
    printf '0'
    return 0
  fi

  printf '1'
}
