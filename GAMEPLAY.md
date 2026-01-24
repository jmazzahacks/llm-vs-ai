# Gameplay Tips for VS AI Agents

This document contains tips and strategies for AI agents controlling bots in Vintage Story via the MCP server. Unlike LEARNINGS.md (which covers coding and system building), this document focuses on **in-game tactics and problem-solving**.

---

## Navigation & Movement

### CRITICAL: Follow Pathfinder Waypoints Sequentially

When using `bot_pathfind` + `bot_walk`:

1. Call `bot_pathfind` to get safe waypoints
2. Walk to EACH waypoint in sequence - do NOT skip to the final destination
3. The pathfinder routes around cliffs and hazards; skipping waypoints bypasses safety

**WRONG:** Pathfind returns 30 waypoints, walk directly to final waypoint → bot walks off cliff and dies from fall damage

**CORRECT:** Walk through waypoints in order, or in small batches

### Getting Unstuck

When pathfinding fails or the bot gets stuck:

1. **Announce your location** (use relative coordinates - subtract 512000 from X and Z)
2. **Scan for walkable terrain** using `bot_blocks` with `filter="soil,grass"`
3. **Do a short goto** (5-10 blocks) to visible walkable terrain instead of long-distance navigation
4. **Repeat** until unstuck, then resume original task

**Why this works:** Long-distance pathfinding can fail to find routes through obstacles (like building exits), but short-distance pathfinding is more thorough and can navigate around local obstacles.

### Short vs Long Distance Goto

- **Long-distance goto (50+ blocks):** More likely to fail or get stuck, especially near structures or complex terrain
- **Short-distance goto (10 blocks):** More reliable, pathfinding explores more thoroughly
- **Strategy:** For long journeys, consider breaking into shorter segments if you encounter stuck issues

### DANGER: Never Use Direct Walk in Unknown Terrain

**`bot_walk` bypasses A* pathfinding entirely** - it walks in a straight line toward the target with no hazard detection.

**When NOT to use `bot_walk`:**
- In unexplored terrain
- When pathfinding repeatedly fails (this often means the terrain is dangerous!)
- Near cliffs, ravines, or holes
- When you can't visually confirm the path is clear

**When `bot_walk` is safe:**
- Following a path you've already confirmed with pathfinding
- Short distances on flat, visible terrain
- As a last resort when truly stuck (but expect potential falls)

**Lesson learned:** During a long-distance test, pathfinding kept failing in difficult terrain. Using `bot_walk` as a fallback resulted in the bot falling into a deep hole (23-block drop). The pathfinder was failing because the terrain WAS dangerous - it was doing its job by refusing to find a path through hazardous areas.

**If pathfinding fails repeatedly:** Don't bypass it. Instead:
1. Try a different direction
2. Backtrack to safer terrain
3. Ask for player assistance
4. Accept that some terrain is impassable

---

## Coordinates

### Relative Coordinates for Chat

When announcing positions to players in chat, **always use relative coordinates**:
- Subtract 512000 from X coordinate
- Subtract 512000 from Z coordinate
- Y stays the same

**Example:** Absolute (512035, 124, 511890) → Relative (35, 124, -110)

This matches how coordinates appear on the player's screen in Vintage Story.

---

## Resource Searching

### Using the Keyword Filter

The `bot_blocks` tool supports a `filter` parameter to search for specific blocks:
- Case-insensitive keyword matching
- Use comma-separated keywords for OR logic: `filter="flint,stone"`
- Reduces response size dramatically (from ~150 blocks to just matches)

**Example searches:**
- `filter="cattail"` - Find cattail plants
- `filter="flint,stone"` - Find flint or stone
- `filter="oak"` - Find oak trees/logs
- `filter="water"` - Find water sources
- `filter="soil,grass"` - Find walkable terrain

### Search Patterns

