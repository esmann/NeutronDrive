using System.Text;
using Microsoft.Extensions.Logging;
using Proton.Sdk;

namespace NeutronDrive;

public class AuthenticationService : IDisposable
{
    private readonly PersistentCache _persistentCache;
    private readonly PersistentSessionCache _sessionCache;
    private readonly PersistentSecretsCache _secretsCache;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger _logger;

    private const string Platform = "external";
    private const string AppName = "neutrondrive";
    private const string Version = "0.1.0-alpha";
    private const string AppVersion = $"{Platform}-drive-{AppName}@{Version}";
    private const string CacheFile = "cache.json";

    public AuthenticationService(ILoggerFactory loggerFactory)
    {
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<AuthenticationService>();
        _persistentCache = new PersistentCache(GetLocalDataPath() + CacheFile, loggerFactory.CreateLogger<PersistentCache>());
        _sessionCache = new PersistentSessionCache(_persistentCache, loggerFactory.CreateLogger<PersistentSessionCache>());
        _secretsCache = new PersistentSecretsCache(_persistentCache, loggerFactory.CreateLogger<PersistentSecretsCache>());
    }

    public async Task<ProtonApiSession> GetSession()
    {
        var options = new ProtonClientOptions()
        {
            AppVersion = AppVersion,
            SecretsCache = _secretsCache,
            LoggerFactory = _loggerFactory
        };
        
        ProtonApiSession session = null;
        
        if (_sessionCache.HasSession)
        {
            _logger.LogInformation("Found existing session and secrets cache.");
            Console.WriteLine("Found existing session and secrets cache, attempting to resume session...");

            var sessionStuff = _sessionCache.Session!;
            var sessionResumeRequest = new SessionResumeRequest()
            {
                AccessToken = sessionStuff.AccessToken,
                RefreshToken = sessionStuff.RefreshToken,
                SessionId = new SessionId(sessionStuff.Id),
                UserId = new UserId(sessionStuff.UserId!),
                Options = options
            };
            _logger.LogInformation("Session resumed successfully.");
            session = ProtonApiSession.Resume(sessionResumeRequest);
        }
        else
        {
            _logger.LogInformation("No existing session found. Starting new session.");
            Console.Write("Username: ");
            var username = Console.ReadLine();
            if (username is null || username.Length == 0)
            {
                Console.WriteLine("Please provide a username.");
                Environment.Exit(-1);
            }

            Console.Write("Password: ");
            var password = ReadPassword();
            if (password.Length == 0)
            {
                Console.WriteLine("Please provide a password.");
                Environment.Exit(-1);
            }


            var sessionBeginRequest = new SessionBeginRequest()
            {
                Username = username,
                Password = password,
                Options = options
            };

            _logger.LogInformation("Beginning new session.");
            try
            {
                session = await ProtonApiSession.BeginAsync(sessionBeginRequest, CancellationToken.None);
            }
            catch (ProtonApiException e)
            {
                Console.WriteLine(e.Message);
                Environment.Exit(-1);
            }

            if (session.IsWaitingForSecondFactorCode)
            {
                Console.Write("Second factor code: ");
                var secondFactorCode = Console.ReadLine();
                if (secondFactorCode is null || secondFactorCode.Length == 0)
                {
                    Console.WriteLine("Please provide a 2FA code.");
                    Environment.Exit(-1);
                }

                try
                {
                    await session.ApplySecondFactorCodeAsync(secondFactorCode, CancellationToken.None);
                }
                catch (ProtonApiException e)
                {
                    Console.WriteLine(e.Message);
                    Environment.Exit(-1);
                }
                _logger.LogInformation("2FA code applied.");
                await session.ApplyDataPasswordAsync(Encoding.UTF8.GetBytes(password), CancellationToken.None);
                _logger.LogInformation("Data password applied.");
                var tokens = await session.TokenCredential.GetAccessTokenAsync(CancellationToken.None);
                var sessionStuff = new SessionStuff
                {
                    Id = session.SessionId.Value,
                    AccessToken = tokens.AccessToken,
                    RefreshToken = tokens.RefreshToken,
                    UserId = session.UserId.Value,
                    Username = session.Username,
                };
                _sessionCache.SaveSession(sessionStuff);
                _logger.LogInformation("New session details saved to cache.");
            }
        }
        return session;
    }

    public void Dispose()
    {
        _secretsCache.Dispose();
        _persistentCache.Dispose();
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

    private static string ReadPassword()
    {
        var password = "";
        while (true)
        {
            var keyInfo = Console.ReadKey(intercept: true);

            if (keyInfo.Key == ConsoleKey.Enter)
            {
                Console.WriteLine();
                break;
            }
            if (keyInfo.Key == ConsoleKey.Backspace)
            {
                if (password.Length <= 0) continue;
                password = password[..^1];
                Console.Write("\b \b");
            }
            else if (!char.IsControl(keyInfo.KeyChar))
            {
                password += keyInfo.KeyChar;
                Console.Write("*");
            }
        }

        return password;
    }
}