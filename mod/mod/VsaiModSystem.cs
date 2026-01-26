using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Reflection;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.Essentials;
using Vintagestory.GameContent;

namespace vsai;

public class VsaiModSystem : ModSystem
{
    private ICoreServerAPI? _serverApi;
    private HttpListener? _httpListener;
    private CancellationTokenSource? _cancellationTokenSource;
    private Thread? _listenerThread;

    // The AI-controlled bot entity
    private Entity? _botEntity;
    private long _botEntityId;

    // Chunk loading to prevent bot despawning at long distances
    private HashSet<long> _forceLoadedChunks = new HashSet<long>();
    private int _lastBotChunkX = int.MinValue;
    private int _lastBotChunkZ = int.MinValue;
    private long _chunkTickListenerId;
    private const int ChunkLoadRadius = 3;  // Load 7x7 chunks (3 in each direction from center)
    private const int ChunkSize = 32;  // VS chunk size in blocks

    // Chat inbox for player messages
    private string _botName = "Claude";
    private readonly Queue<ChatMessage> _chatInbox = new Queue<ChatMessage>();
    private const int MaxInboxSize = 100;
    private readonly object _inboxLock = new object();

    private class ChatMessage
    {
        public long Timestamp { get; set; }
        public string PlayerName { get; set; } = "";
        public string Content { get; set; } = "";
    }

    private const int DefaultPort = 4560;
    private const string DefaultHost = "localhost";

    public override bool ShouldLoad(EnumAppSide side)
    {
        // Load on both client (for minimap) and server (for HTTP API)
        return true;
    }

    public override void Start(ICoreAPI api)
    {
        base.Start(api);
        // Register our custom AI task
        AiTaskRegistry.Register<AiTaskRemoteControl>("vsai:remotecontrol");
        api.Logger.Notification("[VSAI] Registered AI task: vsai:remotecontrol");

        // Register our custom entity class (prevents persistence to save file)
        api.RegisterEntity("vsai.EntityAiBot", typeof(EntityAiBot));
        api.Logger.Notification("[VSAI] Registered entity class: vsai.EntityAiBot");

        // Register our custom inventory behavior with expanded slot count
        api.RegisterEntityBehaviorClass("botinventory", typeof(EntityBehaviorBotInventory));
        api.Logger.Notification("[VSAI] Registered entity behavior: botinventory");
    }

    public override void StartServerSide(ICoreServerAPI api)
    {
        _serverApi = api;
        _serverApi.Logger.Notification("[VSAI] Starting Vintage Story AI Bridge...");

        // Load bot name from environment variable
        var envBotName = Environment.GetEnvironmentVariable("VSAI_BOT_NAME");
        if (!string.IsNullOrEmpty(envBotName))
        {
            _botName = envBotName;
        }
        _serverApi.Logger.Notification($"[VSAI] Bot name: {_botName}");

        // Register entity death handler to track bot deaths
        api.Event.OnEntityDeath += OnEntityDeath;

        // Register player chat handler for inbox
        api.Event.PlayerChat += OnPlayerChat;

        // Register tick listener for chunk loading (every 500ms)
        _chunkTickListenerId = api.Event.RegisterGameTickListener(OnChunkTickUpdate, 500);

        StartHttpServer();

        _serverApi.Logger.Notification($"[VSAI] HTTP server started on http://{DefaultHost}:{DefaultPort}");
    }

    public override void StartClientSide(ICoreClientAPI api)
    {
        api.Logger.Notification("[VSAI] Starting client-side for minimap integration...");

        // Register custom entity renderer for held item rendering
        api.RegisterEntityRendererClass("AiBotRenderer", typeof(EntityAiBotRenderer));
        api.Logger.Notification("[VSAI] Registered entity renderer: AiBotRenderer");

        // Register the bot map layer with the world map
        var mapManager = api.ModLoader.GetModSystem<WorldMapManager>();
        if (mapManager != null)
        {
            mapManager.RegisterMapLayer<AiBotMapLayer>("vsai-bot", 0.5);
            api.Logger.Notification("[VSAI] Registered AiBotMapLayer with WorldMapManager");
        }
        else
        {
            api.Logger.Warning("[VSAI] WorldMapManager not found, minimap integration disabled");
        }
    }

    public override void Dispose()
    {
        // Unregister tick listener
        if (_serverApi != null && _chunkTickListenerId != 0)
        {
            _serverApi.Event.UnregisterGameTickListener(_chunkTickListenerId);
        }

        // Unload all force-loaded chunks
        ClearChunkTrackingState();

        DespawnBot();
        StopHttpServer();
        base.Dispose();
    }

    /// <summary>
    /// Periodic tick to ensure chunks around the bot are loaded.
    /// This prevents the bot from despawning when far from the player.
    /// </summary>
    private void OnChunkTickUpdate(float dt)
    {
        if (_botEntity == null || !_botEntity.Alive || _serverApi == null) return;

        var pos = _botEntity.ServerPos;
        int currentChunkX = (int)Math.Floor(pos.X / ChunkSize);
        int currentChunkZ = (int)Math.Floor(pos.Z / ChunkSize);

        // Only update if bot has moved to a different chunk region
        if (currentChunkX == _lastBotChunkX && currentChunkZ == _lastBotChunkZ) return;

        _lastBotChunkX = currentChunkX;
        _lastBotChunkZ = currentChunkZ;

        EnsureBotChunksLoaded(currentChunkX, currentChunkZ);
    }

    /// <summary>
    /// Force-load chunks around the bot to prevent despawning.
    /// Uses keepLoaded=true to keep chunks loaded until explicitly unloaded.
    /// </summary>
    private void EnsureBotChunksLoaded(int centerChunkX, int centerChunkZ)
    {
        if (_serverApi == null) return;

        var newChunks = new HashSet<long>();

        // Load chunks in a square around the bot
        for (int dx = -ChunkLoadRadius; dx <= ChunkLoadRadius; dx++)
        {
            for (int dz = -ChunkLoadRadius; dz <= ChunkLoadRadius; dz++)
            {
                int cx = centerChunkX + dx;
                int cz = centerChunkZ + dz;
                long key = ChunkKey(cx, cz);
                newChunks.Add(key);

                // Load chunk if not already force-loaded
                if (!_forceLoadedChunks.Contains(key))
                {
                    _serverApi.WorldManager.LoadChunkColumn(cx, cz, true);  // keepLoaded=true
                }
            }
        }

        // Don't unload old chunks - let VS handle cleanup naturally.
        // Forcibly unloading can accidentally unload chunks players need.

        int newlyLoaded = newChunks.Count - _forceLoadedChunks.Intersect(newChunks).Count();

        if (newlyLoaded > 0)
        {
            _serverApi.Logger.Debug($"[VSAI] Chunk update: loaded {newlyLoaded} new chunks, total {newChunks.Count} around bot");
        }

        _forceLoadedChunks = newChunks;
    }

    /// <summary>
    /// Clear chunk tracking state (called on dispose or bot despawn).
    /// We don't forcibly unload chunks - let VS handle cleanup naturally
    /// to avoid accidentally unloading chunks that players need.
    /// </summary>
    private void ClearChunkTrackingState()
    {
        _forceLoadedChunks.Clear();
        _lastBotChunkX = int.MinValue;
        _lastBotChunkZ = int.MinValue;
    }

    /// <summary>
    /// Encode chunk coordinates as a single long for use as a HashSet key.
    /// </summary>
    private static long ChunkKey(int chunkX, int chunkZ)
    {
        return ((long)chunkX << 32) | (uint)chunkZ;
    }

    private void StartHttpServer()
    {
        _cancellationTokenSource = new CancellationTokenSource();
        _httpListener = new HttpListener();
        _httpListener.Prefixes.Add($"http://{DefaultHost}:{DefaultPort}/");

        try
        {
            _httpListener.Start();
            _listenerThread = new Thread(ListenLoop)
            {
                IsBackground = true,
                Name = "VSAI-HttpListener"
            };
            _listenerThread.Start();
        }
        catch (Exception ex)
        {
            _serverApi?.Logger.Error($"[VSAI] Failed to start HTTP server: {ex.Message}");
        }
    }

    private void StopHttpServer()
    {
        _cancellationTokenSource?.Cancel();
        _httpListener?.Stop();
        _httpListener?.Close();
        _listenerThread?.Join(1000);
        _serverApi?.Logger.Notification("[VSAI] HTTP server stopped");
    }

    private void ListenLoop()
    {
        while (_httpListener != null && _httpListener.IsListening && !_cancellationTokenSource!.IsCancellationRequested)
        {
            try
            {
                var context = _httpListener.GetContext();
                Task.Run(() => HandleRequest(context));
            }
            catch (HttpListenerException)
            {
                break;
            }
            catch (Exception ex)
            {
                _serverApi?.Logger.Error($"[VSAI] Error in listen loop: {ex.Message}");
            }
        }
    }

    private void HandleRequest(HttpListenerContext context)
    {
        var request = context.Request;
        var response = context.Response;

        try
        {
            string responseBody;
            int statusCode = 200;

            var path = request.Url?.AbsolutePath ?? "/";
            var method = request.HttpMethod;

            switch (path)
            {
                case "/":
                case "/status":
                    responseBody = HandleStatus();
                    break;

                // Player observation
                case "/players":
                    responseBody = HandlePlayers();
                    break;

                // Bot management
                case "/bot/spawn":
                    if (method != "POST")
                    {
                        statusCode = 405;
                        responseBody = JsonError("Method not allowed. Use POST.");
                    }
                    else
                    {
                        responseBody = HandleBotSpawn(request);
                    }
                    break;

                case "/bot/despawn":
                    if (method != "POST")
                    {
                        statusCode = 405;
                        responseBody = JsonError("Method not allowed. Use POST.");
                    }
                    else
                    {
                        responseBody = HandleBotDespawn();
                    }
                    break;

                case "/bot/cleanup":
                    if (method != "POST")
                    {
                        statusCode = 405;
                        responseBody = JsonError("Method not allowed. Use POST.");
                    }
                    else
                    {
                        responseBody = HandleBotCleanup();
                    }
                    break;

                case "/bot/observe":
                    responseBody = HandleBotObserve();
                    break;

                case "/bot/blocks":
                    responseBody = HandleBotObserveBlocks(request);
                    break;

                case "/bot/entities":
                    responseBody = HandleBotObserveEntities(request);
                    break;

                case "/bot/mine":
                    if (method != "POST")
                    {
                        statusCode = 405;
                        responseBody = JsonError("Method not allowed. Use POST.");
                    }
                    else
                    {
                        responseBody = HandleBotMine(request);
                    }
                    break;

                case "/bot/place":
                    if (method != "POST")
                    {
                        statusCode = 405;
                        responseBody = JsonError("Method not allowed. Use POST.");
                    }
                    else
                    {
                        responseBody = HandleBotPlaceBlock(request);
                    }
                    break;

                case "/bot/interact":
                    if (method != "POST")
                    {
                        statusCode = 405;
                        responseBody = JsonError("Method not allowed. Use POST.");
                    }
                    else
                    {
                        responseBody = HandleBotInteract(request);
                    }
                    break;

                case "/bot/use_tool":
                    if (method != "POST")
                    {
                        statusCode = 405;
                        responseBody = JsonError("Method not allowed. Use POST.");
                    }
                    else
                    {
                        responseBody = HandleBotUseTool(request);
                    }
                    break;

                case "/bot/attack":
                    if (method != "POST")
                    {
                        statusCode = 405;
                        responseBody = JsonError("Method not allowed. Use POST.");
                    }
                    else
                    {
                        responseBody = HandleBotAttack(request);
                    }
                    break;

                case "/bot/stop":
                    if (method != "POST")
                    {
                        statusCode = 405;
                        responseBody = JsonError("Method not allowed. Use POST.");
                    }
                    else
                    {
                        responseBody = HandleBotStop();
                    }
                    break;

                case "/bot/goto":
                    if (method != "POST")
                    {
                        statusCode = 405;
                        responseBody = JsonError("Method not allowed. Use POST.");
                    }
                    else
                    {
                        responseBody = HandleBotGoto(request);
                    }
                    break;

                case "/bot/movement/status":
                    responseBody = HandleBotMovementStatus();
                    break;

                case "/bot/chat":
                    if (method != "POST")
                    {
                        statusCode = 405;
                        responseBody = JsonError("Method not allowed. Use POST.");
                    }
                    else
                    {
                        responseBody = HandleBotChat(request);
                    }
                    break;

                case "/bot/inventory":
                    responseBody = HandleBotInventory();
                    break;

                case "/bot/collect":
                    if (method != "POST")
                    {
                        statusCode = 405;
                        responseBody = JsonError("Method not allowed. Use POST.");
                    }
                    else
                    {
                        responseBody = HandleBotCollect(request);
                    }
                    break;

                case "/bot/inventory/drop":
                    if (method != "POST")
                    {
                        statusCode = 405;
                        responseBody = JsonError("Method not allowed. Use POST.");
                    }
                    else
                    {
                        responseBody = HandleBotDrop(request);
                    }
                    break;

                case "/bot/pickup":
                    if (method != "POST")
                    {
                        statusCode = 405;
                        responseBody = JsonError("Method not allowed. Use POST.");
                    }
                    else
                    {
                        responseBody = HandleBotPickup(request);
                    }
                    break;

                case "/bot/inbox":
                    responseBody = HandleBotInbox(request);
                    break;

                case "/bot/knap":
                    if (method != "POST")
                    {
                        statusCode = 405;
                        responseBody = JsonError("Method not allowed. Use POST.");
                    }
                    else
                    {
                        responseBody = HandleBotKnap(request);
                    }
                    break;

                case "/bot/craft":
                    if (method != "POST")
                    {
                        statusCode = 405;
                        responseBody = JsonError("Method not allowed. Use POST.");
                    }
                    else
                    {
                        responseBody = HandleBotCraft(request);
                    }
                    break;

                case "/bot/equip":
                    if (method != "POST")
                    {
                        statusCode = 405;
                        responseBody = JsonError("Method not allowed. Use POST.");
                    }
                    else
                    {
                        responseBody = HandleBotEquip(request);
                    }
                    break;

                case "/screenshot":
                    if (method != "POST")
                    {
                        statusCode = 405;
                        responseBody = JsonError("Method not allowed. Use POST.");
                    }
                    else
                    {
                        responseBody = HandleScreenshot(request);
                    }
                    break;

                default:
                    // Handle dynamic routes
                    if (path.StartsWith("/player/") && path.EndsWith("/observe"))
                    {
                        // /player/{name}/observe
                        var playerName = path.Substring(8, path.Length - 16); // Remove "/player/" and "/observe"
                        responseBody = HandlePlayerObserve(playerName);
                    }
                    else
                    {
                        statusCode = 404;
                        responseBody = JsonError($"Not found: {path}");
                    }
                    break;
            }

            response.StatusCode = statusCode;
            response.ContentType = "application/json";
            byte[] buffer = Encoding.UTF8.GetBytes(responseBody);
            response.ContentLength64 = buffer.Length;
            response.OutputStream.Write(buffer, 0, buffer.Length);
        }
        catch (Exception ex)
        {
            _serverApi?.Logger.Error($"[VSAI] Error handling request: {ex.Message}\n{ex.StackTrace}");
            response.StatusCode = 500;
            byte[] buffer = Encoding.UTF8.GetBytes(JsonError(ex.Message));
            response.ContentLength64 = buffer.Length;
            response.OutputStream.Write(buffer, 0, buffer.Length);
        }
        finally
        {
            response.Close();
        }
    }

