using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using Durango.Utils;
using Durango.Utils.Extensions;
using Messages;
using Newtonsoft.Json.Linq;
using Shared.Region;
using Yaml;
using Yaml.Util;

namespace Durango.Online;

// Ported from nexonSRC/Durango.Online/Gateway.cs (HTTP 8190), with required mobile-client compatibility glue.
// Original routes retained: /knock, /notice, /sessions, /admission, /entry, /players, /terrains/*, and unhandled URL chunks.
// Compatibility deviations (full documentation is in docs/server/ServerNx.md):
// 1) /knock: the original points to Nexon CDNs; this server points to its own host.
//    Bundle files are served from disk; the mobile client still needs real asset bundles.
// 2) /sessions: the original accepts only the "player" field for LAN joiners; this adds session tokens and player slots.
//    Multiple players are supported while preserving the original response shape (user_id + session_token).
// 3) /entry: frontend_addresses uses --public-host or the Host header instead of a fixed 127.0.0.1.
public class Gateway
{
    public const int DefaultPort = 8190;

    private WebServer _webServer;

    private readonly GameServer _gameServer;

    private readonly WorldContext _worldCtx;

    private readonly PlayerContext _playerCtx;

    private readonly Host _host;

    public int Port { get; private set; }

    public string PublicHost { get; set; }

    public string AssetBundleAndroidDir { get; set; }

    /// <summary>
/// <summary>JSON directory served through /assets/* (usually &lt;data&gt;/assets).</summary>
    ///
/// The client loads data tables differently depending on ClusterMode; see client/Yaml.Util/Loader.cs:164.
/// Mode.Online → HTTP GameManager.GatewayUrl + "/assets/&lt;name&gt;" (served here).
/// Other modes → Resources bundled in the game at "offline/assets/&lt;name&gt;".
/// The built-in server never runs online, so this route is required when Online mode is enabled.
/// Otherwise CheckDataLoaded stalls after the loader retries each file five times.
    /// </summary>
    public string AssetsDir { get; set; }

    /// <summary>
/// Admin route token (currently used only by /health); Host supplies it during initialization.
/// Empty means localhost-only access. See <see cref="IsAdminAllowed"/>.
    /// </summary>
    public string AdminToken { get; set; }

    /// <summary>
/// Server data directory used to read and write configuration files through the admin API.
    /// </summary>
    public string DataDir { get; set; }

    /// <summary>
/// Minimum allowed client version; <c>null</c> means all versions are accepted by default.
/// Set with <c>--min-client-version</c>; see /knock for why version enforcement is disabled by default.
    /// </summary>
    public string MinClientVersion { get; set; }

/// <summary>Download link shown to players when a client update is required.</summary>
    public string DownloadUrl { get; set; }

    private string _bundleIndexAndroidCache;

    public Gateway(Host host, GameServer gameServer, WorldContext worldCtx, PlayerContext playerCtx)
    {
        Port = 8190;
        _host = host;
        _gameServer = gameServer;
        _worldCtx = worldCtx;
        _playerCtx = playerCtx;
    }

    public void Start(int port)
    {
        Port = port;
        _webServer = new WebServer(port);
        RegisterRoutes();
    }

    public void Close() => _webServer?.Close();

    public void Process() => _webServer?.Process();

