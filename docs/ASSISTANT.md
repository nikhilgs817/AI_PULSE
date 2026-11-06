# Optional local assistant (in development)

This page describes the experimental, opt-in assistant in the 0.3.2 source/local preview. It is not included in the published 0.3.0 download. Basic AI-off and selection/endpoint checks are covered; end-to-end model-answer validation and standalone runtime setup packaging are still pending. No model is downloaded automatically.

## Model selection

The assistant catalog is restricted to Google and Microsoft models, following the maintainer's requirement to use established US company brands. Models from other developers are not selectable for the assistant. This policy concerns the model publisher; it does not claim that every contributor, training source or dependency is from the US.

- **Google Gemma 3 1B** (`gemma3:1b`): the default selection, approximately 806 MB with Ollama's Q4_K_M package. Intended for short explanations of measured monitoring data.
- **Google Gemma 3 270M** (`gemma3:270m`): approximately 292 MB with Q8_0. An ultra-light alternative with limited answers. Google positions it for specialized tasks and fine-tuning; it should not be treated as a general diagnostic expert.
- **Microsoft Phi-4 Mini** (`phi4-mini:3.8b`): approximately 2.5 GB with Q4_K_M. A larger alternative when the additional memory and download are acceptable.

Download sizes are estimates from the upstream catalog, checked on October 22, 2026, and can change. Runtime memory also includes context and other overhead. SmolLM2 has been removed from the assistant catalog. A saved, unsupported model identifier cannot be used for chat or download; the page resets its selection to the approved default.

## Optional setup and use

No model weights are bundled, and local AI is off by default. With a compatible local Ollama runtime installed and running, open **Assistant**, enable local AI, select a model and click **Download selected model**. The download happens only when explicitly requested. Without local AI enabled, **Show measured summary** uses AIPULSE's own calculations and makes no model request.

The assistant connects only to local loopback Ollama. Downloading a model causes Ollama to contact its model registry; it does not send your monitoring questions to Google or Microsoft cloud APIs. Existing models used by other applications are not deleted or changed by this assistant preference.

The current request settings use CPU inference, two threads, a 2,048-token context and a maximum of 192 generated tokens, and request model unloading after each answer. These bounds reduce overhead; they do not guarantee a fixed memory footprint or a response time on every machine.

Measured figures are displayed separately from model interpretation. The model has no tools to change settings or execute commands. Chat text remains in dashboard memory and is not saved by AIPULSE; input/output usage counts are stored on the dashboard computer. Ollama has its own configuration and privacy behavior. Select an individual host to ask about its data.

## Upstream information and licenses

- [Google Gemma 3 documentation](https://ai.google.dev/gemma/docs/core)
- [Google's description of Gemma 3 270M](https://developers.googleblog.com/introducing-gemma-3-270m/)
- [Gemma terms](https://ai.google.dev/gemma/terms)
- [Gemma 3 1B package](https://ollama.com/library/gemma3:1b)
- [Gemma 3 270M package](https://ollama.com/library/gemma3:270m)
- [Microsoft Phi family announcement](https://azure.microsoft.com/en-us/blog/empowering-innovation-the-next-generation-of-the-phi-family/)
- [Phi-4 Mini package and MIT license](https://ollama.com/library/phi4-mini:3.8b)

AIPULSE is independent of these publishers and does not claim their endorsement. Model licenses are separate from AIPULSE's license.
