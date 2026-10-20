# Future k3s integration

## Available now

Settings accepts named remote Ollama endpoints. If an Ollama service runs inside your future cluster and is reachable over a private network, add a line such as:

```text
ai-node-01 | http://private-host:11434
```

The dashboard can then discover its installed and resident models alongside local models. The host label distinguishes repeated names. The fixed hardware strip still describes this desktop, not the remote node.

This feature does **not** install k3s, deploy Kubernetes collectors, query pods, securely store cluster credentials, or collect remote token usage. Do not expose an unauthenticated Ollama endpoint to the public internet. Use an existing trusted private connection; credential-managed endpoint support is a future enhancement.

## Planned cluster monitoring

- Node metrics from node-exporter, container utilization from kubelet/cAdvisor and lifecycle/restarts/readiness from kube-state-metrics.
- A vendor-compatible GPU exporter on each node; temperature and memory are independent of Kubernetes pod resource metrics.
- Model-serving metrics from the actual runtime, including queue/KV-cache metrics only if that runtime exports them.
- Cluster, namespace, node, pod, container and model-instance identity in the ingestion schema.
- A secured Prometheus/collector endpoint with read-only access and bounded labels.
- Cluster-side history so collection continues while the desktop is asleep/off; reconnect queries/backfill with clock-skew handling.

Metrics Server alone does not provide three-month history, model tokens or GPU temperatures. Live deployment and acceptance need the actual cluster topology, runtime, GPU vendor and access method. The present release's remote Ollama provider is the first integration point; full k3s observability is not advertised as complete.

## Central hub integration path

The proposed [Host Agent architecture](FLEET-ARCHITECTURE.md) provides durable outbound telemetry and host-scoped identity before adding node/pod/runtime adapters. Selectable LAN/VPN/Internet profiles belong to the transport setup; cluster authorization and hardware sensor permissions remain separate.
