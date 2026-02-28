using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.Logging;
using Proton.Sdk;
using Proton.Sdk.Drive;

namespace NeutronDrive;

internal static class NeutronDrive
{
    private static async Task Main(string[] args)
    {

        var verbose = args.Contains("--verbose");
        using var loggerFactory = LoggerFactory.Create(builder =>
        {
            if (!verbose) return;
            builder.AddSimpleConsole(options =>
            {
                options.IncludeScopes = true;
                options.SingleLine = true;
                options.TimestampFormat = "hh:mm:ss ";
            });
            builder.SetMinimumLevel(LogLevel.Debug);
        });

        var authenticationService = new AuthenticationService(loggerFactory);

        var mainLogger = loggerFactory.CreateLogger("NeutronDrive");

        mainLogger.LogInformation("Starting application.");

        var fileArgIndex = Array.IndexOf(args, "--file");
        if (fileArgIndex == -1 || fileArgIndex + 1 >= args.Length)
        {
            Console.WriteLine("Please provide a file path using the --file argument.");
            return;
        }
        var filePath = args[fileArgIndex + 1];
        mainLogger.LogInformation("File path specified: {FilePath}", filePath);

        var folderArgIndex = Array.IndexOf(args, "--folder");
        string folder;
        if (folderArgIndex == -1 || folderArgIndex + 1 >= args.Length)
        {
            mainLogger.LogInformation("No folder specified, using root folder.");
            folder = "";
        }
        else
        {
            folder = args[folderArgIndex + 1];
            mainLogger.LogInformation("Folder specified: {Folder}", folder);
        }

        var session = await authenticationService.GetSession();
       
        try
        {
            var cancellationToken = CancellationToken.None; // Remove this if you have an actual cancellation token
            var client = new ProtonDriveClient(session);
            var accountClient = new ProtonAccountClient(session);

            mainLogger.LogInformation("Fetching default address.");
            var address = await accountClient.GetDefaultAddressAsync(cancellationToken);

            mainLogger.LogInformation("Fetching volumes.");
            var volumes = await client.GetVolumesAsync(cancellationToken);
            var mainVolume = volumes[0];

            mainLogger.LogInformation("Fetching root share.");
            var share = await client.GetShareAsync(mainVolume.RootShareId, cancellationToken);
            var shareMetaData = new ShareMetadata
            {
                ShareId = share.ShareId,
                MembershipAddressId = address.Id,
                MembershipEmailAddress = address.EmailAddress
            };


            INode? folderNode;
            if (folder.Length == 0)
            {
                mainLogger.LogInformation("Getting root folder node.");
                folderNode = await client.GetNodeAsync(share.ShareId, share.RootNodeId, cancellationToken);
            }
            else
            {
                mainLogger.LogInformation("Checking for existence of folder '{Folder}'.", folder);
                var children = client.GetFolderChildrenAsync(new NodeIdentity(share.ShareId, mainVolume.Id, share.RootNodeId), cancellationToken);
                folderNode = await children.FirstOrDefaultAsync(child => child.Name == folder, cancellationToken);
                if (folderNode is FolderNode)
                {
                    mainLogger.LogInformation("Folder '{Folder}' already exists.", folder);
                    Console.WriteLine($"Folder {folder} already exists.");
                }
                else
                {
                    mainLogger.LogInformation("Creating folder '{Folder}'.", folder);
                    folderNode = await client.CreateFolderAsync(shareMetaData, new NodeIdentity(share.ShareId, mainVolume.Id, share.RootNodeId), folder, cancellationToken);
                    mainLogger.LogInformation("Folder '{Folder}' created.", folder);
                }
            }

            var fileInfo = new FileInfo(filePath);
            mainLogger.LogInformation("Preparing to upload file '{FileName}' of size {FileSize} bytes.", fileInfo.Name, fileInfo.Length);
            var waitForUpload = await client.WaitForFileUploaderAsync(fileInfo.Length, 0, CancellationToken.None);

            var fileContent = File.OpenRead(filePath);
            var fileSamples = Array.Empty<FileSample>();
            var modificationTime = File.GetLastWriteTimeUtc(filePath);
            var onProgress = new Action<long, long>((uploaded, total) => Console.Write($"\rUploaded {uploaded} of {total} bytes"));
            var contentType = GetContentType(filePath);
            mainLogger.LogInformation("Content type determined as: {ContentType}", contentType);

            mainLogger.LogInformation("Starting file upload.");
            await waitForUpload.UploadNewFileAsync(
                shareMetaData,
                folderNode.NodeIdentity,
                fileInfo.Name,
                contentType,
                fileContent,
                fileSamples,
                modificationTime,
                onProgress,
                CancellationToken.None);
            Console.WriteLine();
            mainLogger.LogInformation("File upload completed.");
        }
        catch (Exception e)
        {
            Console.WriteLine($"Something failed: {e.Message}");
            mainLogger.LogError(e, "An exception occurred: {ErrorMessage}", e.Message);
            mainLogger.LogInformation("Session ended and cache cleared.");
            throw;
        }

    }
    
    private static string GetContentType(string filePath)
    {
        var provider = new FileExtensionContentTypeProvider();
        if (!provider.TryGetContentType(filePath, out var contentType))
        {
            contentType = "application/octet-stream"; // Default if unknown
        }

        return contentType;
    }

}
