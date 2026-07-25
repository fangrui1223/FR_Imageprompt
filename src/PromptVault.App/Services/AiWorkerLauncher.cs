using System.Diagnostics;
using PromptVault.Core;

namespace PromptVault.App.Services;

public sealed class AiWorkerLauncher
{
    private readonly LibraryRepository _repository;
    private readonly AppSettings _settings;
    private int _running;

    public AiWorkerLauncher(LibraryRepository repository, AppSettings settings)
    {
        _repository = repository;
        _settings = settings;
    }

    public async Task EnqueueAndRunAsync(
        long itemId,
        string hash,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await _repository.EnqueueAiJobAsync(new AiJobInput(
                itemId,
                "analyze",
                LocalClipAiProvider.ProviderId,
                LocalClipAiProvider.ModelVersion,
                $"{hash}:{LocalClipAiProvider.ProviderId}:{LocalClipAiProvider.ModelVersion}"),
                cancellationToken).ConfigureAwait(false);
            if (_settings.OnlineAiEnabled
                && OnlineAiConfiguration.TryCreate(_settings, out var options, out _)
                && options is not null
                && WindowsCredentialStore.HasOnlineAiKey())
            {
                await EnqueueOnlineJobAsync(itemId, hash, options, cancellationToken)
                    .ConfigureAwait(false);
            }
            await RunWorkerAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            AppLog.Warning("ai-worker", "Independent AI worker could not complete.", ex);
        }
    }

    public async Task<bool> EnqueueOnlineAndRunAsync(
        long itemId,
        string hash,
        CancellationToken cancellationToken = default)
    {
        if (!_settings.OnlineAiEnabled
            || !OnlineAiConfiguration.TryCreate(_settings, out var options, out _)
            || options is null
            || !WindowsCredentialStore.HasOnlineAiKey())
            return false;
        var jobId = await EnqueueOnlineJobAsync(itemId, hash, options, cancellationToken)
            .ConfigureAwait(false);
        await RunWorkerAsync(cancellationToken).ConfigureAwait(false);
        return (await _repository.GetAiJobsAsync(cancellationToken: cancellationToken)
                .ConfigureAwait(false))
            .FirstOrDefault(job => job.Id == jobId)?.Status == AiJobStatus.Completed;
    }

    private async Task<long> EnqueueOnlineJobAsync(
        long itemId,
        string hash,
        OnlineAiProviderOptions options,
        CancellationToken cancellationToken)
    {
        var modelVersion = OnlineAiConfiguration.GetModelVersion(options);
        var job = await _repository.EnqueueAiJobAsync(new AiJobInput(
            itemId,
            "analyze",
            OpenAiCompatibleVisionProvider.ProviderId,
            modelVersion,
            $"{hash}:{OpenAiCompatibleVisionProvider.ProviderId}:{modelVersion}"),
            cancellationToken).ConfigureAwait(false);
        return job.Id;
    }

    private async Task RunWorkerAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.CompareExchange(ref _running, 1, 0) != 0) return;
        try
        {
            var executable = Environment.ProcessPath
                ?? throw new InvalidOperationException("无法定位 AI Worker 宿主。");
            var start = new ProcessStartInfo
            {
                FileName = executable,
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };
            if (Path.GetFileNameWithoutExtension(executable)
                .Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            {
                start.ArgumentList.Add(
                    Path.GetFullPath(Environment.GetCommandLineArgs()[0]));
            }
            start.ArgumentList.Add("--ai-worker");
            start.ArgumentList.Add("--library");
            start.ArgumentList.Add(_repository.Paths.Root);
            start.ArgumentList.Add("--settings");
            start.ArgumentList.Add(_settings.StorageFilePath);
            using var process = Process.Start(start)
                ?? throw new InvalidOperationException("无法启动独立 AI Worker。");
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            if (process.ExitCode != 0)
                throw new InvalidOperationException($"AI Worker 异常退出：{process.ExitCode}");
        }
        finally
        {
            Interlocked.Exchange(ref _running, 0);
        }
    }

    public void MarkUiActive()
    {
        var marker = Path.Combine(_repository.Paths.Root, ".ai-ui-active");
        try { File.WriteAllText(marker, DateTimeOffset.UtcNow.ToString("O")); }
        catch (Exception ex) { AppLog.Warning("ai-worker", "Could not update UI activity marker.", ex); }
    }
}
