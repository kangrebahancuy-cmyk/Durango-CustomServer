using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Durango.Logic.Clusters;
using Durango.Utils;

namespace Durango.Online;

/// <summary>
/// [Sep 5, 2026] Centralized server health metrics exposed through <c>/health</c>.
///
/// Previously there were no metrics, so diagnosing server slowdowns required guesswork.
/// For example, a drop from 120 to 2 TPS during mobile bundle downloads was initially mistaken for other issues.
/// These metrics show whether ticks are slow, saves are stuck, and how often errors occur.
///
/// Designed to be lightweight: timings use a preallocated ring buffer with no per-tick allocations.
/// Store microseconds rather than milliseconds because normal ticks can be shorter than 1 ms.
/// Sort samples for p50/p99 only when /health is requested, not on every tick.
///
/// ⚠️ Written from the main loop and read by /health on the same loop (Gateway.Process
/// is called from Host.Process), so no lock is needed.
/// </summary>
public static class ServerMetrics
{
/// <summary>Number of historical samples; 512 samples represent about 4 seconds at 120 TPS.</summary>
    private const int SampleCount = 512;

    private static readonly double _usPerTimestamp = 1_000_000.0 / System.Diagnostics.Stopwatch.Frequency;

/// <summary>Full tick duration from one tick start to the next, including Thread.Sleep; useful for detecting TPS drops.</summary>
    private static readonly int[] _tickUs = new int[SampleCount];

/// <summary>Actual processing time spent inside host.Process; useful for detecting work that exceeds the tick budget.</summary>
    private static readonly int[] _workUs = new int[SampleCount];

    private static int _sampleAt;

    private static int _sampleFilled;

    private static long _bootAt;

    private static long _lastSaveAt;

    private static int _saveFailures;

    private static int _loopErrors;

    public static void MarkBoot() => _bootAt = Environment.TickCount64;

/// <summary>Called at the end of each main-loop tick; converts raw Stopwatch timestamps into durations.</summary>
    public static void RecordTick(long loopTimestamps, long workTimestamps)
    {
        int i = _sampleAt;
        _tickUs[i] = ToMicros(loopTimestamps);
        _workUs[i] = ToMicros(workTimestamps);
        _sampleAt = (i + 1) % SampleCount;
        if (_sampleFilled < SampleCount) _sampleFilled++;
    }

    private static int ToMicros(long timestamps)
    {
        if (timestamps <= 0) return 0;
        double us = timestamps * _usPerTimestamp;
        return us >= int.MaxValue ? int.MaxValue : (int)us;
    }

    public static void RecordSaveOk() => _lastSaveAt = Environment.TickCount64;

    public static void RecordSaveFailed() => _saveFailures++;

    public static void RecordLoopError() => _loopErrors++;

    public static long UptimeSec => _bootAt == 0 ? 0 : (Environment.TickCount64 - _bootAt) / 1000;

/// <summary>Seconds since the last successful save; -1 means no save has succeeded since startup.</summary>
    public static long LastSaveAgoSec => _lastSaveAt == 0 ? -1 : (Environment.TickCount64 - _lastSaveAt) / 1000;

    public static int SaveFailures => _saveFailures;

    public static int LoopErrors => _loopErrors;

    public static int Samples => _sampleFilled;

    public static void TickStats(out double p50, out double p99, out double max) => Stats(_tickUs, out p50, out p99, out max);

    public static void WorkStats(out double p50, out double p99, out double max) => Stats(_workUs, out p50, out p99, out max);

/// <summary>Convert recorded microseconds to milliseconds for human-readable output.</summary>
    private static void Stats(int[] src, out double p50, out double p99, out double max)
    {
        int n = _sampleFilled;
        if (n == 0)
        {
            p50 = p99 = max = 0.0;
            return;
        }
// Samples are contiguous from index 0 until the buffer fills: _sampleFilled == _sampleAt.
// Once full, old entries are overwritten; all array entries are valid and can be sorted.
        int[] sorted = new int[n];
        Array.Copy(src, sorted, n);
        Array.Sort(sorted);
        p50 = Math.Round(sorted[n / 2] / 1000.0, 2);
        p99 = Math.Round(sorted[Math.Min(n - 1, (int)(n * 0.99))] / 1000.0, 2);
        max = Math.Round(sorted[n - 1] / 1000.0, 2);
    }
}

