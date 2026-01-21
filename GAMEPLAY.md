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

*Add new tips as you discover them!*