    private static string JsonError(string message)
    {
        return JsonSerializer.Serialize(new { error = message });
    }

    private string HandleStatus()
    {
        var players = _serverApi?.World?.AllOnlinePlayers ?? Array.Empty<IPlayer>();
        bool botActive = _botEntity != null && _botEntity.Alive;

        int pendingMessages;
        lock (_inboxLock)
        {
            pendingMessages = _chatInbox.Count;
        }

        return JsonSerializer.Serialize(new
        {
            status = "ok",
            mod = "vsai",
            version = "0.2.0",
            botName = _botName,
            playerCount = players.Length,
            players = GetPlayerNames(players),
            bot = new
            {
                active = botActive,
                entityId = botActive ? _botEntityId : 0
            },
            inbox = new
            {
                pendingCount = pendingMessages
            }
        });
    }

    private string HandlePlayers()
    {
        var players = _serverApi?.World?.AllOnlinePlayers ?? Array.Empty<IPlayer>();

        var playerList = new List<object>();
        foreach (var player in players)
        {
            var entity = player.Entity;
            if (entity != null)
            {
                playerList.Add(new
                {
                    name = player.PlayerName,
                    position = new { x = entity.Pos.X, y = entity.Pos.Y, z = entity.Pos.Z },
                    alive = entity.Alive
                });
            }
        }

        return JsonSerializer.Serialize(new
        {
            playerCount = playerList.Count,
            players = playerList,
            world = new
            {
                timeOfDay = _serverApi?.World?.Calendar?.HourOfDay ?? 0,
                dayCount = _serverApi?.World?.Calendar?.TotalDays ?? 0
            }
        });
    }

    private string HandlePlayerObserve(string playerName)
    {
        var players = _serverApi?.World?.AllOnlinePlayers ?? Array.Empty<IPlayer>();
        IPlayer? targetPlayer = null;

        foreach (var p in players)
        {
            if (p.PlayerName.Equals(playerName, StringComparison.OrdinalIgnoreCase))
            {
                targetPlayer = p;
                break;
            }
        }

        if (targetPlayer?.Entity == null)
        {
            return JsonError($"Player not found: {playerName}");
        }

        var entity = targetPlayer.Entity;
        var pos = entity.Pos;

        return JsonSerializer.Serialize(new
        {
            player = new
            {
                name = targetPlayer.PlayerName,
                position = new { x = pos.X, y = pos.Y, z = pos.Z },
                rotation = new { yaw = pos.Yaw, pitch = pos.Pitch },
                alive = entity.Alive,
                onGround = entity.OnGround
            },
            world = new
            {
                timeOfDay = _serverApi?.World?.Calendar?.HourOfDay ?? 0,
                dayCount = _serverApi?.World?.Calendar?.TotalDays ?? 0
            }
        });
    }

    private string HandleBotSpawn(HttpListenerRequest request)
    {
        var player = GetFirstPlayer();
        if (player?.Entity == null)
        {
            return JsonError("No player connected to spawn bot near");
        }

        var body = ReadRequestBody(request);
        double offsetX = 2;
        double offsetZ = 0;
        string entityCode = "vsai:aibot";  // Custom entity with TaskAI but no autonomous tasks

        if (!string.IsNullOrEmpty(body))
        {
            try
            {
                using var doc = JsonDocument.Parse(body);
                var root = doc.RootElement;

                if (root.TryGetProperty("offsetX", out var ox))
                    offsetX = ox.GetDouble();
                if (root.TryGetProperty("offsetZ", out var oz))
                    offsetZ = oz.GetDouble();
                if (root.TryGetProperty("entityCode", out var ec))
                    entityCode = ec.GetString() ?? entityCode;
            }
            catch (JsonException)
            {
                // Use defaults
            }
        }

        var playerPos = player.Entity.ServerPos;
        var spawnPos = new Vec3d(
            playerPos.X + offsetX,
            playerPos.Y,
            playerPos.Z + offsetZ
        );

        // Execute on main thread
        string? errorMsg = null;
        bool success = false;
        var waitHandle = new ManualResetEventSlim(false);

        _serverApi?.Event.EnqueueMainThreadTask(() =>
        {
            try
            {
                // Log current entity count before spawn
                int existingCount = _serverApi.World.LoadedEntities.Values.Count(e => e.Code?.Path == "aibot");
                _serverApi.Logger.Notification($"[VSAI] Before spawn: {existingCount} aibot entities in LoadedEntities");

                _botEntity = null;
                _botEntityId = 0;

                var entityType = _serverApi.World.GetEntityType(new AssetLocation(entityCode));
                if (entityType == null)
                {
                    errorMsg = $"Entity type not found: {entityCode}";
                    waitHandle.Set();
                    return;
                }

                _botEntity = _serverApi.World.ClassRegistry.CreateEntity(entityType);
                if (_botEntity == null)
                {
                    errorMsg = "Failed to create entity";
                    waitHandle.Set();
                    return;
                }

                _botEntity.ServerPos.SetPos(spawnPos);
                _botEntity.ServerPos.SetYaw((float)playerPos.Yaw);
                _botEntity.Pos.SetFrom(_botEntity.ServerPos);

                _serverApi.World.SpawnEntity(_botEntity);
                _botEntityId = _botEntity.EntityId;

                // Force load chunks around the spawn position
                int spawnChunkX = (int)Math.Floor(spawnPos.X / ChunkSize);
                int spawnChunkZ = (int)Math.Floor(spawnPos.Z / ChunkSize);
                _lastBotChunkX = spawnChunkX;
                _lastBotChunkZ = spawnChunkZ;
                EnsureBotChunksLoaded(spawnChunkX, spawnChunkZ);

                // Log entity count after spawn
                int afterCount = _serverApi.World.LoadedEntities.Values.Count(e => e.Code?.Path == "aibot");
                _serverApi.Logger.Notification($"[VSAI] Bot spawned: {entityCode} at {spawnPos}, EntityId={_botEntityId}");
                _serverApi.Logger.Notification($"[VSAI] After spawn: {afterCount} aibot entities in LoadedEntities, {_forceLoadedChunks.Count} chunks force-loaded");
                success = true;
            }
            catch (Exception ex)
            {
                errorMsg = ex.Message;
                _serverApi.Logger.Error($"[VSAI] Failed to spawn bot: {ex.Message}");
            }
            finally
            {
                waitHandle.Set();
            }
        }, "VSAI-SpawnBot");

        waitHandle.Wait(5000);

        if (!success)
        {
            return JsonError(errorMsg ?? "Timeout spawning bot");
        }

        return JsonSerializer.Serialize(new
        {
            success = true,
            bot = new
            {
                entityId = _botEntityId,
                entityCode = entityCode,
                position = new { x = spawnPos.X, y = spawnPos.Y, z = spawnPos.Z }
            }
        });
    }

    private string HandleBotDespawn()
    {
        if (_botEntity == null)
        {
            return JsonError("No bot to despawn");
        }

        DespawnBot();

        return JsonSerializer.Serialize(new { success = true, message = "Bot despawned" });
    }

    private void DespawnBot()
    {
        if (_botEntity != null)
        {
            var entityToRemove = _botEntity;
            _serverApi?.Event.EnqueueMainThreadTask(() =>
            {
                entityToRemove.Die();
            }, "VSAI-DespawnBot");

            _botEntity = null;
            _botEntityId = 0;

            // Unload force-loaded chunks when bot despawns
            ClearChunkTrackingState();

            _serverApi?.Logger.Notification("[VSAI] Bot despawned");
        }
    }

    /// <summary>
    /// Called when any entity dies. We track bot deaths to help diagnose issues.
    /// </summary>
    private void OnEntityDeath(Entity entity, DamageSource? damageSource)
    {
        // Only care about our tracked bot
        if (entity.EntityId != _botEntityId) return;

        var pos = entity.ServerPos;
        string damageInfo = "unknown cause";

        if (damageSource != null)
        {
            var sourceEntity = damageSource.SourceEntity;
            var causeEntity = damageSource.CauseEntity;
            string sourceName = sourceEntity?.Code?.Path ?? "none";
            string causeName = causeEntity?.Code?.Path ?? "none";

            damageInfo = $"type={damageSource.Type}, source={sourceName}, cause={causeName}";
        }

        _serverApi?.Logger.Notification(
            $"[VSAI] BOT DIED at ({pos.X:F1}, {pos.Y:F1}, {pos.Z:F1}) - {damageInfo}"
        );

        // Clear our reference since the bot is dead
        _botEntity = null;
        _botEntityId = 0;

        // Unload force-loaded chunks when bot dies
        ClearChunkTrackingState();
    }

    /// <summary>
    /// Called when any player sends a chat message. Checks if message is addressed to the bot.
    /// </summary>
    private void OnPlayerChat(IServerPlayer byPlayer, int channelId, ref string message, ref string data, BoolRef consumed)
    {
        if (string.IsNullOrEmpty(message)) return;

        // Strip HTML player name prefix: "<strong>PlayerName:</strong> actual message"
        string actualMessage = message;
        string playerPrefix = $"<strong>{byPlayer.PlayerName}:</strong> ";
        if (message.StartsWith(playerPrefix))
        {
            actualMessage = message.Substring(playerPrefix.Length);
        }

        string lowerMessage = actualMessage.ToLower();
        string lowerBotName = _botName.ToLower();
        string? content = null;

        // Check "BotName:" or "BotName," prefix
        if (lowerMessage.StartsWith($"{lowerBotName}:") || lowerMessage.StartsWith($"{lowerBotName},"))
        {
            content = actualMessage.Substring(_botName.Length + 1).Trim();
        }
        // Check "@BotName " prefix
        else if (lowerMessage.StartsWith($"@{lowerBotName} "))
        {
            content = actualMessage.Substring(_botName.Length + 2).Trim();
        }

        if (!string.IsNullOrEmpty(content))
        {
            lock (_inboxLock)
            {
                _chatInbox.Enqueue(new ChatMessage
                {
                    Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                    PlayerName = byPlayer.PlayerName,
                    Content = content
                });
                while (_chatInbox.Count > MaxInboxSize)
                {
                    _chatInbox.Dequeue();
                }
            }
            _serverApi?.Logger.Debug($"[VSAI] Inbox from {byPlayer.PlayerName}: {content}");
        }
        // Don't consume - message still appears in chat
    }

    /// <summary>
    /// Get the AiTaskRemoteControl from the bot entity.
    /// </summary>
    private AiTaskRemoteControl? GetRemoteControlTask()
    {
        if (_botEntity is not EntityAgent agent) return null;

        var taskAi = agent.GetBehavior<EntityBehaviorTaskAI>();
        if (taskAi == null) return null;

        // Find our task in the task manager
        foreach (var task in taskAi.TaskManager.AllTasks)
        {
            if (task is AiTaskRemoteControl remoteTask)
            {
                return remoteTask;
            }
        }
        return null;
    }

    private string HandleBotCleanup()
    {
        // Just despawn the tracked bot - don't iterate through all entities
        // as that triggers chunk loading which resurrects old bots from save
        if (_botEntity == null)
        {
            return JsonSerializer.Serialize(new
            {
                success = true,
                message = "No tracked bot to clean up"
            });
        }

        var waitHandle = new ManualResetEventSlim(false);
        string? errorMsg = null;
        long entityId = _botEntityId;

        _serverApi?.Event.EnqueueMainThreadTask(() =>
        {
            try
            {
                if (_botEntity != null)
                {
                    _botEntity.Die(EnumDespawnReason.Removed, null);
                    _serverApi.World.DespawnEntity(_botEntity, new EntityDespawnData { Reason = EnumDespawnReason.Removed });
                    _serverApi.Logger.Notification($"[VSAI] Despawned tracked bot entity {_botEntityId}");
                }

                _botEntity = null;
                _botEntityId = 0;
            }
            catch (Exception ex)
            {
                errorMsg = ex.Message;
            }
            finally
            {
                waitHandle.Set();
            }
        }, "VSAI-CleanupBots");

        waitHandle.Wait(5000);

        if (errorMsg != null)
        {
            return JsonError(errorMsg);
        }

        return JsonSerializer.Serialize(new
        {
            success = true,
            message = $"Cleaned up tracked bot (ID: {entityId})"
        });
    }

