using System.Globalization;
using System.Text.RegularExpressions;

namespace PromptVault.Tests;

public sealed class UiLayoutContractTests
{
    [Fact]
    public void TextBoxTemplateKeepsContentHostStretchable()
    {
        var xaml = ReadProjectFile("src", "PromptVault.App", "App.xaml");

        Assert.Contains("x:Name=\"PART_ContentHost\"", xaml);
        Assert.Contains("VerticalAlignment=\"Stretch\"", xaml);
        Assert.Contains("<Setter Property=\"MinHeight\" Value=\"40\"", xaml);
        Assert.Contains("<Trigger Property=\"AcceptsReturn\" Value=\"True\">", xaml);
        Assert.Contains("<Setter Property=\"VerticalContentAlignment\" Value=\"Stretch\"", xaml);
    }

    [Theory]
    [InlineData(1.00)]
    [InlineData(1.25)]
    [InlineData(1.50)]
    [InlineData(1.75)]
    [InlineData(2.00)]
    public void SingleLineTextBoxContractHasEnoughPhysicalContentHeight(double scale)
    {
        var xaml = ReadProjectFile("src", "PromptVault.App", "App.xaml");
        var minHeight = ReadSetterNumber(xaml, "MinHeight");
        var padding = Regex.Match(
            xaml,
            """<Setter Property="Padding" Value="(?<horizontal>[\d.]+),(?<vertical>[\d.]+)"\s*/>""");
        Assert.True(padding.Success);

        var verticalPadding = double.Parse(
            padding.Groups["vertical"].Value,
            CultureInfo.InvariantCulture);
        var physicalContentHeight = (minHeight - (2 * verticalPadding) - 2) * scale;
        var minimumPhysicalGlyphHeight = 14 * 1.25 * scale;

        Assert.True(
            physicalContentHeight >= minimumPhysicalGlyphHeight,
            $"Scale {scale:P0}: content {physicalContentHeight:0.##}px < glyph {minimumPhysicalGlyphHeight:0.##}px.");
    }

    [Fact]
    public void InspectorAndCaptureTagEditorsUseMultilineWrapping()
    {
        var main = ReadProjectFile("src", "PromptVault.App", "MainWindow.xaml");
        var capture = ReadProjectFile("src", "PromptVault.App", "CaptureWindow.xaml");

        Assert.Matches(
            "(?s)x:Name=\"InspectorTagsEditor\".*?MinHeight=\"40\".*?MaxHeight=\"96\".*?AcceptsReturn=\"True\".*?TextWrapping=\"Wrap\"",
            main);
        Assert.Matches(
            "(?s)x:Name=\"InspectorNotesEditor\".*?MinHeight=\"68\".*?AcceptsReturn=\"True\".*?TextWrapping=\"Wrap\"",
            main);
        Assert.Matches(
            "(?s)x:Name=\"TagsBox\".*?MinHeight=\"42\".*?MaxHeight=\"58\".*?AcceptsReturn=\"True\".*?TextWrapping=\"Wrap\"",
            capture);
        Assert.Matches(
            "(?s)x:Name=\"NotesBox\".*?AcceptsReturn=\"True\".*?TextWrapping=\"Wrap\"",
            capture);
        Assert.Matches(
            "(?s)x:Name=\"PromptBox\".*?AcceptsReturn=\"True\".*?TextWrapping=\"Wrap\"",
            capture);
    }

    [Fact]
    public void ProductPackagingUsesFrImagepromptExecutableAndIcon()
    {
        var project = ReadProjectFile("src", "PromptVault.App", "PromptVault.App.csproj");
        var releaseScript = ReadProjectFile("tools", "release", "Publish-Release.ps1");
        var icon = new FileInfo(FindProjectFile("FR_Imageprompt.ico"));

        Assert.Contains("<AssemblyName>FR_Imageprompt</AssemblyName>", project);
        Assert.Contains("<ApplicationIcon>..\\..\\FR_Imageprompt.ico</ApplicationIcon>", project);
        Assert.Contains("FR_Imageprompt.exe", releaseScript);
        Assert.DoesNotContain("PromptVault.exe", releaseScript);
        Assert.True(icon.Length > 0);
    }

    private static double ReadSetterNumber(string xaml, string property)
    {
        var match = Regex.Match(
            xaml,
            $"""<Setter Property="{Regex.Escape(property)}" Value="(?<value>[\d.]+)"\s*/>""");
        Assert.True(match.Success, $"Missing numeric setter for {property}.");
        return double.Parse(match.Groups["value"].Value, CultureInfo.InvariantCulture);
    }

    private static string ReadProjectFile(params string[] segments)
        => File.ReadAllText(FindProjectFile(segments));

    private static string FindProjectFile(params string[] segments)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine([directory.FullName, .. segments]);
            if (File.Exists(candidate)) return candidate;
            directory = directory.Parent;
        }

        throw new FileNotFoundException($"Could not locate {Path.Combine(segments)} from the test output.");
    }
}
