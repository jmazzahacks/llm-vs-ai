# Vintage Story AI Bot - Development Learnings

This document captures key learnings and discoveries made while developing an AI-controlled bot for Vintage Story.

## Architecture Overview

```
Python/LLM Layer (future)
        |
        | HTTP (localhost:4560)
        v
   C# Server Mod
        |
        v
  Bot Entity in VS World
```

## SOLUTION: Custom Entity with Custom AI Task

**The winning approach:** Create a custom entity type (`vsai:aibot`) that has:
- TaskAI behavior with our custom `AiTaskRemoteControl` task
- Uses `PathTraverser.NavigateTo()` for A* pathfinding
- Uses trader shape/texture for humanoid appearance
- `stepHeight: 1.01` to handle full-block terrain

**Why this works:** We get native VS movement with pathfinding, controlled via HTTP API.

### Custom Entity File Structure

```
mod/assets/vsai/
├── entities/
│   └── aibot.json          # Entity definition
├── shapes/
│   └── entity/humanoid/
│       └── trader.json     # COPIED from survival mod (required!)
└── textures/
    └── entity/humanoid/
        └── trader.png      # COPIED from survival mod
```

**CRITICAL:** Shape files must be COPIED into your mod's assets folder. Cross-mod references like `survival:entity/humanoid/trader` do NOT work reliably - VS looks in your mod first, fails, and makes entity invisible.

### Entity JSON Key Points

```json
{
    "code": "aibot",
    "class": "EntityAgent",
    "client": {
        "shape": { "base": "entity/humanoid/trader" },  // NO mod prefix!
        "texture": { "base": "entity/humanoid/trader" },
        "behaviors": [
            { "code": "controlledphysics", "stepHeight": 1.01 }  // MUST be >1.0 for full blocks!
        ],
        "animations": [
            // Use ACTUAL animation names from shape file:
            { "code": "idle", "animation": "balanced-idle", ... },
            { "code": "walk", "animation": "balanced-walk", ... },
            { "code": "run", "animation": "balanced-run", ... }
        ]
    },
    "server": {
        "attributes": {
            "pathfinder": {
                "minTurnAnglePerSec": 360,
                "maxTurnAnglePerSec": 720
            }
        },
        "behaviors": [
            { "code": "controlledphysics", "stepHeight": 1.01 },  // CRITICAL: both client AND server!
            { "code": "health", "currenthealth": 20, "maxhealth": 20 },
            { "code": "floatupwhenstuck", "onlyWhenDead": false },
            {
                "code": "taskai",
                "aiCreatureType": "LandCreature",
                "aitasks": [
                    {
                        "code": "vsai:remotecontrol",  // Our custom AI task
                        "priority": 999,
                        "movespeed": 0.03
                    }
                ]
            }
        ]
    }
}
```

### stepHeight - CRITICAL for Terrain Navigation

The `stepHeight` parameter in `controlledphysics` determines how tall an obstacle the entity can "step up" onto without jumping.

- **stepHeight: 0.6** (default) - Can only step up slabs, stairs, partial blocks
- **stepHeight: 1.01** - Can step up FULL blocks (standard terrain)
- **stepHeight: 1.5+** - Can step up 1.5 block obstacles

**MUST set on BOTH client AND server behaviors!**

