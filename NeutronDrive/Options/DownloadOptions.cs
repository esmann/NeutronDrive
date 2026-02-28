using CommandLine;

namespace NeutronDrive.Options;

[Verb("download", HelpText = "Download a file from Proton Drive.")]
internal class DownloadOptions
{
    [Option('f', "filename", Required = true, HelpText = "Name of the file to download from Proton Drive.")]
    public string Filename { get; set; } = string.Empty;

    [Option('d', "folder", Required = false, Default = "", HelpText = "Folder in Proton Drive where the file is located. Defaults to root.")]
    public string Folder { get; set; } = string.Empty;

    [Option('o', "output", Required = false, Default = "", HelpText = "Local output path. Defaults to the file's name in the current directory.")]
    public string Output { get; set; } = string.Empty;

    [Option('v', "verbose", Required = false, Default = false, HelpText = "Enable verbose logging.")]
    public bool Verbose { get; set; }
}