    private string HandleBotObserve()
    {
        if (_botEntity == null || !_botEntity.Alive)
        {
            return JsonError("No active bot. Spawn one first with POST /bot/spawn");
        }

        var pos = _botEntity.ServerPos;

        // Diagnostic: check if entity is actually in the world's LoadedEntities
        bool inLoadedEntities = _serverApi?.World.LoadedEntities.ContainsKey(_botEntityId) ?? false;
        string? entityState = _botEntity.State.ToString();

        // Get health info if available
        float currentHealth = 0;
        float maxHealth = 0;
        if (_botEntity is EntityAgent agent)
        {
            var healthBehavior = agent.GetBehavior<EntityBehaviorHealth>();
            if (healthBehavior != null)
            {
                currentHealth = healthBehavior.Health;
                maxHealth = healthBehavior.MaxHealth;
            }
        }

        return JsonSerializer.Serialize(new
        {
            bot = new
            {
                entityId = _botEntityId,
                position = new { x = pos.X, y = pos.Y, z = pos.Z },
                rotation = new { yaw = pos.Yaw, pitch = pos.Pitch },
                alive = _botEntity.Alive,
                health = currentHealth,
                maxHealth = maxHealth,
                onGround = _botEntity.OnGround,
                inWater = _botEntity.Swimming,
                inLoadedEntities = inLoadedEntities,
                state = entityState
            }
        });
    }

    private string HandleBotObserveBlocks(HttpListenerRequest request)
    {
        if (_botEntity == null || !_botEntity.Alive)
        {
            return JsonError("No active bot");
        }

        int radius = 3;
        if (int.TryParse(request.QueryString["radius"], out int r))
        {
            radius = Math.Clamp(r, 1, 32);
        }

        var pos = _botEntity.ServerPos.AsBlockPos;
        var blocks = new List<object>();
        var blockAccessor = _serverApi?.World?.BlockAccessor;

        if (blockAccessor == null)
        {
            return JsonError("World not available");
        }

        for (int x = -radius; x <= radius; x++)
        {
            for (int y = -radius; y <= radius; y++)
            {
                for (int z = -radius; z <= radius; z++)
                {
                    var blockPos = new BlockPos(pos.X + x, pos.Y + y, pos.Z + z, pos.dimension);
                    var block = blockAccessor.GetBlock(blockPos);

                    if (block != null && block.Code != null && block.Code.Path != "air")
                    {
                        blocks.Add(new
                        {
                            relativePos = new { x, y, z },
                            worldPos = new { x = blockPos.X, y = blockPos.Y, z = blockPos.Z },
                            code = block.Code.Path,
                            fullCode = block.Code.ToString(),
                            isSolid = block.SideSolid.All
                        });
                    }
                }
            }
        }

        return JsonSerializer.Serialize(new
        {
            botPos = new { x = pos.X, y = pos.Y, z = pos.Z },
            radius = radius,
            blockCount = blocks.Count,
            blocks = blocks
        });
    }

    private string HandleBotObserveEntities(HttpListenerRequest request)
    {
        if (_botEntity == null || !_botEntity.Alive)
        {
            return JsonError("No active bot");
        }

        double radius = 16;
        if (double.TryParse(request.QueryString["radius"], out double r))
        {
            radius = Math.Clamp(r, 1, 64);
        }

        var pos = _botEntity.ServerPos.XYZ;
        var nearbyEntities = _serverApi?.World?.GetEntitiesAround(pos, (float)radius, (float)radius, e => e.EntityId != _botEntityId);

        var entities = new List<object>();
        if (nearbyEntities != null)
        {
            foreach (var entity in nearbyEntities)
            {
                entities.Add(new
                {
                    entityId = entity.EntityId,
                    type = entity.Code?.Path ?? "unknown",
                    position = new { x = entity.Pos.X, y = entity.Pos.Y, z = entity.Pos.Z },
                    distance = pos.DistanceTo(entity.Pos.XYZ),
                    alive = entity.Alive,
                    isPlayer = entity is EntityPlayer
                });
            }
        }

        return JsonSerializer.Serialize(new
        {
            botPos = new { x = pos.X, y = pos.Y, z = pos.Z },
            radius = radius,
            entityCount = entities.Count,
            entities = entities
        });
    }

