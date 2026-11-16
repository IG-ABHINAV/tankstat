# OpenAI-compatible answers (examples)

Example bodies as OpenAI-compatible servers write them, for the app's adapter tests (`OpenAiCompatibleRecognitionProviderTests`). There is
no producer of this contract in the repository: these are written by hand after the servers' documentation, so a server that drifts from
them shows up in the adapter's tests, not in the app.

- `chat.*.json`: `POST /v1/chat/completions` answers in the shapes seen from OpenAI (`fuel-receipt`, `unknown`, `refusal`, `cut-off`),
  LM Studio (`odometer`), Ollama (`expense-receipt`, and `tyre-receipt`: a tyre service's receipt naming the tyre work, #290), llama.cpp (`fenced`: a `<think>` block and code fences around the JSON, a number
  where a string was asked for) and a server that answers in text parts (`text-parts`).
- `error.*.json`: the error bodies the same servers write. They differ: OpenAI a string `code`, llama.cpp a number, LM Studio a bare string,
  and some (`array`: Google's OpenAI-compatible endpoint) an array around the whole thing.
- `models.*.json`: what `GET /v1/models` lists (OpenAI many, Ollama with `:latest` tags, llama.cpp the one file it was started with, LM Studio
  the loaded ones).

No pictures live here, and none of the values are real.
