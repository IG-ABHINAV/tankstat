using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Tankstat.Application.Recognition;
using Tankstat.Domain.Recognition;
using Tankstat.Infrastructure.Recognition;
using Tankstat.TestSupport;

namespace Tankstat.Infrastructure.UnitTests;

/// <summary>The adapter to an OpenAI-compatible chat completions API, against answers as OpenAI, LM Studio, Ollama and llama.cpp write them.</summary>
public class OpenAiCompatibleRecognitionProviderTests
{
    private static readonly byte[] Picture = [0x52, 0x49, 0x46, 0x46, 1, 2, 3];

    private static string Contract(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "contracts", "openai-compatible", name));

    private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    /// <summary>An answer in OpenAI's envelope around what the model said.</summary>
    private static string Said(string content, string finish = "stop", string model = "m-1") =>
        JsonSerializer.Serialize(new { model, choices = new[] { new { message = new { role = "assistant", content }, finish_reason = finish } } });

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        public List<(HttpRequestMessage Request, string Body)> Seen { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Seen.Add((request, request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct)));
            return await respond(request, ct);
        }
    }

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private static (OpenAiCompatibleRecognitionProvider Provider, Handler Handler) Model(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond, string url = "http://model:1234/v1", string? key = null, string? prompt = null,
        OpenAiResponseFormat format = OpenAiResponseFormat.JsonSchema, double? temperature = null, int timeoutSeconds = 30, string model = "qwen2.5-vl", CapturedLog? log = null)
    {
        var handler = new Handler(respond);
        var options = new OpenAiCompatibleRecognitionOptions { BaseUrl = url, ApiKey = key, Model = model, ResponseFormat = format, Temperature = temperature, TimeoutSeconds = timeoutSeconds };
        return (new OpenAiCompatibleRecognitionProvider(new Factory(handler), options, prompt, (log ?? new CapturedLog()).For<OpenAiCompatibleRecognitionProvider>()), handler);
    }

    private static RecognitionRequest Request(long? lastOdometer = 123_456, string? currency = "HUF", DocumentKind receipt = DocumentKind.FuelReceipt, Guid? id = null) => new(
        Picture, "image/webp", new HashSet<DocumentKind> { DocumentKind.Odometer, receipt }, "hu", lastOdometer, currency, new DateOnly(2026, 10, 1), id);

    private static JsonElement Body(Handler handler) => JsonDocument.Parse(handler.Seen.Single().Body).RootElement;

    private static string Shown(RecognitionResult result) => string.Join(' ', result.Values.Select(v => $"{v.Name}={v.Value}@{v.Confidence}"));

    [Fact]
    public async Task Reading_SendsThePhotoAsADataUrl_WithThePromptAndTheContract_AndAsksForTheShapeOfTheAnswer()
    {
        var (model, handler) = Model((_, _) => Task.FromResult(Json(HttpStatusCode.OK, Contract("chat.fuel-receipt.json"))));

        var result = await model.ReadAsync(Request(), default);

        var (request, _) = Assert.Single(handler.Seen);
        Assert.Equal((HttpMethod.Post, "http://model:1234/v1/chat/completions", false), (request.Method, request.RequestUri!.ToString(), request.Headers.Contains("Authorization")));
        var body = Body(handler);
        Assert.Equal(("qwen2.5-vl", false), (body.GetProperty("model").GetString(), body.GetProperty("stream").GetBoolean()));
        Assert.False(body.TryGetProperty("temperature", out _));
        var messages = body.GetProperty("messages");
        Assert.Equal(("system", OpenAiCompatiblePrompt.DefaultSystemPrompt), (messages[0].GetProperty("role").GetString(), messages[0].GetProperty("content").GetString()));
        var user = messages[1].GetProperty("content");
        Assert.Equal("data:image/webp;base64," + Convert.ToBase64String(Picture), user[0].GetProperty("image_url").GetProperty("url").GetString());
        var contract = user[1].GetProperty("text").GetString()!;
        Assert.Contains("\"fuel-receipt\"", contract);
        Assert.Contains("yyyy-MM-dd", contract);
        Assert.Contains("Hungarian", contract);
        Assert.DoesNotContain("123456", contract); // the hints stay with the app: a model given a number tends to answer with it
        var format = body.GetProperty("response_format");
        Assert.Equal(("json_schema", true), (format.GetProperty("type").GetString(), format.GetProperty("json_schema").GetProperty("strict").GetBoolean()));
        var schema = format.GetProperty("json_schema").GetProperty("schema");
        Assert.Equal(["odometer", "fuel-receipt", "unknown"], schema.GetProperty("properties").GetProperty("kind").GetProperty("enum").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal(["odometer", "total", "volume", "unitPrice", "discount", "currency", "date"],
            schema.GetProperty("properties").GetProperty("fields").GetProperty("items").GetProperty("properties").GetProperty("name").GetProperty("enum").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal(("gpt-vision-example", DocumentKind.FuelReceipt), (result.ModelVersion, result.Kind));
        Assert.Equal("Total=24687@0.95 Volume=38.52@0.9 UnitPrice=640.9@0.9 Currency=HUF@0.9 Date=2026-09-17@0.85", Shown(result));
        Assert.All(result.Values, v => Assert.Equal(ValueSource.Read, v.Source));
    }

    [Fact]
    public async Task AKey_GoesAsABearerToken_AndTheOperatorsPrompt_ReplacesTheBuiltInOne_ButNeverTheContract()
    {
        var (model, handler) = Model((_, _) => Task.FromResult(Json(HttpStatusCode.OK, Contract("chat.unknown.json"))), url: "https://api.example/v1/", key: "secret", prompt: "Read it your way.");

        var result = await model.ReadAsync(Request(), default);

        var (request, _) = handler.Seen.Single();
        Assert.Equal(("https://api.example/v1/chat/completions", "Bearer secret"), (request.RequestUri!.ToString(), request.Headers.Authorization!.ToString()));
        var messages = Body(handler).GetProperty("messages");
        Assert.Equal("Read it your way.", messages[0].GetProperty("content").GetString());
        Assert.Contains("\"kind\"", messages[1].GetProperty("content")[1].GetProperty("text").GetString());
        Assert.Equal((DocumentKind.Unknown, 0), (result.Kind, result.Values.Count));
    }

    [Theory]
    [InlineData(OpenAiResponseFormat.JsonSchema, "json_schema")]
    [InlineData(OpenAiResponseFormat.JsonObject, "json_object")]
    [InlineData(OpenAiResponseFormat.None, null)]
    public async Task TheShapeOfTheAnswer_IsAskedForTheWayTheSettingSays_AndSoIsTheTemperature(OpenAiResponseFormat format, string? type)
    {
        var (model, handler) = Model((_, _) => Task.FromResult(Json(HttpStatusCode.OK, Contract("chat.unknown.json"))), format: format, temperature: 0);

        await model.ReadAsync(Request(), default);

        var body = Body(handler);
        Assert.Equal(type, body.TryGetProperty("response_format", out var asked) ? asked.GetProperty("type").GetString() : null);
        Assert.Equal(0, body.GetProperty("temperature").GetDouble());
    }

    [Theory]
    [InlineData("chat.odometer.json", DocumentKind.FuelReceipt, DocumentKind.Odometer, "Odometer=123789")]
    [InlineData("chat.fuel-receipt.json", DocumentKind.FuelReceipt, DocumentKind.FuelReceipt, "Total=24687 Volume=38.52 UnitPrice=640.9 Currency=HUF Date=2026-09-17")]
    [InlineData("chat.expense-receipt.json", DocumentKind.ExpenseReceipt, DocumentKind.ExpenseReceipt, "Total=12.5 Currency=EUR Date=2026-09-30 Title=Shell")]
    [InlineData("chat.unknown.json", DocumentKind.FuelReceipt, DocumentKind.Unknown, "")]
    [InlineData("chat.fenced.json", DocumentKind.FuelReceipt, DocumentKind.Odometer, "Odometer=123789")] // a reasoning block, code fences, a number for the value
    [InlineData("chat.text-parts.json", DocumentKind.FuelReceipt, DocumentKind.Odometer, "Odometer=123789")]
    public async Task EveryShapeOfAnswerTheServersWrite_IsUnderstood(string example, DocumentKind receipt, DocumentKind kind, string values)
    {
        var (model, _) = Model((_, _) => Task.FromResult(Json(HttpStatusCode.OK, Contract(example))));

        var result = await model.ReadAsync(Request(receipt: receipt), default);

        Assert.Equal((kind, values), (result.Kind, string.Join(' ', result.Values.Select(v => $"{v.Name}={v.Value}"))));
    }

    [Fact]
    public async Task AKindThePhotoMayNotShow_CountsAsUnknown_AndNamesAreReadWhateverTheirSpelling()
    {
        var (receipt, _) = Model((_, _) => Task.FromResult(Json(HttpStatusCode.OK, Contract("chat.expense-receipt.json"))));
        var (spelled, _) = Model((_, _) => Task.FromResult(Json(HttpStatusCode.OK, Said("""{"kind": "FuelReceipt", "fields": [{"name": "unit_price", "value": "640.9", "confidence": 0.9}, {"name": "Total", "value": "24687", "confidence": 0.9}]}"""))));

        var unknown = await receipt.ReadAsync(Request(receipt: DocumentKind.FuelReceipt), default); // an expense receipt was not on the cards
        var read = await spelled.ReadAsync(Request(), default);

        Assert.Equal((DocumentKind.Unknown, 0), (unknown.Kind, unknown.Values.Count));
        Assert.Equal((DocumentKind.FuelReceipt, "UnitPrice=640.9@0.9 Total=24687@0.9"), (read.Kind, Shown(read)));
    }

    [Fact]
    public async Task TheChecksApply_ToWhatAModelReads()
    {
        var (far, _) = Model((_, _) => Task.FromResult(Json(HttpStatusCode.OK, Said("""{"kind":"odometer","fields":[{"name":"odometer","value":"999999","confidence":0.99}]}"""))));
        var (misfit, _) = Model((_, _) => Task.FromResult(Json(HttpStatusCode.OK, Said("""{"kind":"fuel-receipt","fields":[{"name":"total","value":"30000","confidence":0.9},{"name":"volume","value":"38.52","confidence":0.9},{"name":"unitPrice","value":"640.9","confidence":0.9}]}"""))));

        var dropped = await far.ReadAsync(Request(lastOdometer: 123_456), default); // far more than a hundred thousand since the last log
        var doubted = await misfit.ReadAsync(Request(), default);

        Assert.Equal((DocumentKind.Odometer, 0), (dropped.Kind, dropped.Values.Count));
        Assert.Equal("Total=30000@0.9 Volume=38.52@0.5 UnitPrice=640.9@0.5", Shown(doubted));
    }

    [Fact]
    public async Task ARefusal_IsFinal_WhileACutOffOrUnusableAnswer_IsWorthAnotherTry()
    {
        var (refused, _) = Model((_, _) => Task.FromResult(Json(HttpStatusCode.OK, Contract("chat.refusal.json"))));
        var (filtered, _) = Model((_, _) => Task.FromResult(Json(HttpStatusCode.OK, Said("", finish: "content_filter"))));
        var (cutOff, _) = Model((_, _) => Task.FromResult(Json(HttpStatusCode.OK, Contract("chat.cut-off.json"))));
        var (prose, _) = Model((_, _) => Task.FromResult(Json(HttpStatusCode.OK, Said("I cannot see an odometer here, the number 987654 is the trip."))));
        var (silent, _) = Model((_, _) => Task.FromResult(Json(HttpStatusCode.OK, Said(""))));
        var (noChoice, _) = Model((_, _) => Task.FromResult(Json(HttpStatusCode.OK, """{ "choices": [] }""")));
        var (nullChoice, _) = Model((_, _) => Task.FromResult(Json(HttpStatusCode.OK, """{ "choices": [ null ] }""")));
        var (list, _) = Model((_, _) => Task.FromResult(Json(HttpStatusCode.OK, "[]"))); // not even an object
        var (nonsense, _) = Model((_, _) => Task.FromResult(Json(HttpStatusCode.OK, "not json")));

        await Assert.ThrowsAsync<RecognitionRejectedException>(() => refused.ReadAsync(Request(), default));
        await Assert.ThrowsAsync<RecognitionRejectedException>(() => filtered.ReadAsync(Request(), default));
        await Assert.ThrowsAsync<RecognitionUnavailableException>(() => cutOff.ReadAsync(Request(), default));
        var talked = await Assert.ThrowsAsync<RecognitionUnavailableException>(() => prose.ReadAsync(Request(), default));
        await Assert.ThrowsAsync<RecognitionUnavailableException>(() => silent.ReadAsync(Request(), default));
        await Assert.ThrowsAsync<RecognitionUnavailableException>(() => noChoice.ReadAsync(Request(), default));
        await Assert.ThrowsAsync<RecognitionUnavailableException>(() => nullChoice.ReadAsync(Request(), default));
        await Assert.ThrowsAsync<RecognitionUnavailableException>(() => list.ReadAsync(Request(), default));
        await Assert.ThrowsAsync<RecognitionUnavailableException>(() => nonsense.ReadAsync(Request(), default));
        Assert.DoesNotContain("987654", talked.Message); // what the model said is never repeated: it could be anything
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, null, false, "Recognition:OpenAiCompatible:ApiKey")]
    [InlineData(HttpStatusCode.Unauthorized, "error.array.json", false, "Recognition:OpenAiCompatible:ApiKey")] // an array around the error (Google)
    [InlineData(HttpStatusCode.Forbidden, null, false, "Recognition:OpenAiCompatible:ApiKey")]
    [InlineData(HttpStatusCode.NotFound, null, false, "/v1")]
    [InlineData(HttpStatusCode.RequestEntityTooLarge, null, true, "large")]
    [InlineData(HttpStatusCode.BadRequest, "error.openai.json", true, "invalid_image")]
    [InlineData(HttpStatusCode.BadRequest, "error.llamacpp.json", false, "400")] // a number for a code, nothing about the picture in a token: our request, so later
    [InlineData(HttpStatusCode.BadRequest, "error.lmstudio.json", false, "400")]
    [InlineData(HttpStatusCode.TooManyRequests, null, false, "429")]
    [InlineData(HttpStatusCode.UnsupportedMediaType, null, false, "415")]
    [InlineData(HttpStatusCode.InternalServerError, null, false, "500")]
    public async Task Errors_AreToldApart_ByWhetherTryingAgainCanHelp_WithoutRepeatingTheServersWords(HttpStatusCode status, string? example, bool rejected, string said)
    {
        var body = example is null ? new StringContent("<html>proxy error</html>") : new StringContent(Contract(example), Encoding.UTF8, "application/json");
        var (model, _) = Model((_, _) => Task.FromResult(new HttpResponseMessage(status) { Content = body }));

        var error = await Assert.ThrowsAnyAsync<Exception>(() => model.ReadAsync(Request(), default));

        Assert.IsType(rejected ? typeof(RecognitionRejectedException) : typeof(RecognitionUnavailableException), error);
        Assert.Contains(said, error.Message);
        Assert.DoesNotContain("Invalid image.", error.Message);
        Assert.DoesNotContain("not loaded", error.Message);
        Assert.DoesNotContain("not supported", error.Message);
    }

    [Fact]
    public async Task AServerThatCannotBeReached_OrIsTooSlow_IsUnavailable()
    {
        var (unreachable, _) = Model((_, _) => throw new HttpRequestException("connection refused"));
        var (slow, _) = Model(async (_, ct) => { await Task.Delay(TimeSpan.FromSeconds(10), ct); return Json(HttpStatusCode.OK, "{}"); }, timeoutSeconds: 1);

        var down = await Assert.ThrowsAsync<RecognitionUnavailableException>(() => unreachable.ReadAsync(Request(), default));
        var timedOut = await Assert.ThrowsAsync<RecognitionUnavailableException>(() => slow.ReadAsync(Request(), default));

        Assert.IsType<HttpRequestException>(down.InnerException);
        Assert.Contains("1 seconds", timedOut.Message);
    }

    [Fact]
    public async Task TheModelsName_IsTheVersion_OrElseTheConfiguredOne()
    {
        var path = "/models/" + new string('m', 70) + ".gguf"; // the reading cuts it to what its column holds
        var (named, _) = Model((_, _) => Task.FromResult(Json(HttpStatusCode.OK, Said("""{"kind":"unknown","fields":[]}""", model: path))));
        var (unnamed, _) = Model((_, _) => Task.FromResult(Json(HttpStatusCode.OK, """{ "choices": [ { "message": { "content": "{\"kind\":\"unknown\"}" } } ] }""")));

        Assert.Equal(path, (await named.ReadAsync(Request(), default)).ModelVersion);
        Assert.Equal("qwen2.5-vl", (await unnamed.ReadAsync(Request(), default)).ModelVersion);
        var (blank, _) = Model((_, _) => Task.FromResult(Json(HttpStatusCode.OK, Said("""{"kind":"unknown","fields":[]}""", model: ""))));
        Assert.Equal("qwen2.5-vl", (await blank.ReadAsync(Request(), default)).ModelVersion);
    }

    [Fact]
    public async Task FieldsThatNameNoValueTheAppKnows_OrHoldNoText_AreLeftOut()
    {
        var (model, _) = Model((_, _) => Task.FromResult(Json(HttpStatusCode.OK, Said("""
            {"kind":"odometer","fields":[
              {"name":"colour","value":"red","confidence":0.9},
              {"value":"123000","confidence":0.9},
              "odometer",
              {"name":"odometer","value":true,"confidence":0.9},
              {"name":"odometer","value":null,"confidence":0.9},
              {"name":"odometer","value":"   ","confidence":0.9},
              {"name":"odometer","value":"123789","confidence":0.9}
            ]}
            """))));

        var result = await model.ReadAsync(Request(), default);

        Assert.Equal("Odometer=123789@0.9", Shown(result));
    }

    [Theory]
    [InlineData("""{ "choices": [ { "message": { "content": null }, "finish_reason": "stop" } ] }""")] // no text
    [InlineData("""{ "choices": [ { "message": { "content": 5 }, "finish_reason": "stop" } ] }""")] // neither text nor parts
    [InlineData("""{ "choices": [ { "finish_reason": "stop" } ] }""")] // no message at all
    [InlineData("""{ "choices": [ { "message": { "content": "{ kind: odometer, fields: [ }" }, "finish_reason": "stop" } ] }""")] // braces, but not JSON
    public async Task AnAnswerWithoutTextOrWithoutItsJson_IsWorthAnotherTry(string answer)
    {
        var (model, _) = Model((_, _) => Task.FromResult(Json(HttpStatusCode.OK, answer)));

        var error = await Assert.ThrowsAsync<RecognitionUnavailableException>(() => model.ReadAsync(Request(), default));

        Assert.DoesNotContain("kind:", error.Message);
    }

    [Fact]
    public async Task Health_FindsTheModelByTheFileNameAServerLists_AndPassesOverEntriesWithoutAnId()
    {
        const string listed = """{ "object": "list", "data": [ { "id": "/models/other.gguf" }, { "object": "model" }, { "id": "C:\\models\\qwen2.5-vl.GGUF" } ] }""";
        var (model, _) = Model((_, _) => Task.FromResult(Json(HttpStatusCode.OK, listed)), model: "qwen2.5-vl");
        var (missing, _) = Model((_, _) => Task.FromResult(Json(HttpStatusCode.OK, listed)), model: "llava");

        Assert.True(await model.IsHealthyAsync(default));
        await Assert.ThrowsAsync<RecognitionUnavailableException>(() => missing.IsHealthyAsync(default));
    }

    [Theory]
    [InlineData("http://user:secret@model:1234/v1/?key=abc", "http://model:1234/v1")] // neither user info nor a query: either can carry a key
    [InlineData("not an address", "(no usable address)")]
    [InlineData(null, "(no usable address)")]
    public void TheServer_IsNamedInTheLogWithoutWhatCouldCarryAKey(string? url, string shown) => Assert.Equal(shown, ServerAddress.Of(url));

    [Theory]
    [InlineData("odometer", DocumentKind.Odometer)]
    [InlineData("fuel-receipt", DocumentKind.FuelReceipt)]
    [InlineData("expense-receipt", DocumentKind.ExpenseReceipt)]
    [InlineData("unknown", DocumentKind.Unknown)]
    [InlineData(null, DocumentKind.Unknown)]
    public void AKindsName_IsReadBack_AndAnyOtherNameIsUnknown(string? name, DocumentKind kind) => Assert.Equal(kind, RecognitionNames.Kind(name));

    [Fact]
    public async Task Health_AListThatIsNoList_IsNotUnderstood()
    {
        var (model, _) = Model((_, _) => Task.FromResult(Json(HttpStatusCode.OK, """{ "object": "list", "data": "qwen2.5-vl" }""")));

        var error = await Assert.ThrowsAsync<RecognitionUnavailableException>(() => model.IsHealthyAsync(default));

        Assert.Contains("Recognition:OpenAiCompatible:BaseUrl", error.Message);
    }

    [Theory]
    [InlineData("models.openai.json", "gpt-vision-example", true)]
    [InlineData("models.openai.json", "GPT-Vision-Example", true)] // case does not matter
    [InlineData("models.openai.json", "gpt-other", false)]
    [InlineData("models.ollama.json", "llama3.2-vision", true)] // Ollama lists the tag
    [InlineData("models.ollama.json", "qwen2.5vl:7b", true)]
    [InlineData("models.ollama.json", "qwen2.5vl", false)]
    [InlineData("models.llamacpp.json", "anything", true)] // one model: the server answers with it whatever is asked for
    [InlineData("models.lmstudio.json", "qwen2.5-vl-7b-instruct", true)]
    [InlineData("models.lmstudio.json", "qwen2.5-vl", false)]
    public async Task Health_NeedsTheModelAmongThoseListed_UnlessThereIsOnlyOne(string example, string wanted, bool healthy)
    {
        var (model, _) = Model((_, _) => Task.FromResult(Json(HttpStatusCode.OK, Contract(example))), model: wanted);

        if (healthy)
        {
            Assert.True(await model.IsHealthyAsync(default));
        }
        else
        {
            var missing = await Assert.ThrowsAsync<RecognitionUnavailableException>(() => model.IsHealthyAsync(default));
            Assert.Contains("Recognition:OpenAiCompatible:Model", missing.Message);
        }
    }

    [Fact]
    public async Task Health_AsksForTheModels_WithTheKey_AndOtherwiseSaysWhyNot()
    {
        var (keyed, handler) = Model((_, _) => Task.FromResult(Json(HttpStatusCode.OK, Contract("models.openai.json"))), key: "secret", model: "gpt-vision-example");
        var (refusing, _) = Model((_, _) => Task.FromResult(Json(HttpStatusCode.Unauthorized, """{ "error": { "message": "Incorrect API key provided", "type": "invalid_request_error", "code": "invalid_api_key" } }""")));
        var (scoped, _) = Model((_, _) => Task.FromResult(Json(HttpStatusCode.Forbidden, """{ "error": { "message": "insufficient permissions", "type": "invalid_request_error", "code": "insufficient_permissions" } }""")));
        var (misaddressed, _) = Model((_, _) => Task.FromResult(Json(HttpStatusCode.NotFound, "<html>not found</html>")));
        var (broken, _) = Model((_, _) => Task.FromResult(Json(HttpStatusCode.ServiceUnavailable, "<html>down</html>")));
        var (empty, _) = Model((_, _) => Task.FromResult(Json(HttpStatusCode.OK, """{ "object": "list", "data": [] }""")));
        var (unreachable, _) = Model((_, _) => throw new HttpRequestException("connection refused"));
        var (nonsense, _) = Model((_, _) => Task.FromResult(Json(HttpStatusCode.OK, "<html>a web page, not an API</html>")));

        Assert.True(await keyed.IsHealthyAsync(default));
        var (request, _) = handler.Seen.Single();
        Assert.Equal(("http://model:1234/v1/models", "Bearer secret"), (request.RequestUri!.ToString(), request.Headers.Authorization!.ToString()));
        var refused = await Assert.ThrowsAsync<RecognitionUnavailableException>(() => refusing.IsHealthyAsync(default));
        Assert.Contains("Recognition:OpenAiCompatible:ApiKey", refused.Message);
        Assert.DoesNotContain("Incorrect", refused.Message);
        Assert.True(await scoped.IsHealthyAsync(default)); // a key that may not list models may still read: the first read is the judge
        var lost = await Assert.ThrowsAsync<RecognitionUnavailableException>(() => misaddressed.IsHealthyAsync(default));
        Assert.Contains("/v1", lost.Message); // the usual mistake: an address without the API's version
        var down = await Assert.ThrowsAsync<RecognitionUnavailableException>(() => broken.IsHealthyAsync(default));
        Assert.Contains("503", down.Message); // what an operator has to go by: it answered, with an error
        var none = await Assert.ThrowsAsync<RecognitionUnavailableException>(() => empty.IsHealthyAsync(default));
        Assert.Contains("no models", none.Message);
        var away = await Assert.ThrowsAsync<RecognitionUnavailableException>(() => unreachable.IsHealthyAsync(default));
        Assert.IsType<HttpRequestException>(away.InnerException); // and the cause behind "could not be reached"
        var page = await Assert.ThrowsAsync<RecognitionUnavailableException>(() => nonsense.IsHealthyAsync(default));
        Assert.Contains("Recognition:OpenAiCompatible:BaseUrl", page.Message);
    }

    [Fact]
    public async Task AHealthCheckThatIsCancelledByTheCaller_IsACancellation_NotAnUnavailableServer()
    {
        var (model, _) = Model(async (_, ct) => { await Task.Delay(TimeSpan.FromSeconds(10), ct); return Json(HttpStatusCode.OK, "{}"); });
        using var cancelled = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => model.IsHealthyAsync(cancelled.Token));
    }

    [Fact]
    public async Task NeitherTheKey_NorThePrompt_NorWhatTheServerOrTheModelSaid_EverReachesAnErrorMessage()
    {
        const string key = "hunter2-key", prompt = "SECRET PROMPT TEXT", server = "leak-me-server-message", read = "987654";
        var problems = new List<Exception>();
        var log = new CapturedLog();
        async Task Collect(OpenAiCompatibleRecognitionProvider model, bool health = false)
        {
            problems.Add(await Assert.ThrowsAnyAsync<Exception>(() => health ? model.IsHealthyAsync(default) : model.ReadAsync(Request(), default)));
        }
        var error = (HttpStatusCode status, string code) => Json(status, $$"""{ "error": { "message": "{{server}}", "type": "invalid_request_error", "code": "{{code}}" } }""");

        await Collect(Model((_, _) => Task.FromResult(error(HttpStatusCode.Unauthorized, "invalid_api_key")), key: key, prompt: prompt, log: log).Provider);
        await Collect(Model((_, _) => Task.FromResult(error(HttpStatusCode.BadRequest, "invalid_image")), key: key, prompt: prompt, log: log).Provider);
        await Collect(Model((_, _) => Task.FromResult(error(HttpStatusCode.BadRequest, server)), key: key, prompt: prompt, log: log).Provider); // a "code" that is a sentence is no code
        await Collect(Model((_, _) => Task.FromResult(Json(HttpStatusCode.OK, Said($"The odometer reads {read} km, I think."))), key: key, prompt: prompt, log: log).Provider);
        await Collect(Model((_, _) => Task.FromResult(Json(HttpStatusCode.OK, "{" + server)), key: key, prompt: prompt, log: log).Provider);
        await Collect(Model((_, _) => Task.FromResult(error(HttpStatusCode.Unauthorized, "invalid_api_key")), key: key, prompt: prompt, log: log).Provider, health: true);
        await Collect(Model((_, _) => Task.FromResult(Json(HttpStatusCode.InternalServerError, server)), key: key, prompt: prompt, log: log).Provider, health: true);
        await Collect(Model((_, _) => Task.FromResult(Json(HttpStatusCode.OK, "<html>" + server)), key: key, prompt: prompt, log: log).Provider, health: true);

        Assert.Equal(8, problems.Count);
        foreach (var text in problems.SelectMany(p => new[] { p.Message, p.InnerException?.Message ?? "" }))
            foreach (var secret in new[] { key, prompt, server, read })
                Assert.DoesNotContain(secret, text);
        Assert.NotEmpty(log.Entries); // the failed reads said so, and still none of it reached a line
        foreach (var secret in new[] { key, prompt, server, read }) Assert.False(log.Mentions(secret), secret);
    }

    // ---- the trail of a photo in the log -----------------------------------------------------------------------

    [Fact]
    public async Task EveryPhoto_LeavesATrail_FromTheQuestionToTheAnswer_WithoutWhatWasReadNorTheKeyNorThePrompt()
    {
        var log = new CapturedLog();
        var id = Guid.NewGuid();
        var (model, _) = Model((_, _) => Task.FromResult(Json(HttpStatusCode.OK, Contract("chat.fuel-receipt.json"))), key: "secret-key", prompt: "SECRET PROMPT TEXT", log: log);

        await model.ReadAsync(Request(id: id), default);

        var lines = log.From<OpenAiCompatibleRecognitionProvider>().ToList();
        Assert.Equal([LogLevel.Debug, LogLevel.Debug], lines.Select(l => l.Level));
        Assert.Equal<object?[]>([id, "qwen2.5-vl", "http://model:1234/v1", "image/webp", 1],
            [lines[0].Values["PhotoId"], lines[0].Values["Model"], lines[0].Values["Server"], lines[0].Values["ContentType"], lines[0].Values["Kilobytes"]]);
        Assert.Equal<object?[]>([id, 200, "stop", "1534", "96", DocumentKind.FuelReceipt, 5],
            [lines[1].Values["PhotoId"], lines[1].Values["Status"], lines[1].Values["Finish"], lines[1].Values["PromptTokens"], lines[1].Values["CompletionTokens"], lines[1].Values["Said"], lines[1].Values["Listed"]]);
        Assert.Equal("Total@0.95, Volume@0.90, UnitPrice@0.90, Currency@0.90, Date@0.85", lines[1].Values["Kept"]);
        Assert.Equal("", lines[1].Values["Issues"]);
        Assert.True(Convert.ToInt64(lines[1].Values["ElapsedMs"]) >= 0);
        foreach (var secret in new[] { "24687", "38.52", "640.9", "2026-09-17", "secret-key", "SECRET PROMPT TEXT" }) Assert.False(log.Mentions(secret), secret);
    }

    [Fact]
    public async Task ADroppedValue_IsInTheTrailWithItsReason_AndSoIsWhatTheModelCouldNotTell()
    {
        var log = new CapturedLog();
        var (below, _) = Model((_, _) => Task.FromResult(Json(HttpStatusCode.OK, Contract("chat.odometer.json"))), log: log);
        var (nothing, _) = Model((_, _) => Task.FromResult(Json(HttpStatusCode.OK, Said("""{"kind":"odometer","fields":[]}"""))), log: log);
        var (unknown, _) = Model((_, _) => Task.FromResult(Json(HttpStatusCode.OK, Contract("chat.unknown.json"))), log: log);

        var older = await below.ReadAsync(Request(lastOdometer: 200_000), default); // the photo shows 123789: the car has been further since
        var blank = await nothing.ReadAsync(Request(), default);
        var other = await unknown.ReadAsync(Request(), default);

        var answers = log.From<OpenAiCompatibleRecognitionProvider>().Where(l => l.Values.ContainsKey("Issues")).ToList();
        Assert.Equal(["Odometer:OdometerBelowLatest", "NothingLegible", "Unrecognised"], answers.Select(a => a.Values["Issues"]));
        Assert.Equal(["", "", ""], answers.Select(a => a.Values["Kept"]));
        Assert.Equal<object?[]>([DocumentKind.Odometer, DocumentKind.Odometer, DocumentKind.Unknown], [older.Kind, blank.Kind, other.Kind]);
        Assert.Equal(["Odometer:OdometerBelowLatest"], older.Issues.Select(i => $"{i.Field}:{i.Code}"));
        Assert.Equal([ReadingIssueCode.NothingLegible], blank.Issues.Select(i => i.Code));
        Assert.Equal([ReadingIssueCode.Unrecognised], other.Issues.Select(i => i.Code));
        Assert.False(log.Mentions("123789"));
    }

    [Fact]
    public async Task AKindThePhotoMayNotShow_AValueNoOneUnderstands_AndAMissingRating_AreToldApart()
    {
        var (receipt, _) = Model((_, _) => Task.FromResult(Json(HttpStatusCode.OK, Contract("chat.expense-receipt.json"))));
        var (spaced, _) = Model((_, _) => Task.FromResult(Json(HttpStatusCode.OK, Said("""{"kind":"odometer","fields":[{"name":"odometer","value":"123 456 km","confidence":0.9}]}"""))));
        var (unrated, _) = Model((_, _) => Task.FromResult(Json(HttpStatusCode.OK, Said("""{"kind":"odometer","fields":[{"name":"odometer","value":"123789"}]}"""))));

        var wrongKind = await receipt.ReadAsync(Request(receipt: DocumentKind.FuelReceipt), default); // an expense receipt was not on the cards
        var notANumber = await spaced.ReadAsync(Request(), default);
        var noRating = await unrated.ReadAsync(Request(), default);

        Assert.Equal([ReadingIssueCode.Unrecognised], wrongKind.Issues.Select(i => i.Code));
        Assert.Equal(["Odometer:NotUnderstood"], notANumber.Issues.Select(i => $"{i.Field}:{i.Code}"));
        Assert.Empty(notANumber.Values);
        Assert.Equal(["Odometer:NoConfidence"], noRating.Issues.Select(i => $"{i.Field}:{i.Code}"));
        Assert.Equal("Odometer=123789@0.5", Shown(noRating)); // kept, but under what the app fills in
    }

    [Fact]
    public async Task APhotoThatCouldNotBeRead_LeavesALine_WithTheTimeItTook_AndTheReasonStaysInTheException()
    {
        var log = new CapturedLog();
        var id = Guid.NewGuid();
        var (broken, _) = Model((_, _) => Task.FromResult(Json(HttpStatusCode.InternalServerError, "leak-me-server-message")), log: log);

        var error = await Assert.ThrowsAsync<RecognitionUnavailableException>(() => broken.ReadAsync(Request(id: id), default));

        var lines = log.From<OpenAiCompatibleRecognitionProvider>().ToList();
        Assert.Equal(2, lines.Count); // the question, then the failure: there is no answer to report
        Assert.Equal<object?[]>([id, "qwen2.5-vl"], [lines[1].Values["PhotoId"], lines[1].Values["Model"]]);
        Assert.True(Convert.ToInt64(lines[1].Values["ElapsedMs"]) >= 0);
        Assert.Contains("500", error.Message);
        Assert.False(log.Mentions("leak-me-server-message"));
    }

    [Fact]
    public async Task WhyTheModelStopped_IsOnlyRepeatedWhenItIsAToken_NotWhenAServerWritesSentencesOrLineBreaks()
    {
        var log = new CapturedLog();
        string Answer(string finish) => Said("""{"kind":"unknown","fields":[]}""", finish: finish);
        var (token, _) = Model((_, _) => Task.FromResult(Json(HttpStatusCode.OK, Answer("stop"))), log: log);
        var (words, _) = Model((_, _) => Task.FromResult(Json(HttpStatusCode.OK, Answer("stopped\nFAKE LINE: signed in as admin"))), log: log);

        await token.ReadAsync(Request(), default);
        await words.ReadAsync(Request(), default);

        Assert.Equal(["stop", "other"], log.From<OpenAiCompatibleRecognitionProvider>().Where(l => l.Values.ContainsKey("Finish")).Select(l => l.Values["Finish"]));
        Assert.False(log.Mentions("FAKE LINE"));
    }

    [Fact]
    public async Task ACallerThatCancels_LeavesNoFailureLine()
    {
        var log = new CapturedLog();
        var (model, _) = Model(async (_, ct) => { await Task.Delay(TimeSpan.FromSeconds(10), ct); return Json(HttpStatusCode.OK, "{}"); }, log: log);
        using var cancelled = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => model.ReadAsync(Request(), cancelled.Token));

        Assert.Single(log.From<OpenAiCompatibleRecognitionProvider>()); // only the question: the app was stopping, the photo is not given up on
    }
}
