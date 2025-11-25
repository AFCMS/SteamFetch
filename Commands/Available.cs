using Spectre.Console;
using Spectre.Console.Cli;
using System.ComponentModel;

public sealed class AvailableCommand : AsyncCommand<AvailableCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<appid>")]
        [Description("Steam App ID of the game.")]
        public uint AppId { get; set; }

        [CommandOption("--filter-type <TYPE>")]
        [Description("Optional filter by artwork type (e.g., library_capsule, library_hero)")]
        public string? FilterType { get; set; }
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        using var sk = new SteamKitInterface();
        IReadOnlyList<SteamKitInterface.ArtworkVariant> list;
        try
        {
            // Fetch name first to display header
            var kv = await sk.GetProductInfoKV(settings.AppId);
            var title = kv?["common"]?["name"]?.Value ?? "(unknown)";

            var t = new Panel($"[bold]{settings.AppId} — {Markup.Escape(title)}[/]")
            {
                Border = BoxBorder.Rounded
            };
            AnsiConsole.Write(t);

            list = await sk.ListAvailableArtworks(settings.AppId);
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] {ex.Message}");
            return -1;
        }

        if (!string.IsNullOrWhiteSpace(settings.FilterType))
        {
            list = list.Where(x => string.Equals(x.Type, settings.FilterType, StringComparison.OrdinalIgnoreCase)).ToList();
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
            var basePrefix = SteamKitInterface.BuildAssetUrl(settings.AppId, string.Empty);
            var display = a.Url.StartsWith(basePrefix, StringComparison.OrdinalIgnoreCase)
                ? a.Url[basePrefix.Length..]
                : a.Url;
            var link = $"[link={a.Url}]{Markup.Escape(display)}[/]";
            table.AddRow(a.Type, a.Variant, a.Language, link);
        }

        AnsiConsole.Write(table);
        return 0;
    }
}
