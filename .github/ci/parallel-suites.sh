# Runs independent test processes concurrently and keeps their output readable.
#
# Source this file from a bash step, start each process with `suite <name> <command...>`, then call
# `finish_suites`. Each process writes its own log; finish_suites waits for all of them, prints every
# log in a collapsible group in start order, annotates each failure, and returns non-zero when any
# process failed. Leading NAME=value arguments set environment variables for that process only, and the
# command may be a shell function defined before the call. A function runs without errexit, so it
# must return a failing status itself.
#
#   source .github/ci/parallel-suites.sh
#   suite caching dotnet run --project tests/Respire.Caching.Tests -c Release -f net10.0 --no-build
#   suite signalr TUNIT_DISABLE_HTML_REPORTER=true dotnet run --project tests/Respire.SignalR.Tests ...
#   finish_suites
#
# SUITE_MAX_PARALLEL caps how many processes run at once (default: processor count).

_suite_log_dir="${RUNNER_TEMP:-/tmp}/parallel-suites"
_suite_max_parallel="${SUITE_MAX_PARALLEL:-$(nproc)}"
_suite_names=()
mkdir -p "$_suite_log_dir"

suite() {
  local name=$1
  shift
  while (( $(jobs -rp | wc -l) >= _suite_max_parallel )); do
    wait -n || true
  done
  _suite_names+=("$name")
  rm -f "$_suite_log_dir/$name.log" "$_suite_log_dir/$name.status"
  echo "Starting $name: $*"
  (
    started=$SECONDS
    while [[ $# -gt 0 && "$1" =~ ^[A-Za-z_][A-Za-z0-9_]*= ]]; do
      export "$1"
      shift
    done
    # The if keeps errexit from ending the subshell before it records the status.
    if "$@" > "$_suite_log_dir/$name.log" 2>&1; then status=0; else status=$?; fi
    echo "$status $((SECONDS - started))" > "$_suite_log_dir/$name.status"
  ) &
}

finish_suites() {
  wait || true
  local name status duration failed=()
  {
    echo "| Test process | Result | Seconds |"
    echo "| --- | --- | ---: |"
  } >> "${GITHUB_STEP_SUMMARY:-/dev/null}"
  for name in "${_suite_names[@]}"; do
    status=1
    duration='?'
    if [[ -f "$_suite_log_dir/$name.status" ]]; then
      read -r status duration < "$_suite_log_dir/$name.status"
    fi
    echo "::group::$name (exit $status, ${duration}s)"
    cat "$_suite_log_dir/$name.log" 2>/dev/null || echo "No output was recorded."
    echo "::endgroup::"
    if [[ "$status" == 0 ]]; then
      echo "| $name | passed | $duration |" >> "${GITHUB_STEP_SUMMARY:-/dev/null}"
    else
      failed+=("$name")
      echo "::error title=$name failed::$name exited with code $status; expand its log group above."
      echo "| $name | **failed ($status)** | $duration |" >> "${GITHUB_STEP_SUMMARY:-/dev/null}"
    fi
  done
  _suite_names=()
  if (( ${#failed[@]} > 0 )); then
    echo "Failed: ${failed[*]}"
    return 1
  fi
}