    private void RegisterRoutes()
    {
        _webServer.GetRoute["/knock"] = delegate(HttpListenerRequest request, Dictionary<string, string> postData)
        {
            string platform = PlatformKey(request.QueryString.Get("platform"));

// [Sep 6, 2026] Version gate: check compatibility against the version reported by the client instead of always returning true.
            //
// Previously the endpoint always returned true and ignored the client's ?version= value.
// Older clients could connect and silently write incompatible unpacked values into save files.
            //
// The default remains accept-all because all current builds report version 5.2.1.
// Without distinct build numbers, enforcing the gate now could block compatible players.
// Enable --min-client-version to enforce it. See Program.cs.
            string clientVersion = request.QueryString.Get("version");
            bool compatible = MinClientVersion == null
                              || string.IsNullOrEmpty(clientVersion)
                              || string.CompareOrdinal(clientVersion, MinClientVersion) >= 0;
            if (!compatible)
            {
                Console.WriteLine($"[version] Rejected client version {clientVersion} (minimum required: {MinClientVersion})");
            }

            JObject jObject = new()
            {
// Original: CurrentBundleVersion.GetClientVersion() = "5.2.1".
                ["server_version"] = "5.2.1",
                ["compatible"] = compatible,
// ⚠️ Always provide a download link when compatible=false, otherwise players are stuck on the error screen.
// This was identified as a separate high-priority audit item.
                ["download_url"] = DownloadUrl ?? "",
                ["assetbundle_index_url"] = $"{RootUrl(request)}/live/{platform}/Info.5.2.1.json",
                ["assetbundle_url_root"] = $"{RootUrl(request)}/live/{platform}/"
            };
            return new WebServer.JsonResponse(jObject.ToString());
        };

        _webServer.GetRoute["/notice"] = (HttpListenerRequest request, Dictionary<string, string> _) =>
            new WebServer.JsonResponse("{}");

        _webServer.PostRoute["/sessions"] = delegate(HttpListenerRequest request, Dictionary<string, string> postData)
        {
            string remoteIp = request?.RemoteEndPoint?.Address?.ToString() ?? "?";

// [Sep 5, 2026] The client already sends an account key in account_id on every request.
// (client/Durango.System/Platform.cs:118 BuildSessionForm), but the original server always returned an empty value.
// The client is patched to return a device-specific key; see Support/AccountKeys for details.
// ⚠️ Missing key means reject the request, not allow it through; unpatched old clients must not connect.
            string ownerKey = AccountKeys.Normalize(postData.Get("account_id"));
            if (ownerKey == null)
            {
                Console.WriteLine($"[gateway] /sessions rejected {remoteIp} — missing account key (old client?)");
                return new WebServer.JsonResponse(
                    new JObject { ["error"] = "no_account_key" }.ToString(), HttpStatusCode.Unauthorized);
            }

            if (BanList.IsBanned(ownerKey))
            {
                Console.WriteLine($"[ban] Rejected {remoteIp} — account {AccountKeys.ForLog(ownerKey)} is banned");
                return new WebServer.JsonResponse(new JObject
                {
                    ["error"] = "banned",
                    ["reason"] = BanList.ReasonOf(ownerKey) ?? ""
                }.ToString(), HttpStatusCode.Forbidden);
            }

            if (Host.Maintenance)
            {
                Console.WriteLine($"[admin] Rejected {remoteIp} — maintenance mode is active");
                return new WebServer.JsonResponse(
                    new JObject { ["error"] = "maintenance" }.ToString(), HttpStatusCode.ServiceUnavailable);
            }

// Original route: LAN joiners send their own PlayerContext JSON in the "player" field.
            string player = postData.Get("player");
            PlayerContext context = null;
            if (!string.IsNullOrEmpty(player))
            {
                try
                {
                    context = Json.Read<PlayerContext>(player);
context?.Initialize(null); // Path = null means no save until /players promotes this context to a real slot.
                }
                catch (Exception e)
                {
                    Console.WriteLine("[gateway] /sessions player parse failed: " + e.Message);
                }
            }

            if (context == null)
            {
// Original behavior used _playerCtx (the host) when player was absent; this server has no host, so it creates a temporary context.
                context = _host.CreateTemporaryContext(null, null, null);
            }
            else if (_host.FindContextByEntityId(context.EntityId) is { } known)
            {
// ⚠️ Previous vulnerability: trusted a caller-supplied entity ID and issued a token for a real on-disk slot.
// A single POST with player_info.player_entity_id set to a victim's ID could take over that character.
// The caller must now own the character before its real slot can be used.
                if (AccountKeys.Same(ownerKey, known.OwnerKey))
                {
context = known;   // The caller's own character; use its on-disk save.
                }
                else
                {
                    Console.WriteLine($"[gateway] /sessions {remoteIp}  attempted to use character {known.EntityId} " +
                                      $" which is not owned by account {AccountKeys.ForLog(ownerKey)} — temporary context will be used");
                    context = _host.CreateTemporaryContext(null, null, null);
                }
            }

// The temporary context belongs to the requesting account; /players saves it when the character is created.
            context.OwnerKey ??= ownerKey;

            _gameServer.Register(context);
            string token = Guid.NewGuid().ToString("N");
            _gameServer.IssueSession(context.EntityId, token, ownerKey);
            Console.WriteLine($"[gateway] /sessions {remoteIp} → {context.PlayerInfo.PlayerName} ({context.EntityId})" +
                              (string.IsNullOrEmpty(context.Path) ? " [temporary]" : ""));
            return new WebServer.JsonResponse(new JObject
            {
                ["user_id"] = context.EntityId,
                ["session_token"] = token
            }.ToString());
        };

        _webServer.GetRoute["/admission"] = (HttpListenerRequest request, Dictionary<string, string> _) =>
            new WebServer.JsonResponse(new JObject { ["admitted"] = true }.ToString());

        _webServer.GetRoute["/entry"] = delegate(HttpListenerRequest request, Dictionary<string, string> _)
        {
// [Sep 5, 2026] Player cap (--max-players) was previously parsed and printed but never enforced.
            //
// /entry is the last gate before the client receives the world's TCP address.
// Rejecting here prevents a character from entering the world without forcibly disconnecting anyone.
            //
// ⚠️ This is a soft gate: clients that already have frontend_addresses can still connect over TCP.
// A hard gate belongs in Auth in Core/GameServer.cs:153, outside this change's scope.
// ⚠️ If online count cannot be determined (PlayersOnline returns -1), allow entry rather than locking everyone out.
            int cap = _host.MaxPlayers;
            if (cap > 0)
            {
                int online = _host.PlayersOnline();
                if (online >= cap)
                {
                    Console.WriteLine($"[gateway] /entry rejected — server is full ({online}/{cap})");
                    return new WebServer.JsonResponse(new JObject
                    {
                        ["error"] = "server_full",
                        ["players_online"] = online,
                        ["max_players"] = cap
                    }.ToString(), HttpStatusCode.ServiceUnavailable);
                }
            }

// [Sep 5, 2026] The client selects its character here via /entry?entity_id=…&platform=…
// (client/Durango.UI/TitleMenuGroup.cs:1039-1046); auth:true means it sends an Authorization header.
// Authorization contains the session token, so remap it to that character or TCP Auth will reject the connection.
// /sessions initially issues a token for a temporary context; Online mode does not send "player".
            string entryEntity = request?.QueryString?["entity_id"];
            string entryToken = request?.Headers?["Authorization"];
            if (!string.IsNullOrEmpty(entryEntity) && _gameServer.BindSessionToEntity(entryToken, entryEntity))
            {
                Console.WriteLine($"[gateway] /entry linked session to character {entryEntity}");
            }

            string tcpHost = !string.IsNullOrEmpty(PublicHost)
                ? PublicHost
                : (request.UserHostName?.Split(':').FirstOrDefault() ?? "127.0.0.1");
// [Sep 7, 2026] Add radiotower_addresses for clan and social chat/notifications.
            //
// client/Durango.UI/TitleMenuGroup.cs:949-952 reads this key and passes it to
// SocialSystem.SetEndpoints. Without it, the endpoint list is empty and Radiotower never connects.
// As a result, ToggleClanNotification(4025),
// GetClanNotificationEnabled(4027), and ResubscribeClanChannel(24)
// registered in Player.Clan.cs can never be reached.
            //
// This server uses one connection per player instead of Nexon's separate Radiotower process.
// Point it at the game port; existing handlers can process it without another port.
            return new WebServer.JsonResponse(new JObject
            {
                ["frontend_addresses"] = new JArray($"{tcpHost}:{_gameServer.Port}"),
                ["radiotower_addresses"] = new JArray($"{tcpHost}:{_gameServer.Port}"),
                ["cluster_mode"] = Host.ClusterMode.ToString()
            }.ToString());
        };

        _webServer.PostRoute["/players"] = delegate(HttpListenerRequest request, Dictionary<string, string> postData)
        {
// Original character-creation flow: name/region/job/gender/model_info updates context and equips the job outfit.
// ⚠️ Previously, missing Authorization fell back to _playerCtx, so an unauthenticated empty POST /players
// could permanently overwrite the first slot's name, gender, appearance, and main-world TerrainId.
// A valid session is now required.
            string ownerKey = _gameServer.OwnerOfSession(request?.Headers?["Authorization"]);
            PlayerContext context = ResolveBySession(request);
            if (context == null || ownerKey == null)
            {
Console.WriteLine("[gateway] /players rejected: no valid session");
                return new WebServer.JsonResponse(
                    new JObject { ["error"] = "unauthorized" }.ToString(), HttpStatusCode.Unauthorized);
            }

// ⚠️ This route creates a new character only. A context with Path already has a saved character.
// Never overwrite it, even for the same user; a repeated request would reset the character.
            if (!string.IsNullOrEmpty(context.Path) || string.IsNullOrEmpty(context.PlayerInfo.PlayerEntityId))
            {
                context = _host.CreateTemporaryContext(null, null, null);
                _gameServer.Register(context);
            }
            context.OwnerKey = ownerKey;
            context.PlayerInfo.PlayerName = postData.Get("name");
            List<string> regionTemplateIds = Singleton<Constants>.Instance?.PersonalRegion?.RegionTemplateIds
                                             ?? new List<string> { TerrainLoader.DefaultTerrainFile };
            if (regionTemplateIds.Count == 0) regionTemplateIds.Add(TerrainLoader.DefaultTerrainFile);
            int index = UnityEngine.Random.Range(0, regionTemplateIds.Count);
            string text = postData.Get("region");
            _worldCtx.TerrainId = !regionTemplateIds.Contains(text) ? regionTemplateIds[index] : text;
            UpdateAppearPlayer(context, postData);
            string[] bodyColor = context.AppearPlayer.Display.BodyColor;
            string[] array = { "clothes_engineer", "clothes_officeworker", "clothes_student", "clothes_farmer", "clothes_waiter", "clothes_soldier", "clothes_homeworker", "clothes_jobless" };
            int value = postData.Get("job").ToInt();
            value = Math.Clamp(value, 0, array.Length - 1);
            string prototypeId = array[value];
            Item? item = Cheats.MakeItem(prototypeId, 1);
            if (item.HasValue)
            {
                Item value2 = item.Value;
// ⚠️ BodyColor can be null when model_info is omitted. The official client always sends it,
// but manually constructed requests may not; previously this caused a NullReferenceException and HTTP 500.
// It was hidden before because the route fell back to _playerCtx, which retained the previous character's color.
                if (bodyColor != null && bodyColor.Length >= 3)
                {
                    value2.ColorR = bodyColor[0];
                    value2.ColorG = bodyColor[1];
                    value2.ColorB = bodyColor[2];
                }
                context.InventoryItems.Add(value2);
                context.EquippedItems["body"] = value2.Id;
            }
            context = _host.PersistAsSlot(context);
            _worldCtx.Save();
            context.Save();
            Console.WriteLine($"[gateway] /players '{context.PlayerInfo.PlayerName}' job={prototypeId} → {context.EntityId}");
            return new WebServer.JsonResponse(new JObject { ["entity_id"] = context.EntityId }.ToString());
        };

// [Sep 5, 2026] Return character slots belonging only to the requesting account.
        //
// Previously, every server character was returned to anyone; the game turned these into selection buttons.
// (client/Durango.UI/TitlePlayerSelectionGroupBase.cs:94,120), so a second player could
// see the first player's character in their own list and enter it without exploiting anything.
// The client also automatically recommends the character most recently disconnected server-wide.
// (client/Durango.Logic.Clusters/Account.cs:34 MaxBy(DisconnectedAt)), making takeover a one-click action.
        //
// The client already sends account_id with this request (Clusters.RequestAccounts uses BuildSessionForm).
        _webServer.PostRoute["/accounts"] = delegate(HttpListenerRequest request, Dictionary<string, string> postData)
        {
            string key = AccountKeys.Normalize(postData.Get("account_id"));
            if (key == null)
            {
// No account key means no account and therefore no characters; never expose every slot.
                return new WebServer.JsonResponse(Json.Write(Host.EmptyAccount()));
            }
            return new WebServer.JsonResponse(Json.Write(_host.BuildAccount(key)));
        };

// [Sep 5, 2026] /health exposes server health metrics for administrators; the game client does not call it.
        //
// Previously, reports of server lag could not be diagnosed because no metrics were available.
// Now it reports tick duration, last successful save, and error counts.
// It also lists packet types sent by the game that the server has not implemented.
        //
// ⚠️ This route runs on the game loop, so keep it lightweight.
// The heaviest work sorts 512 timing samples and runs only when requested.
// ══ Server administration tools ═════════════════════════════════════════════
// Audit note: there were no kick/ban/mute tools; disruptive players could only be handled by shutting down the server.
// All admin routes use the same gate as /health (--admin-token or localhost access).

// List online players before allowing an administrator to kick the correct player.
        _webServer.GetRoute["/admin/who"] = delegate(HttpListenerRequest request, Dictionary<string, string> _)
        {
            if (!IsAdminAllowed(request)) return Forbidden();
            return new WebServer.JsonResponse(Json.Write(_host.DescribeOnline()));
        };

        // Kick a player from the game without banning them; useful for stuck sessions or temporary issues.
        _webServer.PostRoute["/admin/kick"] = delegate(HttpListenerRequest request, Dictionary<string, string> postData)
        {
            if (!IsAdminAllowed(request)) return Forbidden();
            string entityId = postData.Get("entity_id");
            string reason = postData.Get("reason") ?? "Kicked by an administrator";
            bool done = _host.KickPlayer(entityId, reason);
            return new WebServer.JsonResponse(new JObject { ["kicked"] = done }.ToString());
        };

        // Ban the account and kick the player; bans are tied to account keys, not individual characters.
        _webServer.PostRoute["/admin/ban"] = delegate(HttpListenerRequest request, Dictionary<string, string> postData)
        {
            if (!IsAdminAllowed(request)) return Forbidden();
            string entityId = postData.Get("entity_id");
            string reason = postData.Get("reason") ?? "Banned by an administrator";
            PlayerContext target = _host.FindContextByEntityId(entityId);
            if (target == null || string.IsNullOrEmpty(target.OwnerKey))
            {
                return new WebServer.JsonResponse(
                    new JObject { ["error"] = "Character not found or character has no owner" }.ToString(),
                    HttpStatusCode.NotFound);
            }
            BanList.Add(target.OwnerKey, reason);
            _host.KickPlayer(entityId, reason);
            return new WebServer.JsonResponse(new JObject { ["banned"] = true }.ToString());
        };

        _webServer.PostRoute["/admin/unban"] = delegate(HttpListenerRequest request, Dictionary<string, string> postData)
        {
            if (!IsAdminAllowed(request)) return Forbidden();
            string entityId = postData.Get("entity_id");
            PlayerContext target = _host.FindContextByEntityId(entityId);
            bool done = target != null && BanList.Remove(target.OwnerKey);
            return new WebServer.JsonResponse(new JObject { ["unbanned"] = done }.ToString());
        };

        // Server-wide currency metrics for inflation monitoring; this server uses one currency. See Core/Player.Wallet.cs.
        // Compare total_tstone over time; growth faster than character count can indicate inflation.
        // Use ?top=N to return more than the default 20 top holders.
        _webServer.GetRoute["/admin/economy"] = delegate(HttpListenerRequest request, Dictionary<string, string> _)
        {
            if (!IsAdminAllowed(request)) return Forbidden();
            int top = 20;
            string raw = request.QueryString?["top"];
            if (!string.IsNullOrEmpty(raw) && int.TryParse(raw, out int parsed) && parsed > 0) top = Math.Min(parsed, 500);
            return new WebServer.JsonResponse(Json.Write(_host.DescribeEconomy(top)));
        };

        // Set one character's balance for balancing or testing.
        // Set an absolute target balance, not an increment, so repeated requests are idempotent.
        // ⚠️ This is a high-impact economy control protected by the same admin gate as ban/kick.
        //    Every change is logged so values shown in /admin/economy remain auditable.
        _webServer.PostRoute["/admin/economy/set"] = delegate(HttpListenerRequest request, Dictionary<string, string> postData)
        {
            if (!IsAdminAllowed(request)) return Forbidden();
            string entityId = postData.Get("entity_id");
            string rawAmount = postData.Get("amount");
            if (!long.TryParse(rawAmount, out long amount) || amount < 0)
            {
                return new WebServer.JsonResponse(
                    new JObject { ["error"] = "amount must be a non-negative integer" }.ToString(),
                    HttpStatusCode.BadRequest);
            }
            PlayerContext target = _host.FindContextByEntityId(entityId);
            if (target == null)
            {
                return new WebServer.JsonResponse(
                    new JObject { ["error"] = "Character not found" }.ToString(), HttpStatusCode.NotFound);
            }
            long before = target.TStone;
            target.TStone = amount;
            // ⚠️ Save explicitly; offline characters have no Player instance to trigger OnContextChanged.
            //    Without saving, the balance remains in memory and is lost when the server restarts.
            target.Save();
            Console.WriteLine($"[economy] Admin changed character {entityId} balance from {before:N0} to {amount:N0} T Stone");
            // If the character is online, immediately send the new balance or the client will keep showing the old value.
            _host.PushWalletTo(entityId);
            return new WebServer.JsonResponse(
                new JObject { ["entity_id"] = entityId, ["before"] = before, ["after"] = amount }.ToString());
        };

        _webServer.GetRoute["/admin/bans"] = delegate(HttpListenerRequest request, Dictionary<string, string> _)
        {
            if (!IsAdminAllowed(request)) return Forbidden();
            return new WebServer.JsonResponse(Json.Write(BanList.Describe()));
        };

        // Broadcast to all online players, for example before maintenance.
        _webServer.PostRoute["/admin/announce"] = delegate(HttpListenerRequest request, Dictionary<string, string> postData)
        {
            if (!IsAdminAllowed(request)) return Forbidden();
            string text = postData.Get("text");
            if (string.IsNullOrEmpty(text))
            {
                return new WebServer.JsonResponse(
                    new JObject { ["error"] = "text is required" }.ToString(), HttpStatusCode.BadRequest);
            }
            int sent = _host.Announce(text);
            return new WebServer.JsonResponse(new JObject { ["sent"] = sent }.ToString());
        };

        // Maintenance mode blocks new players while allowing current players to continue.
        // Existing players are not kicked immediately, allowing time for a broadcast and orderly logout.
        _webServer.PostRoute["/admin/maintenance"] = delegate(HttpListenerRequest request, Dictionary<string, string> postData)
        {
            if (!IsAdminAllowed(request)) return Forbidden();
            Host.Maintenance = postData.Get("on") == "1";
            Console.WriteLine(Host.Maintenance
                ? "[admin] Maintenance enabled; new players cannot join, current players may continue"
                : "[admin] Maintenance disabled; new players may join again");
            return new WebServer.JsonResponse(new JObject { ["maintenance"] = Host.Maintenance }.ToString());
        };

        // ══ Admin Web Tool — manage config, islands, and whitelist ═══════════════════════════════════

        // Read config.json.
        _webServer.GetRoute["/admin/config"] = delegate(HttpListenerRequest request, Dictionary<string, string> _)
        {
            if (!IsAdminAllowed(request)) return Forbidden();
            string path = Path.Combine(DataDir ?? Json.DataDir, "config.json");
            if (!File.Exists(path))
                return new WebServer.JsonResponse(new JObject { ["error"] = "config.json not found" }.ToString(), HttpStatusCode.NotFound);
            return new WebServer.JsonResponse(File.ReadAllText(path));
        };

        // Update selected key/value pairs in config.json.
        _webServer.PostRoute["/admin/config"] = delegate(HttpListenerRequest request, Dictionary<string, string> postData)
        {
            if (!IsAdminAllowed(request)) return Forbidden();
            string json = postData.Get("json");
            if (string.IsNullOrEmpty(json))
                return new WebServer.JsonResponse(new JObject { ["error"] = "json is required" }.ToString(), HttpStatusCode.BadRequest);
            // Validate JSON before writing.
            try { JObject.Parse(json); }
            catch (Exception e)
            {
                return new WebServer.JsonResponse(
                    new JObject { ["error"] = "Invalid JSON: " + e.Message }.ToString(), HttpStatusCode.BadRequest);
            }
            string path = Path.Combine(DataDir ?? Json.DataDir, "config.json");
            File.WriteAllText(path, json);
            Console.WriteLine("[admin] config.json updated");
            return new WebServer.JsonResponse(new JObject { ["saved"] = true }.ToString());
        };

        // Read config-meta.json (schema and descriptions for the admin UI).
        _webServer.GetRoute["/admin/config/meta"] = delegate(HttpListenerRequest request, Dictionary<string, string> _)
        {
            if (!IsAdminAllowed(request)) return Forbidden();
            string path = Path.Combine(DataDir ?? Json.DataDir, "config-meta.json");
            if (!File.Exists(path))
                return new WebServer.JsonResponse(new JObject { ["error"] = "config-meta.json not found" }.ToString(), HttpStatusCode.NotFound);
            return new WebServer.JsonResponse(File.ReadAllText(path));
        };

        // Read islands.json.
        _webServer.GetRoute["/admin/islands"] = delegate(HttpListenerRequest request, Dictionary<string, string> _)
        {
            if (!IsAdminAllowed(request)) return Forbidden();
            string path = Path.Combine(DataDir ?? Json.DataDir, "islands.json");
            if (!File.Exists(path))
                return new WebServer.JsonResponse(new JObject { ["error"] = "islands.json not found" }.ToString(), HttpStatusCode.NotFound);
            return new WebServer.JsonResponse(File.ReadAllText(path));
        };

        // Write islands.json.
        _webServer.PostRoute["/admin/islands"] = delegate(HttpListenerRequest request, Dictionary<string, string> postData)
        {
            if (!IsAdminAllowed(request)) return Forbidden();
            string json = postData.Get("json");
            if (string.IsNullOrEmpty(json))
                return new WebServer.JsonResponse(new JObject { ["error"] = "json is required" }.ToString(), HttpStatusCode.BadRequest);
            try { JObject.Parse(json); }
            catch (Exception e)
            {
                return new WebServer.JsonResponse(
                    new JObject { ["error"] = "Invalid JSON: " + e.Message }.ToString(), HttpStatusCode.BadRequest);
            }
            string path = Path.Combine(DataDir ?? Json.DataDir, "islands.json");
            File.WriteAllText(path, json);
            Console.WriteLine("[admin] islands.json updated");
            return new WebServer.JsonResponse(new JObject { ["saved"] = true }.ToString());
        };

        // Read whitelist.txt.
        _webServer.GetRoute["/admin/whitelist"] = delegate(HttpListenerRequest request, Dictionary<string, string> _)
        {
            if (!IsAdminAllowed(request)) return Forbidden();
            string path = Path.Combine(DataDir ?? Json.DataDir, "whitelist.txt");
            if (!File.Exists(path))
                return new WebServer.JsonResponse(new JObject { ["error"] = "whitelist.txt not found" }.ToString(), HttpStatusCode.NotFound);
            string[] lines = File.ReadAllLines(path);
            JArray arr = new();
            foreach (string line in lines)
            {
                string trimmed = line.Trim();
                if (trimmed.Length > 0 && !trimmed.StartsWith("#"))
                    arr.Add(trimmed);
            }
            return new WebServer.JsonResponse(new JObject { ["entries"] = arr }.ToString());
        };

        // Write whitelist.txt.
        _webServer.PostRoute["/admin/whitelist"] = delegate(HttpListenerRequest request, Dictionary<string, string> postData)
        {
            if (!IsAdminAllowed(request)) return Forbidden();
            string entries = postData.Get("entries");
            if (string.IsNullOrEmpty(entries))
                return new WebServer.JsonResponse(new JObject { ["error"] = "entries is required" }.ToString(), HttpStatusCode.BadRequest);
            string path = Path.Combine(DataDir ?? Json.DataDir, "whitelist.txt");
            File.WriteAllText(path, # Allowed players (entity ID or character name, one per line)\n + entries);
            Console.WriteLine("[admin] whitelist.txt ถูกอัปเดตแล้ว");
            return new WebServer.JsonResponse(new JObject { ["saved"] = true }.ToString());
        };

        // อ่าน per-island config
        _webServer.GetRoute["/admin/island/config"] = delegate(HttpListenerRequest request, Dictionary<string, string> _)
        {
            if (!IsAdminAllowed(request)) return Forbidden();
            string islandId = request.QueryString.Get("id");
            if (string.IsNullOrEmpty(islandId))
                return new WebServer.JsonResponse(new JObject { ["error"] = "?id= is required" }.ToString(), HttpStatusCode.BadRequest);
            // Prevent path traversal.
            if (islandId.Contains("..") || islandId.Contains('/') || islandId.Contains('\\'))
                return new WebServer.BadRequestResponse();
            string path = Path.Combine(DataDir ?? Json.DataDir, "islands", islandId, "config.json");
            if (!File.Exists(path))
                return new WebServer.JsonResponse(new JObject { ["error"] = $"Config not found for island {islandId}" }.ToString(), HttpStatusCode.NotFound);
            return new WebServer.JsonResponse(File.ReadAllText(path));
        };

        // Write per-island configuration.
        _webServer.PostRoute["/admin/island/config"] = delegate(HttpListenerRequest request, Dictionary<string, string> postData)
        {
            if (!IsAdminAllowed(request)) return Forbidden();
            string islandId = postData.Get("id");
            string json = postData.Get("json");
            if (string.IsNullOrEmpty(islandId) || string.IsNullOrEmpty(json))
                return new WebServer.JsonResponse(new JObject { ["error"] = "id and json are required" }.ToString(), HttpStatusCode.BadRequest);
            if (islandId.Contains("..") || islandId.Contains('/') || islandId.Contains('\\'))
                return new WebServer.BadRequestResponse();
            try { JObject.Parse(json); }
            catch (Exception e)
            {
                return new WebServer.JsonResponse(
                    new JObject { ["error"] = "Invalid JSON: " + e.Message }.ToString(), HttpStatusCode.BadRequest);
            }
            string dir = Path.Combine(DataDir ?? Json.DataDir, "islands", islandId);
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, "config.json");
            File.WriteAllText(path, json);
            Console.WriteLine($"[admin] islands/{islandId}/config.json updated");
            return new WebServer.JsonResponse(new JObject { ["saved"] = true }.ToString());
        };

        // Reload config (hot reload)
        _webServer.PostRoute["/admin/reload"] = delegate(HttpListenerRequest request, Dictionary<string, string> _)
        {
            if (!IsAdminAllowed(request)) return Forbidden();
            try
            {
                DataStore.Load(DataDir ?? Json.DataDir);
                Console.WriteLine("[admin] Config reload succeeded");
                return new WebServer.JsonResponse(new JObject { ["reloaded"] = true }.ToString());
            }
            catch (Exception e)
            {
                return new WebServer.JsonResponse(
                    new JObject { ["error"] = "Reload failed: " + e.Message }.ToString(), HttpStatusCode.InternalServerError);
            }
        };

        // ══ Admin Web UI — serves admin/index.html, style.css, and app.js ══════════════════════════
        // /admin/ serves index.html; /admin/style.css serves CSS; /admin/app.js serves JavaScript.

        _webServer.GetRoute["/health"] = delegate(HttpListenerRequest request, Dictionary<string, string> _)
        {
            if (!IsAdminAllowed(request))
            {
                return new WebServer.TextResponse("text/plain", "403 Forbidden", HttpStatusCode.Forbidden);
            }

            ServerMetrics.TickStats(out double tickP50, out double tickP99, out double tickMax);
            ServerMetrics.WorkStats(out double workP50, out double workP99, out double workMax);

            // แพ็กเก็ตที่ยังไม่มี handler — ตัวนับของจริงอยู่ที่ Connection.UnhandledCounts แล้ว
            // (GameCode/Durango.Online/Connection.cs:430) เธรดรับ TCP เป็นคนเขียน ⇒ ต้องอ่านใต้ล็อกเดียวกัน
            JObject unhandled = new();
            lock (Connection.UnhandledCounts)
            {
                foreach (KeyValuePair<uint, int> kv in Connection.UnhandledCounts)
                {
                    unhandled[kv.Key.ToString()] = kv.Value;
                }
            }

            JObject health = new()
            {
                ["uptime_sec"] = ServerMetrics.UptimeSec,
                ["tick_ms"] = new JObject
                {
                    ["p50"] = tickP50,
                    ["p99"] = tickP99,
                    ["max"] = tickMax,
                    ["samples"] = ServerMetrics.Samples
                },
                // เวลาที่ใช้ทำงานจริงต่อรอบ (ไม่รวม sleep) — แยกไว้เพราะ tick_ms รวมเวลานอนไปด้วย
                ["work_ms"] = new JObject { ["p50"] = workP50, ["p99"] = workP99, ["max"] = workMax },
                ["players_online"] = _host.PlayersOnline(),
                ["max_players"] = _host.MaxPlayers,
                ["worlds_loaded"] = _host.WorldsLoaded(),
                ["regions_in_catalog"] = RegionCatalog.All?.Count ?? 0,
                ["last_save_ago_sec"] = ServerMetrics.LastSaveAgoSec,
                ["save_failures"] = ServerMetrics.SaveFailures,
                ["loop_errors"] = ServerMetrics.LoopErrors,
                ["unhandled_packet_types"] = unhandled
            };
            return new WebServer.JsonResponse(health.ToString());
        };

        // /terrains/* ทั้งหมดจัดการใน UnhandledUrl เพราะชื่อเกาะเป็นตัวแปร (ดู TerrainRoute)

        _webServer.UnhandledUrl += UnhandledUrl;
    }

