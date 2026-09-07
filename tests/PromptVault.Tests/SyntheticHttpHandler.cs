using System.Net;
using System.Net.Http;

namespace PromptVault.Tests;

internal sealed class SyntheticHttpHandler(byte[] payload, Action<CancellationToken>? onRequest = null) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        onRequest?.Invoke(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(payload) });
    }
}
