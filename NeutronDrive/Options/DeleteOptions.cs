using CommandLine;

namespace NeutronDrive.Options;

[Verb("delete", HelpText = "Delete a file from Proton Drive (moves to trash).")]
internal class DeleteOptions
{
    [Option('f', "filename", Required = true, HelpText = "Name of the file to delete from Proton Drive.")]
    public string Filename { get; set; } = string.Empty;

    [Option('d', "folder", Required = false, Default = "", HelpText = "Folder in Proton Drive where the file is located. Defaults to root.")]
    public string Folder { get; set; } = string.Empty;

    [Option('v', "verbose", Required = false, Default = false, HelpText = "Enable verbose logging.")]
    public bool Verbose { get; set; }
}

