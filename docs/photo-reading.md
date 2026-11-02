# Reading photos with an OpenAI-compatible model

For operators who want the photos of receipts and dashboards read by a vision model, and for anyone debugging what a model is told and what it answers.

Photo reading is optional and off by default. With it on, the app sends each photo picked in the add dialogs to a vision model behind an **OpenAI-compatible chat completions API**, which reads the odometer from a dashboard (seven-segment displays included) or the total, litres, price per litre, currency and date from a receipt (Hungarian, English and German receipts). The server can be one on your own network, which keeps the photos at home: [LM Studio](https://lmstudio.ai) (`http://host:1234/v1`), [Ollama](https://ollama.com) (`http://host:11434/v1`), llama.cpp's `llama-server` (`http://host:8080/v1`) or vLLM (`http://host:8000/v1`), with a model loaded that takes images (Qwen2.5-VL, Gemma 3, Llama 3.2 Vision and the like). It can also be a paid API such as OpenAI's (`https://api.openai.com/v1`, with a key), **which then receives every photo your users pick**: receipts show shops, times and the end of a card number, dashboards show the car. Choose that knowingly and tell your users. Only the app's own server talks to the model server; the browser never does. What the person at the screen sees is described in the [user guide](user-guide.md#reading-values-from-photos-optional).

## Settings

```yaml
services:
  tankstat:
    image: ghcr.io/<owner>/<repo>:1.2.3
    environment:
      Recognition__Provider: OpenAiCompatible
      Recognition__OpenAiCompatible__BaseUrl: http://ollama:11434/v1
      Recognition__OpenAiCompatible__Model: qwen2.5vl:7b
      Recognition__MaxConcurrent: "1" # one GPU reads one photo at a time
```

| Setting | Environment variable | Meaning |
| --- | --- | --- |
| `Recognition:Provider` | `Recognition__Provider` | `None` (default: photos are not read) or `OpenAiCompatible` |
| `Recognition:OpenAiCompatible:BaseUrl` | `Recognition__OpenAiCompatible__BaseUrl` | the API's address including its version, e.g. `http://localhost:1234/v1` (the app adds `/chat/completions` and `/models`) |
| `Recognition:OpenAiCompatible:Model` | `Recognition__OpenAiCompatible__Model` | **required**: the model's name as `GET /v1/models` lists it (Ollama's `name:tag`, LM Studio's model id, a paid API's model name; a llama.cpp server with one model answers with it whatever is asked) |
| `Recognition:OpenAiCompatible:ApiKey` | `Recognition__OpenAiCompatible__ApiKey` | sent as `Authorization: Bearer` when set; servers on your own network usually need none |
| `Recognition:OpenAiCompatible:SystemPrompt` | `Recognition__OpenAiCompatible__SystemPrompt` | your own instructions to the model instead of the built-in ones (below) |
| `Recognition:OpenAiCompatible:SystemPromptFile` | `Recognition__OpenAiCompatible__SystemPromptFile` | the same from a file (e.g. `/data/prompt.txt`, at most 16 KB; read at startup, so restart after changing it); wins over `SystemPrompt` |
| `Recognition:OpenAiCompatible:ResponseFormat` | `Recognition__OpenAiCompatible__ResponseFormat` | how the shape of the answer is enforced: `JsonSchema` (default: OpenAI, LM Studio, Ollama, llama.cpp, vLLM), `JsonObject` (older servers; LM Studio rejects it) or `None` (the JSON is picked out of the text) |
| `Recognition:OpenAiCompatible:Temperature` | `Recognition__OpenAiCompatible__Temperature` | sent only when set (0 to 2): `0` makes a local model read the same digits the same way every time; some paid models refuse it |
| `Recognition:OpenAiCompatible:TimeoutSeconds` | `Recognition__OpenAiCompatible__TimeoutSeconds` | how long one photo may take before the attempt counts as failed and is tried again later (default `120`: a model on a CPU, or one Ollama has to load first, takes a while) |
| `Recognition:OpenAiCompatible:LogTraffic` | `Recognition__OpenAiCompatible__LogTraffic` | writes every request to the model server and every answer to the log, to see what the model is sent and says (default `false`; [details](#seeing-what-is-sent-to-the-model)) |
| `Recognition:MinConfidence` | `Recognition__MinConfidence` | values the model is less sure of are not filled in (0 to 1, default `0.6`) |
| `Recognition:MaxConcurrent` | `Recognition__MaxConcurrent` | photos read at the same time (default `2`; `1` for a single local GPU) |

Settings that cannot be used turn photo reading off with a warning in the log; they never stop the app. Photos are queued as they are uploaded and read by a background worker; a busy or unreachable model server is tried again later (five attempts), so nothing is lost while it restarts. What was read is kept with the photo (the values only, never another copy of the picture) and goes away with it.

## How an answer is checked

A model rates its own answers, but those ratings are not to be trusted on their own, so the app checks what it can before believing a value: an odometer may not be below the vehicle's latest reading nor more than 100 000 above it, litres × price per litre must fit the total, a receipt's date must be recent and not in the future, and amounts must be within reason. A value that fails is dropped rather than filled in (a blank beats a wrong value); one that is merely unlikely (an odometer 50 000 to 100 000 above the latest reading, litres and price that do not fit the total) stays below the default fill-in threshold (0.5 against 0.6); and the model's own rating only ever lowers the result (one given in per cent is read as such). A refusal fails the photo; an answer that is not the JSON asked for, a cut-off one, or a server that is busy or unreachable is tried again (five attempts, the reason in the log).

## What the model is told

Two texts go with each photo. The *system prompt* says how to read a dashboard or a receipt; replace it with `SystemPrompt` or `SystemPromptFile` when your photos need other guidance (a language the built-in one does not mention, a cluster it keeps misreading). The *contract* goes with the photo whatever the prompt and cannot be changed: what this photo may show (an odometer, or the receipt of the entry being added), the exact JSON shape and value formats, and how numbers and dates are written in the language the user works in (the dialog's language: Hungarian, German or English). So a custom prompt changes how the model reads, never what the app gets back. The vehicle's latest odometer reading, its usual currency and today's date are deliberately kept from the model (given a number, a model tends to answer with it); the app uses them to check the answer instead. The built-in system prompt is:

> You read photos for a vehicle fuel log and answer in JSON only. A photo shows one of two things: a car's instrument cluster, or a receipt.
>
> Dashboards. Read the odometer: the total distance the car has driven, a whole number of 4 to 7 digits, usually labelled km or mi (or ODO) and shown on the small display between the gauges or at the bottom of the cluster. It is not the trip meter (a smaller number with one decimal, labelled trip, A or B), not a service countdown ("Oil change and inspection in 10100 km", "4800 km múlva"), not a distance or consumption since start, not the clock, the date, the outside temperature, the speed or the fuel range. If the display shows no total distance, say the photo shows no odometer. Many displays use seven-segment digits: read each digit on its own and watch the pairs that look alike (0 and 8, 6 and 8, 1 and 7, 5 and 6, 3 and 9). Give the number exactly as printed, without spaces or units.
>
> Fuel receipts. The total is the amount paid for the fuel itself: on a receipt with nothing but fuel, the final amount after discounts (labelled TOTAL, Összesen, Fizetendő, Summe or Gesamt); on one that also lists other items (a car wash, screen wash, snacks), the fuel line's own amount after the discount printed for it, never the receipt's grand total. Then read the volume of fuel (litres or gallons, usually with 2 or 3 decimals next to the fuel's name: Diesel, 95, E10, benzin, gázolaj, Super), the price per litre as printed, the currency and the date of purchase. Volume times unit price equals the total unless a discount is printed for the fuel; if they differ and none is printed, re-read them before answering.
>
> Other receipts. Read the total paid, the currency, the date and the shop's name as the title (the name printed at the top, not its address or tax number).
>
> Rules. Report only what you can actually read on the photo. Leave a value out rather than guess, and never invent a value that is not printed. Rate each value with a confidence between 0 and 1: 1 when it is clearly legible and unambiguous, about 0.8 when it is readable but small or partly blurred, under 0.6 when you had to guess. Read numbers and dates in the conventions of the receipt's language (a comma may be the decimal separator) and convert them to the output format you are asked for.

## What the model is sent

One `POST {BaseUrl}/chat/completions` per photo. The body has this shape (`temperature` only when `Temperature` is set; no token limit is sent, the timeout bounds a runaway answer):

```json
{
  "model": "<Recognition:OpenAiCompatible:Model>",
  "stream": false,
  "messages": [
    { "role": "system", "content": "<the system prompt: the built-in one above, or yours>" },
    { "role": "user", "content": [
      { "type": "image_url", "image_url": { "url": "data:image/jpeg;base64,<the photo>" } },
      { "type": "text", "text": "<the contract, below>" }
    ] }
  ],
  "temperature": 0,
  "response_format": { "type": "json_schema", "json_schema": { "name": "photo_reading", "strict": true, "schema": { "...": "the schema, below" } } }
}
```

The photo comes first, then the contract, in one user message. The photo is the browser's resized copy (at most 1600 px on the longer side), as JPEG while reading is on and as WebP while it is off. `ResponseFormat=JsonObject` sends `"response_format": { "type": "json_object" }` instead and `None` sends none, so the contract alone says what the answer looks like. The contract depends on the dialog the photo was picked in (the refuelling dialog expects an odometer or a fuel receipt, the expense and recurring-expense dialogs an odometer or a receipt) and on the language the user works in (it ends with how that language writes numbers and dates; another language gets no such sentence). The contract for a photo picked in the **refuelling** dialog, in Hungarian, is:

> This photo was taken for a refuelling entry, so it shows an odometer ("odometer"), a fuel receipt ("fuel-receipt") or neither ("unknown"). Answer with JSON only, in exactly this shape: {"kind": "...", "fields": [{"name": "...", "value": "...", "confidence": 0.0}]}. The fields of each kind: odometer: odometer. fuel-receipt: total, volume, unitPrice, currency, date. Leave out a field you cannot read; with "unknown" there are no fields. Formats: odometer as digits only, without unit or separators; total, volume and unitPrice with "." as the decimal separator, no thousands separators, no currency sign; currency as an ISO 4217 code (HUF, EUR, USD); date as yyyy-MM-dd; title as the shop's name, at most 120 characters. Every value is a string. The receipt is probably Hungarian: numbers use a comma as the decimal separator and a space or a dot between thousands (1 234,5 or 1.234,5), and dates are written year first (2026.10.05.). Convert them to the formats above.

The contract for a photo picked in the **expense** dialogs, in English, is:

> This photo was taken for an expense entry, so it shows an odometer ("odometer"), a receipt ("expense-receipt") or neither ("unknown"). Answer with JSON only, in exactly this shape: {"kind": "...", "fields": [{"name": "...", "value": "...", "confidence": 0.0}]}. The fields of each kind: odometer: odometer. expense-receipt: total, currency, date, title. Leave out a field you cannot read; with "unknown" there are no fields. Formats: odometer as digits only, without unit or separators; total, volume and unitPrice with "." as the decimal separator, no thousands separators, no currency sign; currency as an ISO 4217 code (HUF, EUR, USD); date as yyyy-MM-dd; title as the shop's name, at most 120 characters. Every value is a string. The receipt is probably in English: numbers use a dot as the decimal separator and a comma between thousands (1,234.5); a date may be day/month/year or month/day/year, decide from the other clues on the receipt. Convert them to the formats above.

The schema of `ResponseFormat=JsonSchema`, for the refuelling dialog (the expense dialogs allow `expense-receipt` instead of `fuel-receipt` as the kind and `odometer`, `total`, `currency`, `date`, `title` as the names):

```json
{
  "type": "object",
  "additionalProperties": false,
  "required": ["kind", "fields"],
  "properties": {
    "kind": { "type": "string", "enum": ["odometer", "fuel-receipt", "unknown"] },
    "fields": {
      "type": "array",
      "items": {
        "type": "object",
        "additionalProperties": false,
        "required": ["name", "value", "confidence"],
        "properties": {
          "name": { "type": "string", "enum": ["odometer", "total", "volume", "unitPrice", "currency", "date"] },
          "value": { "type": "string" },
          "confidence": { "type": "number" }
        }
      }
    }
  }
}
```

## How the answer is read

The first `{` to the last `}` of what the model said is the JSON (a reasoning block, code fences or a sentence around it are ignored; numbers are accepted for values; names are matched whatever their spelling, `unit_price` or `Unit Price` included). A value is only filled in when its `confidence` (0 to 1) is at least `Recognition:MinConfidence` after the checks above; a missing or unusable confidence counts as 0.5.

## Replaying a photo by hand

In a chat interface such as Open WebUI: put the system prompt in the chat's system prompt, attach the photo and paste the contract into the same message. What can still make an answer differ from the app's: the photo (click a thumbnail in the add dialog and save it: that is the resized copy that is sent, a phone's original is a different picture to the model), the `response_format` (a chat interface usually sends none: try `Recognition__OpenAiCompatible__ResponseFormat=None` to compare), the temperature, and any system prompt or parameters the chat interface adds on its own.

## Following a photo through the log

With `Logging__LogLevel__Tankstat=Debug` (see [Logging](configuration.md#logging)) every photo leaves a trail:

```
Asking qwen2.5-vl at http://lmstudio:1234/v1 to read photo 3f2c0a6e-... (image/jpeg, 231 KB)
qwen2.5-vl answered photo 3f2c0a6e-... in 3412 ms (HTTP 200, finish stop, 1534+96 tokens): said Odometer, listed 1, kept [], issues [Odometer:OdometerBelowLatest]
Read photo 3f2c0a6e-...: it shows Odometer, 0 of 0 values are sure enough to be filled in (rated 0.6 or more)
```

`listed` is how many values the model gave, `kept` the ones that passed the checks with their confidence, and `issues` what became of the others, as `Field:Reason` (or just the reason when it concerns the whole photo). Never the values themselves.

| Reason | Meaning |
| --- | --- |
| `Unrecognised` | the model took the photo for something this entry has no use for, or for nothing (`unknown`) |
| `NothingLegible` | the model knew what the photo shows but gave no value it could read |
| `NotUnderstood` | the value is not written the way the app takes it: an odometer must be digits only (`123 456`, `123.456` or `123456 km` are dropped), a date `yyyy-MM-dd`, an amount a plain number |
| `OutOfRange` | an amount no fill-up, price or total can be |
| `OdometerBelowLatest` | the odometer is lower than the vehicle's latest logged reading (it never goes back): dropped |
| `OdometerTooFarAbove` | more than 100 000 above the latest reading: dropped |
| `OdometerFarAbove` | 50 000 to 100 000 above it: kept, but at 0.5, under what is filled in |
| `DateTooOld`, `DateInFuture` | a receipt date more than three years back, or after tomorrow: dropped |
| `AmountsDoNotAdd` | litres times price do not fit the total: litres and price are kept at 0.5, the total stays |
| `NoConfidence` | the model gave the value no usable confidence: it counts as 0.5 |
| `Unsure` | the model rated the value under `Recognition:MinConfidence` (worked out when the reading is listed, so it follows the setting) |

The add dialogs say the same in the user's language (English or Hungarian): when a photo gave less than it might have, a notice next to what was filled in, or instead of it, says why ("The photo shows an odometer reading that is lower than the last one logged (300 000 km), so it was not filled in."), for the fields the dialog has. The reasons are kept with the reading as codes, never the value, and the `photoDrafts` query returns them (`reading { values issues { code field } }`), which is what the browser's network tab shows while you try a photo.

## Notes on servers

Ollama loads a model on the first request after a while idle (keep it loaded with `OLLAMA_KEEP_ALIVE`, or allow a longer `TimeoutSeconds`) and runs it with a short context by default, which a photo can exceed (`OLLAMA_CONTEXT_LENGTH=8192` or more). llama.cpp's server needs the model's projector (`--mmproj`) to take images. A reverse proxy in front of the server must accept request bodies of a few megabytes (a photo travels as base64). The health check is `GET /v1/models`: the key must be allowed to list models (an OpenAI restricted key needs "Models: Read"), though a server that answers 403 to it is taken as available and judged on the first read. While reading is on, photos picked in the add dialogs are uploaded as JPEG rather than WebP, which not every server decodes. The dialog waits up to about two minutes for a reading and says so next to Save, with a note that you can save right away; a slower one lands on the saved entry later, marked for review. A total whose currency the photo does not show is left empty there for you to fill in: it is never stored in the vehicle's usual currency, which a foreign receipt would get wrong. Measure a model on your own photos before trusting it: a wrong value the model is sure of is the one thing the checks cannot always catch.

## Seeing what is sent to the model

The trail above says what became of each value, not what the model said. For that, set `Recognition__OpenAiCompatible__LogTraffic=true` (it needs no Debug level). Every request to the model server and every answer is then written to the log at `Information`, one entry each, pretty-printed, with a number that pairs them and the photo's id (`health check` for the call that asks which models there are):

```
Model request 3 (photo 3f2c0a6e-...): POST http://lmstudio:1234/v1/chat/completions
Authorization: Bearer ***
Content-Type: application/json; charset=utf-8
{
  "model": "qwen2.5-vl",
  "stream": false,
  "messages": [
    { "role": "system", "content": "You read photos for a vehicle fuel log ..." },
    { "role": "user", "content": [ { "type": "image_url", "image_url": { "url": "data:image/jpeg;base64,[231 KB]" } }, ... ] }
  ],
  ...
}
Model answer 3 (photo 3f2c0a6e-...): HTTP 200 after 3412 ms
Content-Type: application/json
{ "choices": [ { "message": { "content": "{\"kind\":\"odometer\",\"fields\":[...]}" }, "finish_reason": "stop" } ], ... }
```

**These entries hold what no other line of the app does: the prompts and what the model read** (amounts, odometer readings, dates, shop names). The photo itself is only written as its size, the key is masked (the `Authorization` header, and the key itself wherever a server repeats it in an answer) and a body is cut after 16 000 characters. A server's own text cannot add a line to the log: line breaks in an entry come from its layout only, so with `Logging__Console__FormatterName=json` each entry stays one line for a log collector. The app warns at start while the setting is on. Switch it on while you investigate and off afterwards, since whoever collects your logs keeps them. In Kubernetes it is one more entry in the container's `env:` (`name: Recognition__OpenAiCompatible__LogTraffic`, `value: "true"`); `kubectl logs deploy/<name> -f` then shows each photo as it is read. The model server's own log shows the same from its side (LM Studio's developer log, `OLLAMA_DEBUG=1` for Ollama, `--verbose` for llama.cpp).
