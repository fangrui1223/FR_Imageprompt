namespace PromptVault.App;

public sealed record CommandPaletteItem(
    string Id,
    string Title,
    string Description,
    string SearchTerms,
    string Scope,
    string? Argument = null);

public static class CommandPaletteSearch
{
    public static IReadOnlyList<CommandPaletteItem> Filter(
        IEnumerable<CommandPaletteItem> commands,
        string? query)
    {
        var terms = (query ?? "")
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (terms.Length == 0) return commands.ToArray();

        return commands
            .Where(command =>
            {
                var searchable = string.Join(
                    ' ',
                    command.Title,
                    command.Description,
                    command.SearchTerms,
                    command.Scope);
                return terms.All(term => searchable.Contains(term, StringComparison.OrdinalIgnoreCase));
            })
            .OrderByDescending(command =>
                command.Title.StartsWith(query!, StringComparison.OrdinalIgnoreCase))
            .ThenBy(command => command.Title, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
    }
}