// Replaces nexonSRC/Durango.Online/Server.cs and Servers.cs (the client-side host).
// Differences from the original are documented in docs/server/ServerNx.md:
// - Original: one slot per world (single-player offline host); here: world in slot 0 plus multiple players.
// - Cluster.OnRequestAccount in PortraitBuilder/UI is removed; /accounts is handled by Gateway.
// - .player/.world save format remains compatible with AppData/offline/{cluster}/.
public class Host
{
/// <summary>Cluster mode returned by /knock and /entry.</summary>
    ///
/// [Sep 5, 2026] Default changed from Offline to Online because this project targets online play only.
/// The value reaches the client through two paths: /entry → TitleMenuGroup.cs:895 when using Server.ConnectTo,
/// and the selected cluster on the title screen → TitleMenuUserControlBase.cs:148.
/// ⚠️ The client knows five modes (Online/Offline/Editable/SingleMode/MultiMode; see
/// client/Durango.Online/GameServer.cs:159-160), but this server port includes only the three used by the original server.
/// Values missing from the client enum will be discarded by its fallback.</summary>
    public static Mode ClusterMode = Mode.Online;

    private readonly string _clusterKey;

    private readonly List<Context> _contexts = new();

    private WorldContext _worldCtx;

    private PlayerContext _fallbackPlayer;

/// <summary>Worlds for all islands (boat travel); created after GameServer in Start().</summary>
    public WorldRegistry Worlds { get; private set; }

    public GameServer GameServer { get; private set; }

    public Gateway Gateway { get; private set; }

    public IReadOnlyList<Context> Contexts => _contexts;

    /// <summary>
/// [Sep 5, 2026] Maximum concurrent online players (--max-players); 0 or a negative value means unlimited.
/// Previously Program parsed and printed this setting but never enforced it; see Gateway /entry.
    /// </summary>
    public int MaxPlayers { get; set; }

    /// <summary>
/// Admin token for /health; empty means only the local machine can access it.
/// Set with --admin-token or DURANGO_ADMIN_TOKEN; see Gateway.IsAdminAllowed.
    /// </summary>
    public string AdminToken { get; set; }

/// <summary>Minimum accepted client version; null means all versions are accepted. See Gateway /knock.</summary>
    public string MinClientVersion { get; set; }

/// <summary>Client download URL, required when version gating is enabled.</summary>
    public string DownloadUrl { get; set; }

    public Host(string clusterKey)
    {
        _clusterKey = string.IsNullOrEmpty(clusterKey) ? "nx" : clusterKey;
    }

/// <summary>Load slots from disk (equivalent to the original Server constructor) and prepare world slot 0.</summary>
    public void Load()
    {
        string basePath = WorldContext.GetBasePath(_clusterKey);
        string[] worldFiles = AppData.GetFiles(basePath, "*.world", SearchOption.TopDirectoryOnly) ?? Array.Empty<string>();
        string[] playerFiles = AppData.GetFiles(basePath, "*.player", SearchOption.TopDirectoryOnly) ?? Array.Empty<string>();

        var playersBySlot = new Dictionary<int, PlayerContext>();
        foreach (string file in playerFiles)
        {
            PlayerContext player = PlayerContext.Load(file);
            if (player != null) playersBySlot[player.PlayerSlot] = player;
        }

        var worlds = new SortedDictionary<int, WorldContext>();
        foreach (string file in worldFiles)
        {
            WorldContext world = WorldContext.Load(file);
            if (world != null) worlds[world.PlayerSlot] = world;
        }

// World uses slot 0 (created if missing); player slots start at 1.
        if (!worlds.TryGetValue(0, out _worldCtx))
        {
            _worldCtx = new WorldContext();
            _worldCtx.Initialize(WorldContext.MakePath(0, _clusterKey));
            _worldCtx.PlayerSlot = 0;
            if (string.IsNullOrEmpty(_worldCtx.TerrainId))
            {
                _worldCtx.TerrainId = TerrainLoader.DefaultTerrainFile;
            }
            _worldCtx.Save(persistent: false);
            Console.WriteLine($"[host] Created new world (terrain {_worldCtx.TerrainId}) → {_worldCtx.Path}");
        }

        foreach (var pair in worlds)
        {
            if (pair.Key == 0) continue;
            playersBySlot.TryGetValue(pair.Key, out var player);
            if (player == null)
            {
                player = new PlayerContext();
                player.Initialize(PlayerContext.MakePath(pair.Key, _clusterKey));
                player.PlayerSlot = pair.Key;
            }
            _contexts.Add(new Context(pair.Value, player));
        }
        foreach (var pair in playersBySlot)
        {
            if (pair.Key == 0) continue;
            if (_contexts.Any(c => c.PlayerSlot == pair.Key)) continue;
            _contexts.Add(new Context(_worldCtx, pair.Value));
        }
        _contexts.Sort((a, b) => a.PlayerSlot.CompareTo(b.PlayerSlot));

// Fallback player context, following the original behavior: first slot or a new context if none exists.
        _fallbackPlayer = _contexts.FirstOrDefault()?.Player;
        if (_fallbackPlayer == null)
        {
            _fallbackPlayer = CreatePlayerContext(NextSlot());
            _contexts.Add(new Context(_worldCtx, _fallbackPlayer));
        }

            Console.WriteLine($"[host] Cluster '{_clusterKey}': loaded {_contexts.Count} player slots from {AppData.CombinePath(basePath)}");
// Ban lists are stored beside each cluster's save files; each cluster has its own list.
        BanList.Load(System.IO.Path.Combine(AppData.CombinePath(basePath), "bans.json"));
    }

