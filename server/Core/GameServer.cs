using System;
using System.Collections.Generic;
using System.Net.Sockets;
using Durango.Network;
using Durango.Utils;
using Durango.Utils.Extensions;
using JetBrains.Annotations;
using Messages;
using Shared.Region;

namespace Durango.Online;

// Ported from nexonSRC/Durango.Online/GameServer.cs: TCP 8191 with the original GetClock → Auth → Ready handshake.
// Differences from the original are documented in docs/server/ServerNx.md.
// This server supports multiple players in one world; the original offline host used one player per slot and saved only _playerCtx.
// ContextChanged now saves the changed context when it has a persistent on-disk Path.
// IssueSession maps a session token to an entity ID for /sessions; the original did not use real session tokens.
public class GameServer
{
    public const int DefaultPort = 8191;

    private readonly Listener _listener;

    private readonly PlayerContext _playerCtx;

    private readonly Dictionary<string, PlayerContext> _playerContexts = new();

    private readonly List<Connection> _connections = new();

    private readonly Dictionary<Connection, string> _connectionDict = new();

    private readonly Dictionary<string, string> _sessionTokens = new();

    /// <summary>
/// Maps each session token to its account owner's key, preventing the token from being redirected to arbitrary characters.
    ///
/// ⚠️ Without this table, BindSessionToEntity only verifies that the server issued the token.
/// It would not verify that the target character belongs to the token holder, allowing account takeover with a short curl sequence.
/// Example attack: POST /sessions to get a token, then GET /entry?entity_id=&lt;victim&gt; to authenticate as the victim.
    /// </summary>
    private readonly Dictionary<string, string> _sessionOwners = new();

    public World World { get; }

    /// <summary>
/// [Sep 5, 2026] Worlds for every island; Host sets this after GameServer is created.
/// Each player enters the world identified by PlayerContext.RegionId rather than sharing one world as in the original.
    /// </summary>
    public WorldRegistry Worlds { get; set; }

/// <summary>World containing this player; falls back to the starting world when multi-island support is unavailable.</summary>
    public World WorldOf(PlayerContext context) =>
        Worlds == null ? World : Worlds.GetOrCreate(context?.RegionId);

    public int Port { get; private set; }

    public GameServer(WorldContext worldCtx, PlayerContext playerCtx)
    {
        _listener = new Listener();
        Port = 8191;
        _playerCtx = playerCtx;
        World = new World(worldCtx);
    }

    public void Start(int port)
    {
        Port = port;
        _listener.Start(port);
        _listener.ClientAccepted += Listener_ClientAccepted;
    }

    public void Close()
    {
        try
        {
            _listener.Close();
            for (int num = _connections.Count - 1; num >= 0; num--)
            {
                _connections[num].Close();
            }
            _connections.Clear();
            if (Worlds != null) Worlds.StopAll(); else World.Stop();
        }
        catch (Exception)
        {
        }
    }

    public void Process()
    {
        _listener.Process();
        for (int num = _connections.Count - 1; num >= 0; num--)
        {
            _connections[num].Process();
        }
        DropStaleUnauthenticated();
        if (Worlds != null) Worlds.ProcessAll(); else World.Process();
    }

    /// <summary>
/// Disconnect connections that do not complete Auth within the configured timeout.
    ///
/// ⚠️ Without this timeout, a TCP connection could reserve about 4 MB of buffers indefinitely.
/// No token or account is required to hold that connection open.
    /// </summary>
    private void DropStaleUnauthenticated()
    {
        if (_pendingAuth.Count == 0) return;
        double now = Gauge.CurrentTime;

        List<Connection> stale = null;
        foreach (KeyValuePair<Connection, double> pair in _pendingAuth)
        {
            if (now - pair.Value < UnauthenticatedTimeoutSeconds) continue;
            (stale ??= new List<Connection>()).Add(pair.Key);
        }
        if (stale == null) return;

        foreach (Connection connection in stale)
        {
            Console.WriteLine("[auth] Disconnected a connection that did not authenticate before timeout");
            _pendingAuth.Remove(connection);
            try { connection.Close(); } catch (Exception) { }
            _connections.Remove(connection);
        }
    }

/// <summary>Register a context, either persistent or temporary; called by /sessions.</summary>
    public bool Register(PlayerContext context)
    {
        if (context != null && !string.IsNullOrEmpty(context.EntityId))
        {
            _playerContexts[context.EntityId] = context;
            return true;
        }
        return false;
    }

/// <summary>Issue a session token to the holder of account key <paramref name="ownerKey"/>.</summary>
    public void IssueSession(string entityId, string token, string ownerKey)
    {
        _sessionTokens[token] = entityId;
        _sessionOwners[token] = ownerKey;
    }

/// <summary>Account key associated with a token, or null if the token is unknown.</summary>
    public string OwnerOfSession(string token) => _sessionOwners.Get(token ?? "");