    /// <summary>
    /// [5 ก.ย. 2026] แผนที่ของเกาะ — <c>/terrains/&lt;ชื่อเกาะ&gt;</c> และ chunk ใต้เส้นนั้น
    ///
    /// ต้นฉบับจดเส้นทางเป็น "/terrains/1" ตายตัวได้เพราะมีโลกเดียว แต่ตัวเกมประกอบ URL จาก
    /// <c>Region.TerrainId</c> ที่เซิร์ฟส่งไปกับ Welcome ตรง ๆ โดยไม่ตรวจอะไร
    /// (client/Durango.Terrain/TerrainMeta.cs:130 · TerrainBase.cs:337 · MapSystem.cs:752)
    /// ⇒ พอส่งชื่อเกาะจริงไป เส้นทางก็กลายเป็น /terrains/ri35te/… ตามนั้น
    ///
    /// ⚠️ ต้องอ่านชื่อเกาะจาก URL ไม่ใช่จาก session token: chunk กับ terrain info ถูกขอแบบ
    /// **ไม่มี header Authorization** (มีเฉพาะ /whole_biomes) จึงระบุตัวผู้ขอไม่ได้
    /// ⚠️ และชื่อต้องต่างกันต่อเกาะจริง ๆ เพราะ chunk ขอด้วย disableCache:false
    /// (TerrainBase.cs:332) ⇒ ถ้าใช้ชื่อซ้ำ เกาะใหม่จะได้แผนที่เกาะเก่าจากแคชของ client
    ///
    /// รูปแบบ:  /terrains/&lt;id&gt;            → info.yml (TerrainInfoJson)
    ///          /terrains/&lt;id&gt;/whole_biomes → biome ทั้งแผ่น
    ///          /terrains/&lt;id&gt;/ocean|rivers/&lt;x&gt;,&lt;y&gt; → chunk เฉพาะชั้น
    ///          /terrains/&lt;id&gt;/&lt;x&gt;,&lt;y&gt;      → chunk รวม (biome+ocean+river+landmark)
    /// </summary>
    private WebServer.RouteFunction TerrainRoute(string url)
    {
        string rest = url.Substring("/terrains/".Length).Split('?')[0];
        int slash = rest.IndexOf('/');
        string regionId = slash < 0 ? rest : rest.Substring(0, slash);
        string tail = slash < 0 ? "" : rest.Substring(slash + 1);

        World world = _gameServer.Worlds?.GetOrCreate(regionId) ?? _gameServer.World;

        if (tail.Length == 0)
        {
            return (HttpListenerRequest _, Dictionary<string, string> __) =>
                new WebServer.JsonResponse(Json.Write(world.TerrainInfo));
        }
        if (tail.StartsWith("whole_biomes", StringComparison.OrdinalIgnoreCase))
        {
            return (HttpListenerRequest _, Dictionary<string, string> __) =>
                new WebServer.BinaryReponse { Content = world.Biomes };
        }
        if (tail.StartsWith("ocean", StringComparison.OrdinalIgnoreCase))
        {
            return (HttpListenerRequest _, Dictionary<string, string> __) =>
                new WebServer.BinaryReponse { Content = world.GetChunkOcean(GetPoint2FromUrl(url)) };
        }
        if (tail.StartsWith("rivers", StringComparison.OrdinalIgnoreCase))
        {
            return (HttpListenerRequest _, Dictionary<string, string> __) =>
                new WebServer.BinaryReponse { Content = world.GetChunkRiver(GetPoint2FromUrl(url)) };
        }
        return (HttpListenerRequest _, Dictionary<string, string> __) =>
        {
            Point2 chunk = GetPoint2FromUrl(url);
            byte[] biomes = world.GetChunkBiomes(chunk);
            byte[] ocean = world.GetChunkOcean(chunk);
            byte[] river = world.GetChunkRiver(chunk);
            byte[] landmark = world.GetChunkLandmark(chunk);
            var ms = new MemoryStream();
            ms.Write(biomes, 0, biomes.Length);
            ms.Write(ocean, 0, ocean.Length);
            ms.Write(river, 0, river.Length);
            if (landmark != null)
            {
                ms.Write(landmark, 0, landmark.Length);
            }
            return new WebServer.BinaryReponse { Content = ms.ToArray() };
        };
    }

