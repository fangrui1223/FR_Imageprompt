using PromptVault.Core;

if (args.Contains("--probe", StringComparer.OrdinalIgnoreCase))
{
    Console.WriteLine($"PromptVault.AiWorker pid={Environment.ProcessId} uiProcess=false");
    return 0;
}

Console.Error.WriteLine("AI Worker provider host is installed; use --probe for process-isolation verification.");
return 2;
