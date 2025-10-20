# SteamFetch CLI

A .NET 9 command-line tool to discover and download Steam library artworks (capsules, heroes, logos, headers) for a given app. It uses [SteamKit2](https://github.com/SteamRE/SteamKit) to fetch app product info anonymously and constructs direct CDN URLs.

- No API key or login needed
- Supports fetching a single artwork, listing available artworks, and batch downloads from CSV

## Install / Run

You can install the dotnet tool:

```bash
# Example (when published)
dotnet tool install -g SteamFetch

# Then run
steam_fetch --help
```

## Commands

Top-level syntax:

```bash
steam_fetch [command] [options]
```

Available commands:

- `available` — List available artworks (types, variants, languages) for an app
- `single` — Fetch a single artwork for an app
- `batch` — Process a CSV of multiple app/artwork specs and download each

Use `--help` on any command to see details, e.g. `steam_fetch single --help`.

---

## available

List available artworks (types, variants, languages) for an app and show their URLs.

```bash
steam_fetch available <appid> [--filter-type <TYPE>]
```

- `<appid>`: Steam App ID (e.g., 570 for Dota 2)
- `--filter-type`: Optional type filter (e.g., `library_capsule`, `library_hero`, `library_logo`, `library_header`)

Example:

```bash
steam_fetch available 570
steam_fetch available 570 --filter-type library_capsule
```

Output shows a table with columns: Type, Variant, Language, URL. The URL is clickable in supported terminals, and the display text is shortened (relative path) when possible.

Notes:

- Non-image metadata entries under `library_logo` such as `logo_position` are filtered out.

---

## single

Fetch a single artwork by type, variant, and language. Either prints the URL or downloads the file if an output path is provided.

```bash
steam_fetch single <appid> <type> <variant> <language> [-o|--output <OUTPUT>]
```

- `<appid>`: Steam App ID
- `<type>`: Artwork type (e.g., `library_capsule`, `library_hero`, `library_logo`, `library_header`)
- `<variant>`: Artwork variant (commonly `image` or `image2x`)
- `<language>`: Language key (e.g., `english`, `schinese`, `tchinese`, `japanese`)
- `-o|--output <OUTPUT>`: File path or directory to save the image. If omitted, the URL is printed.

Examples:

```bash
# Print URL only
steam_fetch single 570 library_capsule image2x english

# Download into current directory, inferring filename from URL
steam_fetch single 570 library_capsule image2x english -o ./

# Download to a specific file
steam_fetch single 570 library_hero image english -o ./art/dota2-hero-en.jpg
```

Tips:

- If `--output` points to a directory (or ends with `/` or `\`), the filename is inferred from the URL.
- Some combinations (e.g., `library_logo/logo_position`) are metadata and not image URLs; the tool will warn you.

---

## batch

Process multiple downloads from a CSV. The CSV must have exactly 4 columns per row:

```
AppId,BaseSpec,FallbackSpec,OutputPath
```

- `AppId` — Steam App ID (unsigned integer)
- `BaseSpec` — The primary spec to try first
- `FallbackSpec` — A secondary spec to try if the base spec isn’t found. May be empty
- `OutputPath` — File or directory path to save the result

Spec syntax for BaseSpec/FallbackSpec:

```
<type>:<variant>[:<language>]
```

- Language defaults to `english` when omitted
- Common `variant` examples: `image`, `image2x`

Examples of CSV rows (comma-delimited):

```
570,library_capsule:image2x:english,,./out/
730,library_hero:image,library_capsule:image2x,./out/
440,library_logo:image2x:english,,./logos/
```

Usage:

```bash
# From a file
steam_fetch batch apps.csv

# From stdin
cat apps.csv | steam_fetch batch

# With a custom delimiter (e.g., semicolon)
steam_fetch batch apps.csv --delimiter ';'
```

Behavior:

- The tool enforces exactly 4 columns per row; invalid rows are skipped with a warning
- `FallbackSpec` may be empty. If the base spec returns no artwork, the fallback spec is tried
- If `OutputPath` is a directory (or ends with `/` or `\`), the filename is inferred from the URL
- A live table is displayed showing progress and results; clickable file links are shown when downloads succeed

---

## Discovering valid combinations

What values can you use for `<type>`, `<variant>`, and `<language>`? Use `available` to list them for a specific appid:

```bash
steam_fetch available 570
```

That output is your authoritative reference. Typical values include:

- Types: `library_capsule`, `library_hero`, `library_logo`, `library_header`
- Variants: `image`, `image2x` (varies per type)
- Languages: Steam language keys like `english`, `schinese`, `tchinese`, `japanese`, `spanish`, etc.

If a combination isn’t found, `single` and `batch` will report no artwork or try the fallback.
