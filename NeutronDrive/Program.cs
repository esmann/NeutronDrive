using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.Logging;
using Proton.Sdk;
using Proton.Sdk.Drive;

namespace NeutronDrive;

internal static class NeutronDrive
{
    private static async Task Main(string[] args)
    {
        const string platform = "external";
        const string appName = "neutrondrive";
        const string version = "0.1.0-alpha";
        const string appVersion = $"{platform}-drive-{appName}@{version}";
        const string cacheFile = "cache.json";

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

        var mainLogger = loggerFactory.CreateLogger("NeutronDrive");

        mainLogger.LogInformation("Starting application.");

        var persistentCache = new PersistentCache(GetLocalDataPath() + cacheFile, loggerFactory.CreateLogger<PersistentCache>());
        var sessionCache = new PersistentSessionCache(persistentCache, loggerFactory.CreateLogger<PersistentSessionCache>());
        var secretsCache = new PersistentSecretsCache(persistentCache, loggerFactory.CreateLogger<PersistentSecretsCache>());

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

        var options = new ProtonClientOptions()
        {
            AppVersion = appVersion,
            SecretsCache = secretsCache,
            LoggerFactory = loggerFactory
        };


        ProtonApiSession session;
        if (sessionCache.HasSession)
        {
            mainLogger.LogInformation("Found existing session and secrets cache.");
            Console.WriteLine("Found existing session and secrets cache, attempting to resume session...");
            var sessionStuff = sessionCache.Session!;
            var sessionResumeRequest = new SessionResumeRequest()
            {
                AccessToken = sessionStuff.AccessToken,
                RefreshToken = sessionStuff.RefreshToken,
                SessionId = new SessionId(sessionStuff.Id),
                UserId = new UserId(sessionStuff.UserId),
                Options = options
            };
            session = ProtonApiSession.Resume(sessionResumeRequest);
            mainLogger.LogInformation("Session resumed successfully.");
        } else
        {
            mainLogger.LogInformation("No existing session found. Starting new session.");
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

            var sessionBeginRequest = new SessionBeginRequest()
            {
                Username = username,
                Password = password,
                Options = options
            };

            mainLogger.LogInformation("Beginning new session.");
            session = await ProtonApiSession.BeginAsync(sessionBeginRequest, CancellationToken.None);

            if (session.IsWaitingForSecondFactorCode)
            {
                Console.Write("Second factor code: ");
                var secondFactorCode = Console.ReadLine();
                if (secondFactorCode is null || secondFactorCode.Length == 0)
                {
                    Console.WriteLine("Please provide a 2FA code.");
                    return;
                }

                await session.ApplySecondFactorCodeAsync(secondFactorCode, CancellationToken.None);
                mainLogger.LogInformation("2FA code applied.");
                await session.ApplyDataPasswordAsync(Encoding.UTF8.GetBytes(password), CancellationToken.None);
                mainLogger.LogInformation("Data password applied.");
                var tokens = await session.TokenCredential.GetAccessTokenAsync(CancellationToken.None);
                var sessionStuff = new SessionStuff
                {
                    Id = session.SessionId.Value,
                    AccessToken = tokens.AccessToken,
                    RefreshToken = tokens.RefreshToken,
                    UserId = session.UserId.Value,
                    Username = session.Username,
                };
                sessionCache.SaveSession(sessionStuff);
                mainLogger.LogInformation("New session details saved to cache.");
            }
        }
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
            Console.WriteLine("Something failed, attempting to end session...");
            mainLogger.LogError(e, "An exception occurred: {ErrorMessage}", e.Message);
            var token = await session.TokenCredential.GetAccessTokenAsync(CancellationToken.None);
            await ProtonApiSession.EndAsync(session.SessionId.Value, token.AccessToken, new ProtonClientOptions { AppVersion = appVersion });
            secretsCache.Dispose();
            persistentCache.Clear();
            mainLogger.LogInformation("Session ended and cache cleared.");
            Console.WriteLine(e);
            throw;
        }
        finally
        {
            secretsCache.Dispose();
            persistentCache.Dispose();
            mainLogger.LogInformation("Caches disposed.");
        }

    }

    private static string GetLocalDataPath()
    {   
        var cachePath = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        if (string.IsNullOrEmpty(cachePath))
        {
            cachePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
        }
        cachePath += "/NeutronDrive/";
        if (File.Exists(cachePath.TrimEnd('/')))
        {
            throw new IOException($"Cannot create data directory: a file already exists at '{cachePath.TrimEnd('/')}'.");
        }
        if (!Directory.Exists(cachePath))
        {
            Directory.CreateDirectory(cachePath);
        }
        return cachePath;
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