    /// <summary>
    /// [5 ก.ย. 2026] ด่านกันคนนอกของเส้นทางสำหรับคนดูแล (/health)
    ///
    /// เซิร์ฟนี้ bind แบบ wildcard (WebServer.cs:302) ⇒ ทุกเส้นทางเปิดออกอินเทอร์เน็ตหมด
    /// ตัวเลขใน /health บอกจำนวนคนออนไลน์/สถานะเซิร์ฟ ไม่ควรให้ใครก็อ่านได้
    ///
    /// ตั้ง token แล้ว → ต้องส่ง ?token=… (หรือหัว X-Admin-Token) มาให้ตรง เรียกจากที่ไหนก็ได้
    /// ไม่ได้ตั้ง      → ยอมเฉพาะ loopback (curl บนเครื่องเซิร์ฟเอง) เพื่อให้ไล่บั๊กได้โดยไม่เผลอเปิดให้คนนอก
    ///
    /// เทียบแบบใช้เวลาคงที่ ไม่ให้เดา token ทีละตัวอักษรจากเวลาตอบกลับได้
    /// </summary>
    private bool IsAdminAllowed(HttpListenerRequest request)
    {
        string want = AdminToken;
        if (string.IsNullOrEmpty(want))
        {
            IPAddress from = request?.RemoteEndPoint?.Address;
            return from != null && IPAddress.IsLoopback(from);
        }
        string got = request?.QueryString?["token"];
        if (string.IsNullOrEmpty(got))
        {
            got = request?.Headers?["X-Admin-Token"];
        }
        if (string.IsNullOrEmpty(got) || got.Length != want.Length)
        {
            return false;
        }
        int diff = 0;
        for (int i = 0; i < want.Length; i++)
        {
            diff |= got[i] ^ want[i];
        }
        return diff == 0;
    }

