using Spectre.Console.Cli;

partial class Program
{
    static async Task<int> Main(string[] args)
    {
        var app = new CommandApp();

        app.Configure(config =>
        {
            config.SetApplicationName("steam_fetch");
            config.SetApplicationVersion("0.0.1");

            config.AddCommand<BatchCommand>("batch")
                    .WithDescription("Process a CSV of appids and artwork specs, downloading each")
                    .WithExample(new[] { "batch", "apps.csv" });

            config.AddCommand<SingleCommand>("single")
                .WithDescription("Fetch a single app artwork (library capsule)")
                .WithExample(["single", "570", "library_capsule", "image2x", "english", "-o", "out/"]);

            config.AddCommand<AvailableCommand>("available")
                .WithDescription("List available artworks (types, variants, languages) for an app")
                .WithExample(["available", "570"])
                .WithExample(["available", "570", "--filter-type", "library_capsule"]);
        });

        return await app.RunAsync(args);
    }
}