**Two systems at play:**
1. **A* Pathfinder** - calculates a theoretical path (has its own stepHeight parameter)
2. **Physics Engine** - actually moves the entity (uses entity's controlledphysics stepHeight)

If pathfinder finds a path but physics can't execute it (stepHeight too low), the bot gets stuck.

**The physics engine does NOT automatically jump.** It only "glides up" obstacles within stepHeight. For full-block terrain, use stepHeight >= 1.01.

## Movement Approaches

### Approach 6: Custom AI Task with PathTraverser.NavigateTo - BEST SOLUTION!

**The winning approach:** Create a custom AI task that uses VS's native `PathTraverser.NavigateTo()` for A* pathfinding and physics-based movement.

```csharp
public class AiTaskRemoteControl : AiTaskBase
{
    private Vec3d? _pendingTarget;
    private float _moveSpeed = 0.03f;
    private string _status = "idle";  // idle, pending, moving, reached, stuck

    public AiTaskRemoteControl(EntityAgent entity, JsonObject taskConfig, JsonObject aiConfig)
        : base(entity, taskConfig, aiConfig) { }

    public override bool ShouldExecute() => true;  // Always active

    public override bool ContinueExecute(float dt)
    {
        if (_pendingTarget != null && _status != "moving")
        {
            _status = "moving";
            // NavigateTo uses A* pathfinding internally!
            pathTraverser.NavigateTo(_pendingTarget, _moveSpeed, 0.5f, OnGoalReached, OnStuck);
            _pendingTarget = null;
        }
        return true;
    }

    private void OnGoalReached() => _status = "reached";
    private void OnStuck() => _status = "stuck";

    public void SetTarget(Vec3d target, float speed) {
        _pendingTarget = target.Clone();
        _moveSpeed = speed;
        _status = "pending";
    }
}
```

**Registration in ModSystem.Start():**
```csharp
AiTaskRegistry.Register<AiTaskRemoteControl>("vsai:remotecontrol");
```

**Entity JSON configuration:**
```json
{
    "code": "taskai",
    "aiCreatureType": "LandCreature",
    "aitasks": [
        {
            "code": "vsai:remotecontrol",
            "priority": 999,
            "movespeed": 0.03
        }
    ]
}
```

**Why this works:**
- `NavigateTo()` runs A* pathfinding to find a valid path
- PathTraverser handles following waypoints
- Physics engine (`controlledphysics`) handles actual movement each tick
- Bot can navigate around obstacles, climb hills, handle terrain changes
- Native VS movement = proper animations, physics, collision

**WalkTowards vs NavigateTo vs Direct Walk:**
- `WalkTowards(target, ...)` - walks in a STRAIGHT LINE toward target, no pathfinding
- `NavigateTo(target, ...)` - runs A* pathfinding, follows waypoints around obstacles
- **Direct Walk** - sets motion vectors directly, bypasses both PathTraverser and pathfinding

### Direct Walk (Motion Vector Approach)

When A* pathfinding fails due to unloaded chunks (beyond ~128 blocks from player), use direct walking:

```csharp
// In AiTaskRemoteControl
private Vec3d? _directWalkTarget;
private float _directWalkSpeed = 0.06f;

public override bool ContinueExecute(float dt)
{
    if (_directWalkTarget != null)
    {
        var currentPos = entity.ServerPos.XYZ;
        double dx = _directWalkTarget.X - currentPos.X;
        double dz = _directWalkTarget.Z - currentPos.Z;
        double dist = Math.Sqrt(dx * dx + dz * dz);

        if (dist < 1.0)  // Arrival threshold
        {
            _directWalkTarget = null;
            entity.ServerPos.Motion.Set(0, entity.ServerPos.Motion.Y, 0);
            _status = "reached";
        }
        else
        {
            // Normalize and set motion
            entity.ServerPos.Motion.X = (dx / dist) * _directWalkSpeed;
            entity.ServerPos.Motion.Z = (dz / dist) * _directWalkSpeed;

            // Face direction of travel
            entity.ServerPos.Yaw = (float)Math.Atan2(-dx, dz);
        }
    }
    return true;
}
```

**Key differences from PathTraverser:**
- Sets `entity.ServerPos.Motion.X/Z` directly (horizontal only)
- Preserves `Motion.Y` for gravity
- No path calculation - walks in straight line
- Works even with unloaded chunks (no A* needed)
- **Will NOT avoid obstacles** - use only in open terrain or as fallback

**API Endpoint:** `/bot/walk` with same params as `/bot/goto`

**MCP Tool:** `bot_walk` - blocks until destination reached (polls isDirectWalking)

### Approach 1: Setting Controls (EntityControls) - DOESN'T WORK
```csharp
agent.Controls.Forward = true;
agent.Controls.Sprint = true;
```
- **Result:** Does NOT move non-player entities
- **Reason:** Server-side control changes don't drive physics movement

### Approach 2: Setting Motion/Velocity - DOESN'T WORK
```csharp
agent.ServerPos.Motion.Set(motionX, motionY, motionZ);
```
- **Result:** Motion gets reset by physics each tick
- **Reason:** Physics behavior overrides motion values

### Approach 3: Teleportation - WORKS (but ugly)
```csharp
entity.TeleportTo(new Vec3d(x, y, z));
```
- **Result:** Works reliably, instant position change
- **Limitation:** Not smooth movement

### Approach 4: PathTraverser - DOESN'T WORK ALONE
```csharp
var taskAiBehavior = entity.GetBehavior("taskai");
var pathTraverser = /* get via reflection */;
pathTraverser.WalkTowards(targetPos, speed, ...);
```
- **Result:** Rotates entity to face target but does NOT move it
- **Reason:** Movement happens in AI task processing loop - empty aitasks = no movement
- **Note:** Works on creatures with active AI tasks, but then AI interferes

### Approach 5: Tick-Based Direct Position Updates - DEPRECATED
We previously used manual tick handlers to update position directly. This worked but was replaced by Approach 6 (Custom AI Task) which uses VS's native movement system.

### Why Approaches 1-4 Failed

**Setting Controls alone:** `agent.Controls.Forward = true` does NOT move non-player entities. Movement requires active AI tasks.

**Setting Motion:** `ServerPos.Motion.X/Z` gets reduced by physics friction each tick.

**PathTraverser alone:** Without an AI task calling it each tick, PathTraverser doesn't execute movement.

## SOLUTION: Animation Triggering - WORKING!

**Problem:** Bot moves but doesn't play walking animation.

**What DOESN'T work:**
- `AnimManager.StartAnimation("walk")` - server-side call doesn't sync to client
- `AnimManager.StartAnimation(new AnimationMetaData { ClientSide = false })` - still doesn't sync

**What WORKS:** Use `triggeredBy` with `onControls` in the entity JSON!

The VS animation system uses `EnumEntityActivity` flags to determine which animations play. When you set `agent.Controls.Forward = true`, it makes `Controls.TriesToMove` return `true`, which sets the `Move` activity flag. Animations with matching `triggeredBy.onControls` will automatically play.

### Entity JSON Animation Configuration
```json
{
    "animations": [
        {
            "code": "idle",
            "animation": "balanced-idle",
            "weight": 1,
            "animationSpeed": 1,
            "triggeredBy": { "defaultAnim": true }
        },
        {
            "code": "walk",
            "animation": "balanced-walk",
            "weight": 10,
            "animationSpeed": 1.5,
            "triggeredBy": {
                "onControls": ["move"],
                "matchExact": false
            }
        },
        {
            "code": "run",
            "animation": "balanced-run",
            "weight": 10,
            "animationSpeed": 1.5,
            "triggeredBy": {
                "onControls": ["move", "sprintmode"],
                "matchExact": true
            }
        }
    ]
}
```

### Valid `onControls` Values (from EnumEntityActivity)
- `idle`, `move`, `sprintmode`, `sneakmode`, `fly`, `swim`, `jump`, `fall`, `climb`, `floorsitting`, `dead`, `break`, `place`, `glide`, `mounted`

**Key Discovery:** Trader shape uses different animation names:
- `balanced-walk` (not "walk")
- `balanced-run` (not "run")
- `balanced-idle` (not "idle")

## Pathfinding - WORKING!

VS has built-in A* pathfinding in the `VSEssentials` mod. We expose it via `/bot/pathfind`.

### Setup
Add reference to VSEssentials.dll in your mod project:
```xml
<Reference Include="VSEssentials">
  <HintPath>$(VINTAGE_STORY)/Mods/VSEssentials.dll</HintPath>
  <Private>false</Private>
</Reference>
```

### Usage
```csharp
using Vintagestory.Essentials;

// Initialize once
_astar = new AStar(api);

// Find path
List<Vec3d> waypoints = _astar.FindPathAsWaypoints(
    startPos,           // BlockPos
    endPos,             // BlockPos
    maxFallHeight,      // int (e.g., 4)
    stepHeight,         // float (e.g., 0.6f)
    entityCollisionBox, // Cuboidf
    searchDepth,        // int (default 9999)
    mhdistanceTolerance,// int (default 0)
    EnumAICreatureType.Humanoid
);
```

### API Endpoint
```bash
curl -X POST http://localhost:4560/bot/pathfind \
  -H "Content-Type: application/json" \
  -d '{"x": 512140, "y": 123, "z": 511870}'

# Response:
{
  "success": true,
  "waypointCount": 10,
  "distance": 11.49,
  "waypoints": [
    {"x": 512131.49, "y": 123, "z": 511864.41},
    ...
  ]
}
```

### How It Works Now
With the custom AI task approach, you just call `/bot/goto` and `NavigateTo()` handles pathfinding internally. The `/bot/pathfind` endpoint is available for debugging or manual path inspection.

### Pathfinding Parameters (for /bot/pathfind endpoint)
- **stepHeight:** Default 1.2 (must be >1.0 to handle 1-block terrain steps)
- **maxFallHeight:** Default 4 blocks
- **searchDepth:** Default 9999. Increase for very long paths

### Important: Target Y Coordinate
The target Y must be at actual ground level (where the bot can stand). If target Y is in the air, the bot will reach X/Z but report "stuck" because it can't reach a floating point.

## Entity Types Comparison

| Entity Type | Has PathTraverser | Has Own AI | Controllable | Notes |
|-------------|-------------------|------------|--------------|-------|
| `playerbot` | NO | NO | Partially | No pathfinding infrastructure |
| `humanoid-trader-*` | YES | YES | NO | AI interferes with control |
| `chicken-rooster` | YES | YES | NO | AI makes it flee |
| `vsai:aibot` (custom) | YES | Custom (remote-controlled) | YES | **Our solution!** |

## API Endpoints

| Endpoint | Method | Description |
|----------|--------|-------------|
| `/status` | GET | Server status, player count, bot status |
| `/player/observe` | GET | Player position & world info |
| `/bot/spawn` | POST | Spawn bot entity (default: vsai:aibot) |
| `/bot/despawn` | POST | Remove tracked bot |
| `/bot/cleanup` | POST | Kill ALL aibot entities in world |
| `/bot/observe` | GET | Bot position, rotation, state |
| `/bot/blocks?radius=N` | GET | Blocks around bot |
| `/bot/entities?radius=N` | GET | Entities around bot |
| `/bot/move` | POST | Teleport bot to position |
| `/bot/goto` | POST | Walk to position (uses NavigateTo pathfinding) |
| `/bot/walk` | POST | Direct walk to position (bypasses A* pathfinding) |
| `/bot/stop` | POST | Stop movement |
| `/bot/break` | POST | Break block at position |
| `/bot/place` | POST | Place block at position |
| `/bot/interact` | POST | Interact with block (doors, gates, levers, etc.) |
| `/bot/pathfind` | POST | Calculate path using VS AStar (returns waypoints) |
| `/bot/movement/status` | GET | Get current movement status (for async polling) |
| `/bot/inventory` | GET | Get inventory contents (slots + hand items) |
| `/bot/collect` | POST | Pick up loose surface item block at position |
| `/bot/pickup` | POST | Pick up dropped item entity (nearest or by ID) |
| `/bot/inventory/drop` | POST | Drop item from inventory to world |
| `/bot/knap` | POST | Knap flint/stone tool (axe, knife, shovel, etc.) |
| `/bot/craft` | POST | Grid craft by output name (e.g., axe from head + stick) |
| `/bot/equip` | POST | Equip item from inventory to hand (by slotIndex or itemCode) |
| `/screenshot` | POST | Take screenshot (macOS, requires Screen Recording permission) |

### Movement Status Endpoint

The `/bot/movement/status` endpoint enables async movement monitoring - critical for MCP tool integration.

```bash
curl -s http://localhost:4560/bot/movement/status
```

**Response:**
```json
{
    "success": true,
    "status": "moving",           // idle, pending, moving, reached, stuck
    "statusMessage": "Moving to (100.0, 72.0, 100.0)",
    "position": { "x": 95.5, "y": 72.0, "z": 95.5 },
    "target": { "x": 100.0, "y": 72.0, "z": 100.0 },
    "isActive": true,
    "onGround": true
}
```

**MCP Tool Pattern:**
1. Issue `/bot/goto` command
2. Poll `/bot/movement/status` every 100ms
3. Collect position samples
4. Return complete movement trace when status is "reached" or "stuck"

This allows LLM agents to issue a single tool call and receive the full result.

## Key VS Modding Concepts

### Server vs Client Authority
- **Player movement:** Client is authoritative - server cannot override
- **Entity movement:** Server is authoritative - can be controlled from server
- **Position sync:** Update `ServerPos`, then call `Pos.SetFrom(ServerPos)`

### Threading
- HTTP requests come in on background threads
- Game state modifications must be done on main thread:
```csharp
_serverApi.Event.EnqueueMainThreadTask(() => {
    // Safe to modify game state here
}, "TaskName");
```

### Game Tick Listener
For continuous updates (like movement):
```csharp
// Register (returns listener ID for later removal)
long id = _serverApi.Event.RegisterGameTickListener(MyTickHandler, 50); // 50ms interval

// Unregister when done
_serverApi.Event.UnregisterGameTickListener(id);
```

### Block Manipulation
```csharp
// Break block
blockAccessor.SetBlock(0, blockPos);  // 0 = air
blockAccessor.TriggerNeighbourBlockUpdate(blockPos);

// Place block
var block = world.GetBlock(new AssetLocation("game:dirt"));
blockAccessor.SetBlock(block.BlockId, blockPos);

// Place torch
blockAccessor.SetBlock(world.GetBlock(new AssetLocation("torch-basic-lit-up")).BlockId, blockPos);
```

## Common Errors & Solutions

### "Entity shape not found... Entity will be invisible!"
- **Cause:** Shape path references another mod but VS looks in your mod first
- **Solution:** Copy the shape JSON file into your mod's `assets/[modid]/shapes/` folder

### Animation trigger "onControls" parse error
- **Cause:** Using control names like "forward" instead of EnumEntityActivity values
- **Solution:** Use activity names like "Walk", "Sprint", or remove triggeredBy and trigger manually

### Entity spawns but doesn't move with PathTraverser
- **Cause:** PathTraverser needs to be called from within an active AI task
- **Solution:** Create a custom AI task that calls `pathTraverser.NavigateTo()` - see Approach 6

### Bot gets stuck on 1-block steps
- **Cause:** `stepHeight` in `controlledphysics` is too low (default 0.6)
- **Solution:** Set `stepHeight: 1.01` on BOTH client and server controlledphysics behaviors

### Pathfinder returns "No path found" for nearby valid destinations
- **Cause:** Default `stepHeight` (0.6) is too low to handle 1-block terrain rises
- **Solution:** Use `stepHeight >= 1.1` for natural terrain with 1-block elevation changes
- **Note:** Pathfinder expects AIR block coordinates (where entity stands), not ground block coordinates. Use `ServerPos.AsBlockPos` directly without Y adjustment.

### Pathfinder coordinate system
- **Pathfinder expects:** Air block Y coordinate (where entity's feet are)
- **NOT:** Ground block Y coordinate (what entity stands ON)
- **Example:** Bot stands at Y=122 (air) on ground at Y=121 (soil) → pass Y=122 to pathfinder
- **Caller must provide:** Target Y at the air level where bot should end up standing

## File Locations

- **VS Installation:** `/Applications/Vintage Story.app`
- **Mods folder:** `~/Library/Application Support/VintageStoryData/Mods/`
- **Server logs:** `~/Library/Application Support/VintageStoryData/Logs/server-main.log`
- **Client logs:** `~/Library/Application Support/VintageStoryData/Logs/client-main.log`
- **Build command:** `VINTAGE_STORY="/Applications/Vintage Story.app" dotnet build`

## Project Structure

```
VintageStory-AI/
├── LEARNINGS.md            # This file
├── CLAUDE.md               # Claude Code instructions
├── mod/                    # C# Vintage Story mod (git repo)
│   ├── mod.csproj          # References VintagestoryAPI.dll, VSEssentials.dll
│   ├── modinfo.json        # Mod metadata (type: "Code")
│   ├── VsaiModSystem.cs    # Main mod code with HTTP server
│   ├── AiTaskRemoteControl.cs  # Custom AI task for remote control
│   └── assets/
│       └── vsai/
│           ├── entities/
│           │   └── aibot.json
│           ├── shapes/
│           │   └── entity/humanoid/
│           │       └── trader.json   # Copied from survival mod
│           └── textures/
│               └── entity/humanoid/
│                   └── trader.png    # Copied from survival mod
└── tools/                  # Python tools (git repo)
    └── bot_move.py         # CLI tool for movement testing
```

## Python CLI Tool (bot_move.py)

A CLI tool that demonstrates the MCP async movement pattern:

```bash
# Move to absolute position
python3 tools/bot_move.py 100 72 100

# Relative movement
python3 tools/bot_move.py 5 0 5 --relative

# Custom speed
python3 tools/bot_move.py 100 72 100 --speed 0.06

# Faster polling
python3 tools/bot_move.py 100 72 100 --poll-interval 50
```

**What it does:**
1. Gets initial position from `/bot/movement/status`
2. Issues POST to `/bot/goto`
3. Polls `/bot/movement/status` every 100ms
4. Prints real-time progress
5. Returns summary with distance, time, samples

**Sample output:**
```
Starting position: (100.50, 72.00, 100.50)
Target position: (105.50, 72.00, 105.50)
Distance: 7.07 blocks

Movement started, polling status...
--------------------------------------------------
  [ 0.10s] (100.60, 72.00, 100.60) | status=moving | to_target=6.93
  [ 2.35s] (105.50, 72.00, 105.50) | status=reached | to_target=0.12
--------------------------------------------------

MOVEMENT RESULT: ✓ reached
Total traveled: 6.60 blocks
Elapsed time: 2.12s
Average speed: 3.15 blocks/s
```

## Resources

- [VS API Docs](https://apidocs.vintagestory.at/)
- [VS Modding Wiki](https://wiki.vintagestory.at/Modding:Entity_Behaviors)
- [vsapi source](https://github.com/anegostudios/vsapi) - API source code
- [vssurvivalmod source](https://github.com/anegostudios/vssurvivalmod) - Contains AI task implementations

## Hostile Creature Targeting

### Do Hostile Creatures Attack the Bot?

**By default, NO.** Hostile creatures like drifters use AI tasks with `entityCodes` that specify valid targets:

```json
{
    "code": "meleeattack",
    "entityCodes": ["player"],
    ...
}
```

Our bot has entity code `"aibot"` and uses class `EntityAgent` (not `EntityPlayer`), so it doesn't match `"player"` and is ignored.

### Making the Bot Attackable (Future Enhancement)

**Option 1: JSON Patch (Recommended)**

Add a patch file in the mod to add `"aibot"` to hostile creature target lists:

```
mod/assets/vsai/patches/hostile-targeting.json
```

```json
[
    {
        "op": "add",
        "path": "/server/behaviors/*/aitasks/*/entityCodes/-",
        "value": "aibot",
        "file": "game:entities/land/drifter-*.json",
        "condition": {
            "path": "/server/behaviors/*/aitasks/*/code",
            "value": "meleeattack"
        }
    }
]
```

**Note:** The exact patch syntax may need refinement - VS JSON patching has specific rules for wildcard paths.

**Creatures to patch:**
- `drifter-*` (surface, deep, tainted, corrupt, nightmare)
- `wolf-*`
- `bear-*`
- `hyena-*`
- `locust-*`
- Other hostile mobs

**Option 2: Change Bot Entity Code**

Could potentially use an entity code that already matches hostile targeting (e.g., wildcards like `humanoid-*`), but this risks:
- Breaking other mod interactions
- Unintended targeting by other systems
- Not recommended

### Relevant VS Modding Docs

- [AiTaskBaseTargetable](https://wiki.vintagestory.at/Modding:AiTaskBaseTargetable) - Target selection properties
- [Entity Behavior taskai](https://wiki.vintagestory.at/Modding:Entity_Behavior_taskai) - AI task system
- [JSON Patching](https://wiki.vintagestory.at/Modding:JSON_Patching) - How to patch entity files

### Key Properties for Target Selection

| Property | Description |
|----------|-------------|
| `entityCodes` | List of entity codes to target (exact or wildcard with `*` suffix) |
| `skipEntityCodes` | List of entity codes to never target |
| `friendlyTarget` | Override hostility restrictions when true |
| `creatureHostility` | World config (aggressive/passive/off) affects player targeting |

## Bot Inventory System - IMPLEMENTED!

### Overview

The bot now has a full inventory system using the `seraphinventory` behavior. This provides ~4 inventory slots plus hand slots.

**Key decision:** We chose **manual collection** over auto-pickup (`collectitems` behavior) because auto-pickup was deemed too aggressive for an AI-controlled bot. The bot must be explicitly commanded to pick up items.

### Entity JSON Configuration

Added `seraphinventory` to both client and server behaviors:

```json
{
    "client": {
        "behaviors": [
            { "code": "seraphinventory" }
        ]
    },
    "server": {
        "behaviors": [
            { "code": "seraphinventory" }
        ]
    }
}
```

### C# Implementation Details

**Custom inventory behavior:** We use `EntityBehaviorBotInventory` with `InventoryGeneric` (32 slots) instead of `SeraphInventory` (only 4 usable slots).

```csharp
// Access inventory behavior
var inventoryBehavior = agent.GetBehavior<EntityBehaviorBotInventory>();
var inventory = inventoryBehavior?.Inventory;

// Give items to entity
agent.TryGiveItemStack(itemStack);  // Returns true if successful

// Access inventory slots
var slot = inventory[slotIndex];
var item = slot.Itemstack;

// Take items from slot
var taken = slot.TakeOut(quantity);
slot.MarkDirty();

// Access hand slots
agent.LeftHandItemSlot
agent.RightHandItemSlot
```

### API Endpoints

| Endpoint | Method | Description |
|----------|--------|-------------|
| `/bot/inventory` | GET | Get all inventory slots and hand items |
| `/bot/collect` | POST | Pick up loose surface item block at position |
| `/bot/pickup` | POST | Pick up dropped item entity (nearest or by ID) |
| `/bot/inventory/drop` | POST | Drop item from inventory to world |

### /bot/inventory Response

```json
{
    "success": true,
    "slotCount": 4,
    "handLeft": null,
    "handRight": { "code": "game:flint", "quantity": 1, "name": "Flint" },
    "slots": [
        { "index": 0, "code": "game:stone-granite", "quantity": 3, "name": "Stone" },
        { "index": 1, "code": null, "quantity": 0, "name": null },
        ...
    ]
}
```

### /bot/collect Request/Response

**Request:**
```json
{ "x": 512100, "y": 120, "z": 511850 }
```

**Response:**
```json
{
    "success": true,
    "position": { "x": 512100, "y": 120, "z": 511850 },
    "brokenBlock": "looseflints-granite-free",
    "collectedItems": [
        { "code": "game:flint", "quantity": 1, "name": "Flint" }
    ]
}
```

**Constraints:**
- Bot must be within 5 blocks of target
- Only collectible loose items: `looseflints-*`, `loosestones-*`, `looseboulders-*`, `looseores-*`, `stick-*`, and blocks with `-free` suffix
- If inventory is full, remaining items spawn in world

### /bot/pickup Request/Response

**Important distinction:**
- `/bot/collect` - For **loose item blocks** (natural worldgen spawns like flint on ground)
- `/bot/pickup` - For **dropped item entities** (items dropped by players or from breaking blocks)

**Request (pick up nearest):**
```json
{}
```

**Request (by entity ID):**
```json
{ "entityId": 1130, "maxDistance": 5 }
```

**Response:**
```json
{
    "success": true,
    "entityId": 1130,
    "pickedUpItem": { "code": "game:stick", "quantity": 1, "name": "Stick" }
}
```

**Constraints:**
- Bot must be within `maxDistance` blocks (default: 5)
- Only picks up `EntityItem` entities (dropped items)
- Item entity is despawned after pickup

### /bot/inventory/drop Request/Response

**Request (by slot index):**
```json
{ "slotIndex": 0, "quantity": 1 }
```

**Request (by item code):**
```json
{ "itemCode": "flint", "quantity": 2 }
```

**Response:**
```json
{
    "success": true,
    "slotIndex": 0,
    "droppedItem": { "code": "game:flint", "quantity": 1, "name": "Flint" }
}
```

### MCP Tools

| Tool | Description |
|------|-------------|
| `bot_inventory` | Get inventory contents |
| `bot_collect` | Pick up loose item block at position |
| `bot_pickup` | Pick up dropped item entity |
| `bot_inventory_drop` | Drop item from inventory |
| `bot_interact` | Interact with block (doors, gates, levers) |

### Typical Workflow

1. Scan for loose items: `bot_blocks` with filter "flint,stone"
2. Move near the item: `bot_goto` to a position near the target
3. Collect the item: `bot_collect` with exact block coordinates
4. Verify collection: `bot_inventory` to see what was picked up
5. Return to base: `bot_goto` back to starting position
6. Drop items: `bot_inventory_drop` to deposit items

### Relevant VS Modding Docs

- [Entity Behaviors](https://wiki.vintagestory.at/Modding:Entity_Behaviors)
- [EntityAgent API](https://apidocs.vintagestory.at/api/Vintagestory.API.Common.EntityAgent.html)
- [Basic Inventory Handling](https://wiki.vintagestory.at/Modding:Basic_Inventory_Handling)

## Bot Block Interaction - IMPLEMENTED!

### Overview

The bot can now interact with activatable blocks like doors, gates, trapdoors, and levers using the `block.Activate()` API.

### API Endpoint

| Endpoint | Method | Description |
|----------|--------|-------------|
| `/bot/interact` | POST | Interact with a block (simulate right-click) |

### /bot/interact Request/Response

**Request:**
```json
{ "x": 512100, "y": 120, "z": 511850 }
```

**Request (relative coordinates):**
```json
{ "x": 1, "y": 0, "z": 0, "relative": true }
```

**Response:**
```json
{
    "success": true,
    "message": "Activated block game:door-plank-north",
    "block": "game:door-plank-north",
    "position": { "x": 512100, "y": 120, "z": 511850 }
}
```

### MCP Tool

| Tool | Description |
|------|-------------|
| `bot_interact` | Interact with a block at specified position |

### How It Works

Uses VS `Block.Activate()` API with:
- `Caller` - Identifies the bot entity as the one interacting
- `BlockSelection` - Target block position and face
- `TreeAttribute` - Optional activation parameters

**Key code pattern (from DanaTweaks mod):**
```csharp
block.Activate(world, new Caller() { Entity = entity }, blockSel, new TreeAttribute());
```

### Supported Block Types

| Block Type | Behavior |
|------------|----------|
| Doors | Open/close |
| Fence gates | Open/close |
| Trapdoors | Open/close |
| Levers | Toggle state |
| Buttons | Activate |
| Hatches | Open/close |

### Usage Notes

1. **Distance:** Bot should be near the target block (within 5 blocks recommended)
2. **Block face:** Currently defaults to NORTH; may need enhancement for face-specific blocks
3. **Locked doors:** Does not check for reinforced/locked doors; those will silently fail
4. **Double doors:** May need to activate both halves separately

### Typical Workflow

1. Scan for doors: `bot_blocks` with filter "door"
2. Move near door: `bot_goto` to adjacent position
3. Open door: `bot_interact` with door coordinates
4. Walk through: `bot_goto` to other side
5. Close door: `bot_interact` again

## Bot Hunger/Satiety (Future Enhancement)

### Does the Bot Get Hungry?

**By default, NO.** The hunger/satiety system is controlled by the `hunger` entity behavior, which is only applied to players by default.

### How Hunger Works in VS

- `currentsaturation` - Current fullness level (float)
- `maxsaturation` - Maximum capacity (1500 for player)
- Saturation depletes over time
- When empty, entity takes damage
- Food restores saturation with nutritional categories (fruit, vegetable, grain, protein, dairy)

### Adding Hunger to the Bot

**Entity JSON changes:**
```json
{
    "server": {
        "behaviors": [
            { "code": "hunger", "currentsaturation": 1500, "maxsaturation": 1500 }
        ]
    }
}
```

**EntityAgent API:**
- `ReceiveSaturation(float saturation, EnumFoodCategory foodCat, float saturationLossDelay, float nutritionGainMultiplier)` - Feed the entity
- `ShouldReceiveSaturation(...)` - Check if entity can receive food

### Implementation Considerations

1. **Default behavior designed for players** - May have edge cases for non-player entities
2. **Farm Life mod example** - Adds custom hunger to animals with grazing, feeding from troughs
3. **May need custom behavior** - Override `ShouldReceiveSaturation()` if default doesn't work

### API Endpoints needed

- `/bot/hunger` - GET current saturation status
- `/bot/feed` - POST feed the bot with food item

### Relevant VS Modding Docs

- [Satiety - Vintage Story Wiki](https://wiki.vintagestory.at/Satiety)
- [Entity Behaviors](https://wiki.vintagestory.at/Modding:Entity_Behaviors)
- [EntityAgent API](https://apidocs.vintagestory.at/api/Vintagestory.API.Common.EntityAgent.html)

## Bot Crafting - KNAPPING IMPLEMENTED!

### VS Crafting Systems

Vintage Story has **5 distinct crafting systems**:

| System | Description | Automatable? |
|--------|-------------|--------------|
| Grid crafting | 3×3 pattern matching | YES - via GridRecipe API |
| Knapping | Interactive voxel removal for flint/stone tools | YES - via BlockEntity manipulation |
| Clay forming | Voxel placement for pottery | YES - similar to knapping |
| Smithing | Anvil hammer strikes for metal tools | YES - similar to knapping |
| Casting | Pouring molten metal into molds | Potentially - needs research |

### Grid Crafting API - FULLY DOCUMENTED

**Access all recipes:**
```csharp
List<GridRecipe> recipes = api.World.GridRecipes;
```

**Three-step crafting process:**
```csharp
// 1. Check if items match a recipe
bool matches = recipe.Matches(player, ingredientSlots, gridWidth);

// 2. Remove input items (calls OnConsumedByCrafting callbacks)
recipe.ConsumeInput(player, inputSlots, gridWidth);

// 3. Create output item (calls OnCreatedByCrafting callbacks)
recipe.GenerateOutputStack(inputSlots, outputSlot);
```

**Key GridRecipe methods:**
- `Matches(IPlayer forPlayer, ItemSlot[] ingredients, int gridWidth)` - Validates ingredients match pattern
- `MatchesShapeLess(ItemSlot[] suppliedSlots, int gridWidth)` - For shapeless recipes
- `ConsumeInput(IPlayer byPlayer, ItemSlot[] inputSlots, int gridWidth)` - Removes ingredients
- `GenerateOutputStack(ItemSlot[] inputSlots, ItemSlot outputSlot)` - Creates output, handles attribute copying

**Key GridRecipe properties:**
- `Enabled` - Can disable recipes at runtime
- `Shapeless` - Whether ingredient order matters
- `RequiresTrait` - Player trait requirements (e.g., "knapper")
- `resolvedIngredients` - Ingredients with pattern codes resolved

### Knapping API - FULLY DOCUMENTED

**Key discovery from [Knapster mod](https://github.com/ApacheTech-VintageStory-Mods/Knapster):**

Knapping uses `BlockEntityKnappingSurface` with a 16x16 voxel grid:

```csharp
// Access knapping surface at block position
var blockEntity = world.BlockAccessor.GetBlockEntity(blockPos) as BlockEntityKnappingSurface;

// The voxel grid (16x16)
blockEntity.Voxels[x, z]  // Current state (true = material present)

// The recipe pattern
blockEntity.SelectedRecipe.Voxels[x, 0, z]  // Target pattern

// INSTANT COMPLETION - copy recipe voxels directly!
for (var x = 0; x < 16; x++) {
    for (var z = 0; z < 16; z++) {
        blockEntity.Voxels[x, z] = blockEntity.SelectedRecipe.Voxels[x, 0, z];
    }
}
```

**How Knapster works:**
1. Uses Harmony to patch `BlockEntityKnappingSurface.OnUseOver()`
2. Intercepts before vanilla code runs
3. Directly copies recipe voxels to complete instantly
4. Has configurable "voxels per click" for partial automation

**Clay Forming / Smithing:**
Similar pattern with `BlockEntityClayForm` and `BlockEntityAnvil` - same voxel grid approach.

### Implementation Plan for Bot Crafting

**Phase 1: Grid Crafting** - IMPLEMENTED!

The `/bot/craft` endpoint allows the bot to craft items using grid recipes:

```bash
# Craft a flint axe (bot must have axe head + stick in inventory)
curl -X POST http://localhost:4560/bot/craft \
  -H "Content-Type: application/json" \
  -d '{"recipe": "axe"}'
```

**Implementation approach:**
1. Get all recipes via `_serverApi.World.GridRecipes`
2. Find matching recipe by output name (e.g., "axe" matches "axe-flint")
3. Get required ingredients from `recipe.resolvedIngredients`
4. Check bot inventory for ALL required ingredients using `SatisfiesAsIngredient()`
5. Consume ingredients from inventory
6. Create output itemstack from `recipe.Output.ResolvedItemstack`
7. Give to bot via `TryGiveItemStack()` (drops if inventory full)

**MCP Tool:** `bot_craft(recipe="axe")` - Works for combining tool heads with sticks, etc.

### Tool Ingredients (IsTool Property) - FIXED!

**Problem:** Recipes like firewood (axe + log) were consuming the axe instead of just reducing its durability.

**Root cause:** The `bot_craft` handler was consuming ALL ingredients without checking if they were tools.

**Solution:** Check `CraftingRecipeIngredient.IsTool` property - if true, reduce durability instead of consuming:

```csharp
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
```

**Key properties:**
- `ingredient.IsTool` - boolean, true if ingredient is a tool that shouldn't be consumed
- `ingredient.ToolDurabilityCost` - int, durability to reduce (defaults to 1)
- `Collectible.DamageItem(world, entity, slot, amount)` - reduces durability
- `Collectible.GetRemainingDurability(stack)` - check if tool broke

**Reference:** [CraftingRecipeIngredient API](https://apidocs.vintagestory.at/api/Vintagestory.API.Common.CraftingRecipeIngredient.html)

**Phase 2: Knapping** - IMPLEMENTED & TESTED!

The `/bot/knap` endpoint allows the bot to craft flint/stone tools instantly:

```bash
# Knap an axe (bot must have flint or stone in inventory)
curl -X POST http://localhost:4560/bot/knap \
  -H "Content-Type: application/json" \
  -d '{"recipe": "axe"}'
```

**Tested Results (2026-01-22):**
- `bot_knap("axe")` - Creates flint axe head from flint ✓
- `bot_knap("spear")` - Creates flint spear head from flint ✓
- `bot_knap("knife")` - Requires stone (andesite, etc.) not flint
- Flint consumed from inventory correctly ✓
- Output created and either added to inventory or dropped ✓

**Implementation approach (ACTUAL WORKING CODE):**
1. Get recipes via `_serverApi.World.Api.GetKnappingRecipes()` extension method
2. Find matching recipe by output name (e.g., "axe" matches "axehead")
3. Find material in bot inventory that satisfies `recipe.Ingredient.SatisfiesAsIngredient()`
4. Create temporary knapping surface block in front of bot
5. Set `selectedRecipeId` via reflection (field is private)
6. Copy `recipe.Voxels[x, 0, z]` to `blockEntity.Voxels[x, z]` for instant completion
7. Create output itemstack and give to bot via `TryGiveItemStack()` (drops if fails)
8. Remove knapping surface block

**Key code patterns:**
```csharp
// Get recipe list - use extension method, NOT direct property
var recipes = _serverApi.World.Api.GetKnappingRecipes();

// Find recipe by output
var recipe = recipes.FirstOrDefault(r =>
    r.Output.ResolvedItemstack.Collectible.Code.Path.Contains(recipeName));

// Set recipe via reflection (selectedRecipeId is private)
var field = blockEntity.GetType().GetField("selectedRecipeId",
    BindingFlags.NonPublic | BindingFlags.Instance);
field.SetValue(blockEntity, recipeIndex);

// Instant completion - copy recipe voxels
for (int x = 0; x < 16; x++) {
    for (int z = 0; z < 16; z++) {
        blockEntity.Voxels[x, z] = recipe.Voxels[x, 0, z];
    }
}
```

**Phase 3: Clay Forming / Smithing** (TODO)
Same pattern as knapping with respective BlockEntity classes.

### Mods Analyzed

| Mod | Approach | Useful For |
|-----|----------|------------|
| [Knapster](https://github.com/ApacheTech-VintageStory-Mods/Knapster) | Harmony patches + voxel copy | Knapping instant-complete code |
| [QPTECH](https://github.com/Novocain1/qptech) | Custom config-based recipes | Machine automation patterns |
| [ImmersiveCrafting](https://github.com/Craluminum-Mods/ImmersiveCrafting) | Custom block behaviors | Liquid/tool crafting |

### Crafting API Endpoints

| Endpoint | Method | Status | Description |
|----------|--------|--------|-------------|
| `/bot/knap` | POST | **DONE** | Knap flint/stone tool from inventory material |
| `/bot/craft` | POST | **DONE** | Grid craft by output name (e.g., combine head + stick) |
| `/bot/equip` | POST | **DONE** | Equip item from inventory to hand slot |
| `/bot/recipes` | GET | TODO | List available grid recipes bot can craft |
| `/bot/cancraft` | GET | TODO | Check if bot has materials for recipe |
| `/bot/clayform` | POST | TODO | Complete clay forming at position |
| `/bot/smith` | POST | TODO | Complete smithing at position |

### What the Bot CAN Craft

**Grid Recipes (Phase 1):**
- Planks from logs
- Sticks from planks
- Torches
- Rope
- Some food items
- Storage containers
- Basic blocks

**Knapping (Phase 2):**
- Flint axe, knife, shovel, hoe, spear
- Stone versions of all tools

**Clay Forming (Phase 3):**
- Bowls, pots, storage vessels
- Crucibles, molds

**Smithing (Phase 3):**
- Metal tools (after smelting)
- Weapons, armor

### Relevant VS Modding Docs

- [GridRecipe API](https://apidocs.vintagestory.at/api/Vintagestory.API.Common.GridRecipe.html)
- [IWorldAccessor.GridRecipes](https://apidocs.vintagestory.at/api/Vintagestory.API.Common.IWorldAccessor.html)
- [Grid Recipes Guide](https://wiki.vintagestory.at/Modding:Grid_Recipes_Guide)
- [Knapping Recipes](https://wiki.vintagestory.at/Modding:Asset_Type_-_Recipes_(Knapping))
- [Crafting Wiki](https://wiki.vintagestory.at/Crafting)
- [Knapping Wiki](https://wiki.vintagestory.at/Knapping)

## Bot Mining

### Available Endpoints

| Endpoint | Behavior | Use Case |
|----------|----------|----------|
| `/bot/break` | Instant break, always drops | Testing/cheat mode |
| `/bot/mine` | Checks tool tier, conditional drops | Realistic mining |

### `/bot/mine` Implementation (DONE!)

The `/bot/mine` endpoint respects tool tier requirements:

```csharp
var toolSlot = agent.RightHandItemSlot;
int toolTier = toolSlot?.Itemstack?.Collectible?.ToolTier ?? 0;
int requiredTier = block.RequiredMiningTier;
bool canGetDrops = toolTier >= requiredTier;

// Block always breaks, but drops only if tool tier is sufficient
if (canGetDrops) {
    blockDrops = block.GetDrops(_serverApi.World, blockPos, null);
    // Spawn drops in world
}
blockAccessor.SetBlock(0, blockPos);
```

**Response format:**
```json
{
  "success": true,
  "brokenBlock": "rock-granite",
  "position": {"x": 512100, "y": 120, "z": 511850},
  "tool": {"code": "pickaxe-flint", "tier": 1},
  "requiredTier": 1,
  "dropsObtained": true,
  "drops": ["1x stone-granite"]
}
```

### How VS Mining Actually Works

**Block properties that control mining:**

| Property | Type | Description |
|----------|------|-------------|
| `requiredminingtier` | int (0-5) | Minimum tool tier to get drops |
| `resistance` | float | Breaking time in seconds (0.5 to 60) |
| `blockmaterial` | enum | Stone, Metal, Wood, Soil - affects tool speed |

**Tool tier hierarchy:**

| Tier | Materials |
|------|-----------|
| 0 | Metal scraps (emergency tools) |
| 1 | Stone, flint |
| 2 | Copper, gold, silver |
| 3 | Bronze alloys |
| 4 | Iron, meteoric iron |
| 5 | Steel |

**Example:** Copper ore requires tier 2+. Using flint pickaxe (tier 1) = block breaks but NO DROPS.

### Tool-Conditional Drops

Blocks can specify different drops based on tool:

```json
"drops": [
  { "code": "ore-copper", "tool": "pickaxe" },
  { "code": "rockdust", "tool": "*" }
]
```

### API Methods for Mining

| Method | Description |
|--------|-------------|
| `block.GetDrops(world, pos, player)` | Returns drops based on player's tool |
| `block.RequiredMiningTier` | Get minimum tier needed |
| `block.Resistance` | Get breaking time |
| `tool.ToolTier` | Get tool's mining tier |
| `tool.MiningSpeed[material]` | Get speed multiplier for material |

### Proper Bot Mining Implementation

To implement realistic mining:

1. **Require inventory system** - Bot needs to hold tools
2. **Check tool compatibility:**
   ```csharp
   var tool = botEntity.RightHandItemSlot?.Itemstack?.Collectible;
   int toolTier = tool?.ToolTier ?? 0;
   int requiredTier = block.RequiredMiningTier;

   if (toolTier < requiredTier) {
       // Can break but no drops
   }
   ```
3. **Simulate mining time:**
   ```csharp
   float miningSpeed = tool?.MiningSpeed[block.BlockMaterial] ?? 1f;
   float breakTime = block.Resistance / miningSpeed;
   // Wait breakTime seconds before breaking
   ```
4. **Pass player/tool to GetDrops:**
   ```csharp
   // Need IPlayer or tool context for proper drops
   var drops = block.GetDrops(world, pos, playerWithTool);
   ```

### Future Enhancements

- `/bot/canmine` - GET check if bot can mine block with current tool
- Mining time simulation based on `block.Resistance / tool.MiningSpeed[material]`
- Tool durability tracking
- Tool type matching (pickaxe for stone, axe for wood)

### Relevant VS Modding Docs

- [Mining Wiki](https://wiki.vintagestory.at/Mining)
- [Tools Wiki](https://wiki.vintagestory.at/Tools)
- [Block JSON Properties](https://wiki.vintagestory.at/Modding:Block_Json_Properties)
- [Block API](https://apidocs.vintagestory.at/api/Vintagestory.API.Common.Block.html)
- [CollectibleObject API](https://apidocs.vintagestory.at/api/Vintagestory.API.Common.CollectibleObject.html)

## Next Steps

1. ~~**Fix walking animation**~~ - DONE! Use `triggeredBy.onControls` in entity JSON
2. ~~**Add pathfinding**~~ - DONE! Using VS AStar via `/bot/pathfind` endpoint
3. ~~**Fix terrain handling**~~ - DONE! Using custom AI task with NavigateTo + stepHeight=1.01
4. ~~**Movement status endpoint**~~ - DONE! `/bot/movement/status` for async polling
5. ~~**Python CLI tool**~~ - DONE! `tools/bot_move.py` demonstrates MCP pattern
6. ~~**MCP Server**~~ - DONE! Python MCP server with visibility filtering
7. ~~**Ground-level targeting**~~ - DONE! Auto-detect ground Y at target X/Z coordinates
8. ~~**Minimap integration**~~ - DONE! Bot appears on minimap/world map with custom cyan marker
9. **Hostile creature targeting** - JSON patch to make creatures attack the bot
10. ~~**Inventory management**~~ - DONE! Manual pickup/drop with seraphinventory behavior
11. **Hunger/satiety system** - Add hunger behavior, feeding endpoints
12. **Crafting system** - Simulated grid crafting for basic recipes
13. ~~**Mining system**~~ - DONE! `/bot/mine` with tool tier checks, conditional drops
14. **Combat** - Attack entities, defend

## Key Insights from Session 6

1. **Target Y matters:** When giving movement targets, the Y coordinate must be at actual ground level. If target Y is in the air (no ground), bot will reach X/Z but report "stuck" because it can't reach floating point.

2. **Two movement systems:**
   - `WalkTowards()` - straight line, no pathfinding
   - `NavigateTo()` - A* pathfinding around obstacles

3. **Physics doesn't jump:** The `controlledphysics` behavior only "glides up" within stepHeight. It never triggers actual jumps. For full-block terrain, stepHeight must be >= 1.01.

4. **stepHeight on both sides:** Must set stepHeight in controlledphysics on BOTH client AND server behaviors in entity JSON.

## Key Insights from Session 7

1. **MCP Server:** Built Python MCP server (`mcp-server/vsai_server.py`) with tools for all bot endpoints. `bot_goto` blocks until movement completes.

2. **Line-of-sight visibility:** Created `vintage-story-core` Python library with voxel raycast algorithm (Amanatides & Woo) to filter blocks to only those visible from bot's position. Reduces block scan from 713 to ~24 visible surface blocks.

3. **Cleanup on spawn:** Modified `/bot/spawn` to automatically cleanup all existing aibot entities before spawning new one. Prevents orphaned bots accumulating.

4. **Ground-level auto-detection:** `/bot/goto` now automatically finds ground level at target X,Z coordinates. Scans downward to find first solid block, uses Y+1 as standing position.

5. **Bot health:** Bot has 20 HP (like player), takes fall damage, has hurt/death sounds configured.

6. **Hostile targeting:** Hostile creatures (drifters, wolves) do NOT attack the bot by default - they only target `"player"` entity code. Can be changed via JSON patch to add `"aibot"` to their `entityCodes` target list.

## Minimap Integration - WORKING!

### How It Works

The bot now appears on the minimap (F6) and world map (M) as a cyan marker.

**Key Components:**
1. `AiBotMapLayer` - Custom `MapLayer` subclass that tracks aibot entities
2. `EntityMapComponent` - VS class that handles rendering entity markers
3. Cairo texture generation - Creates the cyan circular marker

### Implementation Details

**Mod loads on both sides:**
```csharp
public override bool ShouldLoad(EnumAppSide side)
{
    return true;  // Load on both client and server
}
```

**Register map layer in StartClientSide:**
```csharp
public override void StartClientSide(ICoreClientAPI api)
{
    var mapManager = api.ModLoader.GetModSystem<WorldMapManager>();
    mapManager.RegisterMapLayer<AiBotMapLayer>("vsai-bot", 0.5);
}
```

**Track only aibot entities:**
```csharp
private void OnEntitySpawn(Entity entity)
{
    if (entity.Code?.Path != "aibot") return;
    var component = new EntityMapComponent(_capi, _botTexture, entity);
    _components[entity.EntityId] = component;
}
```

### Required References

Project references needed for minimap integration:
```xml
<Reference Include="VintagestoryLib">
  <HintPath>$(VINTAGE_STORY)/VintagestoryLib.dll</HintPath>
</Reference>
<Reference Include="cairo-sharp">
  <HintPath>$(VINTAGE_STORY)/Lib/cairo-sharp.dll</HintPath>
</Reference>
```

### MapLayer Properties

| Property | Value | Description |
|----------|-------|-------------|
| `Title` | "AI Bot" | Layer name in UI |
| `DataSide` | `EnumMapAppSide.Client` | Data is client-side |
| `LayerGroupCode` | "entities" | Groups with other entity layers |

### Marker Appearance

The bot marker is a cyan (turquoise) circle with a white center dot, making it distinct from the white player marker.

## Key Insights from Session 9

1. **Preventing entity worldgen/runtime spawning:** To prevent an entity from spawning naturally during world generation or runtime, **DO NOT include a `spawnConditions` section at all** in the entity JSON. This is how vanilla traders work.

   **WRONG - causes unwanted spawning:**
   ```json
   "spawnConditions": {
       "worldgen": { "group": "none" },
       "runtime": { "group": "none" }
   }
   ```
   VS interprets `"group": "none"` as a literal group name, not a directive to disable spawning. This caused 20+ aibot entities to spawn during world generation!

   **CORRECT - omit the section entirely:**
   ```json
   "server": {
       "behaviors": [ ... ]
       // NO spawnConditions section = no natural spawning
   }
   ```

2. **EntityAiBot custom class:** Created `EntityAiBot` extending `EntityAgent` with special overrides:
   - `StoreWithChunk => false` - Prevents persistence to save file (no orphan bots)
   - `AlwaysActive => true` - **CRITICAL**: Keeps bot active regardless of distance from players

   **Why AlwaysActive matters:** Without this, when the bot travels far from the player, VS will despawn the entity when its chunk unloads. Since `StoreWithChunk => false`, the entity won't be saved, causing a "ghost bot" situation where the server holds a stale reference but the entity no longer exists in the world. The bot becomes invisible and unresponsive. `AlwaysActive` prevents this by keeping the entity simulating regardless of player distance.

   Note: This alone doesn't prevent worldgen spawning - must also omit spawnConditions from entity JSON.

   **Distance limitations with AlwaysActive:**
   - Bot stays alive and registered (`inLoadedEntities: true`, `state: "Active"`) at any distance
   - Bot can chat and scan blocks at any distance (chunk data accessible via server)
   - Minimap visibility limited to ~128 blocks (client chunk loading)
   - When player returns within range, bot can move again immediately

   **IMPORTANT:** `AlwaysActive` alone is NOT sufficient for long-distance movement. See "Entity Simulation Range" section below for the complete four-property solution (AlwaysActive, ShouldDespawn, SimulationRange, AllowOutsideLoadedRange).

   **Direct Walk (`/bot/walk`):** Bypasses A* pathfinding but still requires entity simulation. Useful when pathfinding fails due to terrain complexity.

3. **Debugging entity spawning:** Check server logs for spawn counts:
   ```
   [VSAI] Before spawn: 0 aibot entities in LoadedEntities
   [VSAI] After spawn: 1 aibot entities in LoadedEntities
   ```
   If "Before spawn" shows non-zero count in a new world, entities are spawning via worldgen.

## Key Insights from Session 8

1. **Loose stones/flint block codes:** Flint and loose stones have specific naming:
   - **Flint:** `looseflints-{rocktype}-free` (e.g., `looseflints-bauxite-free`)
   - **Stones:** `loosestones-{rocktype}-free` (e.g., `loosestones-bauxite-free`)
   - NOT `loosestones-flint` as might be assumed!

2. **Non-solid surface blocks now included:** Loose stones, flints, and plants have `isSolid: false`. The "surface" visibility filter now includes non-solid blocks that rest on solid ground (have a solid block at y-1). This was fixed in Session 8 - previously these were excluded.

3. **Block scanning for exploration:** When searching for specific items like flint:
   - The default `visibility_filter: "surface"` now includes loose items on the ground
   - Search for both `looseflints-*` AND `loosestones-*` patterns
   - The rock type suffix (bauxite, granite, etc.) indicates the underlying rock type, not the stone/flint type

4. **Player-relative coordinates:** The API returns absolute world coordinates (e.g., 512206, 120, 511851), but players see coordinates relative to world spawn. To convert:
   - **Relative X** = Absolute X - 512000
   - **Relative Y** = Absolute Y (height is absolute)
   - **Relative Z** = Absolute Z - 512000
   - Example: (512206, 120, 511851) → (206, 120, -149)
   - Always use relative coordinates when communicating positions to players via chat

## Project Structure Update

```
VintageStory-AI/
├── LEARNINGS.md              # This file
├── CLAUDE.md                 # Claude Code instructions
├── .mcp.json                 # MCP server config for Claude Code
├── mod/                      # C# Vintage Story mod (git repo)
│   ├── mod.csproj            # References VintagestoryAPI, VintagestoryLib, cairo-sharp, VSEssentials
│   ├── modinfo.json
│   ├── VsaiModSystem.cs      # Main mod (server HTTP API + client minimap)
│   ├── AiTaskRemoteControl.cs  # Custom AI task for bot movement
│   ├── AiBotMapLayer.cs      # Minimap integration (client-side)
│   └── assets/vsai/          # Entity definition, shapes, textures
├── mcp-server/               # Python MCP server (git repo)
│   ├── vsai_server.py        # MCP server with 13 tools
│   ├── requirements.txt      # mcp>=1.0.0
│   └── pyproject.toml
├── python-vs-core/           # Python library (git repo)
│   ├── src/vintage_story_core/
│   │   ├── __init__.py
│   │   ├── types.py          # Vec3, BlockInfo
│   │   └── visibility.py     # Line-of-sight filtering
│   ├── pyproject.toml
│   └── README.md
└── tools/                    # Python tools (git repo)
    └── bot_move.py           # CLI tool for movement testing
```

## Entity Display Names (Localization)

### How Entity Names Work

When you look at an entity, VS looks up a translation key in lang files. For creatures, the key format is:
```
item-creature-{entity-code}
```

For our bot with code `"aibot"`, VS looks for `item-creature-aibot`.

### Without Lang File
If no translation exists, VS shows the raw lookup path with mod prefix:
```
vsai:item-creature-aibot
```

### Adding a Lang File

Create `mod/assets/vsai/lang/en.json`:
```json
{
    "item-creature-aibot": "Claude"
}
```

**File location pattern:** `assets/{modid}/lang/{language-code}.json`

### Language Codes
VS supports 30+ languages. Common codes:
- `en.json` - English
- `de.json` - German
- `fr.json` - French
- `es.json` - Spanish
- `ru.json` - Russian
- `zh.json` - Chinese

### Other Translation Key Patterns

| Type | Key Pattern | Example |
|------|-------------|---------|
| Creatures | `item-creature-{code}` | `item-creature-aibot` |
| Blocks | `block-{code}` | `block-dirt` |
| Items | `item-{code}` | `item-stick` |
| Named entities | `nametag-{name}` | `nametag-tobias` |
| Unrevealed names | `nametag-{type}-unrevealedname` | `nametag-trader-unrevealedname` |

### Nametag Behavior (Floating Names)

For entities with floating name tags (like traders), add the `nametag` behavior:

```json
{
    "client": {
        "behaviors": [
            { "code": "nametag" }
        ]
    },
    "server": {
        "behaviors": [
            {
                "code": "nametag",
                "showtagonlywhentargeted": true,
                "selectFromRandomName": ["Claude", "Opus", "Sonnet"]
            }
        ]
    }
}
```

With lang entries:
```json
{
    "nametag-claude": "Claude",
    "nametag-opus": "Opus"
}
```

## Entity Simulation Range - CRITICAL FOR LONG-DISTANCE BOT MOVEMENT

### The Problem

By default, entity physics (movement, pathfinding, AI) only runs when the entity is within ~128 blocks of any player. Beyond this range, the entity's `State` becomes `EnumEntityState.Inactive` and the `BehaviorControlledPhysics.OnPhysicsTick()` method returns early without processing movement.

**Symptoms of being out of simulation range:**
- Bot appears "frozen" - cannot move in any direction
- `bot_goto` and `bot_walk` commands return immediately with no movement
- Block scanning still works (chunk data accessible server-side)
- Chat still works
- Bot is still alive and registered in LoadedEntities

### The Solution

The `SimulationRange` property on `Entity` controls this distance threshold. Default is ~128 blocks (4 chunks × 32 blocks). We extended it to 1000 blocks in our custom `EntityAiBot` class.

**Implementation in EntityAiBot.cs:**
```csharp
public class EntityAiBot : EntityAgent
{
    public override bool StoreWithChunk => false;           // Don't persist to save file
    public override bool AlwaysActive => true;              // Stay active regardless of distance
    public override bool ShouldDespawn => false;            // Don't flag for despawn
    public override bool AllowOutsideLoadedRange => true;   // Exist beyond loaded chunks

    public override void Initialize(EntityProperties properties, ICoreAPI api, long InChunkIndex3d)
    {
        base.Initialize(properties, api, InChunkIndex3d);

        // Extend simulation range from default 128 to 1000 blocks
        SimulationRange = 1000;
    }
}
```

**The Four Properties for Long-Distance Bot Movement:**

| Property | Purpose | Without It |
|----------|---------|------------|
| `AlwaysActive => true` | Keeps entity ticking regardless of player distance | Entity stops simulating when far from player |
| `SimulationRange = 1000` | Extends physics simulation range to 1000 blocks | Physics (movement) stops at ~128 blocks |
| `ShouldDespawn => false` | Prevents entity from being flagged for despawn | Entity marked for despawn when chunk unloads |
| `AllowOutsideLoadedRange => true` | **CRITICAL** Allows entity to exist beyond loaded chunks | Entity removed from LoadedEntities at ~300 blocks |

**Why ShouldDespawn matters:**
- `AlwaysActive` keeps the entity ticking but doesn't prevent removal from the `LoadedEntities` collection
- When the bot's chunk unloads (player moves away), VS checks `ShouldDespawn`
- Default implementation returns `!Alive` (false if alive), but chunk unload logic has additional checks
- `EntityPlayer` overrides this to `=> false` explicitly - we must do the same

**Why AllowOutsideLoadedRange is CRITICAL (Session 15 discovery):**
- Even with `AlwaysActive`, `ShouldDespawn => false`, and `SimulationRange = 1000`, the bot despawned at ~300 blocks
- `AllowOutsideLoadedRange` defaults to `false` in the base Entity class
- This property controls "whether entities can exist beyond normally loaded chunk boundaries"
- When a chunk unloads, VS removes entities that have `AllowOutsideLoadedRange == false` from `LoadedEntities`
- The bot showed `inLoadedEntities: false` and `state: "Despawned"` at ~300 blocks
- Setting `AllowOutsideLoadedRange => true` allows the entity to persist when its chunk is not loaded
- Discovery came from studying the VS API source at https://github.com/anegostudios/vsapi

**Key points:**
- `SimulationRange` is a public field, not a virtual property - set it in `Initialize()`, not as an override
- Must call `base.Initialize()` first, then set the value
- `AlwaysActive` is still needed to keep entity ticking
- Both properties work together: `AlwaysActive` keeps entity loaded, `SimulationRange` controls physics range

### VS Source Code References

The physics tick check is in `BehaviorControlledPhysics.OnPhysicsTick()`:
```csharp
if (entity.State != EnumEntityState.Active) return;
```

Entity state is set based on player proximity in `EntityAgent.DoInitialActiveCheck()` and updated each tick based on `SimulationRange`.

### Testing Results

With `SimulationRange = 1000`:
- Successfully tested bot movement to 190+ blocks from player
- A* pathfinding (`bot_goto`) fails beyond ~128 blocks due to unloaded chunks
- Direct walking (`bot_walk`) works at extended range since it doesn't require pathfinding
- Bot can navigate autonomously at much greater distances

### Related GitHub Issues

- [VS Issue #5875](https://github.com/anegostudios/VintageStory-Issues/issues/5875) - Discusses entity simulation limits
- VS API source: https://github.com/anegostudios/vsapi

## Common Errors & Solutions (continued)

### Entity spawns naturally during worldgen when it shouldn't
- **Cause:** Entity JSON has `spawnConditions` with invalid group like `"group": "none"`
- **Solution:** Remove the entire `spawnConditions` section from the entity JSON. VS only spawns entities that have valid spawnConditions - omitting it entirely prevents all natural spawning.
- **Reference:** See vanilla `trader-*.json` entities which have no spawnConditions and only spawn via structures.

## Key Insights from Session 16 - Collision Detection Bugs

### Blocks with Collision Despite `isSolid: false`

**Critical discovery:** Many block types have collision geometry that blocks entity movement even though the `/bot/blocks` API reports `isSolid: false`. This causes the pathfinder to generate invalid paths.

**Affected block types:**

| Block Type | Example Codes | Notes |
|------------|---------------|-------|
| **Microblocks** | `chiseledblock-*`, `microblock-*` | Player-placed decorative blocks. Have complex hitboxes |
| **Doors** | `door-*`, `roughhewnfencegate-*` | Closed doors are impassable despite isSolid=false |
| **Plank slabs** | `plankslab-oak-up-free` | Partial blocks at trader outposts |
| **Plank stairs** | `plankstairs-oak-down-west-free` | Stair blocks at trader outposts |
| **Ground storage** | `groundstorage` | Items placed on ground |
| **Toolracks** | `toolrack-*` | Wall-mounted storage |
| **Storage vessels** | `storagevessel-*` | Large clay containers |
| **Stationary baskets** | `stationarybasket-*` | Floor containers |
| **Other containers** | `crate-*`, `barrel-*`, `shelf-*` | Various storage |

**Root cause:** The `isSolid` property in VS block data indicates whether a block is a "full solid cube" for rendering/lighting purposes, NOT whether it has collision. Many interactive objects have collision boxes but aren't considered "solid" blocks.

**Impact on pathfinding:**
- Pathfinder treats these blocks as passable (air-like)
- Bot attempts to walk through them but gets stuck
- Results in "stuck" status with bot unable to reach destination

**Solution (Implemented Session 17):** Added `_has_hidden_collision()` function to pathfinding module that detects blocks with collision based on code patterns:

```python
def _has_hidden_collision(code: str) -> bool:
    """Check if a block has collision despite isSolid=false."""
    code_lower = code.lower()

    # Fences and fence gates
    if "fence" in code_lower:
        return True

    # Doors and gates - block when closed
    if "door" in code_lower or "gate" in code_lower:
        return True

    # Chiseled/micro blocks
    if "chiseled" in code_lower or "microblock" in code_lower:
        return True

    # Storage containers
    if any(pattern in code_lower for pattern in [
        "storagevessel", "stationarybasket", "crate-", "barrel-",
        "shelf-", "toolrack", "displaycase", "groundstorage"
    ]):
        return True

    # Plank slabs and stairs - partial blocks with collision
    if "plankslab" in code_lower or "plankstairs" in code_lower:
        return True

    # Wagon parts, crafting stations, beds, signs, etc.
    # ... (see full implementation in pathfinding.py)

    return False
```

**Key implementation details:**
1. Hidden collision blocks are added to `solid_blocks` set (for body clearance checks)
2. Hidden collision blocks are **excluded** from the heightmap (can't walk ON them, only blocked BY them)
3. This creates a "wall" effect that forces pathfinder to route around

### bot_walk Terrain Hazards

**bot_walk bypasses all pathfinding** - it walks in a straight line toward the target. This can cause the bot to:
- Walk off cliffs (tested: 12-block fall, damaged to 14.3/20 HP)
- Walk into water or lava
- Walk into walls and get stuck

**When to use bot_walk:**
- When A* pathfinding fails (e.g., target beyond loaded chunks)
- In open, flat terrain with clear line of sight
- Following pathfinder waypoints one at a time

**When NOT to use bot_walk:**
- In unknown terrain that might have cliffs
- Near structures with complex geometry
- For long distances in unmapped areas

### Bot Spawning Near Player

When spawning the bot, it always appears near the player's current position. If the player is in an enclosed space (underground base, building interior), the bot may spawn trapped.

**Workaround:** Player should move to an open area before spawning the bot.

### Adjacent Cliff Detection Bug (Fixed Session 17)

**Problem:** Pathfinder generated paths through narrow passages next to cliffs where the bot couldn't actually fit.

**Example scenario:**
```
Bot at (512009, 115, 511994) trying to walk north to (512009, 115, 511993)
           [cliff Y=116]
           [cliff Y=115] ← adjacent solid blocks at bot's body level
[peat bog] [cliff Y=114]
   bot →   ↑ path goes through here, but bot can't fit
```

**Root cause:** The `_has_body_clearance` function only checked adjacent blocks when they were at the same heightmap surface level:
```python
if adj_key in heightmap and heightmap[adj_key] == surface_y:  # Only same level!
```

For cliffs at different heights (e.g., surface Y=116 vs destination Y=114), the adjacent check was skipped entirely.

**Fix:** Check adjacent blocks for solid obstructions at body/head level, skipping ONLY when the solid is exactly at the adjacent's surface level (normal ground):
```python
if adj_surface is None or body_y != adj_surface:
    if (adj_x, body_y, adj_z) in solid_blocks:
        return False
```

This correctly:
- Detects cliff faces that block the bot's body (solid at different Y than surface)
- Allows normal step-up/step-down where adjacent ground is at body level

**File:** `python-vs-core/src/vintage_story_core/pathfinding.py` lines 135-155

## Key Insights from Session 18 - Trap Avoidance & Long-Distance Testing

### Trap Avoidance in Pathfinding (Fixed)

**Problem:** Bot could walk into "traps" - non-reversible dead ends like pits. The pathfinder allowed 2-block drops as a fallback, but didn't check if the landing position had a way out.

**Example scenario:**
```
Ground level: Y=115
Pit floor:    Y=113 (2-block drop allowed)
Pit walls:    All directions blocked or requiring step-up
```

Bot would drop into the pit, then be unable to escape because it can only step UP 1 block.

**Solution:** Added `_can_escape_pit()` function that performs BFS to verify the landing position can reach higher ground:

```python
def _can_escape_pit(x, z, surface_y, heightmap, solid_blocks, liquid_blocks, max_search=16):
    """
    Before allowing a 2-block drop, verify the bot can escape via BFS.
    Searches for any reachable step-up (1 block higher) within limited area.
    """
    directions = [(1, 0), (-1, 0), (0, 1), (0, -1)]
    visited = set()
    queue = [(x, z, surface_y)]
    visited.add((x, z))
    positions_checked = 0

    while queue and positions_checked < max_search:
        cx, cz, cy = queue.pop(0)
        positions_checked += 1

        for dx, dz in directions:
            nx, nz = cx + dx, cz + dz
            # ... check for step-up exits or continue BFS on walkable terrain
            if height_diff == 1 and has_clearance:
                return True  # Found escape route
            if -1 <= height_diff <= 0 and has_clearance:
                queue.append((nx, nz, ny))  # Continue searching

    return False  # No escape found - don't allow the drop
```

**Key implementation details:**
- Only affects 2-block drops (1-block drops are always reversible)
- BFS limited to 16 positions to avoid expensive searches
- Looks for any step-up within reachable area (not just adjacent)
- Added to `_get_walkable_neighbors()` before allowing 2-block drop

**File:** `python-vs-core/src/vintage_story_core/pathfinding.py`

### Long-Distance Bot Movement - CONFIRMED WORKING!

**Test results:**
- Bot successfully traveled **317+ blocks** from player
- All four entity properties working correctly:
  - `AlwaysActive => true`
  - `ShouldDespawn => false`
  - `AllowOutsideLoadedRange => true`
  - `SimulationRange = 1000`

**Bot status at 317 blocks:**
```json
{
  "alive": true,
  "health": 20,
  "onGround": true,
  "inLoadedEntities": true,
  "state": "Active"
}
```

**Key observations:**
- A* pathfinding continues to work at 300+ blocks
- Block scanning works (server has chunk data via force loading)
- Movement commands execute correctly
- Bot does NOT despawn when player is far away

**Force chunk loading:** The mod uses `_serverApi.WorldManager.LoadChunkColumnForModifiedEntities()` to keep chunks around the bot loaded, enabling pathfinding to work at any distance.

### Common Obstacles During Navigation

**Trees:** Maple logs and birch branches can block paths. Pathfinder correctly identifies them, but bot_walk can get stuck if terrain not scanned first.

**Birch leaves:** `leavesbranchy-grown*-birch` blocks are solid and have collision, unlike regular leaves.

---

## Known Issues / Bugs to Investigate

### ~~1. Bot Pickup Fails for Tool Heads~~ - RESOLVED

**Root cause:** `TryGiveItemStack()` fails for tool heads on EntityAgent with SeraphInventory, even with empty slots. SeraphInventory reserves slots 0-14 for equipment and only has 4 usable backpack slots (15-18).

**Solution:** Created custom `EntityBehaviorBotInventory` using `InventoryGeneric` with 32 general-purpose slots. All slots can hold any item, no equipment restrictions.

```csharp
// Custom inventory behavior (EntityBehaviorBotInventory.cs)
public class EntityBehaviorBotInventory : EntityBehavior
{
    public InventoryGeneric Inventory { get; private set; }
    public const int DefaultSlotCount = 32;

    public override void Initialize(EntityProperties properties, JsonObject attributes)
    {
        Inventory = new InventoryGeneric(_slotCount, "botinventory", instanceId, entity.Api);
    }
}

// Access in handlers
var inventoryBehavior = agent.GetBehavior<EntityBehaviorBotInventory>();
var inventory = inventoryBehavior?.Inventory;
```

Also kept manual slot insertion fallback in case `TryGiveItemStack()` fails:

```csharp
// Manual slot insertion fallback
if (amountGiven == 0 && inventory != null)
{
    int[] slotOrder = { 15, 16, 17, 18 };
    foreach (int slotIdx in slotOrder)
    {
        if (slotIdx >= inventory.Count) continue;
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
```

### 2. Bot Pathfinding Through Doors

**Symptom:** Bot gets stuck at door even after opening it with `bot_interact`.

**Observations:**
- `bot_interact` successfully opens door (block becomes non-solid)
- `bot_goto` still reports "stuck" at door position
- Pathfinding may cache collision data or not recognize opened doors

**Possible causes:**
- Pathfinder caches block solidity at path calculation time
- Door block still has collision box even when "open"
- Need to recalculate path after door state change

**Workaround:** Despawn/respawn bot on other side of door.

### 3. Bot Spawn Position Ignores Coordinates

**Symptom:** Bot always spawns near player regardless of specified x/y/z coordinates.

**Observations:**
- `bot_spawn(x=511920, y=110, z=512000)` spawns at `(511926, 110, 511969)` near player
- Consistent behavior across multiple spawn attempts

**Possible cause:** Spawn logic might be finding nearest valid spawn point to player.

**To investigate:** Check spawn implementation in HandleBotSpawn.

---

## Bot Held Item Rendering - WORKING!

### The Problem

When the bot equips an item via `/bot/equip`, the API returns success and the inventory shows the item in the hand slot, but the item was NOT visually rendered on the bot's model.

**Root cause:** The default `EntityShapeRenderer` doesn't render held items for non-player entities. `EntityPlayerShapeRenderer` has special `RenderHeldItem()` logic, but regular entities don't get this.

### The Solution

Created a custom entity renderer (`EntityAiBotRenderer`) that:
1. Extends `EntityShapeRenderer`
2. Syncs hand items via `WatchedAttributes` (server → client)
3. Uses the base class `RenderItem()` method for correct positioning

### Key Components

**1. Custom Renderer (EntityAiBotRenderer.cs):**
```csharp
public class EntityAiBotRenderer : EntityShapeRenderer
{
    private DummySlot? _dummySlot;

    public override void DoRender3DOpaque(float dt, bool isShadowPass)
    {
        base.DoRender3DOpaque(dt, isShadowPass);
        RenderBotHeldItem(dt, true, isShadowPass);  // Right hand
        RenderBotHeldItem(dt, false, isShadowPass); // Left hand
    }

    private void RenderBotHeldItem(float dt, bool rightHand, bool isShadowPass)
    {
        // Read from WatchedAttributes (synced from server)
        string attrKey = rightHand
            ? EntityBehaviorBotInventory.RightHandAttrKey
            : EntityBehaviorBotInventory.LeftHandAttrKey;

        ItemStack? stack = entity.WatchedAttributes.GetItemstack(attrKey);
        if (stack == null) return;

        stack.ResolveBlockOrItem(entity.World);
        if (stack.Collectible == null) return;

        // Get attachment point
        string attachmentCode = rightHand ? "RightHand" : "LeftHand";
        var apap = entity.AnimManager?.Animator?.GetAttachmentPointPose(attachmentCode);
        if (apap == null) return;

        // Get render info via dummy slot
        if (_dummySlot == null) _dummySlot = new DummySlot();
        _dummySlot.Itemstack = stack;
        var renderInfo = capi.Render.GetItemStackRenderInfo(_dummySlot, EnumItemRenderTarget.HandTp, dt);
        if (renderInfo?.ModelRef == null) return;

        // Use base class for correct matrix math!
        RenderItem(dt, isShadowPass, stack, apap, renderInfo);
    }
}
```

**2. WatchedAttributes Sync (EntityBehaviorBotInventory.cs):**
```csharp
public const string RightHandAttrKey = "rightHandItem";
public const string LeftHandAttrKey = "leftHandItem";

// Call after equipping
public void SyncHandSlotToWatchedAttributes(int slotIndex)
{
    string attrKey = slotIndex == 0 ? RightHandAttrKey : LeftHandAttrKey;
    ItemSlot slot = slotIndex == 0 ? RightHandSlot : LeftHandSlot;

    if (slot.Empty)
        entity.WatchedAttributes.RemoveAttribute(attrKey);
    else
        entity.WatchedAttributes.SetItemstack(attrKey, slot.Itemstack);

    entity.WatchedAttributes.MarkPathDirty(attrKey);
}
```

**3. Entity JSON Configuration:**
```json
{
    "client": {
        "renderer": "AiBotRenderer",
        ...
    }
}
```

**4. Renderer Registration (VsaiModSystem.cs):**
```csharp
public override void StartClientSide(ICoreClientAPI api)
{
    api.RegisterEntityRendererClass("AiBotRenderer", typeof(EntityAiBotRenderer));
}
```

### Key Learnings

1. **WatchedAttributes auto-sync:** Server-side changes to `WatchedAttributes` automatically sync to connected clients. Use `SetItemstack()` and `GetItemstack()` for ItemStack storage.

2. **ResolveBlockOrItem required:** After calling `GetItemstack()` on client, MUST call `stack.ResolveBlockOrItem(world)` to properly resolve the item/block reference.

3. **Use base class RenderItem():** Don't manually build transformation matrices - the base `EntityShapeRenderer.RenderItem()` method handles all the complex matrix math for attachment points correctly.

4. **DummySlot for render info:** `GetItemStackRenderInfo()` requires an `ItemSlot`, but we only have an `ItemStack` from WatchedAttributes. Use `DummySlot` as a wrapper.

5. **MultiTextureMeshRef handling:** If manually rendering (not using base RenderItem), iterate through `mmr.meshrefs[]` and `mmr.textureids[]` arrays rather than using `RenderMultiTextureMesh()` which requires specific shader sampler names.

### Crashes Encountered & Fixed

| Error | Cause | Fix |
|-------|-------|-----|
| `KeyNotFoundException: 'tex2d'` | Using `RenderMultiTextureMesh` with wrong sampler name | Use base class `RenderItem()` or iterate meshrefs manually |
| `cannot convert 'MultiTextureMeshRef' to 'MeshRef'` | Calling `RenderMesh(modelRef)` directly | Iterate `mmr.meshrefs[]` array |
| Item at wrong position | Manual matrix math incorrect | Use base class `RenderItem()` method |

### Result

Bot now visually displays held items (stones, spears, tools) in the correct hand position. Items follow the hand animation during movement.

---

## Bot Tool Use - IMPLEMENTED!

### Overview

The `/bot/use_tool` endpoint simulates left-click attack/harvest actions - like using a knife on grass to get dry grass.

### How Tool-Based Harvesting Works in VS

**Key Discovery:** There are TWO different harvesting systems in VS:

#### 1. Tool-Specific Drops (Tallgrass, etc.)

Blocks like tallgrass use the standard `drops` array with a `tool` filter:

```json
// From tallgrass block JSON
"drops": [
  { "type": "item", "code": "drygrass", "tool": "knife" }
]
```

- The drop only occurs when broken with the specified tool
- This is NOT `BlockBehaviorHarvestable` - it's the normal drops system
- Tallgrass does NOT have `BlockBehaviorHarvestable`!

#### 2. BlockBehaviorHarvestable (Berries, Resin, etc.)

Some blocks use the `Harvestable` behavior for renewable harvests:

```json
{
  "code": "harvestable",
  "harvestTime": 0.5,
  "harvestedBlockCode": "log-resinharvested-{wood}-ud",
  "harvestedStack": { "type": "item", "code": "resin" }
}
```

Blocks that use this: `bigberrybush`, `smallberrybush`, `saguarocactus`, `log-resin`

### Endpoint Comparison

| Endpoint | Action | Method Used |
|----------|--------|-------------|
| `/bot/interact` | Right-click | `block.Activate()` |
| `/bot/mine` | Break block | `block.GetDrops()` + `SetBlock(0)` |
| `/bot/use_tool` | Tool-specific harvest | Check drops with tool filter |

### Implementation

The `/bot/use_tool` endpoint handles both systems:

1. **Check tool-specific drops first:**
   - Get equipped tool's type from `collectible.Tool`
   - Check block's `Drops` array for drops with matching `Tool` property
   - If found, manually process those drops and remove block

2. **Fall back to BlockBehaviorHarvestable:**
   - Check for `Harvestable` behavior on block
   - Use reflection to access `harvestedStacks` and `harvestedBlockCode`
   - Process harvest and replace block

3. **Fall back to OnHeldAttack methods:**
   - Call tool's `OnHeldAttackStart/Step/Stop` for other interactions

### API Endpoint

```bash
curl -X POST http://localhost:4560/bot/use_tool \
  -H "Content-Type: application/json" \
  -d '{"x": 511774, "y": 120, "z": 511944}'
```

**Response:**
```json
{
  "success": true,
  "message": "Used knife-generic-flint on tallgrass-medium-free - block changed to air",
  "tool": "knife-generic-flint",
  "blockBefore": "tallgrass-medium-free",
  "blockAfter": "air",
  "position": {"x": 511774, "y": 120, "z": 511944},
  "actionTaken": true
}
```

### Tool Use Reference

| Tool | Block | Result |
|------|-------|--------|
| Knife | Tallgrass | Dry grass bundle |
| Knife | Cattail | Cattail root |
| Axe | Log | Strip bark (debarked log) |
| Hoe | Soil | Farmland |

### MCP Tool

```python
bot_use_tool(x=512100, y=120, z=511850)              # Absolute coordinates
bot_use_tool(x=1, y=0, z=1, relative=True)           # Relative to bot
```

### Notes

1. **Tool types:** VS tools have a `Tool` property (e.g., `EnumTool.Knife`). Block drops filter by this.
2. **Drops handling:** Items are given to bot via `TryGiveItemStack()`, overflow spawns in world.
3. **Tool durability:** Current implementation does not reduce tool durability.
4. **GetDrops limitation:** `Block.GetDrops()` doesn't take a tool parameter - it checks the player's tool. Since our bot is EntityAgent not IPlayer, we manually filter drops by tool type.

---
*Last updated: Session 23 - Fixed bot_use_tool to handle tool-specific drops (tallgrass with knife) correctly. Discovered that tallgrass uses drops array with tool filter, NOT BlockBehaviorHarvestable.*