    /// <summary>
    /// GET /players/{entityId} — รูปแบบตรง <c>client/Durango.Player/PlayerInfoJson.cs</c>
    /// ใช้โชว์ชื่อเจ้าของที่ดิน (님의 사유지) และป็อปอัปข้อมูลผู้เล่น
    /// </summary>
    private WebServer.Response GetPublicPlayerInfo(string entityId)
    {
        PlayerContext ctx = _host.FindContextByEntityId(entityId) ?? _gameServer.GetPlayerContext(entityId);
        if (ctx == null || string.IsNullOrEmpty(ctx.EntityId))
        {
            return new WebServer.JsonResponse("{}", HttpStatusCode.NotFound);
        }
        AppearPlayer appear = ctx.AppearPlayer;
        var body = new JObject
        {
            ["entity_id"] = ctx.EntityId,
            ["freq"] = appear.Freq,
            ["name"] = ctx.PlayerInfo?.PlayerName ?? appear.Name ?? string.Empty,
            ["level"] = ctx.PlayerInfo != null && ctx.PlayerInfo.PlayerLevel > 0
                ? ctx.PlayerInfo.PlayerLevel
                : appear.Level,
            ["clan"] = new JObject
            {
                ["clan_id"] = appear.Member.ClanId ?? string.Empty,
                ["clan_name"] = appear.Member.ClanName ?? string.Empty
            },
            ["personal_region_id"] = ctx.PersonalRegionId ?? string.Empty
        };
        try
        {
            PlayerDisplay display = appear.Display;
            if (string.IsNullOrEmpty(display.EntityId)) display.EntityId = ctx.EntityId;
            body["display"] = JToken.Parse(Json.Write(display));
        }
        catch (Exception e)
        {
            Console.WriteLine($"[gateway] /players display เขียนไม่ได้: {e.Message}");
        }
        return new WebServer.JsonResponse(body.ToString());
    }

