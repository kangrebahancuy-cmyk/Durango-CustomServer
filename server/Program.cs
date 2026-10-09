using System;
using System.IO;
using System.Threading;
using Durango.Online;
using Durango.Utils;
using Durango.Utils.Extensions;
using Yaml;
using Yaml.Util;

namespace DurangoServerNx;

// DurangoServerNx — server ported from the game's built-in server (nexonSRC/Durango.Online).
// Primarily supports the original Android 5.2.1 client; structure and data formats follow the original.
//
// Usage:
//   DurangoServerNx [--name <cluster name>] [--gateway-port 8190] [--game-port 8191]
//                   [--data <data directory>] [--terrains <terrain zip directory>]
//                   [--assetbundles-android <bundle directory>] [--public-host <ip/host>]
//                   [--max-players N] [--cluster-mode Offline|Online|Editable]
internal static class Program
{
    private static int _ticksPerSecond = 120;

    /// <summary>Currently running host; allows the process manager to save before exit.</summary>
    private static Host _host;

    private static int _shutdownDone;

    /// <summary>Save all state and shut down cleanly; safe to call repeatedly.</summary>
    private static void ShutdownSafely(string reason)
    {
        if (System.Threading.Interlocked.Exchange(ref _shutdownDone, 1) != 0) return;
        try
        {
            Console.WriteLine($"[boot] Shutting down server ({reason}); saving first...");
            _host?.SaveAll();
            _host?.Close();
            Console.WriteLine("[boot] Save completed successfully");
        }
        catch (Exception e)
        {
            Console.WriteLine("[boot] ⚠️ Save during shutdown failed: " + e.Message);
        }
    }

