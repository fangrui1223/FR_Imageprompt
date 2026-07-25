using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PromptVault.Core;

namespace PromptVault.App.Services;

public sealed record OnlineAiProviderOptions(
    Uri Endpoint,
    string Model,
    bool IncludeExistingPrompt);

public static class OnlineAiConfiguration
{
    public static bool TryCreate(
        AppSettings settings,
        out OnlineAiProviderOptions? options,
        out string? error)
    {
        options = null;
        error = null;
        if (!Uri.TryCreate(settings.OnlineAiEndpoint?.Trim(), UriKind.Absolute, out var endpoint)
            || (endpoint.Scheme != Uri.UriSchemeHttps
                && !(endpoint.Scheme == Uri.UriSchemeHttp && endpoint.IsLoopback)))
        {
            error = "端点必须是 HTTPS 完整地址；仅本机回环服务可使用 HTTP。";
            return false;
        }
        if (string.IsNullOrWhiteSpace(settings.OnlineAiModel))
        {
            error = "请填写在线模型名称。";
            return false;
        }
        options = new OnlineAiProviderOptions(
            endpoint,
            settings.OnlineAiModel.Trim(),
            settings.OnlineAiIncludeExistingPrompt);
        return true;
    }

    public static string GetModelVersion(OnlineAiProviderOptions options)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{options.Endpoint.GetLeftPart(UriPartial.Path)}\n{options.Model}"));
        return Convert.ToHexString(bytes)[..16].ToLowerInvariant();
    }
}

public sealed class OpenAiCompatibleVisionProvider : IAiProvider
{
    public const string ProviderId = "online-openai-compatible";
    private static readonly string[] DefaultFields =
    [
        "description", "category", "tags", "style", "lighting",
        "color", "composition", "texture", "atmosphere"
    ];

    private readonly OnlineAiProviderOptions _options;
    private readonly Func<string?> _keyProvider;
    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;

    public OpenAiCompatibleVisionProvider(
        OnlineAiProviderOptions options,
        Func<string?> keyProvider,
        HttpClient? httpClient = null)
    {
        _options = options;
        _keyProvider = keyProvider;
        _httpClient = httpClient ?? new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
        _ownsHttpClient = httpClient is null;
        Descriptor = new AiProviderDescriptor(
            ProviderId,
            "在线 OpenAI 兼容视觉 API",
            AiProviderKind.OnlineVision,
            options.Model,
            OnlineAiConfiguration.GetModelVersion(options),
            AiProviderCapabilities.Metadata | AiProviderCapabilities.ChineseDescription,
            SendsDataOffDevice: true);
    }

    public AiProviderDescriptor Descriptor { get; }

