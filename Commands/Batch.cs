using Spectre.Console;
using Spectre.Console.Cli;
using System.Collections.Concurrent;
using System.ComponentModel;

public sealed class BatchCommand : AsyncCommand<BatchCommand.Settings>
{
    public class Settings : JsonCommandSettings
    {
        [CommandArgument(0, "[csv]")]
        [Description("Path to CSV file. If omitted, reads CSV from stdin.")]
        public string? CsvPath { get; set; }

        [CommandOption("--delimiter <CHAR>")]
        [Description("CSV delimiter character (default: ,)")]
        public char Delimiter { get; set; } = ',';
    }

    private sealed record Row(int LineNumber, uint AppId, string BaseSpec, string? FallbackSpec, string OutputPath);
    private sealed record BatchWarning(int LineNumber, string Message, string Row);
    private sealed record ParsedRows(IReadOnlyList<Row> Rows, IReadOnlyList<BatchWarning> Warnings);

    private sealed record BatchRowResult(
        int LineNumber,
        uint AppId,
        string AppTitle,
        string BaseSpec,
        string? FallbackSpec,
        string SelectedSpec,
        string SelectedSpecSource,
        string Type,
        string Variant,
        string Language,
        string? Url,
        string? OutputPath,
        string? FileUri,
        bool Downloaded,
        string Status,
        string? Error);

    private sealed record BatchSummary(
        int ProcessedRows,
        int SkippedRows,
        int Succeeded,
        int Failed,
        int Warnings);

    private sealed record BatchJsonResult(
        BatchSummary Summary,
        IReadOnlyList<BatchWarning> Warnings,
        IReadOnlyList<BatchRowResult> Results);

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        IReadOnlyList<string> lines;

        if (!string.IsNullOrWhiteSpace(settings.CsvPath))
        {
            if (!File.Exists(settings.CsvPath))
            {
                WriteCsvFileNotFound(settings);
                return 2;
            }

            lines = await File.ReadAllLinesAsync(settings.CsvPath, cancellationToken);
        }
        else
        {
            using var sr = new StreamReader(Console.OpenStandardInput());
            var content = await sr.ReadToEndAsync(cancellationToken);
            lines = content.Split(["\r\n", "\n"], StringSplitOptions.None);
        }

        var parsed = ParseRows(lines, settings.Delimiter);

        if (!settings.Json)
        {
            WriteWarnings(parsed.Warnings);
        }

        if (parsed.Rows.Count == 0)
        {
            if (settings.Json)
            {
                CliOutput.WriteJson(BuildJsonResult(parsed.Warnings, []));
                return 0;
            }

            AnsiConsole.MarkupLine("[yellow]No rows to process.[/]");
            return 0;
        }

        using var sk = new SteamKitInterface();

