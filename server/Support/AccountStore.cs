using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using Newtonsoft.Json;

namespace Durango.Online;

/// <summary>
/// Persistent username/password accounts for the custom server.
/// Passwords are stored as PBKDF2-SHA256 hashes; the plaintext password is never persisted.
/// Account IDs are random bearer keys and must be kept private by the client.
/// </summary>
public static class AccountStore
{
    private const int Iterations = 210_000;
    private const int SaltBytes = 16;
    private const int HashBytes = 32;
    private const int MaxAttemptsPerMinute = 10;
    private static readonly object Sync = new();
    private static readonly Dictionary<string, Queue<DateTime>> Attempts = new(StringComparer.OrdinalIgnoreCase);

    private sealed class StoreDocument
    {
        public int Version { get; set; } = 1;
        public List<AccountRecord> Accounts { get; set; } = new();
    }

    private sealed class AccountRecord
    {
        public string Username { get; set; }
        public string UsernameNormalized { get; set; }
        public string AccountId { get; set; }
        public string Salt { get; set; }
        public string PasswordHash { get; set; }
        public DateTime CreatedUtc { get; set; }
    }

    public enum RegisterResult { Created, InvalidUsername, InvalidPassword, UsernameTaken, RateLimited, StorageError }
    public enum LoginResult { Success, InvalidCredentials, RateLimited, StorageError }

    public static bool TryConsumeAttempt(string remoteAddress)
    {
        string key = string.IsNullOrWhiteSpace(remoteAddress) ? "unknown" : remoteAddress;
        DateTime now = DateTime.UtcNow;
        lock (Sync)
        {
            if (!Attempts.TryGetValue(key, out Queue<DateTime> queue))
                Attempts[key] = queue = new Queue<DateTime>();

            while (queue.Count > 0 && (now - queue.Peek()).TotalMinutes >= 1)
                queue.Dequeue();

            if (queue.Count >= MaxAttemptsPerMinute) return false;
            queue.Enqueue(now);
            return true;
        }
    }

    public static RegisterResult Register(string username, string password, out string accountId)
    {
        accountId = null;
        username = (username ?? string.Empty).Trim();
        if (username.Length < 3 || username.Length > 24 ||
            username.Any(c => !(char.IsAsciiLetterOrDigit(c) || c == '_' || c == '-')))
            return RegisterResult.InvalidUsername;

        if (string.IsNullOrEmpty(password) || password.Length < 10 || password.Length > 128)
            return RegisterResult.InvalidPassword;

        string normalized = username.ToLowerInvariant();
        byte[] salt = RandomNumberGenerator.GetBytes(SaltBytes);
        byte[] hash = HashPassword(password, salt);
        string newAccountId = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();

        lock (Sync)
        {
            try
            {
                StoreDocument store = Load();
                if (store.Accounts.Any(a => string.Equals(a.UsernameNormalized, normalized, StringComparison.Ordinal)))
                    return RegisterResult.UsernameTaken;

                store.Accounts.Add(new AccountRecord
                {
                    Username = username,
                    UsernameNormalized = normalized,
                    AccountId = newAccountId,
                    Salt = Convert.ToBase64String(salt),
                    PasswordHash = Convert.ToBase64String(hash),
                    CreatedUtc = DateTime.UtcNow
                });
                Save(store);
                accountId = newAccountId;
                return RegisterResult.Created;
            }
            catch (Exception e)
            {
                Console.WriteLine("[account] Registration storage error: " + e.GetType().Name);
                return RegisterResult.StorageError;
            }
        }
    }

    public static LoginResult Login(string username, string password, out string accountId, out string canonicalUsername)
    {
        accountId = null;
        canonicalUsername = null;
        string normalized = (username ?? string.Empty).Trim().ToLowerInvariant();
        if (normalized.Length == 0 || string.IsNullOrEmpty(password) || password.Length > 128)
            return LoginResult.InvalidCredentials;

        lock (Sync)
        {
            try
            {
                StoreDocument store = Load();
                AccountRecord account = store.Accounts.FirstOrDefault(a =>
                    string.Equals(a.UsernameNormalized, normalized, StringComparison.Ordinal));

                // Perform a hash even for unknown users to reduce timing-based username discovery.
                byte[] salt = account == null ? new byte[SaltBytes] : Convert.FromBase64String(account.Salt);
                byte[] expected = account == null ? new byte[HashBytes] : Convert.FromBase64String(account.PasswordHash);
                byte[] actual = HashPassword(password, salt);
                bool valid = CryptographicOperations.FixedTimeEquals(expected, actual);

                if (account == null || !valid) return LoginResult.InvalidCredentials;
                accountId = account.AccountId;
                canonicalUsername = account.Username;
                return LoginResult.Success;
            }
            catch (Exception e)
            {
                Console.WriteLine("[account] Login storage error: " + e.GetType().Name);
                return LoginResult.StorageError;
            }
        }
    }

    private static byte[] HashPassword(string password, byte[] salt) =>
        Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, HashBytes);

    private static string StorePath
    {
        get
        {
            string directory = Path.Combine(Durango.Utils.AppData.BasePath, "accounts");
            Directory.CreateDirectory(directory);
            return Path.Combine(directory, "accounts.json");
        }
    }

    private static StoreDocument Load()
    {
        string path = StorePath;
        if (!File.Exists(path)) return new StoreDocument();
        StoreDocument store = JsonConvert.DeserializeObject<StoreDocument>(File.ReadAllText(path));
        if (store == null || store.Accounts == null) throw new InvalidDataException("Invalid account store");
        return store;
    }

    private static void Save(StoreDocument store)
    {
        string path = StorePath;
        string temp = path + ".tmp";
        File.WriteAllText(temp, JsonConvert.SerializeObject(store, Formatting.Indented));
        File.Move(temp, path, true);
    }
}
