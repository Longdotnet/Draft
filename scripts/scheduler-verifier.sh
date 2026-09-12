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
    printf 'server\t%s\t%s\n' "$server_requested_at" "$server_epoch"
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
  local failure_advanced="${2:-0}"
  local attempt_advanced="${3:-0}"
  local failure_is_fresh="${4:-0}"
  local attempt_is_fresh="${5:-0}"

  if [ "$health_state" != "failed" ]; then
    printf '0'
    return 0
  fi

  # A failed health state can be backed either by a newly persisted failure marker
  # or by a newly advanced attempt whose lease expired before any terminal marker
  # could be written. Preserve both grounded failure paths.
  if { [ "$failure_advanced" -eq 1 ] && [ "$failure_is_fresh" -eq 1 ]; } || \
     { [ "$attempt_advanced" -eq 1 ] && [ "$attempt_is_fresh" -eq 1 ]; }; then
    printf '1'
  else
    printf '0'
  fi
}
