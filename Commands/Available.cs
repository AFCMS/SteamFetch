using Spectre.Console;
using Spectre.Console.Cli;
using System.ComponentModel;

public sealed class AvailableCommand : AsyncCommand<AvailableCommand.Settings>
{
    public sealed class Settings : JsonCommandSettings
    {
        [CommandArgument(0, "<appid>")]
        [Description("Steam App ID of the game.")]
        public uint AppId { get; set; }

        [CommandOption("--filter-type <TYPE>")]
        [Description("Optional filter by artwork type (e.g., library_capsule, library_hero)")]
        public string? FilterType { get; set; }
    }

    private sealed record AvailableJsonResult(
        uint AppId,
        string Title,
        string? FilterType,
        IReadOnlyList<SteamKitInterface.ArtworkVariant> Artworks);

    protected override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        using var sk = new SteamKitInterface();
        IReadOnlyList<SteamKitInterface.ArtworkVariant> list;
        string title;

        try
        {
            var kv = await sk.GetProductInfoKV(settings.AppId);
            title = kv?["common"]?["name"]?.Value ?? "(unknown)";

            if (!settings.Json)
            {
                CliOutput.WriteAppHeader(settings.AppId, title);
            }

            list = await sk.ListAvailableArtworks(settings.AppId);
        }
        catch (Exception ex)
        {
            if (settings.Json)
            {
                CliOutput.WriteJsonError(ex.Message, "error");
                return -1;
            }

            AnsiConsole.MarkupLine($"[red]Error:[/] {ex.Message}");
            return -1;
        }

        if (!string.IsNullOrWhiteSpace(settings.FilterType))
        {
            list = list.Where(x => string.Equals(x.Type, settings.FilterType, StringComparison.OrdinalIgnoreCase)).ToList();
        }

        if (settings.Json)
        {
            CliOutput.WriteJson(new AvailableJsonResult(settings.AppId, title, settings.FilterType, list));
            return 0;
        }

        if (list.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No artworks found.[/]");
            return 0;
        }

        var table = new Table().Border(TableBorder.Rounded);
        table.AddColumn("Type");
        table.AddColumn("Variant");
        table.AddColumn("Language");
        table.AddColumn("URL");

        foreach (var a in list)
        {
            table.AddRow(a.Type, a.Variant, a.Language, CliOutput.FullLink(a.Url));
        }

        AnsiConsole.Write(table);
        return 0;
    }
}