    public async Task<AiProviderResult> AnalyzeAsync(
        AiProviderRequest request,
        CancellationToken cancellationToken = default)
    {
        var key = _keyProvider()?.Trim();
        if (string.IsNullOrWhiteSpace(key))
            throw new InvalidOperationException("在线 AI 密钥尚未配置。");

        var bytes = await File.ReadAllBytesAsync(request.ImagePath, cancellationToken)
            .ConfigureAwait(false);
        var requestedFields = request.RequestedFields.Count == 0
            ? DefaultFields
            : request.RequestedFields
                .Where(field => DefaultFields.Contains(field, StringComparer.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        var userText = BuildUserText(request, requestedFields);
        var payload = new
        {
            model = _options.Model,
            temperature = 0.2,
            response_format = new { type = "json_object" },
            messages = new object[]
            {
                new
                {
                    role = "system",
                    content = "你是图片元数据助手。只返回一个 JSON 对象，不要 Markdown。字段必须来自用户给出的列表；description 使用简洁中文，tags 使用字符串数组。"
                },
                new
                {
                    role = "user",
                    content = new object[]
                    {
                        new { type = "text", text = userText },
                        new
                        {
                            type = "image_url",
                            image_url = new
                            {
                                url = $"data:{GetMimeType(request.ImagePath)};base64,{Convert.ToBase64String(bytes)}",
                                detail = "low"
                            }
                        }
                    }
                }
            }
        };

        using var message = new HttpRequestMessage(HttpMethod.Post, _options.Endpoint);
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        message.Content = new StringContent(
            JsonSerializer.Serialize(payload),
            Encoding.UTF8,
            "application/json");
        using var response = await _httpClient.SendAsync(
            message,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"在线 AI 请求失败（HTTP {(int)response.StatusCode}）。",
                null,
                response.StatusCode);
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(
            stream,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        var content = ExtractContent(document.RootElement);
        var metadata = ParseMetadata(content, requestedFields);
        if (metadata.Count == 0)
            throw new InvalidDataException("在线 AI 返回了空元数据。");
        return new AiProviderResult(metadata, null, "Online metadata draft completed.");
    }

    public Task<float[]?> EmbedTextAsync(
        AiTextEmbeddingRequest request,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<float[]?>(null);

    public Task ReleaseResourcesAsync(CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    public ValueTask DisposeAsync()
    {
        if (_ownsHttpClient) _httpClient.Dispose();
        return ValueTask.CompletedTask;
    }

    private string BuildUserText(
        AiProviderRequest request,
        IReadOnlyList<string> requestedFields)
    {
        var categories = request.Categories
            .Select(category => category.Name)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase);
        var builder = new StringBuilder()
            .Append("请分析图片并返回字段：")
            .Append(string.Join(", ", requestedFields))
            .Append("。可用分类名称：")
            .Append(string.Join("、", categories))
            .Append("。没有依据的字段请省略。");
        if (_options.IncludeExistingPrompt && !string.IsNullOrWhiteSpace(request.ExistingPrompt))
        {
            builder.Append(" 当前提示词：").Append(request.ExistingPrompt);
        }
        return builder.ToString();
    }

    private static string ExtractContent(JsonElement root)
    {
        if (!root.TryGetProperty("choices", out var choices)
            || choices.ValueKind != JsonValueKind.Array
            || choices.GetArrayLength() == 0
            || !choices[0].TryGetProperty("message", out var message)
            || !message.TryGetProperty("content", out var content))
            throw new InvalidDataException("在线 AI 响应缺少消息内容。");
        if (content.ValueKind == JsonValueKind.String) return content.GetString() ?? "";
        if (content.ValueKind == JsonValueKind.Array)
        {
            return string.Concat(content.EnumerateArray()
                .Where(item => item.TryGetProperty("text", out _))
                .Select(item => item.GetProperty("text").GetString()));
        }
        throw new InvalidDataException("在线 AI 消息内容格式不受支持。");
    }

    private static IReadOnlyList<AiMetadataValue> ParseMetadata(
        string content,
        IReadOnlyList<string> requestedFields)
    {
        var trimmed = content.Trim();
        if (trimmed.StartsWith("```", StringComparison.Ordinal))
        {
            var firstLineEnd = trimmed.IndexOf('\n');
            var lastFence = trimmed.LastIndexOf("```", StringComparison.Ordinal);
            if (firstLineEnd >= 0 && lastFence > firstLineEnd)
                trimmed = trimmed[(firstLineEnd + 1)..lastFence].Trim();
        }
        using var document = JsonDocument.Parse(trimmed);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("在线 AI 元数据不是 JSON 对象。");

        double? confidence = null;
        if (document.RootElement.TryGetProperty("confidence", out var confidenceElement)
            && confidenceElement.TryGetDouble(out var score))
            confidence = Math.Clamp(score, 0, 1);

        var values = new List<AiMetadataValue>();
        foreach (var field in requestedFields)
        {
            if (!TryGetPropertyIgnoreCase(document.RootElement, field, out var value)) continue;
            var text = value.ValueKind switch
            {
                JsonValueKind.String => value.GetString(),
                JsonValueKind.Array => string.Join(", ", value.EnumerateArray()
                    .Where(item => item.ValueKind == JsonValueKind.String)
                    .Select(item => item.GetString())
                    .Where(item => !string.IsNullOrWhiteSpace(item))),
                _ => null
            };
            if (!string.IsNullOrWhiteSpace(text))
                values.Add(new AiMetadataValue(field, text.Trim(), confidence));
        }
        return values;
    }

    private static bool TryGetPropertyIgnoreCase(
        JsonElement element,
        string name,
        out JsonElement value)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (!property.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) continue;
            value = property.Value;
            return true;
        }
        value = default;
        return false;
    }

    private static string GetMimeType(string path) =>
        Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".png" => "image/png",
            ".webp" => "image/webp",
            ".gif" => "image/gif",
            _ => "image/jpeg"
        };
}
