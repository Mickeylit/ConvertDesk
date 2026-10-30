// Usage text of the "convertdesk" command.

using System.Reflection;
using Filee.Core.Watching;

namespace Filee.Cli;

internal static class Help
{
    public static string Version =>
        typeof(Help).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "?";

    public static string Text(string? command) => command?.ToLowerInvariant() switch
    {
        "convert" => Convert,
        "watch" => Watch,
        "formats" => "Usage: convertdesk formats [<format>]\n\nLists every format, or what one format converts to.\n",
        "presets" => "Usage: convertdesk presets\n\nLists the presets saved in ConvertDesk (built-in and your own).\n",
        _ => General,
    };

    private const string General = """
        ConvertDesk: convert files offline from the command line.

        Usage:
          convertdesk convert <files or folders...> --to <format> [options]
          convertdesk convert <files or folders...> --preset <preset> [options]
          convertdesk watch <folder> --to <format> [options]
          convertdesk formats [<format>]
          convertdesk presets
          convertdesk help [<command>]
          convertdesk version

        Examples:
          convertdesk convert report.docx --to pdf
          convertdesk convert *.txt --to docx -o converted
          convertdesk convert D:\Scans --recursive --preset to-pdf
          convertdesk watch D:\Inbox --to pdf --move-originals

        Presets and engines are the ones in the ConvertDesk app. Exit codes: 0 all converted, 1 some failed,
        2 wrong arguments or a missing input, 3 nothing to convert.

        """;

    private const string Convert = """
        Usage: convertdesk convert <files or folders...> (--to <format> | --preset <preset>) [options]

        Files can be wildcards (*.docx). Folders convert the files in them in the five supported document formats.

        Options:
          -t, --to <format>      Target format, e.g. html, pdf, pptx, docx, txt (see 'convertdesk formats')
          -p, --preset <preset>  A preset from the app, by id or name (see 'convertdesk presets')
          -o, --output <folder>  Save there instead of next to the sources
          -r, --recursive        Include sub folders
              --overwrite        Replace existing output files (default: add a number)
              --skip             Leave existing output files alone
              --keep-dates       Give converted files the created and modified dates of the originals
              --json             Print the result as JSON (for scripts and other programs)
              --quiet            Print nothing but errors

        """;

    private static string Watch => FormattableString.Invariant($"""
        Usage: convertdesk watch <folder> (--to <format> | --preset <preset>) [options]

        Converts files that land in the folder until Ctrl+C. Files are converted once they are complete (no
        longer growing or open in another program). Output goes to a "converted" folder inside it unless -o is given.

        Options:
          -t, --to <format>      Target format
          -p, --preset <preset>  A preset from the app, by id or name
          -o, --output <folder>  Where converted files go (default: <folder>\converted)
          -r, --recursive        Also watch sub folders (their files keep the sub folder in the output)
              --move-originals   Move converted sources to <folder>\originals; files already waiting are
                                 converted at start
              --settle <seconds> How long a file must stay unchanged (default {FolderWatcher.DefaultSettleTime.TotalSeconds})
              --keep-dates       Give converted files the created and modified dates of the originals
              --quiet            Print nothing but errors

        """);
}
