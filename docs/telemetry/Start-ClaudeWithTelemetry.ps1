param([int]$Port=4319,[Parameter(ValueFromRemainingArguments=$true)][string[]]$ClaudeArguments)
$ErrorActionPreference='Stop'
# Session-scoped only: do not overwrite a user's permanent organization exporter settings.
$names=@('CLAUDE_CODE_ENABLE_TELEMETRY','OTEL_LOGS_EXPORTER','OTEL_EXPORTER_OTLP_LOGS_PROTOCOL','OTEL_EXPORTER_OTLP_LOGS_ENDPOINT','OTEL_LOG_USER_PROMPTS','OTEL_LOG_TOOL_DETAILS','OTEL_LOG_TOOL_CONTENT')
$old=@{};foreach($n in $names){$old[$n]=[Environment]::GetEnvironmentVariable($n,'Process')}
if($Port -lt 1024 -or $Port -gt 65535){throw 'Invalid receiver port'}
try{
  $env:CLAUDE_CODE_ENABLE_TELEMETRY='1';$env:OTEL_LOGS_EXPORTER='otlp'
  $env:OTEL_EXPORTER_OTLP_LOGS_PROTOCOL='http/json'
  $env:OTEL_EXPORTER_OTLP_LOGS_ENDPOINT="http://127.0.0.1:$Port/v1/logs"
  $env:OTEL_LOG_USER_PROMPTS='false';$env:OTEL_LOG_TOOL_DETAILS='false';$env:OTEL_LOG_TOOL_CONTENT='false'
  & claude @ClaudeArguments
  $result=$LASTEXITCODE
}finally{foreach($n in $names){[Environment]::SetEnvironmentVariable($n,$old[$n],'Process')}}
exit $result
