# Optional local Ollama verification

Ollama is not installed or started automatically. The automated suite uses fake HTTP and passes without Ollama or a model.

Transient model-discovery failures retain a cooldown across calls on the same provider instance: exponential delays start at one second (plus up to 50% jitter) and cap at 30 seconds. Waiting is cancellable and serialized; successful discovery clears the cooldown. This also applies to connection checks using that instance. It does not pull or start models, cache failed translations, or delay successful steady-state discovery. Service recovery may therefore take up to the current cooldown before another probe. This discovery safeguard is not evidence that the full release outage/circuit-breaker matrix has passed.

Discovery uses the same HTTP failure classification as translation: 401/403 are non-retryable authentication failures, invalid requests/endpoints (including 400/404) are non-retryable validation failures, and 408/429/500/502/503/504 are transient. Unexpected statuses, including redirects, are not automatically retried. Response bodies are not exposed in error messages, and failed discovery never sends a generation request.

For an optional manual check on a machine where the user has already installed and started Ollama:

1. Configure a provider with `kind: "ollama"`, a chosen installed `model`, and optionally `baseUri: "http://127.0.0.1:11434/"` (omitting `baseUri` uses this loopback default).
2. Open the Provider page and select **Test connection (no game text)**. Confirm that it reports the model ready; an absent model must report that it is missing. The probe calls only `GET /api/tags`.
3. With a user-controlled text-only session and an installed model, verify that short and multiline subtitle translations appear as plain text. A slow first model load must leave the UI responsive; cancelling the session must cancel the request.
4. Stop Ollama and repeat the connection test. It should report the service unavailable without showing a raw exception.

Do not use the app to pull a model. Any Ollama model installation is a separate, explicit user action with the user reviewing size and license first. The adapter never uses `/api/pull`.