    public bool TryGetSessionEntityId(string token, out string entityId)
    {
        return _sessionTokens.TryGetValue(token ?? "", out entityId);
    }

    /// <summary>
/// [Sep 5, 2026] Rebind an existing session token to the character selected on the title screen.
    ///
/// In Online mode, the client does not include the player field in /sessions.
/// The client sends that field only for LAN/ConnectTo when GameManager.ConnectCluster is not null (TitleMenuGroup.cs:334-340).
/// Therefore, the server does not know the selected character when it issues the token.
/// The selected character is provided later through /entry?entity_id=… with auth:true.
/// TitleMenuGroup.cs:1046 calls RquestEntry and Http.cs:36 adds the Authorization header.
/// Rebinding is safe because only the holder of a server-issued token can perform it.
    ///
/// Returns false for an unknown token; Auth will reject the connection.
    /// </summary>
    public bool BindSessionToEntity(string token, string entityId)
    {
        if (string.IsNullOrEmpty(token) || string.IsNullOrEmpty(entityId)
            || !_sessionTokens.ContainsKey(token))
        {
            return false;
        }

// ⚠️ Previously, the server only checked whether a token was server-issued before rebinding it.
// This was insufficient because anyone could request a token at /sessions without authenticating.
// The server must also verify that the target character belongs to the account holding the token.
        string owner = _sessionOwners.Get(token);
        PlayerContext target = _playerContexts.Get(entityId);

// A character unknown to the server is newly requested for this session and has not passed through /players yet.
// It may proceed because it has no owner; /players assigns ownership when creation is persisted.
        if (target == null) { _sessionTokens[token] = entityId; return true; }

        if (!AccountKeys.Same(owner, target.OwnerKey))
        {
            Console.WriteLine($"[auth] Rejected session binding: account {AccountKeys.ForLog(owner)} " +
                $"does not own character {entityId} (owner {AccountKeys.ForLog(target.OwnerKey)})");
            return false;
        }
        _sessionTokens[token] = entityId;
        return true;
    }

    /// <summary>
/// Context for this character; null if the server does not know it.
    ///
/// ⚠️ Previously, an unknown character fell back to _playerCtx, the server's first slot.
/// That behavior came from the single-player offline server and is unsafe in multiplayer.
/// Supplying an arbitrary entity_id could grant access to the first player's character.
/// Autosave could then persist the damage within 60 seconds.
/// Return null instead and let the caller reject the connection.
    /// </summary>
    [CanBeNull]
    public PlayerContext GetPlayerContext(string entityId) => _playerContexts.Get(entityId);

    /// <summary>
/// Maximum number of simultaneous open connections; configured by this server.
    ///
/// ⚠️ --max-players limits only the HTTP /entry gate; the actual game connection uses TCP.
/// TCP connections previously had no limit, allowing unauthenticated connections to reserve buffers.
/// Each connection reserves about 4 MB: seven 512 KB buffers in Connection.cs when accepted.
/// A few hundred connections could exhaust server RAM.
    ///
/// Set the limit to three times the player cap to allow reconnects while old connections are closing.
    /// </summary>
    public static int MaxPlayersHint { get; set; } = 200;

    private static int MaxConnections => Math.Max(32, MaxPlayersHint * 3);

    /// <summary>
/// Maximum time a connection may remain unauthenticated, in seconds.
/// The official client sends Auth immediately, so 30 seconds allows for poor network conditions.
    /// </summary>
    private const double UnauthenticatedTimeoutSeconds = 30.0;

/// <summary>Unauthenticated connection timestamp, used to release buffers reserved by idle connections.</summary>
    private readonly Dictionary<Connection, double> _pendingAuth = new();

