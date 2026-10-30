using Filee.Engines.Infrastructure;

namespace Filee.Engines.Media;

/// <summary>Tool discovery shared by the full converter and the inherited preview helpers.</summary>
internal static class FfmpegTools
{
    // Preview types remain source-compatible with upstream views, but the document product never discovers
    // or starts FFmpeg. Its converter is excluded from the product assembly altogether.
    internal static (string Ffmpeg, string Ffprobe)? Locate()
    {
#if CONVERTDESK_DOCUMENTS_ONLY
        return null;
#else
        var folder = EngineEnvironment.OwnCopyFolder("ffmpeg")
            ?? (EngineEnvironment.FindBundled("ffmpeg") is { } bundled ? EngineEnvironment.FolderWithPrograms("ffmpeg", bundled) : null)
            ?? EngineEnvironment.SystemCopyFolder("ffmpeg");
        return folder is null ? null : (Path.Combine(folder, EngineEnvironment.ProgramName("ffmpeg.exe")),
            Path.Combine(folder, EngineEnvironment.ProgramName("ffprobe.exe")));
#endif
    }
}
