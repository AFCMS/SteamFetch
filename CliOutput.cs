using Spectre.Console;
using Spectre.Console.Cli;
using System.ComponentModel;
using System.Text.Json;

public abstract class JsonCommandSettings : CommandSettings
{
    [CommandOption("--json")]
    [Description("Output JSON instead of the pretty table.")]
    public bool Json { get; set; }
}

public static class CliOutput
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    public static void WriteJson<T>(T value)
        => Console.Out.WriteLine(JsonSerializer.Serialize(value, JsonOptions));

    public static void WriteJsonError(string message, string code)
        => WriteJson(new { error = new { code, message } });

    public static void WriteAppHeader(uint appId, string title)
    {
        var panel = new Panel($"[bold]{appId} — {Markup.Escape(title)}[/]")
        {
            Border = BoxBorder.Rounded
        };

        AnsiConsole.Write(panel);
    }

    public static string FullLink(string target)
        => $"[link={target}]{Markup.Escape(target)}[/]";

    public static string ResolveOutputPath(string outputPath, string url)
    {
        if (Directory.Exists(outputPath) || outputPath.EndsWith('/') || outputPath.EndsWith('\\'))
        {
            var fileName = Path.GetFileName(new Uri(url).AbsolutePath);
            outputPath = Path.Combine(outputPath.TrimEnd('/', '\\'), fileName);
        }

        return Path.GetFullPath(outputPath);
    }

    public static string ToFileUri(string path)
        => new Uri(Path.GetFullPath(path)).AbsoluteUri;
}
