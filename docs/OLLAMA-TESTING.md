# Ollama integration and verification

## Connection

AI Pulse discovers Ollama at `http://127.0.0.1:11434`. Keep that server as-is. The app's observation gateway listens on loopback at `http://127.0.0.1:11435` and forwards supported native/OpenAI-compatible inference and discovery routes to the configured server.

Set a compatible client's Ollama base URL to port 11435. No cloud account/API key is required for ordinary local Ollama. The gateway does not forward account cookies/authorization credentials, expose a public listener, download models or automatically retry generation POSTs. Cross-origin browser requests are rejected. If AI Pulse is stopped, gateway clients must switch back to direct port 11434.

For example:

```bash
curl http://127.0.0.1:11435/api/generate -H 'Content-Type: application/json' -d '{"model":"qwen3:8b","prompt":"Reply with one short sentence.","stream":false,"think":false,"options":{"num_ctx":2048,"num_predict":48}}'
```

The app can detect models used by direct clients, but it cannot recover their request token usage from `/api/ps`. The model row remains Loaded unless a request through the gateway confirms Generating.

## Controlled live test

The included `tests/ollama_smoke.py` requires Python only on the developer/test machine, not for normal app use. It uses **existing** `qwen3:8b` and `nomic-embed-text:latest` models. It never downloads a model.

```bash
python tests/ollama_smoke.py --database /path/to/data/pulse.db --output build/ollama-test-results.json
```

The procedure:

1. Record the initial loaded-model list and existing request IDs.
2. Send a bounded non-streaming Qwen request with a 2,048-token context and 48-token output cap.
3. Repeat using streaming and compare final counts.
4. Send generation and embedding requests concurrently.
5. Verify both models appear in `/api/ps` and the dashboard.
6. Send a request for a deliberately nonexistent model and check that HTTP 404 is preserved.
7. Compare stored input/output totals and decode rates to actual Ollama responses.

Models are kept alive for five minutes and then expire normally; the script does not forcibly unload pre-existing workloads. Choose other small installed models by editing the test explicitly. Do not use a large model just to prove the UI works.

## Results from the first Windows run

The real test completed in approximately 23 seconds. All seven checks passed. Three Qwen calls returned **75 input tokens and 45 output tokens**, exactly matching SQLite. Decode rate matched `eval_count / (eval_duration / 1e9)`. Qwen and the embedding model were simultaneously resident. The nonexistent model returned HTTP 404.

See [VALIDATION.md](VALIDATION.md) for subsequent regression tests, installation checks and remaining limits. This verifies accounting for observed calls; it does not validate sensor calibration, model quality, every compatible endpoint, or every GPU driver.
