using System.Text;
using Proton.Sdk;
using Proton.Sdk.Drive;

using Microsoft.AspNetCore.StaticFiles;

namespace NeutronDrive;

internal static class NeutronDrive
{
    private static async Task Main(string[] args)
    {
        const string platform = "external";
        const string appName = "neutrondrive";
        const string version = "0.1.0-alpha";
        const string appVersion = $"{platform}-drive-{appName}@{version}";

        var fileArgIndex = Array.IndexOf(args, "--file");
        if (fileArgIndex == -1 || fileArgIndex + 1 >= args.Length)
        {
            Console.WriteLine("Please provide a file path using the --file argument.");
            return;
        }
        var filePath = args[fileArgIndex + 1];

        var folderArgIndex = Array.IndexOf(args, "--folder");
        string folder;
        if (folderArgIndex == -1 || folderArgIndex + 1 >= args.Length)
        {
            Console.WriteLine("No folder specified, using root folder.");
            folder = "";
        }
        else
        {
            folder = args[folderArgIndex + 1];
        }

        Console.Write("Username: ");
        var username = Console.ReadLine();
        if (username is null || username.Length == 0)
        {
            Console.WriteLine("Please provide a username.");
            return;
        }

        Console.Write("Password: ");
        var password = Console.ReadLine();
        if (password is null || password.Length == 0)
        {
            Console.WriteLine("Please provide a password.");
            return;
        }

        var options = new ProtonClientOptions()
        {
            AppVersion = appVersion,
        };

        var sessionBeginRequest = new SessionBeginRequest()
        {
            Username = username,
            Password = password,
            Options = options
        };

        var session = await ProtonApiSession.BeginAsync(sessionBeginRequest, CancellationToken.None);

        if (session.IsWaitingForSecondFactorCode)
        {
            Console.Write("Second factor code: ");
            var secondFactorCode = Console.ReadLine();
            if (secondFactorCode is null || secondFactorCode.Length == 0)
            {
                Console.WriteLine("Please provide a password.");
                return;
            }

            await session.ApplySecondFactorCodeAsync(secondFactorCode, CancellationToken.None);
            await session.ApplyDataPasswordAsync(Encoding.UTF8.GetBytes(password), CancellationToken.None);
        }

        try
        {
            var cancellationToken = CancellationToken.None; // Remove this if you have an actual cancellation token
            var client = new ProtonDriveClient(session);
            var accountClient = new ProtonAccountClient(session);
            var address = await accountClient.GetDefaultAddressAsync(cancellationToken);

            var volumes = await client.GetVolumesAsync(cancellationToken);
            var mainVolume = volumes[0];

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
                folderNode = await client.GetNodeAsync(share.ShareId, share.RootNodeId, cancellationToken);
            }
            else
            {
                var children = client.GetFolderChildrenAsync(new NodeIdentity(share.ShareId, mainVolume.Id, share.RootNodeId), cancellationToken);
                folderNode = await children.FirstOrDefaultAsync(child => child.Name == folder, cancellationToken);
                if (folderNode is FolderNode)
                {
                    Console.WriteLine($"Folder {folder} already exists.");
                }
                else
                {
                    folderNode = await client.CreateFolderAsync(shareMetaData, new NodeIdentity(share.ShareId, mainVolume.Id, share.RootNodeId), folder, cancellationToken);
                }
            }

            var fileInfo = new FileInfo(filePath);
            var waitForUpload = await client.WaitForFileUploaderAsync(fileInfo.Length, 0, CancellationToken.None);

            var fileContent = File.OpenRead(filePath);
            var fileSamples = Array.Empty<FileSample>();
            var modificationTime = File.GetLastWriteTimeUtc(filePath);
            var onProgress = new Action<long, long>((uploaded, total) => Console.Write($"\rUploaded {uploaded} of {total} bytes"));
            var contentType = GetContentType(filePath);
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
        }
        catch (Exception e)
        {
            var token = await session.TokenCredential.GetAccessTokenAsync(CancellationToken.None);
            await ProtonApiSession.EndAsync(session.SessionId.Value, token.AccessToken, new ProtonClientOptions { AppVersion = appVersion });
            Console.WriteLine(e);
            throw;
        }
        finally
        {
            var token = await session.TokenCredential.GetAccessTokenAsync(CancellationToken.None);
            await ProtonApiSession.EndAsync(session.SessionId.Value, token.AccessToken, new ProtonClientOptions { AppVersion = appVersion });
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