    /// <summary>หา context จาก Authorization header (session token — client ใส่ทุก request แบบ auth)</summary>
    private static WebServer.Response Forbidden() =>
        new WebServer.TextResponse("text/plain", "403 Forbidden", HttpStatusCode.Forbidden);

    private PlayerContext ResolveBySession(HttpListenerRequest request)
    {
        string token = request?.Headers?["Authorization"];
        if (string.IsNullOrEmpty(token) || !_gameServer.TryGetSessionEntityId(token, out string entityId))
        {
            return null;
        }
        return _host.FindContextByEntityId(entityId) ?? _gameServer.GetPlayerContext(entityId);
    }

    /// <summary>ต้นฉบับ Gateway.UpdateAppearPlayer — เติมหน้าตาจาก model_info ที่ prologue ส่งมา</summary>
    private static void UpdateAppearPlayer(PlayerContext player, Dictionary<string, string> postData)
    {
        bool flag = postData.Get("gender") == "male";
        player.AppearPlayer.EntityType = (ushort)(!flag ? 1001 : 1000);

        // ⚠️ ร่างเปล่า/ชุดชั้นในต้องตามเพศด้วย ไม่งั้นตัวละครหญิงที่ถอดเสื้อจะได้ร่างผู้ชาย
        // สวมทับโครงตัวหญิง (client/PlayerBehavior.cs:327-329 ใช้ DefaultBody เมื่อช่อง body ว่าง)
        // ต้นฉบับทำถูกอยู่แล้วที่ client/Durango.Online/PlayerContext.cs:91-93
        player.AppearPlayer.Display.DefaultBody = flag
            ? "Models/PC/Male/Body/m_body_nothing.FBX"
            : "Models/PC/Female/Body/f_body_nothing.FBX";
        player.AppearPlayer.Display.DefaultInner = flag
            ? "Models/PC/Male/Inner/m_inner_basic.FBX"
            : "Models/PC/Female/Inner/f_inner_basic.FBX";
        player.AppearPlayer.Display.Body = player.AppearPlayer.Display.DefaultBody;
        string json = postData.Get("model_info");
        PlayerDisplay display = player.AppearPlayer.Display;
        if (!string.IsNullOrEmpty(json))
        {
            try
            {
                JObject model = JObject.Parse(json);
                display.Hair = (string)model["hair"];
                display.BodyColor = model["body_color"]?.ToObject<string[]>() ?? display.BodyColor;
                display.HeadColor = model["head_color"]?.ToObject<string[]>() ?? display.HeadColor;
                display.SkinColor = (string)model["skin_color"] ?? display.SkinColor;
                display.HairColor = (string)model["hair_color"] ?? display.HairColor;
                display.LipColor = (string)model["lip_color"] ?? display.LipColor;
                display.EyeColor = (string)model["eye_color"] ?? display.EyeColor;
                display.Portrait = (int?)model["portrait"] ?? display.Portrait;
                display.PortraitBg = (int?)model["portrait_bg"] ?? display.PortraitBg;
                display.PortraitBgColor = (string)model["portrait_bg_color"];
                display.Beard = (string)model["beard"];
                display.VoiceType = (int?)model["voice_type"] ?? display.VoiceType;
                display.BodySize = (float?)model["body_size"] ?? display.BodySize;
            }
            catch (Exception e)
            {
                Console.WriteLine($"[gateway] model_info parse failed: {e.Message} payload={json}");
            }
        }
        player.AppearPlayer.Display = display;
        player.AppearPlayer.Name = player.PlayerInfo.PlayerName;
        player.AppearPlayer.Level = player.PlayerInfo.PlayerLevel;
    }

