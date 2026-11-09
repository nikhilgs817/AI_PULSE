# 0.3.5 active-agent update

- Shows a recently written Codex or Claude Code session immediately, even while a large token-history file is still replaying after startup.
- Keeps the five-minute active/recent window and metadata-only privacy boundary.
- Does not treat loaded Ollama models as agents; resident models remain in the model list.
- Keeps token accounting independent from the agent heartbeat, preventing activity detection from inventing token usage.