    public void Start(int gamePort, int gatewayPort, string publicHost, string androidBundlesDir, string assetsDir, string dataDir = null)
    {
        GameServer = new GameServer(_worldCtx, _fallbackPlayer);
// The original starting-island world (0.world) is reused as the first island in the catalog.
        Worlds = new WorldRegistry(_clusterKey, GameServer.World, _worldCtx?.TerrainId);
        GameServer.Worlds = Worlds;
// Register loaded private islands before any player travels to them.
        foreach (Context context in _contexts)
        {
            PlayerContext pc = context.Player;
            if (pc != null && !string.IsNullOrEmpty(pc.PersonalRegionId) && !string.IsNullOrEmpty(pc.PersonalRegionTemplateId))
            {
                Worlds.RegisterPersonalRegion(pc.PersonalRegionId, pc.PersonalRegionTemplateId);
            }
        }
        GameServer.Start(gamePort);
        foreach (Context context in _contexts)
        {
            GameServer.Register(context.Player);
        }
        Gateway = new Gateway(this, GameServer, _worldCtx, _fallbackPlayer)
        {
            PublicHost = publicHost,
            AssetBundleAndroidDir = androidBundlesDir,
            AssetsDir = assetsDir,
            AdminToken = this.AdminToken,
            MinClientVersion = this.MinClientVersion,
            DownloadUrl = this.DownloadUrl,
            DataDir = dataDir
        };
        Gateway.Start(gatewayPort);
    }

    public void Process()
    {
        Gateway?.Process();
        GameServer?.Process();
    }

    public void Close()
    {
        Gateway?.Close();
        GameServer?.Close();
// Save every world slot before shutdown (original: Ctrl+C → Server.EndServer → World.Save).
        _worldCtx?.Save(persistent: false);
    }

    /// <summary>
/// Save all state, processing each component independently so one failure does not prevent the rest.
    ///
/// [Local fix, Sep 5, 2026] Previously there was no try/catch; if saving the first player failed (full disk or locked file),
/// no other players were saved and the exception bubbled into the main loop as an unrelated loop error.
/// Save failures are now counted separately and exposed through /health.
    /// </summary>
    public void SaveAll()
    {
        bool ok = true;
        try
        {
            _worldCtx?.Save(persistent: false);
        }
        catch (Exception e)
        {
            ok = false;
                Console.WriteLine("[save] ⚠️ Failed to save the main world: " + e.Message);
        }
        try
        {
            Worlds?.SaveAll();
        }
        catch (Exception e)
        {
            ok = false;
                Console.WriteLine("[save] ⚠️ Failed to save an island world: " + e.Message);
        }
        foreach (Context context in _contexts)
        {
            try
            {
                context.Player.Save();
            }
            catch (Exception e)
            {
                ok = false;
                Console.WriteLine($"[save] ⚠️ Failed to save player slot {context.PlayerSlot}: {e.Message}");
            }
        }
        if (ok)
        {
            ServerMetrics.RecordSaveOk();
        }
        else
        {
            ServerMetrics.RecordSaveFailed();
        }
    }

/// <summary>Cached reference to the player list in World; resolved once for PlayersOnline.</summary>
    private static System.Reflection.FieldInfo _worldPlayersField;

    private static bool _worldPlayersFieldMissing;

