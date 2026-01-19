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
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
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

    // Pathfinding
    private AStar? _astar;

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
    }

    public override void StartServerSide(ICoreServerAPI api)
    {
        _serverApi = api;
        _serverApi.Logger.Notification("[VSAI] Starting Vintage Story AI Bridge...");

        // Initialize pathfinding
        _astar = new AStar(api);

        StartHttpServer();

        _serverApi.Logger.Notification($"[VSAI] HTTP server started on http://{DefaultHost}:{DefaultPort}");
    }

    public override void StartClientSide(ICoreClientAPI api)
    {
        api.Logger.Notification("[VSAI] Starting client-side for minimap integration...");

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
        DespawnBot();
        StopHttpServer();
        base.Dispose();
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

                case "/bot/break":
                    if (method != "POST")
                    {
                        statusCode = 405;
                        responseBody = JsonError("Method not allowed. Use POST.");
                    }
                    else
                    {
                        responseBody = HandleBotBreakBlock(request);
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

                case "/bot/pathfind":
                    if (method != "POST")
                    {
                        statusCode = 405;
                        responseBody = JsonError("Method not allowed. Use POST.");
                    }
                    else
                    {
                        responseBody = HandleBotPathfind(request);
                    }
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

        return JsonSerializer.Serialize(new
        {
            status = "ok",
            mod = "vsai",
            version = "0.2.0",
            playerCount = players.Length,
            players = GetPlayerNames(players),
            bot = new
            {
                active = botActive,
                entityId = botActive ? _botEntityId : 0
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

                // Log entity count after spawn
                int afterCount = _serverApi.World.LoadedEntities.Values.Count(e => e.Code?.Path == "aibot");
                _serverApi.Logger.Notification($"[VSAI] Bot spawned: {entityCode} at {spawnPos}, EntityId={_botEntityId}");
                _serverApi.Logger.Notification($"[VSAI] After spawn: {afterCount} aibot entities in LoadedEntities");
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
            _serverApi?.Logger.Notification("[VSAI] Bot despawned");
        }
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

        return JsonSerializer.Serialize(new
        {
            bot = new
            {
                entityId = _botEntityId,
                position = new { x = pos.X, y = pos.Y, z = pos.Z },
                rotation = new { yaw = pos.Yaw, pitch = pos.Pitch },
                alive = _botEntity.Alive,
                onGround = _botEntity.OnGround,
                inWater = _botEntity.Swimming
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
            radius = Math.Clamp(r, 1, 8);
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

    private string HandleBotBreakBlock(HttpListenerRequest request)
    {
        if (_botEntity == null || !_botEntity.Alive)
        {
            return JsonError("No active bot");
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
            var drops = new List<string>();

            var waitHandle = new ManualResetEventSlim(false);
            string? errorMsg = null;

            _serverApi?.Event.EnqueueMainThreadTask(() =>
            {
                try
                {
                    // Get drops before breaking
                    var blockDrops = block.GetDrops(_serverApi.World, blockPos, null);
                    if (blockDrops != null)
                    {
                        foreach (var drop in blockDrops)
                        {
                            drops.Add($"{drop.StackSize}x {drop.Collectible?.Code?.Path ?? "unknown"}");
                        }
                    }

                    // Break the block (set to air)
                    blockAccessor.SetBlock(0, blockPos);
                    blockAccessor.TriggerNeighbourBlockUpdate(blockPos);

                    // Spawn drops as items in the world
                    if (blockDrops != null)
                    {
                        foreach (var drop in blockDrops)
                        {
                            _serverApi.World.SpawnItemEntity(drop, new Vec3d(x + 0.5, y + 0.5, z + 0.5));
                        }
                    }

                    _serverApi.Logger.Debug($"[VSAI] Bot broke block {blockCode} at ({x}, {y}, {z})");
                }
                catch (Exception ex)
                {
                    errorMsg = ex.Message;
                }
                finally
                {
                    waitHandle.Set();
                }
            }, "VSAI-BreakBlock");

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
                drops = drops
            });
        }
        catch (JsonException ex)
        {
            return JsonError($"Invalid JSON: {ex.Message}");
        }
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
            // Stop the AI task
            var task = GetRemoteControlTask();
            if (task != null)
            {
                task.Stop();
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
            onGround = _botEntity.OnGround
        });
    }

    private string HandleBotPathfind(HttpListenerRequest request)
    {
        if (_botEntity == null || !_botEntity.Alive)
        {
            return JsonError("No active bot");
        }

        if (_astar == null || _serverApi == null)
        {
            return JsonError("Pathfinding not initialized");
        }

        var body = ReadRequestBody(request);
        if (string.IsNullOrEmpty(body))
        {
            return JsonError("Empty request body. Provide x, y, z target coordinates.");
        }

        double targetX, targetY, targetZ;
        int maxFallHeight = 4;
        float stepHeight = 1.2f;  // Must be >1.0 to handle 1-block terrain rises
        int searchDepth = 9999;

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

            if (root.TryGetProperty("maxFallHeight", out var mfh))
                maxFallHeight = mfh.GetInt32();
            if (root.TryGetProperty("stepHeight", out var sh))
                stepHeight = (float)sh.GetDouble();
            if (root.TryGetProperty("searchDepth", out var sd))
                searchDepth = sd.GetInt32();
        }
        catch (JsonException ex)
        {
            return JsonError($"Invalid JSON: {ex.Message}");
        }

        // Pathfinder expects position where entity stands (air block), not ground block
        var startPos = _botEntity.ServerPos.AsBlockPos;
        var endPos = new BlockPos((int)targetX, (int)targetY, (int)targetZ, startPos.dimension);

        // Get entity collision box
        var collisionBox = _botEntity.CollisionBox ?? new Cuboidf(-0.3f, 0, -0.3f, 0.3f, 1.75f, 0.3f);

        List<Vec3d>? waypoints = null;
        string? errorMsg = null;
        var waitHandle = new ManualResetEventSlim(false);

        _serverApi.Event.EnqueueMainThreadTask(() =>
        {
            try
            {
                var startBlock = _serverApi.World.BlockAccessor.GetBlock(startPos);
                var endBlock = _serverApi.World.BlockAccessor.GetBlock(endPos);
                _serverApi.Logger.Notification($"[VSAI] Pathfind: start=({startPos.X},{startPos.Y},{startPos.Z}) [{startBlock?.Code}] end=({endPos.X},{endPos.Y},{endPos.Z}) [{endBlock?.Code}]");
                waypoints = _astar.FindPathAsWaypoints(
                    startPos,
                    endPos,
                    maxFallHeight,
                    stepHeight,
                    collisionBox,
                    searchDepth,
                    0,
                    EnumAICreatureType.Humanoid
                );

                if (waypoints == null || waypoints.Count == 0)
                {
                    errorMsg = "No path found";
                }
                else
                {
                    _serverApi.Logger.Notification($"[VSAI] Found path with {waypoints.Count} waypoints");
                }
            }
            catch (Exception ex)
            {
                errorMsg = $"Pathfinding error: {ex.Message}";
                _serverApi.Logger.Error($"[VSAI] {errorMsg}");
            }
            finally
            {
                waitHandle.Set();
            }
        }, "VSAI-Pathfind");

        waitHandle.Wait(10000); // 10 second timeout for pathfinding

        if (errorMsg != null)
        {
            return JsonSerializer.Serialize(new
            {
                success = false,
                error = errorMsg
            });
        }

        if (waypoints == null)
        {
            return JsonSerializer.Serialize(new
            {
                success = false,
                error = "Pathfinding timed out"
            });
        }

        // Calculate total distance
        double totalDistance = 0;
        for (int i = 1; i < waypoints.Count; i++)
        {
            totalDistance += waypoints[i - 1].DistanceTo(waypoints[i]);
        }

        var waypointList = new List<object>();
        foreach (var wp in waypoints)
        {
            waypointList.Add(new { x = wp.X, y = wp.Y, z = wp.Z });
        }

        return JsonSerializer.Serialize(new
        {
            success = true,
            start = new { x = startPos.X, y = startPos.Y, z = startPos.Z },
            target = new { x = targetX, y = targetY, z = targetZ },
            waypointCount = waypoints.Count,
            distance = Math.Round(totalDistance, 2),
            waypoints = waypointList
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

            // Optional bot name prefix (default: "Bot")
            string botName = "Bot";
            if (root.TryGetProperty("name", out var nameEl))
            {
                botName = nameEl.GetString() ?? "Bot";
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

    private static string ReadRequestBody(HttpListenerRequest request)
    {
        using var reader = new StreamReader(request.InputStream, request.ContentEncoding);
        return reader.ReadToEnd();
    }
}