    private string RootUrl(HttpListenerRequest request)
    {
        if (!string.IsNullOrEmpty(PublicHost))
        {
            return $"http://{PublicHost}:{Port}";
        }
        string host = request.UserHostName;
        if (string.IsNullOrEmpty(host))
        {
            return $"http://127.0.0.1:{Port}";
        }
        if (host.Contains(':')) return "http://" + host;
        return $"http://{host}:{Port}";
    }

    /// <summary>ต้นฉบับ Gateway.cs:46 — iPhonePlayer→ios, Android→android, อื่น ๆ→windows</summary>
    private static string PlatformKey(string platform)
    {
        if (platform == null) return "windows";
        if (platform.Equals("iPhonePlayer", StringComparison.OrdinalIgnoreCase)) return "ios";
        if (platform.Equals("Android", StringComparison.OrdinalIgnoreCase)) return "android";
        return "windows";
    }

    private WebServer.RouteFunction UnhandledUrl(string url)
    {
        // ชื่อ/หน้าตาตัวละคร — EstateOwnerWidget กับป็อปอัปอื่นยิง GET /players/<entityId>
        // (client/PlayerInfoManager.cs RequestFunc) ถ้าไม่มีเส้นนี้ชื่อเจ้าของที่ดินไม่ขึ้น
        if (url.StartsWith("/players/", StringComparison.OrdinalIgnoreCase))
        {
            string rest = url.Substring("/players/".Length);
            int qIdx = rest.IndexOf('?');
            if (qIdx >= 0) rest = rest.Substring(0, qIdx);
            if (rest.Length > 0 && rest.IndexOf('/') < 0)
            {
                string entityId = rest;
                return (HttpListenerRequest request, Dictionary<string, string> _) =>
                {
                    if (!string.Equals(request.HttpMethod, "GET", StringComparison.OrdinalIgnoreCase))
                    {
                        return new WebServer.NotFountResponse();
                    }
                    return GetPublicPlayerInfo(entityId);
                };
            }
        }

        // ══ Admin Web UI — เสิร์ฟไฟล์จาก server/admin/ ═══════════════════════════════════════════
        // /admin/ → index.html, /admin/style.css → CSS, /admin/app.js → JS
        // ต้องเปิด admin token ถึงจะเข้าได้ (กันคนนอกเห็นหน้าจัดการเซิร์ฟ)
        if (url.StartsWith("/admin", StringComparison.OrdinalIgnoreCase))
        {
            string adminDir = Path.Combine(AppContext.BaseDirectory, "admin");
            // ถ้าไม่มีโฟลเดอร์ admin ข้าง executable ให้ลองหาใน DataDir
            if (!Directory.Exists(adminDir))
            {
                adminDir = Path.Combine(DataDir ?? Json.DataDir, "..", "admin");
                adminDir = Path.GetFullPath(adminDir);
            }
            if (!Directory.Exists(adminDir))
            {
                return (HttpListenerRequest _, Dictionary<string, string> __) =>
                    new WebServer.TextResponse("text/plain", "Admin UI not found; place the admin/ folder beside the executable", HttpStatusCode.NotFound);
            }

            string adminFile = url.Split('?')[0];
            string targetFile;
            if (adminFile == "/admin" || adminFile == "/admin/" || adminFile == "/admin/index.html")
            {
                targetFile = Path.Combine(adminDir, "index.html");
            }
            else
            {
                // เสิร์ฟไฟล์ static ใน admin/ (style.css, app.js, login.html, etc.)
                string fileName = adminFile.Substring("/admin/".Length);
                if (fileName.Contains("..") || Path.IsPathRooted(fileName))
                    return (HttpListenerRequest _, Dictionary<string, string> __) => new WebServer.BadRequestResponse();
                targetFile = Path.Combine(adminDir, fileName.Replace('/', Path.DirectorySeparatorChar));
            }

            if (!File.Exists(targetFile))
                return (HttpListenerRequest _, Dictionary<string, string> __) =>
                    new WebServer.TextResponse("text/plain", "File not found", HttpStatusCode.NotFound);

            // Use TextResponse to set the correct Content-Type.
            // FileResponse uses DirectLength and skips the Content-Type header, causing the browser to download instead of render.
            string fileContentType = "application/octet-stream";
            if (targetFile.EndsWith(".html", StringComparison.OrdinalIgnoreCase)) fileContentType = "text/html; charset=utf-8";
            else if (targetFile.EndsWith(".css", StringComparison.OrdinalIgnoreCase)) fileContentType = "text/css; charset=utf-8";
            else if (targetFile.EndsWith(".js", StringComparison.OrdinalIgnoreCase)) fileContentType = "application/javascript; charset=utf-8";
            else if (targetFile.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) fileContentType = "application/json; charset=utf-8";
            else if (targetFile.EndsWith(".png", StringComparison.OrdinalIgnoreCase)) fileContentType = "image/png";
            else if (targetFile.EndsWith(".ico", StringComparison.OrdinalIgnoreCase)) fileContentType = "image/x-icon";

            string ct = fileContentType;
            string fPath = targetFile;
            // RawBytesResponse is in server/Support/RawBytesResponse.cs and derives from WebServer.Response.
            // The original GameCode file is intentionally unchanged; see that file for the rationale.
            //
            // ⚠️ Use ReadAllBytes, not ReadAllText+GetBytes; the content types above include binary assets.
            // PNG and icon files would be corrupted if binary data passed through strings.
            // Invalid UTF-8 bytes become U+FFFD and are written back as EF BF BD.
            return (HttpListenerRequest _, Dictionary<string, string> __) =>
                new RawBytesResponse(File.ReadAllBytes(fPath), ct);
        }

        // [Sep 5, 2026] Game data tables for Online mode; see client/Yaml.Util/Loader.cs:155-185.
        // The client requests GET <gateway>/assets/<name> (without extension) and deserializes the JSON response.
        // Files are stored at <AssetsDir>/<name>.json; the game requests 71 routes, all present in data/assets.
        if (url.StartsWith("/assets/", StringComparison.OrdinalIgnoreCase))
        {
            string assetsDir = AssetsDir;
            if (string.IsNullOrEmpty(assetsDir) || !Directory.Exists(assetsDir))
            {
                return null;
            }
            string relative = url.Substring("/assets/".Length);
            int qIdx = relative.IndexOf('?');
            if (qIdx != -1)
            {
                relative = relative.Substring(0, qIdx);
            }
            // Prevent path traversal. — client ขอแค่ <โฟลเดอร์>/<ชื่อ> ธรรมดา ไม่มี .. และไม่ใช่ path เต็ม
            if (relative.Length == 0 || relative.Contains("..") || Path.IsPathRooted(relative))
            {
                return (HttpListenerRequest _, Dictionary<string, string> __) => new WebServer.BadRequestResponse();
            }
            string assetPath = Path.GetFullPath(Path.Combine(assetsDir, relative.Replace('/', Path.DirectorySeparatorChar) + ".json"));
            string rootFull = Path.GetFullPath(assetsDir);
            if (!assetPath.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase))
            {
                return (HttpListenerRequest _, Dictionary<string, string> __) => new WebServer.BadRequestResponse();
            }
            return (HttpListenerRequest _, Dictionary<string, string> __) =>
            {
                if (!File.Exists(assetPath))
                {
                    // ⚠️ A missing file causes five retries and then a stuck loading screen; log it clearly.
                    Console.WriteLine($"[assets] 404 {relative}");
                    return new WebServer.NotFountResponse();
                }
                return new WebServer.BinaryReponse
                {
                    Content = File.ReadAllBytes(assetPath),
                    ContentType = "application/json"
                };
            };
        }