    private void Listener_ClientAccepted(Socket socket)
    {
        if (_connections.Count >= MaxConnections)
        {
            Console.WriteLine($"[auth] Rejected new connection: connection cap reached ({_connections.Count}/{MaxConnections})");
            try { socket.Close(); } catch (Exception) { }
            return;
        }

        Connection connection = new(socket);
        connection.Recv(delegate(GetClock getClock, PacketHeader header)
        {
            Clock msg = default;
            msg.ClientTime = getClock.Time;
            msg.ServerTime = Times.UnixTimeNow();
            connection.Send(msg, header.Seq);
        });
        connection.Recv(delegate(Auth auth, PacketHeader header)
        {
// [Sep 4, 2026] Previously, auth.EntityId was trusted directly, allowing clients to authenticate as any entity.
// Authentication is now bound to the token issued by /sessions.
            if (!TryGetSessionEntityId(auth.SessionToken, out string sessionEntityId)
                || !string.Equals(sessionEntityId, auth.EntityId, StringComparison.Ordinal))
            {
            Console.WriteLine($"[auth] Rejected: token does not match claimed entity ({auth.EntityId})");
            connection.Send(new Abort { Text = "Authentication failed" }, header.Seq);
                connection.Close();
                return;
            }
            string entityId = auth.EntityId;
            PlayerContext playerContext = GetPlayerContext(entityId);
            if (playerContext == null)
            {
// Unknown IDs previously fell back to the first character slot (see GetPlayerContext), allowing arbitrary IDs to connect.
            Console.WriteLine($"[auth] Rejected: unknown character {entityId}");
            connection.Send(new Abort { Text = "Character not found" }, header.Seq);
                connection.Close();
                return;
            }
            _connectionDict[connection] = entityId;
            _pendingAuth.Remove(connection);      // Auth succeeded; timeout tracking is no longer needed.
            SendWelcome(connection, entityId, playerContext.PlayerInfo.PlayerName, header.Seq);
        });
        connection.Recv(delegate(Ready ready, PacketHeader readyHeader)
        {
            string text = _connectionDict.Get(connection);
            if (string.IsNullOrEmpty(text))
            {
                connection.Close();
            }
            else
            {
                PlayerContext playerContext = GetPlayerContext(text);
                if (playerContext == null)
                {
// This should not occur because Auth already filters the request, but retain a guard against null references.
// The old fallback masked this case, so it must be checked explicitly now.
            Console.WriteLine($"[auth] Ready: unknown character {text}; disconnecting");
                    connection.Close();
                    return;
                }
                connection.Send(default(OK), readyHeader.Seq);
                bool flag = playerContext.EntityId == text;
                World playerWorld = WorldOf(playerContext);
                Player player = new(text, connection, playerWorld, playerContext, flag);
                if (flag)
                {
                    player.ContextChanged += delegate
                    {
// The original saved only _playerCtx for its single-player offline host; this server saves the context for each slot.
// Temporary contexts without a Path are not saved until /players promotes them to persistent slots.
                        if (!string.IsNullOrEmpty(playerContext.Path))
                        {
                            playerContext.Save();
                        }
                    };
                }
                playerWorld.AddPlayer(player);
            }
            _connections.Remove(connection);
            _connectionDict.Remove(connection);
        });
        connection.ConnetionClosed += delegate
        {
            _connections.Remove(connection);
            _connectionDict.Remove(connection);
            _pendingAuth.Remove(connection);
        };
        connection.StartReceive();
        _connections.Add(connection);
        _pendingAuth[connection] = Gauge.CurrentTime;
    }

    private void SendWelcome(Connection connection, string entityId, string name, uint seq)
    {
        Welcome msg = new()
        {
            UserId = entityId,
            Name = name
        };
        PlayerContext playerContext = GetPlayerContext(entityId);
        msg.Storage.Data = playerContext.Storage;
            // [Sep 5, 2026] Report the player’s actual island; the original hardcoded 1 because it had only one world.
            // Id identifies the island in RegionCatalog; TerrainId remains 1 because
            // the client uses it to build /terrains/&lt;TerrainId&gt;/… URLs and Gateway serves the route for the requesting player.
            // This avoids requiring a client-side change.
        World playerWorld = WorldOf(playerContext);
        msg.Region.CreatedAt = 0.0;
            // Region.Id and Role tell the client whether the player is on a personal island.
            // The land UI compares GameManager.Region.Role/Id with PersonalRegion.Region.Id.
            // MoveToRegionToDo(Personal) also checks Role() == Personal.
        string regionId = playerContext.RegionId;
        bool onPersonal = !string.IsNullOrEmpty(regionId) &&
                          regionId.StartsWith("personal_", StringComparison.OrdinalIgnoreCase);
        msg.Region.Id = onPersonal
            ? regionId
            : (string.IsNullOrEmpty(regionId) ? (playerWorld.TerrainId ?? "1") : regionId);
        msg.Region.Name = null;
        msg.Region.TemplateId = onPersonal
            ? (playerContext.PersonalRegionTemplateId ?? playerWorld.TerrainInfo.region_template)
            : playerWorld.TerrainInfo.region_template;
            // TerrainId is the actual terrain file name used to load maps and chunks.
        msg.Region.TerrainId = playerWorld.TerrainId ?? "1";
        msg.Region.Role = onPersonal ? Role.Personal : Role.Rural;
            // The player's personal island, or empty if none has been created.
        msg.PersonalRegionId = string.IsNullOrEmpty(playerContext.PersonalRegionId)
            ? null
            : playerContext.PersonalRegionId;
        Console.WriteLine(
            $"[welcome] {entityId[..Math.Min(8, entityId.Length)]} Region.Id={msg.Region.Id} Role={msg.Region.Role} TerrainId={msg.Region.TerrainId} TemplateId={msg.Region.TemplateId} PersonalRegionId={msg.PersonalRegionId ?? "(empty)"}");
        msg.Options.Bool = new[]
        {
            new BoolOption { Key = "market.ui_enabled", Value = true }
        };
        msg.Options.Int = new[]
        {
            new IntegerOption { Key = "market.search.limit", Value = 20L }
        };
        connection.Send(msg, seq);
    }
}