    /// <summary>
    /// Mine a block with tool tier checking. Only gives drops if equipped tool has sufficient tier.
    /// </summary>
    private string HandleBotMine(HttpListenerRequest request)
    {
        if (_botEntity == null || !_botEntity.Alive)
        {
            return JsonError("No active bot");
        }

        if (_botEntity is not EntityAgent agent)
        {
            return JsonError("Bot is not an EntityAgent");
        }

        var body = ReadRequestBody(request);
        if (string.IsNullOrEmpty(body))
        {
            return JsonError("Empty request body. Provide x, y, z block coordinates.");
        }

        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;

            int x, y, z;

            // Support relative coordinates from bot position
            if (root.TryGetProperty("relative", out var relative) && relative.GetBoolean())
            {
                var botBlockPos = _botEntity.ServerPos.AsBlockPos;
                int dx = root.TryGetProperty("x", out var dxEl) ? dxEl.GetInt32() : 0;
                int dy = root.TryGetProperty("y", out var dyEl) ? dyEl.GetInt32() : 0;
                int dz = root.TryGetProperty("z", out var dzEl) ? dzEl.GetInt32() : 0;

                x = botBlockPos.X + dx;
                y = botBlockPos.Y + dy;
                z = botBlockPos.Z + dz;
            }
            else
            {
                if (!root.TryGetProperty("x", out var xEl) ||
                    !root.TryGetProperty("y", out var yEl) ||
                    !root.TryGetProperty("z", out var zEl))
                {
                    return JsonError("Missing x, y, or z coordinates");
                }

                x = xEl.GetInt32();
                y = yEl.GetInt32();
                z = zEl.GetInt32();
            }

            var blockAccessor = _serverApi?.World?.BlockAccessor;
            if (blockAccessor == null)
            {
                return JsonError("World not available");
            }

            var blockPos = new BlockPos(x, y, z, _botEntity.ServerPos.Dimension);
            var block = blockAccessor.GetBlock(blockPos);

            if (block == null || block.Code?.Path == "air")
            {
                return JsonError($"No block at position ({x}, {y}, {z})");
            }

            string blockCode = block.Code?.Path ?? "unknown";

            // Get equipped tool from right hand
            var toolSlot = agent.RightHandItemSlot;
            var toolStack = toolSlot?.Itemstack;
            int toolTier = toolStack?.Collectible?.ToolTier ?? 0;
            string toolCode = toolStack?.Collectible?.Code?.Path ?? "none";

            // Get block's required mining tier
            int requiredTier = block.RequiredMiningTier;

            // Check if tool tier is sufficient
            bool canGetDrops = toolTier >= requiredTier;

            var drops = new List<string>();
            var waitHandle = new ManualResetEventSlim(false);
            string? errorMsg = null;

            _serverApi?.Event.EnqueueMainThreadTask(() =>
            {
                try
                {
                    // Get drops only if tool tier is sufficient
                    ItemStack[]? blockDrops = null;
                    if (canGetDrops)
                    {
                        blockDrops = block.GetDrops(_serverApi.World, blockPos, null);
                        if (blockDrops != null)
                        {
                            foreach (var drop in blockDrops)
                            {
                                drops.Add($"{drop.StackSize}x {drop.Collectible?.Code?.Path ?? "unknown"}");
                            }
                        }
                    }

                    // Break the block (set to air)
                    blockAccessor.SetBlock(0, blockPos);
                    blockAccessor.TriggerNeighbourBlockUpdate(blockPos);

                    // Spawn drops as items in the world (only if we got drops)
                    if (blockDrops != null)
                    {
                        foreach (var drop in blockDrops)
                        {
                            _serverApi.World.SpawnItemEntity(drop, new Vec3d(x + 0.5, y + 0.5, z + 0.5));
                        }
                    }

                    _serverApi.Logger.Debug($"[VSAI] Bot mined block {blockCode} at ({x}, {y}, {z}), tool={toolCode}, tier={toolTier}, required={requiredTier}, dropsObtained={canGetDrops}");
                }
                catch (Exception ex)
                {
                    errorMsg = ex.Message;
                }
                finally
                {
                    waitHandle.Set();
                }
            }, "VSAI-MineBlock");

            waitHandle.Wait(2000);

            if (errorMsg != null)
            {
                return JsonError(errorMsg);
            }

            return JsonSerializer.Serialize(new
            {
                success = true,
                brokenBlock = blockCode,
                position = new { x, y, z },
                tool = new { code = toolCode, tier = toolTier },
                requiredTier = requiredTier,
                dropsObtained = canGetDrops,
                drops = drops
            });
        }
        catch (JsonException ex)
        {
            return JsonError($"Invalid JSON: {ex.Message}");
        }
    }

    private string HandleBotUseTool(HttpListenerRequest request)
    {
        if (_botEntity == null || !_botEntity.Alive)
        {
            return JsonError("No active bot");
        }

        if (_botEntity is not EntityAgent agent)
        {
            return JsonError("Bot is not an EntityAgent");
        }

        var body = ReadRequestBody(request);
        if (string.IsNullOrEmpty(body))
        {
            return JsonError("Empty request body. Provide x, y, z block coordinates.");
        }

        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;

            int x, y, z;

            // Support relative coordinates from bot position
            if (root.TryGetProperty("relative", out var relative) && relative.GetBoolean())
            {
                var botBlockPos = _botEntity.ServerPos.AsBlockPos;
                int dx = root.TryGetProperty("x", out var dxEl) ? dxEl.GetInt32() : 0;
                int dy = root.TryGetProperty("y", out var dyEl) ? dyEl.GetInt32() : 0;
                int dz = root.TryGetProperty("z", out var dzEl) ? dzEl.GetInt32() : 0;

                x = botBlockPos.X + dx;
                y = botBlockPos.Y + dy;
                z = botBlockPos.Z + dz;
            }
            else
            {
                if (!root.TryGetProperty("x", out var xEl) ||
                    !root.TryGetProperty("y", out var yEl) ||
                    !root.TryGetProperty("z", out var zEl))
                {
                    return JsonError("Missing x, y, or z coordinates");
                }

                x = xEl.GetInt32();
                y = yEl.GetInt32();
                z = zEl.GetInt32();
            }

            var blockAccessor = _serverApi?.World?.BlockAccessor;
            if (blockAccessor == null)
            {
                return JsonError("World not available");
            }

            var blockPos = new BlockPos(x, y, z, _botEntity.ServerPos.Dimension);
            var blockBefore = blockAccessor.GetBlock(blockPos);

            if (blockBefore == null || blockBefore.Code?.Path == "air")
            {
                return JsonError($"No block at position ({x}, {y}, {z})");
            }

            // Get equipped tool from right hand
            var toolSlot = agent.RightHandItemSlot;
            var toolStack = toolSlot?.Itemstack;
            if (toolStack == null)
            {
                return JsonError("No tool equipped in right hand");
            }

            var collectible = toolStack.Collectible;
            if (collectible == null)
            {
                return JsonError("Tool has no collectible data");
            }
            string toolCode = collectible.Code?.Path ?? "unknown";
            string blockCodeBefore = blockBefore.Code?.Path ?? "unknown";

            string? resultMsg = null;
            string? errorMsg = null;
            bool actionTaken = false;
            string? blockCodeAfter = null;
            var waitHandle = new ManualResetEventSlim(false);

            _serverApi?.Event.EnqueueMainThreadTask(() =>
            {
                try
                {
                    // Create BlockSelection for the target
                    var blockSel = new BlockSelection
                    {
                        Position = blockPos,
                        Face = BlockFacing.UP
                    };

                    var world = _serverApi.World;
                    var droppedItems = new List<string>();

                    // First, check if block has tool-specific drops (e.g., tallgrass with knife)
                    // VS uses "tool" property in drops array to filter by tool type
                    var blockDrops = blockBefore.Drops;
                    bool hasToolSpecificDrop = false;

                    // Get the tool type from the collectible
                    var toolType = collectible.Tool;

                    if (blockDrops != null && toolType != null)
                    {
                        foreach (var drop in blockDrops)
                        {
                            if (drop?.Tool == toolType)
                            {
                                hasToolSpecificDrop = true;
                                break;
                            }
                        }
                    }

                    if (hasToolSpecificDrop)
                    {
                        // Manually process drops that match our tool type
                        // VS GetDrops doesn't take tool directly - it checks the player's tool
                        // Since we have an EntityAgent not IPlayer, we manually filter
                        foreach (var drop in blockDrops)
                        {
                            if (drop?.Tool != toolType) continue;

                            // Resolve the drop stack
                            var itemStack = drop.GetNextItemStack();
                            if (itemStack == null) continue;

                            int originalSize = itemStack.StackSize;
                            bool given = agent.TryGiveItemStack(itemStack);
                            int givenCount = given ? (originalSize - itemStack.StackSize) : 0;
                            if (givenCount > 0)
                            {
                                droppedItems.Add($"{givenCount}x {itemStack.Collectible?.Code?.Path ?? "unknown"}");
                            }
                            // Drop remainder in world
                            if (itemStack.StackSize > 0)
                            {
                                world.SpawnItemEntity(itemStack, new Vec3d(blockPos.X + 0.5, blockPos.Y + 0.5, blockPos.Z + 0.5));
                                droppedItems.Add($"{itemStack.StackSize}x {itemStack.Collectible?.Code?.Path ?? "unknown"} (dropped)");
                            }
                        }

                        // Remove the block (harvest it)
                        blockAccessor.SetBlock(0, blockPos);
                        actionTaken = true;
                    }
                    // Check for Harvestable behavior on the block (berries, resin, etc.)
                    else
                    {
                        var harvestBehavior = blockBefore.GetBehavior<BlockBehaviorHarvestable>();
                        if (harvestBehavior != null)
                        {
                            // Use reflection to access private/internal fields
                            var behaviorType = harvestBehavior.GetType();
                            var stacksField = behaviorType.GetField("harvestedStacks", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                            var blockCodeField = behaviorType.GetField("harvestedBlockCode", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

                            var harvestedStacks = stacksField?.GetValue(harvestBehavior) as BlockDropItemStack[];
                            var harvestedBlockCode = blockCodeField?.GetValue(harvestBehavior) as AssetLocation;

                            // Give harvested items to bot
                            if (harvestedStacks != null)
                            {
                                foreach (var dropStack in harvestedStacks)
                                {
                                    if (dropStack == null) continue;

                                    // Resolve the drop stack
                                    var itemStack = dropStack.GetNextItemStack();
                                    if (itemStack != null)
                                    {
                                        int originalSize = itemStack.StackSize;
                                        // Try to give to bot
                                        bool given = agent.TryGiveItemStack(itemStack);
                                        int givenCount = given ? (originalSize - itemStack.StackSize) : 0;
                                        if (givenCount > 0)
                                        {
                                            droppedItems.Add($"{givenCount}x {itemStack.Collectible?.Code?.Path ?? "unknown"}");
                                        }
                                        // Drop remainder in world
                                        if (itemStack.StackSize > 0)
                                        {
                                            world.SpawnItemEntity(itemStack, new Vec3d(blockPos.X + 0.5, blockPos.Y + 0.5, blockPos.Z + 0.5));
                                            droppedItems.Add($"{itemStack.StackSize}x {itemStack.Collectible?.Code?.Path ?? "unknown"} (dropped)");
                                        }
                                    }
                                }
                            }

                            // Replace block with harvested block (usually air)
                            if (harvestedBlockCode != null)
                            {
                                var newBlock = world.GetBlock(harvestedBlockCode);
                                if (newBlock != null)
                                {
                                    blockAccessor.SetBlock(newBlock.BlockId, blockPos);
                                }
                                else
                                {
                                    blockAccessor.SetBlock(0, blockPos); // Set to air
                                }
                            }
                            else
                            {
                                blockAccessor.SetBlock(0, blockPos); // Set to air
                            }

                            actionTaken = true;
                        }
                        else
                        {
                            // No harvestable behavior - try tool's OnHeldAttack methods
                            var attackHandling = EnumHandHandling.NotHandled;
                            collectible.OnHeldAttackStart(toolSlot, agent, blockSel, null, ref attackHandling);

                            if (attackHandling != EnumHandHandling.NotHandled)
                            {
                                // Tool handled the attack - simulate completion
                                bool continueAttack = collectible.OnHeldAttackStep(10f, toolSlot, agent, blockSel, null);
                                collectible.OnHeldAttackStop(10f, toolSlot, agent, blockSel, null);
                                actionTaken = true;
                            }
                        }
                    }

                    // Check what happened to the block
                    var blockAfter = blockAccessor.GetBlock(blockPos);
                    blockCodeAfter = blockAfter?.Code?.Path ?? "air";

                    if (blockCodeAfter != blockCodeBefore)
                    {
                        if (droppedItems.Count > 0)
                        {
                            resultMsg = $"Harvested {blockCodeBefore} with {toolCode} - got {string.Join(", ", droppedItems)}";
                        }
                        else
                        {
                            resultMsg = $"Used {toolCode} on {blockCodeBefore} - block changed to {blockCodeAfter}";
                        }
                    }
                    else if (actionTaken)
                    {
                        resultMsg = $"Used {toolCode} on {blockCodeBefore} - action performed but block unchanged";
                    }
                    else
                    {
                        resultMsg = $"Tool {toolCode} did not handle action on {blockCodeBefore} (no matching tool drops or harvestable behavior)";
                    }

                    _serverApi.Logger.Notification($"[VSAI] Bot used tool: {resultMsg}");
                }
                catch (Exception ex)
                {
                    errorMsg = ex.Message;
                    _serverApi.Logger.Error($"[VSAI] UseTool error: {ex.Message}\n{ex.StackTrace}");
                }
                finally
                {
                    waitHandle.Set();
                }
            }, "VSAI-UseTool");

            waitHandle.Wait(5000);

            if (errorMsg != null)
            {
                return JsonError(errorMsg);
            }

            return JsonSerializer.Serialize(new
            {
                success = true,
                message = resultMsg,
                tool = toolCode,
                blockBefore = blockCodeBefore,
                blockAfter = blockCodeAfter,
                position = new { x, y, z },
                actionTaken
            });
        }
        catch (JsonException ex)
        {
            return JsonError($"Invalid JSON: {ex.Message}");
        }
    }

    /// <summary>
    /// Chase and attack a target entity with equipped melee weapon.
    /// </summary>
    private string HandleBotAttack(HttpListenerRequest request)
    {
        if (_botEntity == null || !_botEntity.Alive)
        {
            return JsonError("No active bot");
        }

        if (_botEntity is not EntityAgent agent)
        {
            return JsonError("Bot is not an EntityAgent");
        }

        var body = ReadRequestBody(request);
        if (string.IsNullOrEmpty(body))
        {
            return JsonError("Empty request body. Provide entityId.");
        }

        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;

            if (!root.TryGetProperty("entityId", out var entityIdEl))
            {
                return JsonError("Missing entityId parameter");
            }

            long targetEntityId = entityIdEl.GetInt64();
            float maxChaseDistance = 30f;
            if (root.TryGetProperty("maxChaseDistance", out var maxDistEl))
            {
                maxChaseDistance = maxDistEl.GetSingle();
            }

            // Find target entity on main thread
            Entity? targetEntity = null;
            string? findError = null;
            var findHandle = new ManualResetEventSlim(false);

            _serverApi?.Event.EnqueueMainThreadTask(() =>
            {
                var nearbyEntities = _serverApi?.World?.GetEntitiesAround(
                    _botEntity.ServerPos.XYZ, 100f, 100f,
                    e => e.EntityId == targetEntityId
                );
                if (nearbyEntities != null && nearbyEntities.Any())
                {
                    targetEntity = nearbyEntities.First();
                }
                if (targetEntity == null)
                {
                    findError = $"Entity {targetEntityId} not found within 100 blocks";
                }
                else if (!targetEntity.Alive)
                {
                    findError = $"Entity {targetEntityId} is already dead";
                }
                findHandle.Set();
            }, "VSAI-FindTarget");

            findHandle.Wait(5000);

            if (findError != null)
            {
                return JsonError(findError);
            }

            if (targetEntity == null)
            {
                return JsonError("Failed to find target entity");
            }

            // Get weapon info
            var toolSlot = agent.RightHandItemSlot;
            var toolStack = toolSlot?.Itemstack;
            float attackPower = 0.5f;  // Fist damage
            float attackRange = 2.0f;
            string weaponCode = "fist";

            if (toolStack?.Collectible != null)
            {
                attackPower = toolStack.Collectible.GetAttackPower(toolStack);
                attackRange = toolStack.Collectible.AttackRange > 0
                    ? toolStack.Collectible.AttackRange
                    : 2.0f;
                weaponCode = toolStack.Collectible.Code?.Path ?? "unknown";
            }

            // Chase-attack state - shared between threads
            string result = "error";
            string message = "";
            float totalDamage = 0;
            int attackCount = 0;
            float targetHealth = 0;
            string targetCode = targetEntity.Code?.Path ?? "unknown";
            var completionHandle = new ManualResetEventSlim(false);
            long lastAttackTime = 0;
            long attackCooldownMs = 500;

            // Run chase-attack loop in background thread, dispatch actions to main thread
            var combatThread = new Thread(() =>
            {
                try
                {
                    int maxIterations = 300;  // ~30 seconds at 100ms check rate
                    int iteration = 0;

                    while (iteration < maxIterations)
                    {
                        iteration++;

                        // Variables to capture from main thread
                        bool botAlive = false;
                        bool targetAlive = false;
                        double distance = 0;
                        Vec3d? targetPos = null;

                        // Check state on main thread
                        var stateHandle = new ManualResetEventSlim(false);
                        _serverApi?.Event.EnqueueMainThreadTask(() =>
                        {
                            botAlive = _botEntity?.Alive ?? false;
                            targetAlive = targetEntity?.Alive ?? false;
                            if (botAlive && _botEntity != null && targetEntity != null)
                            {
                                distance = _botEntity.ServerPos.XYZ.DistanceTo(targetEntity.ServerPos.XYZ);
                                targetPos = targetEntity.ServerPos.XYZ.Clone();
                            }
                            stateHandle.Set();
                        }, "VSAI-CheckState");
                        stateHandle.Wait(1000);

                        // Check if bot died
                        if (!botAlive)
                        {
                            result = "bot_died";
                            message = "Bot died during combat";
                            break;
                        }

                        // Check if target died
                        if (!targetAlive)
                        {
                            result = "killed";
                            message = $"Killed {targetCode} after {attackCount} attacks";
                            break;
                        }

                        // Check if target escaped
                        if (distance > maxChaseDistance)
                        {
                            result = "escaped";
                            message = $"Target escaped beyond {maxChaseDistance} blocks (distance: {distance:F1})";
                            // Get final health
                            var healthHandle = new ManualResetEventSlim(false);
                            _serverApi?.Event.EnqueueMainThreadTask(() =>
                            {
                                targetHealth = GetEntityHealth(targetEntity);
                                healthHandle.Set();
                            }, "VSAI-GetHealth");
                            healthHandle.Wait(1000);
                            break;
                        }

                        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

                        // In range - attack!
                        if (distance <= attackRange + 0.5 && (now - lastAttackTime) >= attackCooldownMs)
                        {
                            var attackHandle = new ManualResetEventSlim(false);
                            bool hit = false;

                            _serverApi?.Event.EnqueueMainThreadTask(() =>
                            {
                                var damageSource = new DamageSource
                                {
                                    Type = EnumDamageType.BluntAttack,
                                    SourceEntity = _botEntity,
                                    CauseEntity = _botEntity,
                                    KnockbackStrength = 0.3f
                                };

                                hit = targetEntity.ReceiveDamage(damageSource, attackPower);
                                attackHandle.Set();
                            }, "VSAI-Attack");

                            attackHandle.Wait(1000);

                            if (hit)
                            {
                                totalDamage += attackPower;
                                attackCount++;
                                lastAttackTime = now;
                                _serverApi?.Logger.Debug($"[VSAI] Attack hit! Damage: {attackPower}, Total: {totalDamage}");
                            }
                        }
                        else if (distance > attackRange + 0.5 && targetPos != null)
                        {
                            // Out of range - chase (update target position)
                            _serverApi?.Event.EnqueueMainThreadTask(() =>
                            {
                                var task = GetRemoteControlTask();
                                task?.SetTarget(targetPos, 0.04f);
                            }, "VSAI-Chase");
                        }

                        // Wait before next iteration (in background thread, not main thread)
                        Thread.Sleep(100);
                    }

                    if (iteration >= maxIterations && result == "error")
                    {
                        result = "timeout";
                        message = "Combat timed out after 30 seconds";
                        var healthHandle = new ManualResetEventSlim(false);
                        _serverApi?.Event.EnqueueMainThreadTask(() =>
                        {
                            targetHealth = GetEntityHealth(targetEntity);
                            healthHandle.Set();
                        }, "VSAI-GetHealth");
                        healthHandle.Wait(1000);
                    }
                }
                catch (Exception ex)
                {
                    result = "error";
                    message = ex.Message;
                    _serverApi?.Logger.Error($"[VSAI] Attack error: {ex.Message}\n{ex.StackTrace}");
                }
                finally
                {
                    // Stop movement when combat ends
                    _serverApi?.Event.EnqueueMainThreadTask(() =>
                    {
                        var task = GetRemoteControlTask();
                        task?.Stop();
                    }, "VSAI-StopCombat");
                    completionHandle.Set();
                }
            });

            combatThread.IsBackground = true;
            combatThread.Start();

            // Wait for combat to complete (up to 60 seconds)
            completionHandle.Wait(TimeSpan.FromSeconds(60));

            return JsonSerializer.Serialize(new
            {
                success = result == "killed",
                result,
                message,
                damageDealt = totalDamage,
                attackCount,
                targetHealth,
                weapon = weaponCode,
                attackPower,
                attackRange
            });
        }
        catch (JsonException ex)
        {
            return JsonError($"Invalid JSON: {ex.Message}");
        }
    }

    /// <summary>
    /// Get the current health of an entity.
    /// </summary>
    private float GetEntityHealth(Entity entity)
    {
        var healthBehavior = entity.GetBehavior<EntityBehaviorHealth>();
        return healthBehavior?.Health ?? 0;
    }

    private string HandleBotPlaceBlock(HttpListenerRequest request)
    {
        if (_botEntity == null || !_botEntity.Alive)
        {
            return JsonError("No active bot");
        }

        var body = ReadRequestBody(request);
        if (string.IsNullOrEmpty(body))
        {
            return JsonError("Empty request body. Provide x, y, z and blockCode.");
        }

        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;

            if (!root.TryGetProperty("blockCode", out var blockCodeEl))
            {
                return JsonError("Missing blockCode");
            }

            string blockCode = blockCodeEl.GetString() ?? "";

            int x, y, z;
            if (root.TryGetProperty("relative", out var relative) && relative.GetBoolean())
            {
                var botBlockPos = _botEntity.ServerPos.AsBlockPos;
                int dx = root.TryGetProperty("x", out var dxEl) ? dxEl.GetInt32() : 0;
                int dy = root.TryGetProperty("y", out var dyEl) ? dyEl.GetInt32() : 0;
                int dz = root.TryGetProperty("z", out var dzEl) ? dzEl.GetInt32() : 0;

                x = botBlockPos.X + dx;
                y = botBlockPos.Y + dy;
                z = botBlockPos.Z + dz;
            }
            else
            {
                if (!root.TryGetProperty("x", out var xEl) ||
                    !root.TryGetProperty("y", out var yEl) ||
                    !root.TryGetProperty("z", out var zEl))
                {
                    return JsonError("Missing x, y, or z coordinates");
                }

                x = xEl.GetInt32();
                y = yEl.GetInt32();
                z = zEl.GetInt32();
            }

            var blockAccessor = _serverApi?.World?.BlockAccessor;
            if (blockAccessor == null)
            {
                return JsonError("World not available");
            }

            var block = _serverApi?.World.GetBlock(new AssetLocation(blockCode));
            if (block == null)
            {
                return JsonError($"Block not found: {blockCode}");
            }

            var blockPos = new BlockPos(x, y, z, _botEntity.ServerPos.Dimension);

            var waitHandle = new ManualResetEventSlim(false);
            string? errorMsg = null;

            _serverApi?.Event.EnqueueMainThreadTask(() =>
            {
                try
                {
                    blockAccessor.SetBlock(block.BlockId, blockPos);
                    blockAccessor.TriggerNeighbourBlockUpdate(blockPos);
                    _serverApi.Logger.Debug($"[VSAI] Bot placed block {blockCode} at ({x}, {y}, {z})");
                }
                catch (Exception ex)
                {
                    errorMsg = ex.Message;
                }
                finally
                {
                    waitHandle.Set();
                }
            }, "VSAI-PlaceBlock");

            waitHandle.Wait(2000);

            if (errorMsg != null)
            {
                return JsonError(errorMsg);
            }

            return JsonSerializer.Serialize(new
            {
                success = true,
                placedBlock = blockCode,
                position = new { x, y, z }
            });
        }
        catch (JsonException ex)
        {
            return JsonError($"Invalid JSON: {ex.Message}");
        }
    }

    private string HandleBotInteract(HttpListenerRequest request)
    {
        if (_botEntity == null || !_botEntity.Alive)
        {
            return JsonError("No active bot");
        }

        var body = ReadRequestBody(request);
        if (string.IsNullOrEmpty(body))
        {
            return JsonError("Empty request body. Provide x, y, z coordinates.");
        }

        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;

            int x, y, z;

            // Support relative coordinates from bot position
            if (root.TryGetProperty("relative", out var relative) && relative.GetBoolean())
            {
                var botBlockPos = _botEntity.ServerPos.AsBlockPos;
                int dx = root.TryGetProperty("x", out var dxEl) ? dxEl.GetInt32() : 0;
                int dy = root.TryGetProperty("y", out var dyEl) ? dyEl.GetInt32() : 0;
                int dz = root.TryGetProperty("z", out var dzEl) ? dzEl.GetInt32() : 0;

                x = botBlockPos.X + dx;
                y = botBlockPos.Y + dy;
                z = botBlockPos.Z + dz;
            }
            else
            {
                if (!root.TryGetProperty("x", out var xEl) ||
                    !root.TryGetProperty("y", out var yEl) ||
                    !root.TryGetProperty("z", out var zEl))
                {
                    return JsonError("Missing x, y, or z coordinates");
                }

                x = xEl.GetInt32();
                y = yEl.GetInt32();
                z = zEl.GetInt32();
            }

            var blockAccessor = _serverApi?.World?.BlockAccessor;
            if (blockAccessor == null)
            {
                return JsonError("World not available");
            }

            var blockPos = new BlockPos(x, y, z, _botEntity.ServerPos.Dimension);
            var block = blockAccessor.GetBlock(blockPos);

            if (block == null || block.Code?.Path == "air")
            {
                return JsonError($"No block at position ({x}, {y}, {z})");
            }

            string blockCode = block.Code?.ToString() ?? "unknown";
            string? resultMsg = null;
            string? errorMsg = null;
            var waitHandle = new ManualResetEventSlim(false);

            _serverApi?.Event.EnqueueMainThreadTask(() =>
            {
                try
                {
                    // Create BlockSelection for the interaction
                    var blockSel = new BlockSelection
                    {
                        Position = blockPos,
                        Face = BlockFacing.NORTH  // Default face
                    };

                    // Create Caller with bot entity
                    var caller = new Caller
                    {
                        Entity = _botEntity
                    };

                    // Create activation parameters (for doors, pass "opened" = true)
                    var activationArgs = new TreeAttribute();

                    // Activate the block
                    block.Activate(_serverApi.World, caller, blockSel, activationArgs);

                    resultMsg = $"Activated block {blockCode}";
                    _serverApi.Logger.Notification($"[VSAI] Bot interacted with {blockCode} at ({x}, {y}, {z})");
                }
                catch (Exception ex)
                {
                    errorMsg = ex.Message;
                    _serverApi.Logger.Error($"[VSAI] Interact error: {ex.Message}");
                }
                finally
                {
                    waitHandle.Set();
                }
            }, "VSAI-InteractBlock");

            waitHandle.Wait(2000);

            if (errorMsg != null)
            {
                return JsonError(errorMsg);
            }

            return JsonSerializer.Serialize(new
            {
                success = true,
                message = resultMsg,
                block = blockCode,
                position = new { x, y, z }
            });
        }
        catch (JsonException ex)
        {
            return JsonError($"Invalid JSON: {ex.Message}");
        }
    }

    private string HandleBotStop()
    {
        if (_botEntity == null || !_botEntity.Alive)
        {
            return JsonError("No active bot");
        }

        var waitHandle = new ManualResetEventSlim(false);
        string? status = null;

        _serverApi?.Event.EnqueueMainThreadTask(() =>
        {
            // Stop the AI task (both pathfinding and direct walking)
            var task = GetRemoteControlTask();
            if (task != null)
            {
                task.Stop();
                task.StopDirectWalk();
                status = task.GetStatus();
            }
            waitHandle.Set();
        }, "VSAI-StopBot");

        waitHandle.Wait(2000);

        return JsonSerializer.Serialize(new
        {
            success = true,
            message = "Bot stopped",
            status = status ?? "unknown"
        });
    }

    private string HandleBotGoto(HttpListenerRequest request)
    {
        if (_botEntity == null || !_botEntity.Alive)
        {
            return JsonError("No active bot");
        }

        var body = ReadRequestBody(request);
        if (string.IsNullOrEmpty(body))
        {
            return JsonError("Empty request body. Provide x, y, z target coordinates.");
        }

        double targetX, targetY, targetZ;
        float speed = 0.12f;  // ~2.4 blocks/sec at 20 ticks/sec (player walks ~4 blocks/sec)

        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;

            if (root.TryGetProperty("relative", out var relative) && relative.GetBoolean())
            {
                double dx = root.TryGetProperty("x", out var dxEl) ? dxEl.GetDouble() : 0;
                double dy = root.TryGetProperty("y", out var dyEl) ? dyEl.GetDouble() : 0;
                double dz = root.TryGetProperty("z", out var dzEl) ? dzEl.GetDouble() : 0;

                targetX = _botEntity.ServerPos.X + dx;
                targetY = _botEntity.ServerPos.Y + dy;
                targetZ = _botEntity.ServerPos.Z + dz;
            }
            else
            {
                if (!root.TryGetProperty("x", out var xEl) ||
                    !root.TryGetProperty("y", out var yEl) ||
                    !root.TryGetProperty("z", out var zEl))
                {
                    return JsonError("Missing x, y, or z coordinates");
                }

                targetX = xEl.GetDouble();
                targetY = yEl.GetDouble();
                targetZ = zEl.GetDouble();
            }

            if (root.TryGetProperty("speed", out var speedEl))
            {
                speed = (float)speedEl.GetDouble();
            }
        }
        catch (JsonException ex)
        {
            return JsonError($"Invalid JSON: {ex.Message}");
        }

        // Use the AI task for movement
        string? errorMsg = null;
        var waitHandle = new ManualResetEventSlim(false);

        double finalTargetY = targetY;

        _serverApi?.Event.EnqueueMainThreadTask(() =>
        {
            try
            {
                // Find ground level at target X,Z
                var blockAccessor = _serverApi.World.BlockAccessor;
                int searchX = (int)Math.Floor(targetX);
                int searchZ = (int)Math.Floor(targetZ);

                // Start search from a reasonable height - use bot's current Y + 20 as max
                int startY = Math.Min((int)_botEntity.ServerPos.Y + 20, blockAccessor.MapSizeY - 1);
                int groundY = (int)targetY;

                // Scan downward to find first solid block
                var blockPos = new BlockPos(searchX, startY, searchZ);
                for (int y = startY; y >= 1; y--)
                {
                    blockPos.Y = y;
                    var block = blockAccessor.GetBlock(blockPos);
                    blockPos.Y = y - 1;
                    var blockBelow = blockAccessor.GetBlock(blockPos);

                    // Found ground: current block is air/passable and block below is solid
                    if (!block.SideSolid[BlockFacing.UP.Index] && blockBelow.SideSolid[BlockFacing.UP.Index])
                    {
                        groundY = y;
                        break;
                    }
                }

                finalTargetY = groundY;
                var adjustedTargetPos = new Vec3d(targetX, finalTargetY, targetZ);

                var task = GetRemoteControlTask();
                if (task == null)
                {
                    errorMsg = "RemoteControl AI task not found on entity";
                }
                else
                {
                    task.SetTarget(adjustedTargetPos, speed);
                    _serverApi.Logger.Notification($"[VSAI] AI task moving to ({targetX:F1}, {finalTargetY:F1}, {targetZ:F1}), speed={speed} (ground-adjusted from Y={targetY:F1})");
                }
            }
            catch (Exception ex)
            {
                errorMsg = $"Error: {ex.Message}";
            }
            finally
            {
                waitHandle.Set();
            }
        }, "VSAI-GotoTask");

        waitHandle.Wait(TimeSpan.FromSeconds(5));

        if (errorMsg != null)
        {
            return JsonError(errorMsg);
        }

        return JsonSerializer.Serialize(new
        {
            success = true,
            target = new { x = targetX, y = finalTargetY, z = targetZ },
            requestedY = targetY,
            groundY = finalTargetY,
            speed = speed
        });
    }

    private string HandleBotMovementStatus()
    {
        if (_botEntity == null || !_botEntity.Alive)
        {
            return JsonError("No active bot");
        }

        var pos = _botEntity.ServerPos;
        var task = GetRemoteControlTask();

        if (task == null)
        {
            return JsonSerializer.Serialize(new
            {
                success = true,
                status = "no_task",
                statusMessage = "RemoteControl AI task not found",
                position = new { x = pos.X, y = pos.Y, z = pos.Z },
                isActive = false
            });
        }

        var lastTarget = task.GetLastTarget();

        return JsonSerializer.Serialize(new
        {
            success = true,
            status = task.GetStatus(),
            statusMessage = task.GetStatusMessage(),
            position = new { x = pos.X, y = pos.Y, z = pos.Z },
            target = lastTarget != null ? new { x = lastTarget.X, y = lastTarget.Y, z = lastTarget.Z } : null,
            isActive = task.IsActive(),
            isDirectWalking = task.IsDirectWalking(),
            onGround = _botEntity.OnGround
        });
    }

    private string HandleBotChat(HttpListenerRequest request)
    {
        var body = ReadRequestBody(request);
        if (string.IsNullOrEmpty(body))
        {
            return JsonError("Empty request body. Provide 'message' field.");
        }

        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;

            if (!root.TryGetProperty("message", out var messageEl))
            {
                return JsonError("Missing 'message' field");
            }

            string message = messageEl.GetString() ?? "";
            if (string.IsNullOrEmpty(message))
            {
                return JsonError("Message cannot be empty");
            }

            // Optional bot name prefix (default: _botName)
            string botName = _botName;
            if (root.TryGetProperty("name", out var nameEl))
            {
                botName = nameEl.GetString() ?? _botName;
            }

            // Format the message with bot name in color
            string formattedMessage = $"<font color=\"#00ddff\" weight=\"bold\">[{botName}]</font> {message}";

            // Broadcast to all players
            _serverApi?.BroadcastMessageToAllGroups(formattedMessage, EnumChatType.OthersMessage);

            _serverApi?.Logger.Debug($"[VSAI] Bot chat: {message}");

            return JsonSerializer.Serialize(new
            {
                success = true,
                message = message,
                botName = botName
            });
        }
        catch (JsonException ex)
        {
            return JsonError($"Invalid JSON: {ex.Message}");
        }
    }

    private string HandleBotInventory()
    {
        if (_botEntity == null || !_botEntity.Alive)
        {
            return JsonError("No active bot");
        }

        if (_botEntity is not EntityAgent agent)
        {
            return JsonError("Bot is not an EntityAgent");
        }

        var inventoryBehavior = agent.GetBehavior<EntityBehaviorBotInventory>();
        if (inventoryBehavior == null)
        {
            return JsonError("Bot has no inventory behavior configured");
        }

        var inventory = inventoryBehavior.Inventory;
        if (inventory == null)
        {
            return JsonError("Bot inventory is null");
        }

        var slots = new List<object>();
        for (int i = 0; i < inventory.Count; i++)
        {
            var slot = inventory[i];
            if (slot?.Itemstack != null)
            {
                slots.Add(new
                {
                    index = i,
                    code = slot.Itemstack.Collectible?.Code?.ToString() ?? "unknown",
                    quantity = slot.Itemstack.StackSize,
                    name = slot.Itemstack.GetName()
                });
            }
            else
            {
                slots.Add(new
                {
                    index = i,
                    code = (string?)null,
                    quantity = 0,
                    name = (string?)null
                });
            }
        }

        // Get hand items
        object? leftHand = null;
        object? rightHand = null;

        if (agent.LeftHandItemSlot?.Itemstack != null)
        {
            leftHand = new
            {
                code = agent.LeftHandItemSlot.Itemstack.Collectible?.Code?.ToString() ?? "unknown",
                quantity = agent.LeftHandItemSlot.Itemstack.StackSize,
                name = agent.LeftHandItemSlot.Itemstack.GetName()
            };
        }

        if (agent.RightHandItemSlot?.Itemstack != null)
        {
            rightHand = new
            {
                code = agent.RightHandItemSlot.Itemstack.Collectible?.Code?.ToString() ?? "unknown",
                quantity = agent.RightHandItemSlot.Itemstack.StackSize,
                name = agent.RightHandItemSlot.Itemstack.GetName()
            };
        }

        return JsonSerializer.Serialize(new
        {
            success = true,
            slotCount = inventory.Count,
            handLeft = leftHand,
            handRight = rightHand,
            slots = slots
        });
    }

    private string HandleBotCollect(HttpListenerRequest request)
    {
        if (_botEntity == null || !_botEntity.Alive)
        {
            return JsonError("No active bot");
        }

        if (_botEntity is not EntityAgent agent)
        {
            return JsonError("Bot is not an EntityAgent");
        }

        var body = ReadRequestBody(request);
        if (string.IsNullOrEmpty(body))
        {
            return JsonError("Empty request body. Provide x, y, z coordinates.");
        }

        int x, y, z;

        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;

            if (!root.TryGetProperty("x", out var xEl) ||
                !root.TryGetProperty("y", out var yEl) ||
                !root.TryGetProperty("z", out var zEl))
            {
                return JsonError("Missing x, y, or z coordinates");
            }

            x = xEl.GetInt32();
            y = yEl.GetInt32();
            z = zEl.GetInt32();
        }
        catch (JsonException ex)
        {
            return JsonError($"Invalid JSON: {ex.Message}");
        }

        // Check distance to target
        var botPos = _botEntity.ServerPos.XYZ;
        var targetPos = new Vec3d(x + 0.5, y + 0.5, z + 0.5);
        var distance = botPos.DistanceTo(targetPos);

        if (distance > 5.0)
        {
            return JsonError($"Too far to collect (distance: {distance:F1}, max: 5.0)");
        }

        var blockAccessor = _serverApi?.World?.BlockAccessor;
        if (blockAccessor == null)
        {
            return JsonError("World not available");
        }

        var blockPos = new BlockPos(x, y, z, _botEntity.ServerPos.Dimension);
        var block = blockAccessor.GetBlock(blockPos);

        if (block == null || block.Code?.Path == "air")
        {
            return JsonError($"No block at position ({x}, {y}, {z})");
        }

        // Check if it's a collectible loose item
        var blockCode = block.Code?.Path ?? "";
        bool isLooseItem = blockCode.Contains("-free") ||
                           blockCode.StartsWith("stick") ||
                           blockCode.StartsWith("looseboulders") ||
                           blockCode.StartsWith("looseflints") ||
                           blockCode.StartsWith("loosestones") ||
                           blockCode.StartsWith("looseores");

        if (!isLooseItem)
        {
            return JsonError($"Block '{blockCode}' is not a collectible loose item");
        }

        // Execute on main thread
        var collectedItems = new List<object>();
        string? errorMsg = null;
        var waitHandle = new ManualResetEventSlim(false);

        _serverApi?.Event.EnqueueMainThreadTask(() =>
        {
            try
            {
                // Get drops before breaking
                var drops = block.GetDrops(_serverApi.World, blockPos, null);

                // Break the block (set to air)
                blockAccessor.SetBlock(0, blockPos);
                blockAccessor.TriggerNeighbourBlockUpdate(blockPos);

                // Add drops directly to bot inventory
                if (drops != null)
                {
                    foreach (var drop in drops)
                    {
                        var originalSize = drop.StackSize;
                        bool given = agent.TryGiveItemStack(drop);

                        int amountGiven = originalSize - drop.StackSize;
                        if (amountGiven > 0)
                        {
                            collectedItems.Add(new
                            {
                                code = drop.Collectible?.Code?.ToString() ?? "unknown",
                                quantity = amountGiven,
                                name = drop.GetName()
                            });
                        }

                        // If couldn't give all, spawn remainder in world
                        if (drop.StackSize > 0)
                        {
                            _serverApi.World.SpawnItemEntity(drop, new Vec3d(x + 0.5, y + 0.5, z + 0.5));
                            _serverApi.Logger.Warning($"[VSAI] Bot inventory full, spawned {drop.StackSize}x {drop.Collectible?.Code} in world");
                        }
                    }
                }

                _serverApi.Logger.Notification($"[VSAI] Bot collected {collectedItems.Count} item types from ({x}, {y}, {z})");
            }
            catch (Exception ex)
            {
                errorMsg = ex.Message;
            }
            finally
            {
                waitHandle.Set();
            }
        }, "VSAI-CollectItem");

        waitHandle.Wait(5000);

        if (errorMsg != null)
        {
            return JsonError(errorMsg);
        }

        return JsonSerializer.Serialize(new
        {
            success = true,
            position = new { x, y, z },
            brokenBlock = blockCode,
            collectedItems = collectedItems
        });
    }

    private string HandleBotDrop(HttpListenerRequest request)
    {
        if (_botEntity == null || !_botEntity.Alive)
        {
            return JsonError("No active bot");
        }

        if (_botEntity is not EntityAgent agent)
        {
            return JsonError("Bot is not an EntityAgent");
        }

        var inventoryBehavior = agent.GetBehavior<EntityBehaviorBotInventory>();
        if (inventoryBehavior == null)
        {
            return JsonError("Bot has no inventory behavior configured");
        }

        var inventory = inventoryBehavior.Inventory;
        if (inventory == null)
        {
            return JsonError("Bot inventory is null");
        }

        var body = ReadRequestBody(request);
        if (string.IsNullOrEmpty(body))
        {
            return JsonError("Empty request body. Provide slotIndex or itemCode, and optionally quantity.");
        }

        int? slotIndex = null;
        string? itemCode = null;
        int quantity = 1;

        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;

            if (root.TryGetProperty("slotIndex", out var slotEl))
            {
                slotIndex = slotEl.GetInt32();
            }

            if (root.TryGetProperty("itemCode", out var codeEl))
            {
                itemCode = codeEl.GetString();
            }

            if (root.TryGetProperty("quantity", out var qtyEl))
            {
                quantity = qtyEl.GetInt32();
            }

            if (slotIndex == null && string.IsNullOrEmpty(itemCode))
            {
                return JsonError("Must provide either slotIndex or itemCode");
            }
        }
        catch (JsonException ex)
        {
            return JsonError($"Invalid JSON: {ex.Message}");
        }

        // Find the slot to drop from
        int targetSlotIndex = -1;

        if (slotIndex.HasValue)
        {
            if (slotIndex.Value < 0 || slotIndex.Value >= inventory.Count)
            {
                return JsonError($"Slot index {slotIndex.Value} out of range (0-{inventory.Count - 1})");
            }
            targetSlotIndex = slotIndex.Value;
        }
        else if (!string.IsNullOrEmpty(itemCode))
        {
            // Find first slot with matching item code
            for (int i = 0; i < inventory.Count; i++)
            {
                var slot = inventory[i];
                if (slot?.Itemstack?.Collectible?.Code?.ToString()?.Contains(itemCode, StringComparison.OrdinalIgnoreCase) == true)
                {
                    targetSlotIndex = i;
                    break;
                }
            }

            if (targetSlotIndex < 0)
            {
                return JsonError($"No item matching '{itemCode}' found in inventory");
            }
        }

        var targetSlot = inventory[targetSlotIndex];
        if (targetSlot?.Itemstack == null)
        {
            return JsonError($"Slot {targetSlotIndex} is empty");
        }

        // Execute drop on main thread
        object? droppedItem = null;
        string? errorMsg = null;
        var waitHandle = new ManualResetEventSlim(false);

        _serverApi?.Event.EnqueueMainThreadTask(() =>
        {
            try
            {
                int dropQuantity = Math.Min(quantity, targetSlot.Itemstack.StackSize);
                var itemStack = targetSlot.TakeOut(dropQuantity);
                targetSlot.MarkDirty();

                if (itemStack != null)
                {
                    droppedItem = new
                    {
                        code = itemStack.Collectible?.Code?.ToString() ?? "unknown",
                        quantity = itemStack.StackSize,
                        name = itemStack.GetName()
                    };

                    // Spawn in world at bot's feet
                    _serverApi.World.SpawnItemEntity(itemStack, _botEntity.ServerPos.XYZ);
                    _serverApi.Logger.Notification($"[VSAI] Bot dropped {itemStack.StackSize}x {itemStack.Collectible?.Code}");
                }
            }
            catch (Exception ex)
            {
                errorMsg = ex.Message;
            }
            finally
            {
                waitHandle.Set();
            }
        }, "VSAI-DropItem");

        waitHandle.Wait(5000);

        if (errorMsg != null)
        {
            return JsonError(errorMsg);
        }

        return JsonSerializer.Serialize(new
        {
            success = true,
            slotIndex = targetSlotIndex,
            droppedItem = droppedItem
        });
    }

    private string HandleBotPickup(HttpListenerRequest request)
    {
        if (_botEntity == null || !_botEntity.Alive)
        {
            return JsonError("No active bot");
        }

        if (_botEntity is not EntityAgent agent)
        {
            return JsonError("Bot is not an EntityAgent");
        }

        var body = ReadRequestBody(request);
        long? entityId = null;
        double maxDistance = 5.0;

        if (!string.IsNullOrEmpty(body))
        {
            try
            {
                using var doc = JsonDocument.Parse(body);
                var root = doc.RootElement;

                if (root.TryGetProperty("entityId", out var idEl))
                {
                    entityId = idEl.GetInt64();
                }

                if (root.TryGetProperty("maxDistance", out var distEl))
                {
                    maxDistance = distEl.GetDouble();
                }
            }
            catch (JsonException ex)
            {
                return JsonError($"Invalid JSON: {ex.Message}");
            }
        }

        var botPos = _botEntity.ServerPos.XYZ;

        // Find item entities near the bot
        var nearbyItems = _serverApi?.World?.GetEntitiesAround(
            botPos,
            (float)maxDistance,
            (float)maxDistance,
            e => e is EntityItem
        );

        if (nearbyItems == null || nearbyItems.Length == 0)
        {
            return JsonError($"No item entities within {maxDistance} blocks");
        }

        // Find target entity
        Entity? targetEntity = null;

        if (entityId.HasValue)
        {
            foreach (var e in nearbyItems)
            {
                if (e.EntityId == entityId.Value)
                {
                    targetEntity = e;
                    break;
                }
            }

            if (targetEntity == null)
            {
                return JsonError($"Item entity {entityId.Value} not found within range");
            }
        }
        else
        {
            // Find nearest item entity
            double nearestDist = double.MaxValue;
            foreach (var e in nearbyItems)
            {
                double dist = botPos.DistanceTo(e.Pos.XYZ);
                if (dist < nearestDist)
                {
                    nearestDist = dist;
                    targetEntity = e;
                }
            }
        }

        if (targetEntity is not EntityItem itemEntity)
        {
            return JsonError("Target is not an item entity");
        }

        // Execute pickup on main thread
        object? pickedUpItem = null;
        string? errorMsg = null;
        var waitHandle = new ManualResetEventSlim(false);

        _serverApi?.Event.EnqueueMainThreadTask(() =>
        {
            try
            {
                var itemStack = itemEntity.Itemstack;
                if (itemStack == null)
                {
                    errorMsg = "Item entity has no itemstack";
                    return;
                }

                var originalSize = itemStack.StackSize;
                var stackToGive = itemStack.Clone();

                var inventoryBehavior = agent.GetBehavior<EntityBehaviorBotInventory>();
                var inventory = inventoryBehavior?.Inventory;

                bool given = agent.TryGiveItemStack(stackToGive);
                int amountGiven = originalSize - stackToGive.StackSize;

                // Manual slot insertion fallback if TryGiveItemStack fails
                if (amountGiven == 0 && inventory != null)
                {
                    for (int slotIdx = 0; slotIdx < inventory.Count; slotIdx++)
                    {
                        var slot = inventory[slotIdx];
                        if (slot?.Itemstack == null)
                        {
                            slot.Itemstack = stackToGive.Clone();
                            slot.MarkDirty();
                            amountGiven = originalSize;
                            break;
                        }
                    }
                }

                if (amountGiven > 0)
                {
                    pickedUpItem = new
                    {
                        code = itemStack.Collectible?.Code?.ToString() ?? "unknown",
                        quantity = amountGiven,
                        name = itemStack.GetName()
                    };

                    // Despawn the item entity
                    itemEntity.Die(EnumDespawnReason.PickedUp, null);

                    _serverApi.Logger.Notification($"[VSAI] Bot picked up {amountGiven}x {itemStack.Collectible?.Code}");
                }
                else
                {
                    errorMsg = "Could not add item to inventory (full?)";
                }
            }
            catch (Exception ex)
            {
                errorMsg = ex.Message;
            }
            finally
            {
                waitHandle.Set();
            }
        }, "VSAI-PickupItem");

        waitHandle.Wait(5000);

        if (errorMsg != null)
        {
            return JsonError(errorMsg);
        }

        return JsonSerializer.Serialize(new
        {
            success = true,
            entityId = targetEntity.EntityId,
            pickedUpItem = pickedUpItem
        });
    }

    private string HandleBotInbox(HttpListenerRequest request)
    {
        // Parse query parameters
        bool clear = request.QueryString["clear"]?.ToLower() != "false";  // default true
        int limit = 50;
        if (int.TryParse(request.QueryString["limit"], out int parsedLimit))
        {
            limit = Math.Clamp(parsedLimit, 1, 100);
        }

        List<object> messages;
        lock (_inboxLock)
        {
            messages = _chatInbox.Take(limit)
                .Select(m => (object)new { timestamp = m.Timestamp, player = m.PlayerName, message = m.Content })
                .ToList();
            if (clear)
            {
                _chatInbox.Clear();
            }
        }

        return JsonSerializer.Serialize(new
        {
            success = true,
            botName = _botName,
            messageCount = messages.Count,
            messages = messages
        });
    }

    private string HandleScreenshot(HttpListenerRequest request)
    {
        var body = ReadRequestBody(request);
        string filename = $"vsai_screenshot_{DateTime.Now:yyyyMMdd_HHmmss}.png";
        string directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Pictures", "VSAI");

        if (!string.IsNullOrEmpty(body))
        {
            try
            {
                using var doc = JsonDocument.Parse(body);
                var root = doc.RootElement;

                if (root.TryGetProperty("filename", out var fnEl))
                    filename = fnEl.GetString() ?? filename;
                if (root.TryGetProperty("directory", out var dirEl))
                    directory = dirEl.GetString() ?? directory;
            }
            catch (JsonException)
            {
                // Use defaults
            }
        }

        try
        {
            // Ensure directory exists
            Directory.CreateDirectory(directory);

            string filepath = Path.Combine(directory, filename);

            // First, get the Vintage Story window ID using osascript
            var getWindowId = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "/usr/bin/osascript",
                    Arguments = "-e 'tell application \"System Events\" to get the id of window 1 of (first process whose name contains \"Vintage Story\")'",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };

            getWindowId.Start();
            string windowIdOutput = getWindowId.StandardOutput.ReadToEnd().Trim();
            getWindowId.WaitForExit(3000);

            string screencaptureArgs;
            if (!string.IsNullOrEmpty(windowIdOutput) && int.TryParse(windowIdOutput, out int windowId))
            {
                // Capture specific window by ID
                screencaptureArgs = $"-x -l {windowId} \"{filepath}\"";
            }
            else
            {
                // Fallback: try to capture by window name with AppleScript
                // This captures the frontmost window of Vintage Story
                var bringToFront = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = "/usr/bin/osascript",
                        Arguments = "-e 'tell application \"Vintage Story\" to activate'",
                        UseShellExecute = false,
                        CreateNoWindow = true
                    }
                };
                bringToFront.Start();
                bringToFront.WaitForExit(2000);
                Thread.Sleep(500); // Wait for window to come to front

                // Capture the frontmost window
                screencaptureArgs = $"-x -w \"{filepath}\"";

                // Actually, -w is interactive. Let's just capture the screen if we can't get window ID
                // and warn the user
                _serverApi?.Logger.Warning("[VSAI] Could not get Vintage Story window ID, capturing full screen");
                screencaptureArgs = $"-x \"{filepath}\"";
            }

            // Use macOS screencapture command
            var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "/usr/sbin/screencapture",
                    Arguments = screencaptureArgs,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };

            process.Start();
            bool completed = process.WaitForExit(5000);

            if (!completed)
            {
                process.Kill();
                return JsonError("Screenshot timed out");
            }

            if (process.ExitCode != 0)
            {
                string error = process.StandardError.ReadToEnd();
                return JsonError($"Screenshot failed: {error}");
            }

            if (!File.Exists(filepath))
            {
                return JsonError("Screenshot file was not created");
            }

            _serverApi?.Logger.Notification($"[VSAI] Screenshot saved to {filepath}");

            return JsonSerializer.Serialize(new
            {
                success = true,
                filepath = filepath,
                filename = filename
            });
        }
        catch (Exception ex)
        {
            return JsonError($"Screenshot error: {ex.Message}");
        }
    }

    private IServerPlayer? GetFirstPlayer()
    {
        var players = _serverApi?.World?.AllOnlinePlayers;
        if (players == null || players.Length == 0)
            return null;
        return players[0] as IServerPlayer;
    }

    private static string[] GetPlayerNames(IPlayer[] players)
    {
        var names = new string[players.Length];
        for (int i = 0; i < players.Length; i++)
        {
            names[i] = players[i].PlayerName;
        }
        return names;
    }

    private string HandleBotKnap(HttpListenerRequest request)
    {
        if (_botEntity == null || !_botEntity.Alive)
        {
            return JsonError("No active bot");
        }

        if (_botEntity is not EntityAgent agent)
        {
            return JsonError("Bot is not an EntityAgent");
        }

        var inventoryBehavior = agent.GetBehavior<EntityBehaviorBotInventory>();
        if (inventoryBehavior == null)
        {
            return JsonError("Bot has no inventory behavior configured");
        }

        var inventory = inventoryBehavior.Inventory;
        if (inventory == null)
        {
            return JsonError("Bot inventory is null");
        }

        var body = ReadRequestBody(request);
        if (string.IsNullOrEmpty(body))
        {
            return JsonError("Empty request body. Provide 'recipe' parameter (e.g., 'axe', 'knife', 'shovel', 'hoe', 'spear', 'arrowhead').");
        }

        string? recipeName = null;

        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;

            if (!root.TryGetProperty("recipe", out var recipeEl))
            {
                return JsonError("Missing 'recipe' parameter. Valid options: axe, knife, shovel, hoe, spear, arrowhead");
            }

            recipeName = recipeEl.GetString()?.ToLowerInvariant();
            if (string.IsNullOrEmpty(recipeName))
            {
                return JsonError("Recipe name cannot be empty");
            }
        }
        catch (JsonException ex)
        {
            return JsonError($"Invalid JSON: {ex.Message}");
        }

        // Get all knapping recipes using extension method
        // The GetKnappingRecipes extension method is in Vintagestory.GameContent
        var knappingRecipes = _serverApi?.World?.Api?.GetKnappingRecipes();

        if (knappingRecipes == null || knappingRecipes.Count == 0)
        {
            // Debug: Log what we have
            _serverApi?.Logger.Warning("[VSAI] GetKnappingRecipes returned null or empty");

            // Try alternative: iterate through all recipes
            var allRecipes = new List<KnappingRecipe>();

            // Recipes might be in a grid recipe registry or other location
            var gridRecipes = _serverApi?.World?.GridRecipes;
            _serverApi?.Logger.Notification($"[VSAI] GridRecipes count: {gridRecipes?.Count ?? 0}");

            return JsonError($"No knapping recipes available. GridRecipes: {gridRecipes?.Count ?? 0}");
        }

        // Find matching recipe by output name AND available material in inventory
        // First, collect all inventory materials
        var inventoryMaterials = new List<(int slotIndex, ItemSlot slot)>();
        for (int i = 0; i < inventory.Count; i++)
        {
            var slot = inventory[i];
            if (slot?.Itemstack?.Collectible != null)
            {
                inventoryMaterials.Add((i, slot));
            }
        }
        // Also check hand slots
        if (agent.RightHandItemSlot?.Itemstack != null)
        {
            inventoryMaterials.Add((-2, agent.RightHandItemSlot)); // -2 = right hand marker
        }

        // Find a recipe that matches the requested output AND has material in inventory
        KnappingRecipe? matchingRecipe = null;
        int materialSlotIndex = -1;
        ItemSlot? materialSlot = null;

        foreach (var recipe in knappingRecipes)
        {
            if (recipe?.Output?.ResolvedItemstack?.Collectible == null) continue;
            if (recipe.Ingredient == null) continue;

            var outputCode = recipe.Output.ResolvedItemstack.Collectible.Code?.Path?.ToLowerInvariant() ?? "";

            // Match by output containing the recipe name (e.g., "knifeblade" contains "knife")
            if (!outputCode.Contains(recipeName)) continue;

            // Check if we have a matching ingredient in inventory
            foreach (var (slotIdx, slot) in inventoryMaterials)
            {
                if (recipe.Ingredient.SatisfiesAsIngredient(slot.Itemstack))
                {
                    matchingRecipe = recipe;
                    materialSlotIndex = slotIdx;
                    materialSlot = slot;
                    break;
                }
            }

            if (matchingRecipe != null) break;
        }

        if (matchingRecipe == null)
        {
            // List available recipes for error message
            var available = new List<string>();
            foreach (var recipe in knappingRecipes)
            {
                var outputCode = recipe?.Output?.ResolvedItemstack?.Collectible?.Code?.Path;
                if (!string.IsNullOrEmpty(outputCode) && outputCode.ToLowerInvariant().Contains(recipeName))
                {
                    var ingredient = recipe?.Ingredient?.Code?.Path ?? "unknown";
                    available.Add($"{outputCode} (needs {ingredient})");
                }
            }
            if (available.Count > 0)
            {
                return JsonError($"Found {available.Count} '{recipeName}' recipes but no matching materials in inventory: {string.Join(", ", available)}");
            }
            return JsonError($"No recipe found matching '{recipeName}'.");
        }

        if (materialSlot == null || materialSlot.Itemstack == null)
        {
            return JsonError($"No suitable material found in inventory for recipe.");
        }

        // Execute knapping on main thread
        object? craftedItem = null;
        string? errorMsg = null;
        var waitHandle = new ManualResetEventSlim(false);

        _serverApi?.Event.EnqueueMainThreadTask(() =>
        {
            try
            {
                var blockAccessor = _serverApi.World.BlockAccessor;
                var world = _serverApi.World;

                // Find a suitable ground position in front of bot for knapping surface
                var botPos = _botEntity.ServerPos;
                float yaw = botPos.Yaw;

                // Calculate position 1 block in front of bot
                int frontX = (int)Math.Floor(botPos.X - Math.Sin(yaw));
                int frontZ = (int)Math.Floor(botPos.Z + Math.Cos(yaw));
                int groundY = (int)Math.Floor(botPos.Y);

                // Find ground level (scan down to find solid block)
                var testPos = new BlockPos(frontX, groundY, frontZ, botPos.Dimension);
                for (int dy = 0; dy >= -3; dy--)
                {
                    testPos.Y = groundY + dy;
                    var block = blockAccessor.GetBlock(testPos);
                    if (block != null && block.SideSolid[BlockFacing.UP.Index])
                    {
                        groundY = testPos.Y;
                        break;
                    }
                }

                // Position for knapping surface is on top of ground
                var surfacePos = new BlockPos(frontX, groundY + 1, frontZ, botPos.Dimension);

                // Get knapping surface block
                var knappingSurfaceBlock = world.GetBlock(new AssetLocation("game:knappingsurface"));
                if (knappingSurfaceBlock == null)
                {
                    errorMsg = "Knapping surface block not found in game registry";
                    return;
                }

                // Check if position is clear
                var existingBlock = blockAccessor.GetBlock(surfacePos);
                if (existingBlock != null && existingBlock.Code?.Path != "air")
                {
                    errorMsg = $"Cannot place knapping surface - position blocked by {existingBlock.Code}";
                    return;
                }

                // Place knapping surface block
                blockAccessor.SetBlock(knappingSurfaceBlock.BlockId, surfacePos);

                // Get the block entity
                var blockEntity = blockAccessor.GetBlockEntity(surfacePos) as BlockEntityKnappingSurface;
                if (blockEntity == null)
                {
                    // Clean up if we can't get the entity
                    blockAccessor.SetBlock(0, surfacePos);
                    errorMsg = "Failed to create knapping surface block entity";
                    return;
                }

                // Take one material from inventory
                var materialStack = materialSlot.TakeOut(1);
                materialSlot.MarkDirty();

                if (materialStack == null)
                {
                    blockAccessor.SetBlock(0, surfacePos);
                    errorMsg = "Failed to take material from inventory";
                    return;
                }

                // Set the base material on the knapping surface
                blockEntity.BaseMaterial = materialStack;

                // Find recipe index and set it
                int recipeIndex = knappingRecipes.IndexOf(matchingRecipe);
                if (recipeIndex < 0)
                {
                    blockAccessor.SetBlock(0, surfacePos);
                    agent.TryGiveItemStack(materialStack); // Return material
                    errorMsg = "Recipe not found in registry";
                    return;
                }

                // Set the selected recipe using reflection if needed
                // The selectedRecipeId field controls which recipe is active
                var selectedRecipeField = blockEntity.GetType().GetField("selectedRecipeId",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                if (selectedRecipeField != null)
                {
                    selectedRecipeField.SetValue(blockEntity, recipeIndex);
                }

                // Initialize voxels array if needed
                if (blockEntity.Voxels == null)
                {
                    blockEntity.Voxels = new bool[16, 16];
                    for (int vx = 0; vx < 16; vx++)
                    {
                        for (int vz = 0; vz < 16; vz++)
                        {
                            blockEntity.Voxels[vx, vz] = true;
                        }
                    }
                }

                // Get recipe voxels - the pattern we need to create
                // Recipe voxels are [x, layer, z] - typically layer 0
                var recipeVoxels = matchingRecipe.Voxels;
                if (recipeVoxels == null)
                {
                    blockAccessor.SetBlock(0, surfacePos);
                    agent.TryGiveItemStack(materialStack);
                    errorMsg = "Recipe has no voxel pattern defined";
                    return;
                }

                // Copy recipe pattern to block entity voxels (instant completion)
                // This is the Knapster approach - directly set voxels to match recipe
                for (int vx = 0; vx < 16; vx++)
                {
                    for (int vz = 0; vz < 16; vz++)
                    {
                        // Recipe voxels are [x, layer, z] - we use layer 0
                        blockEntity.Voxels[vx, vz] = recipeVoxels[vx, 0, vz];
                    }
                }

                // Mark block entity as dirty to sync changes
                blockEntity.MarkDirty(true);

                // Create the output item
                var outputStack = matchingRecipe.Output.ResolvedItemstack.Clone();
                outputStack.StackSize = 1;

                var inventoryBehavior = agent.GetBehavior<EntityBehaviorBotInventory>();
                var inventory = inventoryBehavior?.Inventory;

                // Give output to bot
                agent.TryGiveItemStack(outputStack);
                bool actuallyAdded = outputStack.StackSize == 0;

                // Manual slot insertion fallback if TryGiveItemStack fails
                if (!actuallyAdded && inventory != null)
                {
                    for (int slotIdx = 0; slotIdx < inventory.Count; slotIdx++)
                    {
                        var slot = inventory[slotIdx];
                        if (slot?.Itemstack == null)
                        {
                            slot.Itemstack = outputStack.Clone();
                            slot.MarkDirty();
                            outputStack.StackSize = 0;
                            actuallyAdded = true;
                            break;
                        }
                    }
                }

                if (!actuallyAdded)
                {
                    // Drop at bot's feet if inventory is full
                    _serverApi.Logger.Warning($"[VSAI] Inventory full, dropping knapped item at bot's feet");
                    world.SpawnItemEntity(outputStack, _botEntity.ServerPos.XYZ);
                }

                craftedItem = new
                {
                    code = matchingRecipe.Output.ResolvedItemstack.Collectible?.Code?.ToString() ?? "unknown",
                    quantity = 1,
                    name = matchingRecipe.Output.ResolvedItemstack.GetName(),
                    addedToInventory = actuallyAdded
                };

                // Remove the knapping surface
                blockAccessor.SetBlock(0, surfacePos);
                blockAccessor.TriggerNeighbourBlockUpdate(surfacePos);

                _serverApi.Logger.Notification($"[VSAI] Bot completed knapping: {matchingRecipe.Output.ResolvedItemstack.Collectible?.Code}");
            }
            catch (Exception ex)
            {
                errorMsg = $"Knapping failed: {ex.Message}";
                _serverApi.Logger.Error($"[VSAI] Knapping error: {ex.Message}\n{ex.StackTrace}");
            }
            finally
            {
                waitHandle.Set();
            }
        }, "VSAI-Knap");

        waitHandle.Wait(10000);

        if (errorMsg != null)
        {
            return JsonError(errorMsg);
        }

        if (craftedItem == null)
        {
            return JsonError("Knapping operation timed out");
        }

        return JsonSerializer.Serialize(new
        {
            success = true,
            recipe = recipeName,
            output = craftedItem
        });
    }

    private string HandleBotCraft(HttpListenerRequest request)
    {
        if (_botEntity == null || !_botEntity.Alive)
        {
            return JsonError("No active bot");
        }

        if (_botEntity is not EntityAgent agent)
        {
            return JsonError("Bot is not an EntityAgent");
        }

        var inventoryBehavior = agent.GetBehavior<EntityBehaviorBotInventory>();
        if (inventoryBehavior == null)
        {
            return JsonError("Bot has no inventory behavior configured");
        }

        var inventory = inventoryBehavior.Inventory;
        if (inventory == null)
        {
            return JsonError("Bot inventory is null");
        }

        var body = ReadRequestBody(request);
        if (string.IsNullOrEmpty(body))
        {
            return JsonError("Empty request body. Provide 'recipe' parameter (e.g., 'axe', 'knife', 'shovel', 'spear').");
        }

        string? recipeName = null;

        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;

            if (!root.TryGetProperty("recipe", out var recipeEl))
            {
                return JsonError("Missing 'recipe' parameter.");
            }

            recipeName = recipeEl.GetString()?.ToLowerInvariant();
            if (string.IsNullOrEmpty(recipeName))
            {
                return JsonError("Recipe name cannot be empty");
            }
        }
        catch (JsonException ex)
        {
            return JsonError($"Invalid JSON: {ex.Message}");
        }

        // Get all grid recipes
        var gridRecipes = _serverApi?.World?.GridRecipes;
        if (gridRecipes == null || gridRecipes.Count == 0)
        {
            return JsonError("No grid recipes available");
        }

        // Collect inventory items
        var inventoryItems = new List<(int slotIndex, ItemSlot slot)>();
        for (int i = 0; i < inventory.Count; i++)
        {
            var slot = inventory[i];
            if (slot?.Itemstack?.Collectible != null)
            {
                inventoryItems.Add((i, slot));
            }
        }

        // Find matching recipe by output name AND available ingredients
        GridRecipe? matchingRecipe = null;
        var ingredientSlots = new List<(int slotIndex, CraftingRecipeIngredient ingredient)>();

        foreach (var recipe in gridRecipes)
        {
            if (recipe?.Output?.ResolvedItemstack?.Collectible == null) continue;

            var outputCode = recipe.Output.ResolvedItemstack.Collectible.Code?.Path?.ToLowerInvariant() ?? "";

            // Match by output containing the recipe name (e.g., "axe-flint" contains "axe")
            if (!outputCode.Contains(recipeName)) continue;

            // Get required ingredients from this recipe
            var requiredIngredients = new List<CraftingRecipeIngredient>();
            if (recipe.resolvedIngredients != null)
            {
                foreach (var ing in recipe.resolvedIngredients)
                {
                    if (ing != null)
                    {
                        requiredIngredients.Add(ing);
                    }
                }
            }

            if (requiredIngredients.Count == 0) continue;

            // Check if we have all ingredients in inventory
            var tempSlots = new List<(int slotIndex, CraftingRecipeIngredient ingredient)>();
            var usedSlotIndices = new HashSet<int>();
            bool hasAllIngredients = true;

            foreach (var reqIng in requiredIngredients)
            {
                bool foundMatch = false;
                foreach (var (slotIdx, slot) in inventoryItems)
                {
                    if (usedSlotIndices.Contains(slotIdx)) continue;
                    if (slot.Itemstack == null) continue;

                    if (reqIng.SatisfiesAsIngredient(slot.Itemstack))
                    {
                        tempSlots.Add((slotIdx, reqIng));
                        usedSlotIndices.Add(slotIdx);
                        foundMatch = true;
                        break;
                    }
                }

                if (!foundMatch)
                {
                    hasAllIngredients = false;
                    break;
                }
            }

            if (hasAllIngredients)
            {
                matchingRecipe = recipe;
                ingredientSlots = tempSlots;
                break;
            }
        }

        if (matchingRecipe == null)
        {
            // List available recipes matching the name for error message
            var available = new List<string>();
            foreach (var recipe in gridRecipes)
            {
                var outputCode = recipe?.Output?.ResolvedItemstack?.Collectible?.Code?.Path?.ToLowerInvariant() ?? "";
                if (outputCode.Contains(recipeName))
                {
                    var ingredients = new List<string>();
                    if (recipe?.resolvedIngredients != null)
                    {
                        foreach (var ing in recipe.resolvedIngredients)
                        {
                            if (ing?.Code != null)
                            {
                                ingredients.Add(ing.Code.Path);
                            }
                        }
                    }
                    available.Add($"{outputCode} (needs {string.Join(" + ", ingredients)})");
                }
            }
            if (available.Count > 0)
            {
                return JsonError($"Found {available.Count} '{recipeName}' recipes but missing ingredients: {string.Join(", ", available.Take(5))}");
            }
            return JsonError($"No grid recipe found matching '{recipeName}'.");
        }

        // Execute crafting on main thread
        object? craftedItem = null;
        string? errorMsg = null;
        var waitHandle = new ManualResetEventSlim(false);

        _serverApi?.Event.EnqueueMainThreadTask(() =>
        {
            try
            {
                // Consume ingredients (or reduce durability for tools)
                foreach (var (slotIdx, ingredient) in ingredientSlots)
                {
                    var slot = inventory[slotIdx];
                    if (slot?.Itemstack != null)
                    {
                        if (ingredient.IsTool)
                        {
                            // Tool ingredient: reduce durability instead of consuming
                            int durabilityCost = ingredient.ToolDurabilityCost > 0 ? ingredient.ToolDurabilityCost : 1;
                            slot.Itemstack.Collectible.DamageItem(_serverApi.World, agent, slot, durabilityCost);
                            // Check if tool broke from durability loss
                            if (slot.Itemstack?.Collectible?.GetRemainingDurability(slot.Itemstack) <= 0)
                            {
                                slot.Itemstack = null;
                            }
                        }
                        else
                        {
                            // Regular ingredient: consume it
                            int consumeQty = ingredient.Quantity;
                            slot.Itemstack.StackSize -= consumeQty;
                            if (slot.Itemstack.StackSize <= 0)
                            {
                                slot.Itemstack = null;
                            }
                        }
                        slot.MarkDirty();
                    }
                }

                // Create output
                var outputStack = matchingRecipe.Output.ResolvedItemstack.Clone();
                outputStack.StackSize = matchingRecipe.Output.Quantity;

                // Give output to bot
                agent.TryGiveItemStack(outputStack);
                bool actuallyAdded = outputStack.StackSize == 0;

                // Manual slot insertion fallback
                if (!actuallyAdded)
                {
                    for (int slotIdx = 0; slotIdx < inventory.Count; slotIdx++)
                    {
                        var slot = inventory[slotIdx];
                        if (slot?.Itemstack == null)
                        {
                            slot.Itemstack = outputStack.Clone();
                            slot.MarkDirty();
                            outputStack.StackSize = 0;
                            actuallyAdded = true;
                            break;
                        }
                    }
                }

                if (!actuallyAdded)
                {
                    // Drop at bot's feet if inventory is full
                    _serverApi.World.SpawnItemEntity(outputStack, _botEntity.ServerPos.XYZ);
                }

                craftedItem = new
                {
                    code = matchingRecipe.Output.ResolvedItemstack.Collectible?.Code?.ToString() ?? "unknown",
                    quantity = matchingRecipe.Output.Quantity,
                    name = matchingRecipe.Output.ResolvedItemstack.GetName(),
                    addedToInventory = actuallyAdded
                };
            }
            catch (Exception ex)
            {
                errorMsg = $"Crafting failed: {ex.Message}";
            }
            finally
            {
                waitHandle.Set();
            }
        }, "VSAI-Craft");

        waitHandle.Wait(5000);

        if (errorMsg != null)
        {
            return JsonError(errorMsg);
        }

        if (craftedItem == null)
        {
            return JsonError("Crafting produced no output");
        }

        return JsonSerializer.Serialize(new
        {
            success = true,
            recipe = recipeName,
            output = craftedItem
        });
    }

    private string HandleBotEquip(HttpListenerRequest request)
    {
        if (_botEntity == null || !_botEntity.Alive)
        {
            return JsonError("No active bot");
        }

        if (_botEntity is not EntityAgent agent)
        {
            return JsonError("Bot is not an EntityAgent");
        }

        var inventoryBehavior = agent.GetBehavior<EntityBehaviorBotInventory>();
        if (inventoryBehavior == null)
        {
            return JsonError("Bot has no inventory behavior configured");
        }

        var inventory = inventoryBehavior.Inventory;
        if (inventory == null)
        {
            return JsonError("Bot inventory is null");
        }

        var body = ReadRequestBody(request);
        if (string.IsNullOrEmpty(body))
        {
            return JsonError("Empty request body. Provide 'slotIndex' or 'itemCode', and optionally 'hand' (right/left).");
        }

        int? slotIndex = null;
        string? itemCode = null;
        string hand = "right";

        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;

            if (root.TryGetProperty("slotIndex", out var slotEl))
            {
                slotIndex = slotEl.GetInt32();
            }

            if (root.TryGetProperty("itemCode", out var codeEl))
            {
                itemCode = codeEl.GetString()?.ToLowerInvariant();
            }

            if (root.TryGetProperty("hand", out var handEl))
            {
                hand = handEl.GetString()?.ToLowerInvariant() ?? "right";
            }
        }
        catch (JsonException ex)
        {
            return JsonError($"Invalid JSON: {ex.Message}");
        }

        if (slotIndex == null && string.IsNullOrEmpty(itemCode))
        {
            return JsonError("Must provide either 'slotIndex' or 'itemCode'");
        }

        // Find the source slot
        ItemSlot? sourceSlot = null;
        int foundSlotIndex = -1;

        if (slotIndex != null)
        {
            if (slotIndex < 0 || slotIndex >= inventory.Count)
            {
                return JsonError($"Invalid slot index {slotIndex}. Valid range: 0-{inventory.Count - 1}");
            }
            sourceSlot = inventory[slotIndex.Value];
            foundSlotIndex = slotIndex.Value;
        }
        else if (!string.IsNullOrEmpty(itemCode))
        {
            // Search inventory for matching item
            for (int i = 0; i < inventory.Count; i++)
            {
                var slot = inventory[i];
                if (slot?.Itemstack?.Collectible != null)
                {
                    var code = slot.Itemstack.Collectible.Code?.ToString()?.ToLowerInvariant() ?? "";
                    if (code.Contains(itemCode))
                    {
                        sourceSlot = slot;
                        foundSlotIndex = i;
                        break;
                    }
                }
            }
        }

        if (sourceSlot == null || sourceSlot.Itemstack == null)
        {
            return JsonError($"No item found matching criteria");
        }

        // Get the target hand slot
        ItemSlot targetSlot = hand == "left" ? agent.LeftHandItemSlot : agent.RightHandItemSlot;

        if (targetSlot == null)
        {
            return JsonError($"Bot has no {hand} hand slot");
        }

        // Execute equip on main thread
        object? equippedItem = null;
        string? errorMsg = null;
        var waitHandle = new ManualResetEventSlim(false);

        _serverApi?.Event.EnqueueMainThreadTask(() =>
        {
            try
            {
                // Store what we're equipping
                var itemToEquip = sourceSlot.Itemstack.Clone();

                // Swap: put hand item (if any) into inventory slot, put inventory item into hand
                var previousHandItem = targetSlot.Itemstack?.Clone();

                // Move item to hand
                targetSlot.Itemstack = itemToEquip;
                targetSlot.MarkDirty();

                // Put previous hand item (if any) back in inventory slot
                sourceSlot.Itemstack = previousHandItem;
                sourceSlot.MarkDirty();

                // Sync hand slots to WatchedAttributes for client-side rendering
                inventoryBehavior.SyncAllHandSlots();

                equippedItem = new
                {
                    code = itemToEquip.Collectible?.Code?.ToString() ?? "unknown",
                    quantity = itemToEquip.StackSize,
                    name = itemToEquip.GetName(),
                    hand = hand,
                    fromSlot = foundSlotIndex
                };
            }
            catch (Exception ex)
            {
                errorMsg = $"Equip failed: {ex.Message}";
            }
            finally
            {
                waitHandle.Set();
            }
        }, "VSAI-Equip");

        waitHandle.Wait(5000);

        if (errorMsg != null)
        {
            return JsonError(errorMsg);
        }

        if (equippedItem == null)
        {
            return JsonError("Equip produced no result");
        }

        return JsonSerializer.Serialize(new
        {
            success = true,
            equipped = equippedItem
        });
    }

    private static string ReadRequestBody(HttpListenerRequest request)
    {
        using var reader = new StreamReader(request.InputStream, request.ContentEncoding);
        return reader.ReadToEnd();
    }
}
