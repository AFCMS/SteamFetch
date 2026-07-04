using Spectre.Console;
using Spectre.Console.Cli;
using System.ComponentModel;

public class SingleCommand : AsyncCommand<SingleCommand.Settings>
{
    public class Settings : JsonCommandSettings
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

    private sealed record SingleJsonResult(
        uint AppId,
        string Title,
        string Type,
        string Variant,
        string Language,
        string? Url,
        bool Downloaded,
        string? OutputPath,
        string? FileUri,
        IReadOnlyList<string> Warnings,
        string Status,
        string? Error);

    protected override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        using var sk = new SteamKitInterface();

        try
        {
            var kv = await sk.GetProductInfoKV(settings.AppId);
            var title = kv?["common"]?["name"]?.Value ?? "(unknown)";

            if (!settings.Json)
            {
                CliOutput.WriteAppHeader(settings.AppId, title);
            }

            var warnings = new List<string>();
            if (string.Equals(settings.Type, "library_logo", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(settings.Variant, "logo_position", StringComparison.OrdinalIgnoreCase))
            {
                const string metadataWarning = "The 'library_logo/logo_position' is metadata, not an image.";
                warnings.Add(metadataWarning);

                if (!settings.Json)
                {
                    AnsiConsole.MarkupLine($"[yellow]{metadataWarning}[/]");
                }
            }

            var url = await sk.FetchArtworkUrl(settings.AppId, settings.Type, settings.Variant, settings.Language);
            if (string.IsNullOrWhiteSpace(url))
            {
                const string notFound = "No artwork found for the given combination.";

                if (settings.Json)
                {
                    CliOutput.WriteJson(new SingleJsonResult(
                        settings.AppId,
                        title,
                        settings.Type,
                        settings.Variant,
                        settings.Language,
                        null,
                        false,
                        null,
                        null,
                        warnings,
                        "not_found",
                        notFound));
                    return 1;
                }

                AnsiConsole.MarkupLine("[red]No artwork found for the given combination.[/]");
                AnsiConsole.MarkupLine("Tip: run [cyan]available[/] to list valid combinations.");
                return 1;
            }

            var willDownload = !string.IsNullOrWhiteSpace(settings.OutputPath);
            string? finalOutput = null;
            string? fileUri = null;

            if (willDownload)
            {
                finalOutput = CliOutput.ResolveOutputPath(settings.OutputPath!, url);
                await SteamKitInterface.DownloadAsync(url, finalOutput, cancellationToken);
                fileUri = CliOutput.ToFileUri(finalOutput);
            }

            if (settings.Json)
            {
                CliOutput.WriteJson(new SingleJsonResult(
                    settings.AppId,
                    title,
                    settings.Type,
                    settings.Variant,
                    settings.Language,
                    url,
                    willDownload,
                    finalOutput,
                    fileUri,
                    warnings,
                    "ok",
                    null));
                return 0;
            }

            var table = new Table().Border(TableBorder.Rounded);
            table.AddColumn("Type");
            table.AddColumn("Variant");
            table.AddColumn("Language");
            table.AddColumn("URL");

            var linkCell = !string.IsNullOrEmpty(fileUri)
                ? CliOutput.FullLink(fileUri)
                : CliOutput.FullLink(url);

            table.AddRow(settings.Type, settings.Variant, settings.Language, linkCell);
            AnsiConsole.Write(table);
            return 0;
        }
        catch (TimeoutException ex)
        {
            if (settings.Json)
            {
                CliOutput.WriteJsonError(ex.Message, "timeout");
                return -2;
            }

            AnsiConsole.MarkupLine($"[red]Timeout:[/] {ex.Message}");
            return -2;
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
    }
}