    /// <summary>
/// Count players currently in the world across all islands; returns -1 when the count cannot be read.
    ///
/// ⚠️ Reflection is used because World stores players in private readonly List&lt;Player&gt; _players
/// (Core/World.cs:44), while GameServer stores _connections as private (Core/GameServer.cs:27).
/// Both files are outside the scope of this change.
/// A cleaner solution would expose public int PlayerCount => _players.Count in World.cs
/// and use that property instead. This code only reads and caches FieldInfo; it does not modify state.
/// Cost is one field read and Count per island, and it runs only when /health is requested.
    ///
/// Thread safety: the list is modified by the main loop, and /health and /entry also run on that loop.
    /// </summary>
    public int PlayersOnline()
    {
        if (_worldPlayersFieldMissing) return -1;
        if (_worldPlayersField == null)
        {
            _worldPlayersField = typeof(World).GetField("_players",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            if (_worldPlayersField == null)
            {
                _worldPlayersFieldMissing = true;
                Console.WriteLine("[health] Could not count online players; World._players field was not found (renamed?)");
                return -1;
            }
        }
        try
        {
            int total = 0;
            if (Worlds != null)
            {
                foreach (KeyValuePair<string, World> kv in Worlds.Loaded)
                {
                    if (_worldPlayersField.GetValue(kv.Value) is System.Collections.ICollection players)
                    {
                        total += players.Count;
                    }
                }
            }
            else if (GameServer?.World != null && _worldPlayersField.GetValue(GameServer.World) is System.Collections.ICollection one)
            {
                total = one.Count;
            }
            return total;
        }
        catch (Exception)
        {
            return -1;
        }
    }

/// <summary>Number of island worlds currently in memory; worlds are created lazily when players arrive.</summary>
    public int WorldsLoaded()
    {
        if (Worlds == null) return GameServer?.World != null ? 1 : 0;
        int n = 0;
        foreach (KeyValuePair<string, World> _ in Worlds.Loaded) n++;
        return n;
    }

    public PlayerContext FindContextByEntityId(string entityId) =>
        _contexts.FirstOrDefault(c => c.EntityId == entityId)?.Player;

    public int NextSlot()
    {
        int max = 0;
        foreach (Context context in _contexts)
        {
            if (context.PlayerSlot > max) max = context.PlayerSlot;
        }
        return max + 1;
    }

/// <summary>Create a temporary context without saving it; it becomes a real slot when /players or world changes require persistence.</summary>
    public PlayerContext CreateTemporaryContext(string entityId, string name, int? level)
    {
        var context = new PlayerContext();
        context.Initialize(null);
        if (!string.IsNullOrEmpty(entityId))
        {
            context.PlayerInfo.PlayerEntityId = entityId;
            context.AppearPlayer.EntityId = entityId;
            context.AppearPlayer.Title.EntityId = entityId;
            context.AppearPlayer.Member.EntityId = entityId;
            context.AppearPlayer.Move.EntityId = entityId;
            context.AppearPlayer.Survival.EntityId = entityId;
        }
        if (!string.IsNullOrEmpty(name))
        {
            context.PlayerInfo.PlayerName = name;
            context.AppearPlayer.Name = name;
        }
        if (level is > 0)
        {
            context.PlayerInfo.PlayerLevel = level.Value;
            context.AppearPlayer.Level = level.Value;
        }
        return context;
    }

/// <summary>Promote a temporary context to a persistent on-disk player slot; called by /players.</summary>
    public PlayerContext PersistAsSlot(PlayerContext context)
    {
if (!string.IsNullOrEmpty(context.Path)) return context; // Already a persistent slot.
        int slot = NextSlot();
        context.PlayerSlot = slot;
        context.Initialize(PlayerContext.MakePath(slot, _clusterKey));
        context.Save();
        _contexts.Add(new Context(_worldCtx, context));
            Console.WriteLine($"[host] Player '{context.PlayerInfo.PlayerName}' ({context.EntityId}) → slot {slot}");
        return context;
    }

    private PlayerContext CreatePlayerContext(int slot)
    {
        var context = new PlayerContext();
        context.Initialize(PlayerContext.MakePath(slot, _clusterKey));
        context.PlayerSlot = slot;
        context.Save();
        return context;
    }

    /// <summary>
/// [Sep 5, 2026] Allow the first connecting account to adopt unowned characters.
    ///
/// An unowned character is a save created before account ownership was introduced, so it has no <c>owner_key</c>.
/// Such characters are hidden and inaccessible by default, which is the secure behavior.
/// To reuse test-server saves, enable this switch for a one-time migration only.
    ///
/// ⚠️ Never leave this enabled for public play: the first account to connect
/// would receive every unowned character, recreating the original ownership vulnerability in a narrower form.
/// Disabled by default; the server warns on startup if the switch is enabled.
    /// </summary>
    public static bool AdoptOrphans { get; set; }

    /// <summary>
/// Maintenance mode blocks new players while allowing current players to continue.
    ///
/// Existing players are not kicked immediately, so administrators can broadcast a warning and let players leave.
/// Previously, maintenance required shutting down the entire server.
    /// </summary>
    public static bool Maintenance { get; set; }

/// <summary>List online players for the admin panel so the correct player can be kicked.</summary>
    public List<Dictionary<string, object>> DescribeOnline()
    {
        var result = new List<Dictionary<string, object>>();
        foreach (World world in WorldsOf())
        {
            foreach (Player player in world.PlayersSnapshot())
            {
                result.Add(new Dictionary<string, object>
                {
                    ["entity_id"] = player.EntityId,
                    ["name"] = player.Name ?? "",
                    ["region"] = world.TerrainId ?? "",
                    ["banned"] = BanList.IsBanned(FindContextByEntityId(player.EntityId)?.OwnerKey)
                });
            }
        }
        return result;
    }

    /// <summary>
/// Server-wide currency summary for admin-side inflation monitoring.
    ///
/// This server uses only T Stone (see Core/Player.Wallet.cs); total supply is
/// the sum of T Stone across all saved characters, not just those online.
    ///
/// ⚠️ Evaluate changes over time rather than relying on a single snapshot.
/// Inflation occurs when total balances grow faster than the player population.
/// Call this endpoint repeatedly and compare total_tstone with average.
    /// </summary>
    public Dictionary<string, object> DescribeEconomy(int topCount = 20)
    {
        var online = new HashSet<string>(StringComparer.Ordinal);
        foreach (World world in WorldsOf())
        foreach (Player player in world.PlayersSnapshot())
        {
            if (!string.IsNullOrEmpty(player.EntityId)) online.Add(player.EntityId);
        }

        var rows = new List<Dictionary<string, object>>();
        long total = 0;
        int holders = 0;
        long max = 0;
        var amounts = new List<long>();

        foreach (Context context in _contexts)
        {
            PlayerContext player = context?.Player;
            if (player == null || string.IsNullOrEmpty(player.EntityId)) continue;
            long amount = player.TStone;
            total += amount;
            amounts.Add(amount);
            if (amount > 0) holders++;
            if (amount > max) max = amount;
            rows.Add(new Dictionary<string, object>
            {
                ["entity_id"] = player.EntityId,
                ["name"] = player.AppearPlayer.Name ?? "",
                ["t_stone"] = amount,
                ["online"] = online.Contains(player.EntityId)
            });
        }

        amounts.Sort();
        long median = amounts.Count == 0 ? 0 : amounts[amounts.Count / 2];
        rows.Sort((a, b) => ((long)b["t_stone"]).CompareTo((long)a["t_stone"]));

        // ช่วงยอดเงิน — ดูการกระจุกตัว ถ้าคนไม่กี่คนถือเงินเกือบทั้งระบบแปลว่าก๊อกรั่วที่ใครบางคน
        var buckets = new Dictionary<string, int>
        {
            ["0"] = 0, ["1-999"] = 0, ["1k-9,999"] = 0,
            ["10k-99,999"] = 0, ["100k-999,999"] = 0, ["1M+"] = 0
        };
        foreach (long amount in amounts)
        {
            string key = amount switch
            {
                <= 0 => "0",
                < 1_000 => "1-999",
                < 10_000 => "1k-9,999",
                < 100_000 => "10k-99,999",
                < 1_000_000 => "100k-999,999",
                _ => "1M+"
            };
            buckets[key]++;
        }

        return new Dictionary<string, object>
        {
            ["currency"] = "TStone",
            ["total_tstone"] = total,
            ["character_count"] = rows.Count,
            ["holder_count"] = holders,
            ["online_count"] = online.Count,
            ["average"] = rows.Count == 0 ? 0 : total / rows.Count,
            ["median"] = median,
            ["max"] = max,
            ["buckets"] = buckets,
            ["top"] = rows.GetRange(0, Math.Min(topCount, rows.Count))
        };
    }

    /// <summary>
    /// ดันยอดเงินใหม่ไปให้ผู้เล่นที่ออนไลน์อยู่ — คืน false ถ้าไม่ได้ออนไลน์
    /// (ไม่ออนไลน์ก็ไม่เป็นไร เพราะยอดอยู่ในไฟล์เซฟแล้ว เดี๋ยวเข้ามาก็เห็นเอง)
    /// </summary>
    public bool PushWalletTo(string entityId)
    {
        foreach (World world in WorldsOf())
        foreach (Player player in world.PlayersSnapshot())
        {
            if (!string.Equals(player.EntityId, entityId, StringComparison.Ordinal)) continue;
            player.SendWalletNow();
            return true;
        }
        return false;
    }

    /// <summary>เตะผู้เล่นออกจากเกม — คืน false ถ้าไม่ได้ออนไลน์อยู่</summary>
    public bool KickPlayer(string entityId, string reason)
    {
        if (string.IsNullOrEmpty(entityId)) return false;
        foreach (World world in WorldsOf())
        {
            foreach (Player player in world.PlayersSnapshot())
            {
                if (player.EntityId != entityId) continue;
                Console.WriteLine($"[ดูแล] เตะ {entityId} — {reason}");
                player.KickWith(reason);
                return true;
            }
        }
        return false;
    }

    /// <summary>ประกาศถึงทุกคนที่ออนไลน์ — คืนจำนวนคนที่ได้รับ</summary>
    public int Announce(string text)
    {
        int sent = 0;
        foreach (World world in WorldsOf())
        {
            foreach (Player player in world.PlayersSnapshot())
            {
                player.SendNotice(text);
                sent++;
            }
        }
        Console.WriteLine($"[ดูแล] ประกาศถึง {sent} คน: {text}");
        return sent;
    }

    /// <summary>โลกทั้งหมดที่เปิดอยู่ (เกาะเดียวหรือหลายเกาะแล้วแต่โหมด)</summary>
    private IEnumerable<World> WorldsOf()
    {
        if (Worlds != null)
        {
            foreach (KeyValuePair<string, World> pair in Worlds.Loaded)
            {
                if (pair.Value != null) yield return pair.Value;
            }
            yield break;
        }
        if (GameServer?.World != null) yield return GameServer.World;
    }

    /// <summary>บัญชีเปล่า — ใช้ตอบคำขอที่ไม่มีกุญแจบัญชี</summary>
    public static Account EmptyAccount() => new() { PlayerSlotCount = 0, MaxPlayerSlotCount = 2 };

    /// <summary>
    /// รายชื่อตัวละคร **ของบัญชีนี้เท่านั้น** (เทียบเท่า Cluster.OnRequestAccount ต้นฉบับ)
    ///
    /// ⚠️ เดิมคืนตัวละครทุกตัวบนเซิร์ฟให้ทุกคน ⇒ ใครก็กดเข้าเล่นตัวละครคนอื่นได้จากหน้า Title
    /// (เหตุผลเต็มที่ Core/Gateway.cs เส้น /accounts)
    ///
    /// <c>MaxPlayerSlotCount</c> ต้องมากกว่าจำนวนตัวที่มีเสมอ ไม่งั้นปุ่ม "สร้างตัวใหม่" หายไป —
    /// ฝั่งเกมโชว์ปุ่มนั้นเฉพาะช่องที่ index &lt; availableSlotCount
    /// (client/Durango.UI/TitlePlayerSelectionGroupBase.cs:89-97)
    /// </summary>
    public Account BuildAccount(string ownerKey)
    {
        var account = new Account();
        if (string.IsNullOrEmpty(ownerKey)) return EmptyAccount();

        foreach (Context context in _contexts)
        {
            PlayerContext player = context.Player;

            // ตัวละครกำพร้า — รับเป็นของบัญชีแรกที่เข้ามา เฉพาะตอนเปิดสวิตช์ย้ายข้อมูล
            if (string.IsNullOrEmpty(player.OwnerKey) && AdoptOrphans)
            {
                player.OwnerKey = ownerKey;
                player.Save();
                Console.WriteLine($"[บัญชี] ตัวละครกำพร้า '{player.PlayerInfo.PlayerName}' " +
                                  $"({player.EntityId}) → บัญชี {AccountKeys.ForLog(ownerKey)}");
            }

            if (!AccountKeys.Same(player.OwnerKey, ownerKey)) continue;
            account.Players.Add(player.PlayerInfo);
        }

        account.PlayerSlotCount = account.Players.Count;
        // +1 เสมอเพื่อให้มีช่องว่างให้กดสร้างตัวใหม่ (ขั้นต่ำ 2 ตามเดิม)
        account.MaxPlayerSlotCount = Math.Max(2, account.Players.Count + 1);
        return account;
    }
}
