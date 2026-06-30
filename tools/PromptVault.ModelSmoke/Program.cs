using System.Text.Json;
using PromptVault.App.Services;
using PromptVault.Core;

if (args.Length != 2)
{
    Console.Error.WriteLine("Usage: PromptVault.ModelSmoke <models-dir> <image>");
    return 2;
}

var categories = new[]
{
    new CategoryRecord(1, "\u4eba\u7269", "", 1), new CategoryRecord(2, "\u573a\u666f", "", 2),
    new CategoryRecord(3, "\u4ea7\u54c1", "", 3), new CategoryRecord(4, "\u5efa\u7b51", "", 4),
    new CategoryRecord(5, "\u63d2\u753b", "", 5), new CategoryRecord(6, "\u754c\u9762\u8bbe\u8ba1", "", 6),
    new CategoryRecord(7, "\u88e4\u5b50\u53c2\u8003", "", 7), new CategoryRecord(8, "\u4e0a\u8863\u53c2\u8003", "", 8)
};
var result = await new LocalAiClassifier(args[0]).SuggestAsync(args[1], categories);
Console.WriteLine(JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
return result.UsedModel ? 0 : 1;