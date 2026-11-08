#!/bin/sh
set -eu
# Only this Claude Code process receives these settings. No shell profile is edited.
exec env CLAUDE_CODE_ENABLE_TELEMETRY=1 OTEL_LOGS_EXPORTER=otlp \
  OTEL_EXPORTER_OTLP_LOGS_PROTOCOL=http/json \
  OTEL_EXPORTER_OTLP_LOGS_ENDPOINT=http://127.0.0.1:4319/v1/logs \
  OTEL_LOG_USER_PROMPTS=false OTEL_LOG_TOOL_DETAILS=false OTEL_LOG_TOOL_CONTENT=false \
  claude "$@"
