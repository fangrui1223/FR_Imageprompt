using System.IO;
using System.Security.Cryptography;
using System.Windows;
using PromptVault.Licensing;

namespace FR_Imageprompt.LicenseManager;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        try
        {
            if (e.Args.Contains("--initialize-authority", StringComparer.OrdinalIgnoreCase))
            {
                var output = ReadPathOption(e.Args, "--public-key-out")
                    ?? throw new ArgumentException("--initialize-authority 需要 --public-key-out <path>。");
                var authority = new LicenseAuthorityService();
                authority.InitializeIfMissing();
                File.WriteAllText(output, Convert.ToBase64String(authority.ExportPublicKey()));
                Shutdown(0);
                return;
            }
            if (e.Args.Contains("--issue-license", StringComparer.OrdinalIgnoreCase))
            {
                var output = ReadPathOption(e.Args, "--out")
                    ?? throw new ArgumentException("--issue-license 需要 --out <path>。");
                var licensee = ReadValue(e.Args, "--licensee")
                    ?? throw new ArgumentException("--issue-license 需要 --licensee <name>。");
                var deviceCode = ReadValue(e.Args, "--device-code")
                    ?? throw new ArgumentException("--issue-license 需要 --device-code <code>。");
                var duration = ReadValue(e.Args, "--duration") ?? "permanent";
                var oneYear = string.Equals(duration, "one-year", StringComparison.OrdinalIgnoreCase);
                if (!oneYear && !string.Equals(duration, "permanent", StringComparison.OrdinalIgnoreCase))
                    throw new ArgumentException("--duration 只允许 permanent 或 one-year。");
                var authority = new LicenseAuthorityService();
                var document = authority.Issue(licensee, deviceCode, oneYear, DateTimeOffset.UtcNow);
                Directory.CreateDirectory(Path.GetDirectoryName(output)!);
                File.WriteAllText(output, document, new System.Text.UTF8Encoding(false));
                Shutdown(0);
                return;
            }

            var window = new MainWindow(new LicenseAuthorityService());
            MainWindow = window;
            window.Show();
        }
        catch (Exception exception)
        {
            MessageBox.Show(exception.Message, "FR_LicenseManager 启动失败", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    private static string? ReadPathOption(IReadOnlyList<string> args, string name)
    {
        var value = ReadValue(args, name);
        return value is null ? null : Path.GetFullPath(value);
    }

    private static string? ReadValue(IReadOnlyList<string> args, string name)
    {
        for (var index = 0; index < args.Count; index++)
        {
            if (!string.Equals(args[index], name, StringComparison.OrdinalIgnoreCase)) continue;
            if (index + 1 >= args.Count) throw new ArgumentException($"{name} 缺少路径参数。");
            return args[index + 1];
        }
        return null;
    }
}
