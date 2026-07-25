using System.Net;
using System.Net.Http;
using System.Text;
using PromptVault.App.Services;
using PromptVault.Core;

namespace PromptVault.Tests;

public sealed class OnlineAiProviderTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "PromptVaultOnlineAiTests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public void SettingsDefaultOnlineOffAndNeverContainAnApiKey()
    {
        var path = Path.Combine(_root, "settings.json");
        var settings = AppSettings.Load(path);
        Assert.False(settings.OnlineAiEnabled);

        settings.OnlineAiEnabled = true;
        settings.OnlineAiEndpoint = "https://example.invalid/v1/chat/completions";
        settings.OnlineAiModel = "vision-test";
        settings.Save();

        var json = File.ReadAllText(path);
        Assert.Contains("\"OnlineAiEnabled\": true", json, StringComparison.Ordinal);
        Assert.DoesNotContain("ApiKey", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Bearer", json, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("http://remote.example/v1/chat/completions", false)]
    [InlineData("http://127.0.0.1:8080/v1/chat/completions", true)]
    [InlineData("https://api.example/v1/chat/completions", true)]
    public void ConfigurationRequiresEncryptedRemoteEndpoint(string endpoint, bool expected)
    {
        var settings = new AppSettings
        {
            OnlineAiEndpoint = endpoint,
            OnlineAiModel = "vision-test"
        };
        Assert.Equal(expected, OnlineAiConfiguration.TryCreate(settings, out _, out _));
    }

    [Fact]
    public async Task ProviderSendsOnlyDeclaredContentAndParsesDrafts()
    {
        Directory.CreateDirectory(_root);
        var imagePath = Path.Combine(_root, "synthetic.png");
        await File.WriteAllBytesAsync(imagePath, [1, 2, 3, 4, 5]);
        var handler = new RecordingHandler();
        using var client = new HttpClient(handler);
        var options = new OnlineAiProviderOptions(
            new Uri("https://api.example/v1/chat/completions"),
            "vision-test",
            IncludeExistingPrompt: false);
        await using var provider = new OpenAiCompatibleVisionProvider(
            options,
            () => "test-key-never-log",
            client);

        var result = await provider.AnalyzeAsync(new AiProviderRequest(
            7,
            imagePath,
            "private prompt must stay local",
            [new CategoryRecord(1, "产品", "", 0)],
            ["description", "tags"]));

        Assert.Equal("Bearer", handler.AuthorizationScheme);
        Assert.Equal("test-key-never-log", handler.AuthorizationParameter);
        Assert.Contains("data:image/png;base64,AQIDBAU=", handler.Body, StringComparison.Ordinal);
        Assert.Contains("\\u4EA7\\u54C1", handler.Body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("private prompt must stay local", handler.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("test-key-never-log", handler.Body, StringComparison.Ordinal);
        Assert.Equal("合成在线描述", result.Metadata.Single(x => x.FieldType == "description").Value);
        Assert.Equal("极简, 产品", result.Metadata.Single(x => x.FieldType == "tags").Value);
        Assert.All(result.Metadata, value => Assert.Equal(0.82, value.Confidence));
        Assert.True(provider.Descriptor.SendsDataOffDevice);
    }

    [Fact]
    public async Task ProviderIncludesExistingPromptOnlyAfterExplicitOptIn()
    {
        Directory.CreateDirectory(_root);
        var imagePath = Path.Combine(_root, "synthetic-opt-in.png");
        await File.WriteAllBytesAsync(imagePath, [1, 2, 3]);
        var handler = new RecordingHandler();
        using var client = new HttpClient(handler);
        await using var provider = new OpenAiCompatibleVisionProvider(
            new OnlineAiProviderOptions(
                new Uri("https://api.example/v1/chat/completions"),
                "vision-test",
                IncludeExistingPrompt: true),
            () => "test-key-never-log",
            client);

        await provider.AnalyzeAsync(new AiProviderRequest(
            8,
            imagePath,
            "explicit prompt opt-in",
            [],
            ["description"]));

        Assert.Contains("explicit prompt opt-in", handler.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("test-key-never-log", handler.Body, StringComparison.Ordinal);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, true); } catch { }
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public string Body { get; private set; } = "";
        public string? AuthorizationScheme { get; private set; }
        public string? AuthorizationParameter { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Body = await request.Content!.ReadAsStringAsync(cancellationToken);
            AuthorizationScheme = request.Headers.Authorization?.Scheme;
            AuthorizationParameter = request.Headers.Authorization?.Parameter;
            const string json =
                """{"choices":[{"message":{"content":"{\"description\":\"合成在线描述\",\"tags\":[\"极简\",\"产品\"],\"confidence\":0.82}"}}]}""";
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
        }
    }
}