        try
        {
            await sk.PrefetchProductInfos(parsed.Rows.Select(r => r.AppId), cancellationToken);

            var results = settings.Json
                ? await ProcessRowsAsync(parsed.Rows, sk, cancellationToken)
                : await ProcessRowsWithTableAsync(parsed.Rows, sk, cancellationToken);

            if (settings.Json)
            {
                CliOutput.WriteJson(BuildJsonResult(parsed.Warnings, results));
            }

            return 0;
        }
        catch (TimeoutException ex)
        {
            WriteError(settings, ex.Message, "timeout");
            return -2;
        }
        catch (Exception ex)
        {
            WriteError(settings, ex.Message, "error");
            return -1;
        }
    }

    private static void WriteCsvFileNotFound(Settings settings)
    {
        var message = $"CSV file not found: {settings.CsvPath}";

        if (settings.Json)
        {
            CliOutput.WriteJsonError(message, "csv_file_not_found");
            return;
        }

        AnsiConsole.MarkupLine($"[red]CSV file not found:[/] {Markup.Escape(settings.CsvPath!)}");
    }

    private static void WriteError(Settings settings, string message, string code)
    {
        if (settings.Json)
        {
            CliOutput.WriteJsonError(message, code);
            return;
        }

        AnsiConsole.MarkupLine($"[red]Error:[/] {Markup.Escape(message)}");
    }

    private static ParsedRows ParseRows(IReadOnlyList<string> lines, char delimiter)
    {
        var rows = new List<Row>();
        var warnings = new List<BatchWarning>();

        for (var index = 0; index < lines.Count; index++)
        {
            var raw = lines[index];
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            var cols = line.Split(delimiter);
            var lineNumber = index + 1;

            if (cols.Length != 4)
            {
                warnings.Add(new BatchWarning(
                    lineNumber,
                    $"Expected 4 columns: AppId{delimiter}BaseSpec{delimiter}FallbackSpec{delimiter}OutputPath.",
                    line));
                continue;
            }

            if (!uint.TryParse(cols[0], out var appId))
            {
                warnings.Add(new BatchWarning(lineNumber, "Invalid AppID.", line));
                continue;
            }

            var outputPath = cols[3].Trim();
            if (string.IsNullOrWhiteSpace(outputPath))
            {
                warnings.Add(new BatchWarning(lineNumber, "OutputPath is required.", line));
                continue;
            }

            var baseSpec = cols[1].Trim();
            var fallbackRaw = cols[2].Trim();
            var fallbackSpec = string.IsNullOrWhiteSpace(fallbackRaw) ? null : fallbackRaw;

            rows.Add(new Row(lineNumber, appId, baseSpec, fallbackSpec, outputPath));
        }

        return new ParsedRows(rows, warnings);
    }

    private static void WriteWarnings(IReadOnlyList<BatchWarning> warnings)
    {
        foreach (var warning in warnings)
        {
            AnsiConsole.MarkupLine(
                $"[yellow]Skipping line {warning.LineNumber}: {Markup.Escape(warning.Message)}[/] {Markup.Escape(warning.Row)}");
        }
    }

    private static async Task<IReadOnlyList<BatchRowResult>> ProcessRowsAsync(
        IReadOnlyList<Row> rows,
        SteamKitInterface sk,
        CancellationToken cancellationToken)
    {
        var tasks = rows.Select(row => ProcessRowAsync(row, sk, cancellationToken));
        return await Task.WhenAll(tasks);
    }

    private static async Task<IReadOnlyList<BatchRowResult>> ProcessRowsWithTableAsync(
        IReadOnlyList<Row> rows,
        SteamKitInterface sk,
        CancellationToken cancellationToken)
    {
        var table = new Table().Border(TableBorder.Rounded);
        table.AddColumn("App");
        table.AddColumn("Type");
        table.AddColumn("Variant");
        table.AddColumn("Language");
        table.AddColumn("URL");

        var completedRows = new ConcurrentQueue<BatchRowResult>();
        var completed = 0;
        var tasks = rows.Select(async row =>
        {
            var result = await ProcessRowAsync(row, sk, cancellationToken);
            completedRows.Enqueue(result);
            Interlocked.Increment(ref completed);
            return result;
        }).ToArray();

        await AnsiConsole.Live(new Rows(table, BuildProgressMarkup(0, rows.Count)))
            .AutoClear(false)
            .StartAsync(async ctx =>
            {
                while (Volatile.Read(ref completed) < rows.Count)
                {
                    DrainCompletedRows(table, completedRows);
                    ctx.UpdateTarget(new Rows(table, BuildProgressMarkup(Volatile.Read(ref completed), rows.Count)));
                    await Task.Delay(150, cancellationToken);
                }

                DrainCompletedRows(table, completedRows);
                ctx.UpdateTarget(new Rows(table, BuildProgressMarkup(rows.Count, rows.Count)));
            });

        return await Task.WhenAll(tasks);
    }

    private static void DrainCompletedRows(Table table, ConcurrentQueue<BatchRowResult> completedRows)
    {
        while (completedRows.TryDequeue(out var result))
        {
            table.AddRow(ToTableRow(result));
        }
    }

    private static string[] ToTableRow(BatchRowResult result)
    {
        var appCell = $"{result.AppId} — {Markup.Escape(result.AppTitle)}";
        var linkCell = result.Status switch
        {
            "downloaded" when !string.IsNullOrWhiteSpace(result.FileUri) => CliOutput.FullLink(result.FileUri),
            "not_found" => "[red]no artwork found[/]",
            "download_failed" => $"[red]{Markup.Escape(result.Error ?? "download failed")}[/]",
            "error" => $"[red]{Markup.Escape(result.Error ?? "error")}[/]",
            _ when !string.IsNullOrWhiteSpace(result.Url) => CliOutput.FullLink(result.Url),
            _ => "[red]failed[/]"
        };

        return [appCell, result.Type, result.Variant, result.Language, linkCell];
    }

    private static async Task<BatchRowResult> ProcessRowAsync(
        Row row,
        SteamKitInterface sk,
        CancellationToken cancellationToken)
    {
        var appTitle = "(unknown)";
        var selectedSpec = row.BaseSpec;
        var selectedSpecSource = "base";
        var (type, variant, lang) = ParseSpec(row.BaseSpec);

        try
        {
            appTitle = await GetAppTitleAsync(sk, row.AppId, cancellationToken);
            var url = await sk.FetchArtworkUrl(row.AppId, type, variant, lang, cancellationToken);

            if (string.IsNullOrWhiteSpace(url) && !string.IsNullOrWhiteSpace(row.FallbackSpec))
            {
                selectedSpec = row.FallbackSpec!;
                selectedSpecSource = "fallback";
                (type, variant, lang) = ParseSpec(selectedSpec);
                url = await sk.FetchArtworkUrl(row.AppId, type, variant, lang, cancellationToken);
            }

            if (string.IsNullOrWhiteSpace(url))
            {
                return CreateResult(row, appTitle, selectedSpec, selectedSpecSource, type, variant, lang, null, null, null, false, "not_found", "No artwork found.");
            }

            var outputPath = CliOutput.ResolveOutputPath(row.OutputPath, url);

            try
            {
                await SteamKitInterface.DownloadAsync(url, outputPath, cancellationToken);
                return CreateResult(row, appTitle, selectedSpec, selectedSpecSource, type, variant, lang, url, outputPath, CliOutput.ToFileUri(outputPath), true, "downloaded", null);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return CreateResult(row, appTitle, selectedSpec, selectedSpecSource, type, variant, lang, url, outputPath, null, false, "download_failed", ex.Message);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return CreateResult(row, appTitle, selectedSpec, selectedSpecSource, type, variant, lang, null, null, null, false, "error", ex.Message);
        }
    }

    private static BatchRowResult CreateResult(
        Row row,
        string appTitle,
        string selectedSpec,
        string selectedSpecSource,
        string type,
        string variant,
        string language,
        string? url,
        string? outputPath,
        string? fileUri,
        bool downloaded,
        string status,
        string? error)
        => new(
            row.LineNumber,
            row.AppId,
            appTitle,
            row.BaseSpec,
            row.FallbackSpec,
            selectedSpec,
            selectedSpecSource,
            type,
            variant,
            language,
            url,
            outputPath,
            fileUri,
            downloaded,
            status,
            error);

    private static async Task<string> GetAppTitleAsync(SteamKitInterface sk, uint appId, CancellationToken cancellationToken)
    {
        var kv = await sk.GetProductInfoKV(appId, cancellationToken);
        return kv?["common"]?["name"]?.Value ?? "(unknown)";
    }

    private static (string type, string variant, string lang) ParseSpec(string spec)
    {
        var parts = spec.Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length < 2)
        {
            return (spec, "image2x", "english");
        }

        var lang = parts.Length >= 3 ? parts[2] : "english";
        return (parts[0], parts[1], lang);
    }

    private static BatchJsonResult BuildJsonResult(
        IReadOnlyList<BatchWarning> warnings,
        IReadOnlyList<BatchRowResult> results)
    {
        var succeeded = results.Count(result => result.Downloaded);
        var failed = results.Count - succeeded;
        var summary = new BatchSummary(results.Count, warnings.Count, succeeded, failed, warnings.Count);

        return new BatchJsonResult(summary, warnings, results);
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