    private static int Main(string[] args)
    {
        // [Sep 5, 2026] Ctrl+C previously called Environment.Exit(0) without saving; Host.Close()
        // (which saves the world before shutdown) was never called, so each restart could lose up to 60 seconds.
        // The server now saves before Ctrl+C, normal process exit, and crashes whenever possible.
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            ShutdownSafely("Ctrl+C");
            Environment.Exit(0);
        };
        AppDomain.CurrentDomain.ProcessExit += (_, _) => ShutdownSafely("process exit");
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            Console.WriteLine("[boot] ❌ Unhandled exception: " + (args.ExceptionObject as Exception)?.Message);
            ShutdownSafely("crash");
        };
        try
        {
            // Keep console output in English for consistent Windows code-page support.
            Console.OutputEncoding = System.Text.Encoding.UTF8;
        }
        catch (Exception) { }

        // ---- CLI ----
        string name = "nx";
        int gatewayPort = Gateway.DefaultPort;   // 8190 is the port hardcoded into the mobile client.
        int gamePort = GameServer.DefaultPort;   // 8191
        string dataDir = Path.Combine(AppContext.BaseDirectory, "data");
        string androidBundles = null;
        string publicHost = null;
        int maxPlayers = 200;
        // The /health token can be supplied through an environment variable to avoid exposing it in process arguments.
        string adminToken = Environment.GetEnvironmentVariable("DURANGO_ADMIN_TOKEN");
        string admins = Environment.GetEnvironmentVariable("DURANGO_ADMINS");
        string minClientVersion = null;
        string downloadUrl = null;

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--quest-check":
                {
                    string checkData = Path.Combine(AppContext.BaseDirectory, "data");
                    while (i + 1 < args.Length)
                    {
                        if (args[i + 1] == "--data")
                        {
                            checkData = args[i + 2];
                            i += 2;
                            continue;
                        }
                        i++;
                    }
                    return QuestCatalogCheck.Run(checkData);
                }
                case "--fx-check":
                {
                    string checkData = Path.Combine(AppContext.BaseDirectory, "data");
                    while (i + 1 < args.Length)
                    {
                        if (args[i + 1] == "--data")
                        {
                            checkData = args[i + 2];
                            i += 2;
                            continue;
                        }
                        i++;
                    }
                    return LevelUpFxCheck.Run(checkData);
                }
                case "--se-check":
                {
                    string checkData = Path.Combine(AppContext.BaseDirectory, "data");
                    while (i + 1 < args.Length)
                    {
                        if (args[i + 1] == "--data")
                        {
                            checkData = args[i + 2];
                            i += 2;
                            continue;
                        }
                        i++;
                    }
                    return StatusEffectWorldCheck.Run(checkData);
                }
                case "--farm-check":
                {
                    string checkData = Path.Combine(AppContext.BaseDirectory, "data");
                    while (i + 1 < args.Length)
                    {
                        if (args[i + 1] == "--data")
                        {
                            checkData = args[i + 2];
                            i += 2;
                            continue;
                        }
                        i++;
                    }
                    return FarmHarvestCheck.Run(checkData);
                }
                case "--selftest":
                {
                    int stGateway = 18290, stGame = 18291;
                    while (i + 1 < args.Length)
                    {
                        if (args[i + 1] == "--gateway-port") stGateway = int.Parse(args[i + 2]);
                        if (args[i + 1] == "--game-port") stGame = int.Parse(args[i + 2]);
                        i++;
                    }
                    return SelfTest.Run(stGateway, stGame);
                }
                case "--probe":
                {
                    int stGateway = 18290, stGame = 18291;
                    while (i + 1 < args.Length)
                    {
                        if (args[i + 1] == "--gateway-port") stGateway = int.Parse(args[i + 2]);
                        if (args[i + 1] == "--game-port") stGame = int.Parse(args[i + 2]);
                        i++;
                    }
                    int rc = SelfTestPackages.Run(stGateway, stGame);
                    // The packet listener is not a background thread; exiting incorrectly can leave the DLL locked.
                    Environment.Exit(rc);
                    return rc;
                }
                // [Sep 7, 2026] Validate harvest tables and building menus without starting the server.
                //
                // These checks cover data-driven issues such as missing reed stalks and the campfire ignition button.
                // They concern data sent by the server to the game and can be validated directly from tables.
                // Re-run these checks whenever CollectibleTable or HandleTouchMsg changes.
                case "--check-data":
                {
                    MoCatalog.Load(dataDir);
                    DataStore.Load(dataDir);
                    WorkbenchTags.AssetsDir = Path.Combine(dataDir, "assets");
                    return DataCheck.Run();
                }
                case "--name": name = args[++i]; break;
                case "--gateway-port": gatewayPort = int.Parse(args[++i]); break;
                case "--game-port": gamePort = int.Parse(args[++i]); break;
                case "--data": dataDir = args[++i]; break;
                case "--terrains": TerrainLoader.TerrainDir = args[++i]; break;
                case "--terrain": TerrainLoader.DefaultTerrainFile = args[++i]; break;
                case "--assetbundles-android": androidBundles = args[++i]; break;
                case "--public-host": publicHost = args[++i]; break;
                case "--url-prefix":
                {
                    // [Sep 4, 2026] The mobile client appends the literal port 8190; it cannot be changed as an integer.
                    // To run this server on another port, patch the literal in the client.
                    // Use "http://ip:<actual-port>/p" and strip the "/p8190" prefix before routing. See the Android documentation.
                    string prefix = args[++i].Trim();
                    if (!prefix.StartsWith("/")) prefix = "/" + prefix;
                    Durango.Online.WebServer.PathPrefix = prefix.TrimEnd('/');
                    break;
                }
                case "--max-players": maxPlayers = int.Parse(args[++i]); break;

                // One-time migration: let the first account that connects adopt unowned characters.
                // ⚠️ Never leave this enabled when allowing public players. See Core/Host.AdoptOrphans.
                case "--adopt-orphans": Host.AdoptOrphans = true; break;

                // Administrator character entity IDs, comma-separated; only these players can use cheat commands.
                // If unset, cheat commands are disabled for everyone, which is safest for public servers.
                case "--admins": admins = args[++i]; break;
                case "--min-client-version": minClientVersion = args[++i]; break;
                case "--download-url": downloadUrl = args[++i]; break;
                case "--admin-token": adminToken = args[++i]; break;
                case "--tps": _ticksPerSecond = int.Parse(args[++i]); break;
                case "--cluster-mode":
                    Host.ClusterMode = args[++i].ToEnum(Durango.Logic.Clusters.Mode.Offline);
                    break;
                case "--help":
                case "-h":
                    Console.WriteLine("DurangoServerNx — native server port, mobile-client focused");
                    Console.WriteLine("  --quest-check [--data <dir>]  Validate the Phase 1 daily catalog (server does not need to be running)");
                    Console.WriteLine("  --fx-check [--data <dir>]     Validate level-up and category-up reward packets");
                    Console.WriteLine("  --se-check [--data <dir>]     Validate world buff rules (rain/water → wet)");
                    Console.WriteLine("  --farm-check [--data <dir>]   Validate crop harvesting flow (grows_to → inventory items)");
                    Console.WriteLine("  --name, --gateway-port, --game-port, --data, --terrains, --terrain,");
                    Console.WriteLine("  --assetbundles-android, --public-host, --url-prefix, --max-players, --tps, --cluster-mode,");
                    Console.WriteLine("  --admin-token <t>   token for /health (or DURANGO_ADMIN_TOKEN env); if unset, accessible only from localhost");
                    Console.WriteLine("  --adopt-orphans     allow the first account to adopt unowned characters (one-time migration only; never leave enabled)");
                    Console.WriteLine("  --admins <id,id>    admin character entity IDs (or DURANGO_ADMINS env); if unset, cheat commands are disabled");
                    Console.WriteLine("  --min-client-version <v>  minimum allowed client version; if unset, accept all versions");
                    Console.WriteLine("  --download-url <url>      download URL for the client; required when version gating is enabled");
                    return 0;
            }
        }

        foreach (string id in (admins ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            Player.Admins.Add(id.Trim());
        }

        Console.WriteLine("=== DurangoServerNx (native server port · mobile-client focused) ===");
        Console.WriteLine(Player.Admins.Count > 0
            ? $"[boot] {Player.Admins.Count} administrator(s) configured; only these players can use cheat commands"
            : "[boot] No admins configured (--admins); cheat commands are disabled for everyone");
        if (Host.AdoptOrphans)
        {
            Console.WriteLine("[boot] ⚠️⚠️ --adopt-orphans is enabled; the first account to connect will adopt all unowned characters");
            Console.WriteLine("[boot]      Use for one-time migration only. Disable before allowing public players.");
        }
        Console.WriteLine($"[boot] data={dataDir} terrains={TerrainLoader.TerrainDir}");

        // ---- Game data (equivalent to the client Loader) ----
        // ⚠️ Load this before DataStore, which reads JSON and initializes Gettext immediately.
        // Gettext.ToString() retrieves translations from this catalog. See Support/MoCatalog.cs.
        MoCatalog.Load(dataDir);
        DataStore.Load(dataDir);

        // Island catalog used by boat travel to list destinations from each port; load after TerrainLoader.TerrainDir.
        WorkbenchTags.AssetsDir = Path.Combine(dataDir, "assets");
        RegionCatalog.Load(Path.Combine(dataDir, "assets"));

        // ---- host + saves ----
        // AppData (.player/.world saves) is stored beside the data directory, as in the original game.
        AppData.BasePath = Path.GetFullPath(Path.Combine(dataDir, "..", "AppData-nx"));
        var host = new Host(name);
        _host = host;
        // [Sep 5, 2026] Set these values before host.Start(), because Start creates the Gateway.
        // AdminToken is passed during creation; previously --max-players was only printed and never applied.
        host.MaxPlayers = maxPlayers;
        // TCP connection limits follow the player limit; --max-players alone only limits the HTTP gateway.
        GameServer.MaxPlayersHint = maxPlayers;
        host.AdminToken = adminToken;
        host.MinClientVersion = minClientVersion;
        host.DownloadUrl = downloadUrl;
        host.Load();

        try
        {
            // Assets are game data that the client downloads over HTTP when cluster_mode = Online.
            // (client/Yaml.Util/Loader.cs:164; other modes read Resources bundled in the game.)
            host.Start(gamePort, gatewayPort, publicHost, androidBundles, Path.Combine(dataDir, "assets"), dataDir);
        }
        catch (Exception e)
        {
            Console.WriteLine($"[boot] ❌ Failed to open ports: {e.Message}");
            Console.WriteLine("       (port 8190 may require a netsh URL ACL or elevated privileges; see docs/server/ServerNx.md)");
            return 1;
        }

        Console.WriteLine($"[boot] Ready — gateway http://0.0.0.0:{gatewayPort} · game tcp:{gamePort} · " +
                          $"cluster_mode={Host.ClusterMode} · max-players={maxPlayers}");
        Console.WriteLine("[boot] Mobile clients connect to gateway port 8190 as embedded in the APK (or use --url-prefix if changed).");
        Console.WriteLine(string.IsNullOrEmpty(adminToken)
            ? "[boot] /health is restricted to 127.0.0.1 because --admin-token is not configured"
            : "[boot] /health requires ?token=… (configured through --admin-token or environment)");

        // ---- Main loop: GameManager.Update → Server.Process each frame; target is 120 TPS. ----
        int frameMs = 1000 / _ticksPerSecond;
        long lastSave = 0;
        int loopErrors = 0;
        ServerMetrics.MarkBoot();
        while (true)
        {
            // [Sep 5, 2026] Measure each tick for /health using Stopwatch.GetTimestamp().
            // It reads the CPU counter directly (around 20 ns) without allocating objects, so it is safe at 120 ticks per second.
            // Track both host.Process work time and full tick duration including sleep and saves.
            // TPS drops may come from overloaded work or a garbage-collection pause.
            long tickBegin = System.Diagnostics.Stopwatch.GetTimestamp();
            try
            {
                host.Process();
            }
            catch (Exception e)
            {
                // Errors must be visible; if failures repeat continuously, stop instead of looping in a damaged state.
                loopErrors++;
                ServerMetrics.RecordLoopError();
                Console.WriteLine($"[loop] ⚠️ Error on iteration {loopErrors}: {e}");
                if (loopErrors >= 100)
                {
                    Console.WriteLine("[loop] ❌ Too many repeated errors; shutting down server");
                    ShutdownSafely("too many repeated errors");
                    return 1;
                }
            }
            long workEnd = System.Diagnostics.Stopwatch.GetTimestamp();
            Thread.Sleep(frameMs);

            // Save the world every 60 seconds as a safety net; the original server saved immediately on each event.
            long now = Environment.TickCount64;
            if (now - lastSave > 60_000)
            {
                lastSave = now;
                host.SaveAll();
            }
            ServerMetrics.RecordTick(System.Diagnostics.Stopwatch.GetTimestamp() - tickBegin, workEnd - tickBegin);
        }
    }

    private static Durango.Logic.Clusters.Mode ToEnum(this string s, Durango.Logic.Clusters.Mode def) =>
        Enum.TryParse(s, ignoreCase: true, out Durango.Logic.Clusters.Mode v) ? v : def;
}
