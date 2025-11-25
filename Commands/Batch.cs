using Spectre.Console;
using Spectre.Console.Cli;
using System.ComponentModel;
using System.Linq;

public sealed class BatchCommand : AsyncCommand<BatchCommand.Settings>
{
    public class Settings : CommandSettings
    {
        [CommandArgument(0, "[csv]")]
        [Description("Path to CSV file. If omitted, reads CSV from stdin.")]
        public string? CsvPath { get; set; }

        [CommandOption("--delimiter <CHAR>")]
        [Description("CSV delimiter character (default: ,)")]
        public char Delimiter { get; set; } = ',';
    }

    private record Row(uint AppId, string BaseSpec, string? FallbackSpec, string OutputPath);

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        // Read CSV lines either from file or stdin
        IEnumerable<string> lines;
        if (!string.IsNullOrWhiteSpace(settings.CsvPath))
        {
            if (!File.Exists(settings.CsvPath))
            {
                AnsiConsole.MarkupLine($"[red]CSV file not found:[/] {Markup.Escape(settings.CsvPath)}");
                return 2;
            }
            lines = await File.ReadAllLinesAsync(settings.CsvPath, cancellationToken);
        }
        else
        {
            using var sr = new StreamReader(Console.OpenStandardInput());
            var content = await sr.ReadToEndAsync(cancellationToken);
            lines = content.Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries);
        }

        // Parse CSV rows
        var rows = new List<Row>();
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
                continue;
            var cols = line.Split(settings.Delimiter);

            // Require exactly 4 columns: AppId, BaseSpec, FallbackSpec (can be empty), OutputPath
            if (cols.Length != 4)
            {
                AnsiConsole.MarkupLine($"[yellow]Skipping invalid row (expected 4 columns: AppId{settings.Delimiter}BaseSpec{settings.Delimiter}FallbackSpec{settings.Delimiter}OutputPath):[/] {Markup.Escape(line)}");
                continue;
            }
            if (!uint.TryParse(cols[0], out var appId))
            {
                AnsiConsole.MarkupLine($"[yellow]Skipping row with invalid AppID:[/] {Markup.Escape(line)}");
                continue;
            }

            var baseSpec = cols[1].Trim();
            var fallbackRaw = cols[2].Trim();
            string? fallbackSpec = string.IsNullOrWhiteSpace(fallbackRaw) ? null : fallbackRaw;
            string outputPath = cols[3].Trim();

            rows.Add(new Row(appId, baseSpec, fallbackSpec, outputPath));
        }

        if (rows.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No rows to process.[/]");
            return 0;
        }

        using var sk = new SteamKitInterface();

        // Prefetch all app KVs in one go to minimize API calls
        await sk.PrefetchProductInfos(rows.Select(r => r.AppId));

        // Prepare live table
        var table = new Table().Border(TableBorder.Rounded);
        table.AddColumn("App");
        table.AddColumn("Type");
        table.AddColumn("Variant");
        table.AddColumn("Language");
        table.AddColumn("URL");

        // Track progress
        int completed = 0;

        // Launch processing tasks
        var tasks = rows.Select(async row =>
        {
            string type, variant, lang;
            (type, variant, lang) = ParseSpec(row.BaseSpec);
            string? url = await sk.FetchArtworkUrl(row.AppId, type, variant, lang);

            if (string.IsNullOrEmpty(url) && !string.IsNullOrWhiteSpace(row.FallbackSpec))
            {
                (type, variant, lang) = ParseSpec(row.FallbackSpec!);
                url = await sk.FetchArtworkUrl(row.AppId, type, variant, lang);
            }

            string appTitle = (await sk.GetProductInfoKV(row.AppId))?["common"]?["name"]?.Value ?? "(unknown)";

            string linkCell;
            string typeCell = type;
            string variantCell = variant;
            string langCell = lang;
            if (string.IsNullOrEmpty(url))
            {
                linkCell = "[red]no artwork found[/]";
            }
            else
            {
                // Resolve output path
                var output = row.OutputPath;
                if (Directory.Exists(output) || output.EndsWith("/") || output.EndsWith("\\"))
                {
                    var fileName = Path.GetFileName(new Uri(url).AbsolutePath);
                    output = Path.Combine(output.TrimEnd('/', '\\'), fileName);
                }

                try
                {
                    await SteamKitInterface.DownloadAsync(url, output);
                    var abs = Path.GetFullPath(output);
                    var fileUri = new Uri(abs).AbsoluteUri;
                    var display = Path.GetFileName(abs);
                    linkCell = $"[link={fileUri}]{Markup.Escape(display)}[/]";
                }
                catch (Exception ex)
                {
                    linkCell = $"[red]{Markup.Escape(ex.Message)}[/]";
                }
            }

            // Update live UI
            lock (table)
            {
                table.AddRow($"{row.AppId} — {Markup.Escape(appTitle)}", typeCell, variantCell, langCell, linkCell);
            }
            Interlocked.Increment(ref completed);
        }).ToList();

        // Render live table + inline progress without cursor moves
        await AnsiConsole.Live(new Rows(table, BuildProgressMarkup(0, rows.Count)))
            .AutoClear(false)
            .StartAsync(async ctx =>
            {
                var total = rows.Count;
                while (completed < total)
                {
                    ctx.UpdateTarget(new Rows(table, BuildProgressMarkup(completed, total)));
                    await Task.Delay(150);
                }
                ctx.UpdateTarget(new Rows(table, BuildProgressMarkup(total, total)));
            });

        await Task.WhenAll(tasks);
        return 0;
    }

    private static (string type, string variant, string lang) ParseSpec(string spec)
    {
        // Accept type:variant or type:variant:lang (default lang=english)
        var parts = spec.Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length < 2)
            return (spec, "image2x", "english");
        var lang = parts.Length >= 3 ? parts[2] : "english";
        return (parts[0], parts[1], lang);
    }

    private static Markup BuildProgressMarkup(int completed, int total)
    {
        total = Math.Max(total, 1);
        var ratio = completed / (double)total;
        var percentage = (int)(ratio * 100);
        var consoleWidth = Console.IsOutputRedirected ? 80 : Console.WindowWidth;
        var width = Math.Max(10, Math.Min(60, consoleWidth - 20));
        var filled = (int)(width * ratio);
        var bar = new string('█', filled) + new string('░', width - filled);
        return new Markup($"Progress: [green]{percentage,3}%[/] {bar}");
    }
}