        // The client builds CDN-style URLs: /{live|release}/{platform}/<file>, based on /knock URLs.
        if (url.StartsWith("/live/", StringComparison.OrdinalIgnoreCase) ||
            url.StartsWith("/release/", StringComparison.OrdinalIgnoreCase))
        {
            string[] seg = url.Split(new[] { '/' }, 4, StringSplitOptions.RemoveEmptyEntries);
            if (seg.Length == 3)
            {
                url = (PlatformKey(seg[1]) == "android" ? "/assetbundles/android/" : "/assetbundles/") + seg[2];
            }
        }

        if (url.StartsWith("/assetbundles/android/"))
        {
            if (string.IsNullOrEmpty(AssetBundleAndroidDir) || !Directory.Exists(AssetBundleAndroidDir))
            {
                return null;
            }
            string aName = Path.GetFileName(url.Substring("/assetbundles/android/".Length).Split('?')[0]);
            if (string.IsNullOrEmpty(aName) || aName.Contains(".."))
            {
                return (HttpListenerRequest request, Dictionary<string, string> postData) => new WebServer.BadRequestResponse();
            }
            string aPath = Path.Combine(AssetBundleAndroidDir, aName);
            // [Sep 5, 2026] Stream bundles with FileResponse instead of File.ReadAllBytes.
            //
            // Bundles are several MB; ReadAllBytes allocates large byte arrays on the Large Object Heap for each request.
            // Concurrent mobile downloads caused frequent GC pauses and slowed the game loop (13 players measured at 2 TPS).
            // FileResponse streams 64 KB at a time to OutputStream, keeping memory usage stable and avoiding the LOH.
            // This class existed since Sep 4 but was not previously used.
            return (HttpListenerRequest request, Dictionary<string, string> postData) =>
            {
                if (File.Exists(aPath))
                {
                    return new WebServer.FileResponse(aPath);
                }
                string resolvedA = ResolveBundleIgnoringHash(aName, AssetBundleAndroidDir);
                if (resolvedA != null)
                {
                    return new WebServer.FileResponse(resolvedA);
                }
                // Voice-over soundbanks are language-specific; the Android bundle only has en_us, so serve it for all languages.
                string fallbackA = ResolveVoiceBankFallback(aName, AssetBundleAndroidDir);
                if (fallbackA != null)
                {
                    Console.WriteLine("[assetbundle-android] {0} missing; serving en_us instead", aName);
                    return new WebServer.FileResponse(fallbackA);
                }
                Console.WriteLine("[assetbundle-android] 404 {0}", aName);
                return new WebServer.NotFountResponse();
            };
        }

        if (url.StartsWith("/terrains/", StringComparison.OrdinalIgnoreCase))
        {
            return TerrainRoute(url);
        }

        // [Removed Sep 5, 2026] A duplicate /assetbundles/android/ block used to exist here.
        // It was unreachable because the first matching condition handled the same URL, so it was removed.
        // Keep the behavior in one place to avoid inconsistent fixes.
        return (HttpListenerRequest request, Dictionary<string, string> _) => new WebServer.BadRequestResponse();
    }

    /// <summary>The client requests &lt;name&gt;.&lt;crc&gt;.bundle; if the CRC does not match a disk file, look up the unhashed name.</summary>
    private static string ResolveBundleIgnoringHash(string requestedName, string dir)
    {
        const string suffix = ".bundle";
        if (!requestedName.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) return null;
        string stem = requestedName.Substring(0, requestedName.Length - suffix.Length);
        int lastDot = stem.LastIndexOf('.');
        if (lastDot <= 0) return null;
        string prefix = stem.Substring(0, lastDot + 1);
        try
        {
            return Directory.GetFiles(dir, prefix + "*.bundle").FirstOrDefault();
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Voice-over soundbanks: soundbanks$android/// <summary>เสียงพากย์: soundbanks$android$<lang>$voice_*.bnk — เซิร์ฟมีแค่ en_us</summary>lt;lang&gt;$voice_*.bnk; only en_us is available on the server.</summary>
    private static string ResolveVoiceBankFallback(string requestedName, string dir)
    {
        const string marker = "$android$";
        int idx = requestedName.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return null;
        int langStart = idx + marker.Length;
        int langEnd = requestedName.IndexOf('$', langStart);
        if (langEnd < 0 || !requestedName.Contains("$voice_")) return null;
        string fallback = requestedName.Substring(0, langStart) + "en_us" + requestedName.Substring(langEnd);
        string path = Path.Combine(dir, fallback);
        return File.Exists(path) ? path : null;
    }

    private static Point2 GetPoint2FromUrl(string url)
    {
        int num = url.LastIndexOf("/", StringComparison.Ordinal) + 1;
        string[] array = url.Substring(num, url.Length - num).Split(',');
        return new Point2(array[0].ToInt(), array[1].ToInt());
    }
}