When searching for a resource:
1. Start near player or known landmark
2. Move in expanding circles or systematic grid
3. Use larger radius scans (8-10 blocks) to cover more area
4. Check biome-appropriate locations (e.g., cattails near water, flint on gravel)

---

## Communication

### Chat Announcements

Use `bot_chat` to communicate with players:
- Announce when stuck and need help
- Report when you've found something
- Always use relative coordinates in messages

**Example:**
```
"Help! I'm stuck at (35, 124, -110). Can you come help?"
```

---

## Common Resources & Where to Find Them

| Resource | Typical Location | Search Filter |
|----------|-----------------|---------------|
| Flint | On ground, gravel areas | `flint` |
| Cattails | Near water/ponds | `cattail` |
| Clay | Near water, riverbanks | `clay` |
| Berries | Forest edges | `berry` |
| Reeds | Wetlands, pond edges | `reed` |

---

## Biome Awareness

Not all resources exist in all biomes:
- Oak trees may not exist in certain biomes (birch, larch, maple are alternatives)
- Cattails require water nearby
- Adjust search expectations based on observed terrain

---

## Tool Crafting

### Basic Tool Creation Pipeline

To craft stone-age tools:

1. **Collect materials:** Find flint (`bot_blocks` with `filter="flint"`) and sticks (`filter="stick"`)
2. **Knap a tool head:** `bot_knap(recipe="knife")` - requires flint in inventory
3. **Craft the tool:** `bot_craft(recipe="knife")` - combines blade + stick
4. **Equip the tool:** `bot_equip(itemCode="knife")`

**Available knapping recipes:** `axe`, `knife`, `shovel`, `hoe`, `spear`, `arrowhead`

### Knapping Location

Knapping creates a temporary work surface on the ground. If you get "position blocked" error:
- Move to a clear area (no tallgrass, items, or other blocks)
- Try again after moving a few blocks

---

## Tool Usage (Harvesting)

### Using bot_use_tool

`bot_use_tool` simulates left-click with equipped tool. Different tools harvest different blocks:

| Tool | Block | Result |
|------|-------|--------|
| Knife | Tallgrass | Dry grass |
| Knife | Cattail | Cattail root |
| Axe | Log | Strip bark |
| Hoe | Soil | Farmland |

### Harvesting Workflow

Example: Collecting dry grass with a knife

1. **Equip knife:** `bot_equip(itemCode="knife")`
2. **Find tallgrass:** `bot_blocks(radius=10, filter="tallgrass")`
3. **Harvest:** `bot_use_tool(x, y, z)` - block becomes air, item drops
4. **Pickup:** `bot_pickup()` - collects nearby dropped items
5. **Repeat** until you have enough

**Note:** Items drop as entities, not directly to inventory. Always `bot_pickup` after harvesting.

### Efficient Harvesting

- Harvest multiple blocks before picking up (items persist for a while)
- Use `bot_entities(radius=10)` to see dropped items
- `bot_pickup` grabs the nearest item - call multiple times for multiple drops

---

## Block Interaction

### Doors and Gates

Use `bot_interact` to open/close doors and gates:

```
bot_interact(x, y, z)  # Toggle door state
```

Works on: doors, gates, trapdoors, levers

### Finding Interactable Blocks

Search with `bot_blocks`:
- `filter="door"` - Find doors
- `filter="gate"` - Find fence gates
- `filter="lever"` - Find levers

---

## Item Management

### Dropping Items

Use `bot_inventory_drop` to drop items:
- `bot_inventory_drop(itemCode="drygrass")` - Drop 1 dry grass
- `bot_inventory_drop(slotIndex=0)` - Drop from specific slot

**Note:** Each call drops 1 item. Call multiple times to drop stacks.

### Checking Inventory

`bot_inventory` shows:
- `handRight` / `handLeft` - Currently equipped items
- `slots` - All inventory slots with item codes and quantities

---

*Add new tips as you discover them!*
