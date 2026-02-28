using CommandLine;

namespace NeutronDrive.Options;

[Verb("upload", HelpText = "Upload a file to Proton Drive.")]
internal class UploadOptions
{
    [Option('f', "filename", Required = true, HelpText = "Path to the file to upload.")]
    public string Filename { get; set; } = string.Empty;

    [Option('d', "folder", Required = false, Default = "", HelpText = "Destination folder in Proton Drive. Defaults to root.")]
    public string Folder { get; set; } = string.Empty;

    [Option('v', "verbose", Required = false, Default = false, HelpText = "Enable verbose logging.")]
    public bool Verbose { get; set; }
}

