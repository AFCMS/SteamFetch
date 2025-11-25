using Spectre.Console;
using Spectre.Console.Cli;
using System.ComponentModel;

public class SingleCommand : AsyncCommand<SingleCommand.Settings>
{
    public class Settings : CommandSettings
    {
        [CommandArgument(0, "<appid>")]
        [Description("Steam App ID of the game.")]
        public uint AppId { get; set; }

        [CommandArgument(1, "<type>")]
        [Description("Artwork type (e.g., library_capsule, library_hero, library_logo, library_header)")]
        public string Type { get; set; } = "library_capsule";

        [CommandArgument(2, "<variant>")]
        [Description("Artwork variant (e.g., image, image2x)")]
        public string Variant { get; set; } = "image2x";

        [CommandArgument(3, "<language>")]
        [Description("Language key (e.g., english, schinese, tchinese)")]
        public string Language { get; set; } = "english";

        [CommandOption("-o|--output <OUTPUT>")]
        [Description("Output file path or directory. If omitted, the URL is printed.")]
        public string? OutputPath { get; set; }
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        using var sk = new SteamKitInterface();

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

            // Warn for non-image metadata combinations we know aren't images
            if (string.Equals(settings.Type, "library_logo", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(settings.Variant, "logo_position", StringComparison.OrdinalIgnoreCase))
            {
                AnsiConsole.MarkupLine("[yellow]The 'library_logo/logo_position' is metadata, not an image.[/]");
            }

            // Fetch URL for the requested type/variant/language
            var url = await sk.FetchArtworkUrl(settings.AppId, settings.Type, settings.Variant, settings.Language);
            if (string.IsNullOrWhiteSpace(url))
            {
                AnsiConsole.MarkupLine("[red]No artwork found for the given combination.[/]");
                AnsiConsole.MarkupLine("Tip: run [cyan]available[/] to list valid combinations.");
                return 1;
            }

            // Determine whether to download
            var willDownload = !string.IsNullOrWhiteSpace(settings.OutputPath);
            string? finalOutput = null;

            if (willDownload)
            {
                // Resolve output path – if it's a directory, infer filename from URL
                var output = settings.OutputPath!;
                if (Directory.Exists(output) || output.EndsWith('/') || output.EndsWith('\\'))
                {
                    var fileName = System.IO.Path.GetFileName(new Uri(url).AbsolutePath);
                    output = System.IO.Path.Combine(output.TrimEnd('/', '\\'), fileName);
                }

                finalOutput = output;

                await SteamKitInterface.DownloadAsync(url, output);
            }

            // Build a table like in 'available' with a single row
            var table = new Table().Border(TableBorder.Rounded);
            table.AddColumn("Type");
            table.AddColumn("Variant");
            table.AddColumn("Language");
            table.AddColumn("URL");

            string linkCell;
            if (!string.IsNullOrEmpty(finalOutput))
            {
                var abs = Path.GetFullPath(finalOutput);
                var fileUri = new Uri(abs).AbsoluteUri; // file:///...
                var displayPath = Path.GetFileName(abs);
                linkCell = $"[link={fileUri}]{Markup.Escape(displayPath)}[/]";
            }
            else
            {
                var basePrefix = SteamKitInterface.BuildAssetUrl(settings.AppId, string.Empty);
                var display = url.StartsWith(basePrefix, StringComparison.OrdinalIgnoreCase)
                    ? url[basePrefix.Length..]
                    : url;
                linkCell = $"[link={url}]{Markup.Escape(display)}[/]";
            }

            table.AddRow(settings.Type, settings.Variant, settings.Language, linkCell);
            AnsiConsole.Write(table);
            return 0;
        }
        catch (TimeoutException ex)
        {
            AnsiConsole.MarkupLine($"[red]Timeout:[/] {ex.Message}");
            return -2;
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] {ex.Message}");
            return -1;
        }
    }
}