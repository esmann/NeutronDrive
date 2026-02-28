using System.Text.RegularExpressions;
using CommandLine;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.Logging;
using NeutronDrive.Options;
using Proton.Sdk;
using Proton.Sdk.Drive;

namespace NeutronDrive;


internal static class NeutronDrive
{
    private static async Task<int> Main(string[] args)
    {
        var result = Parser.Default.ParseArguments<UploadOptions, DownloadOptions, DeleteOptions>(args);

        return await result.MapResult(
            (UploadOptions opts) => RunUploadAsync(opts),
            (DownloadOptions opts) => RunDownloadAsync(opts),
            (DeleteOptions opts) => RunDeleteAsync(opts),
            _ => Task.FromResult(1));
    }

    private static ILoggerFactory CreateLoggerFactory(bool verbose)
    {
        return LoggerFactory.Create(builder =>
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
    }

    private static async Task<int> RunUploadAsync(UploadOptions opts)
    {
        using var loggerFactory = CreateLoggerFactory(opts.Verbose);

        using var authenticationService = new AuthenticationService(loggerFactory);

        var mainLogger = loggerFactory.CreateLogger("NeutronDrive");

        mainLogger.LogInformation("Starting application.");

        var filePath = opts.Filename;
        mainLogger.LogInformation("File path specified: {FilePath}", filePath);

        var folder = opts.Folder;

        var session = await authenticationService.GetSession();

        try
        {
            var cancellationToken = CancellationToken.None;
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
            if (string.IsNullOrEmpty(folder))
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

            return 0;
        }
        catch (Exception e)
        {
            Console.WriteLine($"Something failed: {e.Message}");
            mainLogger.LogError(e, "An exception occurred: {ErrorMessage}", e.Message);
            mainLogger.LogInformation("Session ended and cache cleared.");
            throw;
        }
    }

    private static async Task<int> RunDownloadAsync(DownloadOptions opts)
    {
        using var loggerFactory = CreateLoggerFactory(opts.Verbose);

        var authenticationService = new AuthenticationService(loggerFactory);
        var mainLogger = loggerFactory.CreateLogger("NeutronDrive");

        mainLogger.LogInformation("Starting download.");

        var session = await authenticationService.GetSession();

        try
        {
            var cancellationToken = CancellationToken.None;
            var client = new ProtonDriveClient(session);

            mainLogger.LogInformation("Fetching volumes.");
            var volumes = await client.GetVolumesAsync(cancellationToken);
            var mainVolume = volumes[0];

            mainLogger.LogInformation("Fetching root share.");
            var share = await client.GetShareAsync(mainVolume.RootShareId, cancellationToken);

            // Determine which folder to search in
            var parentNodeIdentity = new NodeIdentity(share.ShareId, mainVolume.Id, share.RootNodeId);

            if (!string.IsNullOrEmpty(opts.Folder))
            {
                mainLogger.LogInformation("Looking for folder '{Folder}'.", opts.Folder);
                var rootChildren = client.GetFolderChildrenAsync(parentNodeIdentity, cancellationToken);
                var folderNode = await rootChildren.FirstOrDefaultAsync(child => child.Name == opts.Folder, cancellationToken);

                if (folderNode is not FolderNode)
                {
                    Console.WriteLine($"Folder '{opts.Folder}' not found.");
                    return 1;
                }

                parentNodeIdentity = folderNode.NodeIdentity;
                parentNodeIdentity.ShareId = share.ShareId;
            }

            mainLogger.LogInformation("Looking for file '{Filename}'.", opts.Filename);
            var children = client.GetFolderChildrenAsync(parentNodeIdentity, cancellationToken);
            var fileNode = await children.FirstOrDefaultAsync(child => child.Name == opts.Filename, cancellationToken);

            if (fileNode is not FileNode foundFile)
            {
                Console.WriteLine($"File '{opts.Filename}' not found.");
                return 1;
            }

            mainLogger.LogInformation("File found. Fetching revisions.");
            foundFile.NodeIdentity.ShareId = share.ShareId;
            var revisions = await client.GetFileRevisionsAsync(foundFile.NodeIdentity, cancellationToken);
            var activeRevision = revisions.FirstOrDefault();

            if (activeRevision is null)
            {
                Console.WriteLine("No active revision found for the file.");
                return 1;
            }

            var outputPath = string.IsNullOrEmpty(opts.Output) ? opts.Filename : opts.Output;
            mainLogger.LogInformation("Downloading to '{OutputPath}'.", outputPath);

            var onProgress = new Action<long, long>((downloaded, total) => Console.Write($"\rDownloaded {downloaded} of {total} bytes"));

            using var downloader = await client.WaitForFileDownloaderAsync(cancellationToken);
            var status = await downloader.DownloadAsync(
                foundFile.NodeIdentity,
                activeRevision,
                outputPath,
                onProgress,
                cancellationToken);

            Console.WriteLine();
            mainLogger.LogInformation("Download completed with status: {Status}.", status);
            Console.WriteLine($"File downloaded to '{outputPath}' (verification: {status}).");

            return 0;
        }
        catch (Exception e)
        {
            Console.WriteLine($"Something failed: {e.Message}");
            mainLogger.LogError(e, "An exception occurred: {ErrorMessage}", e.Message);
            throw;
        }
    }

    private static async Task<int> RunDeleteAsync(DeleteOptions opts)
    {
        using var loggerFactory = CreateLoggerFactory(opts.Verbose);

        var authenticationService = new AuthenticationService(loggerFactory);
        var mainLogger = loggerFactory.CreateLogger("NeutronDrive");

        mainLogger.LogInformation("Starting delete.");

        var session = await authenticationService.GetSession();

        try
        {
            var cancellationToken = CancellationToken.None;
            var client = new ProtonDriveClient(session);

            mainLogger.LogInformation("Fetching volumes.");
            var volumes = await client.GetVolumesAsync(cancellationToken);
            var mainVolume = volumes[0];

            mainLogger.LogInformation("Fetching root share.");
            var share = await client.GetShareAsync(mainVolume.RootShareId, cancellationToken);

            // Determine which folder to search in
            var parentNodeIdentity = new NodeIdentity(share.ShareId, mainVolume.Id, share.RootNodeId);

            if (!string.IsNullOrEmpty(opts.Folder))
            {
                mainLogger.LogInformation("Looking for folder '{Folder}'.", opts.Folder);
                var rootChildren = client.GetFolderChildrenAsync(parentNodeIdentity, cancellationToken);
                var folderNode = await rootChildren.FirstOrDefaultAsync(child => child.Name == opts.Folder, cancellationToken);

                if (folderNode is not FolderNode)
                {
                    Console.WriteLine($"Folder '{opts.Folder}' not found.");
                    return 1;
                }

                parentNodeIdentity = folderNode.NodeIdentity;
                parentNodeIdentity.ShareId = share.ShareId;
            }

            mainLogger.LogInformation("Looking for file '{Filename}'.", opts.Filename);
            var children = client.GetFolderChildrenAsync(parentNodeIdentity, cancellationToken);
            var fileNode = await children.FirstOrDefaultAsync(child => child.Name == opts.Filename, cancellationToken);

            if (fileNode is null)
            {
                Console.WriteLine($"File '{opts.Filename}' not found.");
                return 1;
            }

            mainLogger.LogInformation("Trashing file '{Filename}'.", opts.Filename);
            await client.TrashNodesAsync(parentNodeIdentity, [fileNode.NodeIdentity.NodeId], cancellationToken);

            Console.WriteLine($"File '{opts.Filename}' moved to trash.");
            mainLogger.LogInformation("File '{Filename}' trashed successfully.", opts.Filename);

            return 0;
        }
        catch (Exception e)
        {
            Console.WriteLine($"Something failed: {e.Message}");
            mainLogger.LogError(e, "An exception occurred: {ErrorMessage}", e.Message);
            throw;
        }
    }

    private static string GetContentType(string filePath)
    {
        var provider = new FileExtensionContentTypeProvider();
        if (!provider.TryGetContentType(filePath, out var contentType))
        {
            contentType = "application/octet-stream";
        }

        return contentType;
    }

}
