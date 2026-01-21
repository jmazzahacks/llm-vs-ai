#!/usr/bin/env python3
"""
MCP Server for Vintage Story AI Bot Control.

Provides tools for LLMs to control an AI bot in Vintage Story.
"""

import asyncio
import json
import time
from dataclasses import dataclass
from typing import Any
from urllib.error import URLError
from urllib.request import Request, urlopen

from mcp.server import Server
from mcp.server.stdio import stdio_server
from mcp.types import Tool, TextContent
from vintage_story_core import filter_visible_blocks, get_visible_surface_blocks, find_safe_path

# Configuration
VS_API_BASE_URL = "http://localhost:4560"
MOVEMENT_POLL_INTERVAL_SEC = 0.1
MOVEMENT_TIMEOUT_SEC = 600  # 10 minutes - complex terrain needs long winding paths


@dataclass
class Position:
    x: float
    y: float
    z: float

    def distance_to(self, other: "Position") -> float:
        dx = self.x - other.x
        dy = self.y - other.y
        dz = self.z - other.z
        return (dx * dx + dy * dy + dz * dz) ** 0.5

    def __str__(self) -> str:
        return f"({self.x:.1f}, {self.y:.1f}, {self.z:.1f})"


def http_get(endpoint: str) -> dict[str, Any]:
    """Make a GET request to the VS API and return JSON response."""
    url = f"{VS_API_BASE_URL}{endpoint}"
    req = Request(url, method="GET")
    req.add_header("Content-Type", "application/json")

    with urlopen(req, timeout=10) as response:
        return json.loads(response.read().decode())


def http_post(endpoint: str, data: dict[str, Any]) -> dict[str, Any]:
    """Make a POST request to the VS API with JSON body."""
    url = f"{VS_API_BASE_URL}{endpoint}"
    body = json.dumps(data).encode()
    req = Request(url, data=body, method="POST")
    req.add_header("Content-Type", "application/json")

    with urlopen(req, timeout=10) as response:
        return json.loads(response.read().decode())


def wait_for_movement_complete() -> dict[str, Any]:
    """
    Poll movement status until the bot reaches destination or gets stuck.
    Returns the final movement status.
    """
    terminal_states = {"idle", "reached", "stuck", "no_task"}
    start_time = time.time()

    while True:
        if time.time() - start_time > MOVEMENT_TIMEOUT_SEC:
            return {"error": "Movement timeout", "status": "timeout"}

        try:
            status = http_get("/bot/movement/status")
        except URLError as e:
            return {"error": f"Failed to get status: {e}", "status": "error"}

        if "error" in status:
            return status

        current_status = status.get("status", "unknown")
        is_active = status.get("isActive", False)

        # Check for bot despawn
        bot_status = http_get("/bot/observe")
        if "bot" in bot_status:
            bot_state = bot_status["bot"].get("state", "")
            in_loaded = bot_status["bot"].get("inLoadedEntities", True)
            if bot_state == "Despawned" or not in_loaded:
                return {
                    "error": "Bot despawned during movement",
                    "status": "despawned",
                    "position": status.get("position", {}),
                    "statusMessage": "Bot unexpectedly despawned"
                }

        # Check for terminal state with no active movement
        if current_status in terminal_states and not is_active:
            # Confirm by waiting one more poll
            time.sleep(MOVEMENT_POLL_INTERVAL_SEC)
            confirm = http_get("/bot/movement/status")
            if confirm.get("status") in terminal_states and not confirm.get("isActive", False):
                return confirm

        time.sleep(MOVEMENT_POLL_INTERVAL_SEC)


def wait_for_direct_walk_complete() -> dict[str, Any]:
    """
    Poll movement status until the bot finishes direct walking.
    Direct walking is simpler than pathfinding - just checks isDirectWalking flag.
    Returns the final movement status.
    """
    terminal_states = {"idle", "reached", "stuck", "no_task"}
    start_time = time.time()

    while True:
        if time.time() - start_time > MOVEMENT_TIMEOUT_SEC:
            return {"error": "Movement timeout", "status": "timeout"}

        try:
            status = http_get("/bot/movement/status")
        except URLError as e:
            return {"error": f"Failed to get status: {e}", "status": "error"}

        if "error" in status:
            return status

        current_status = status.get("status", "unknown")
        is_direct_walking = status.get("isDirectWalking", False)

        # Check for bot despawn
        bot_status = http_get("/bot/observe")
        if "bot" in bot_status:
            bot_state = bot_status["bot"].get("state", "")
            in_loaded = bot_status["bot"].get("inLoadedEntities", True)
            if bot_state == "Despawned" or not in_loaded:
                return {
                    "error": "Bot despawned during movement",
                    "status": "despawned",
                    "position": status.get("position", {}),
                    "statusMessage": "Bot unexpectedly despawned"
                }

        # Check if direct walking has completed
        if not is_direct_walking and current_status in terminal_states:
            # Confirm by waiting one more poll
            time.sleep(MOVEMENT_POLL_INTERVAL_SEC)
            confirm = http_get("/bot/movement/status")
            if not confirm.get("isDirectWalking", False):
                return confirm

        time.sleep(MOVEMENT_POLL_INTERVAL_SEC)


# Create MCP server
server = Server("vsai")


@server.list_tools()
async def list_tools() -> list[Tool]:
    """List all available tools."""
    return [
        Tool(
            name="bot_status",
            description="Get the current status of the VS API server and bot.",
            inputSchema={
                "type": "object",
                "properties": {},
                "required": []
            }
        ),
        Tool(
            name="bot_spawn",
            description="Spawn the AI bot at a specific position in the world.",
            inputSchema={
                "type": "object",
                "properties": {
                    "x": {"type": "number", "description": "X coordinate"},
                    "y": {"type": "number", "description": "Y coordinate (vertical)"},
                    "z": {"type": "number", "description": "Z coordinate"}
                },
                "required": ["x", "y", "z"]
            }
        ),
        Tool(
            name="bot_despawn",
            description="Remove the AI bot from the world.",
            inputSchema={
                "type": "object",
                "properties": {},
                "required": []
            }
        ),
        Tool(
            name="bot_observe",
            description="Get detailed observation of the bot's current state including position, health, and surroundings.",
            inputSchema={
                "type": "object",
                "properties": {},
                "required": []
            }
        ),
        # TEMPORARILY DISABLED - testing pathfinding without bot_goto
        # Tool(
        #     name="bot_goto",
        #     description="Command the bot to walk to a position using A* pathfinding. Blocks until the bot reaches the destination or gets stuck. The target Y coordinate must be at actual ground level.",
        #     inputSchema={
        #         "type": "object",
        #         "properties": {
        #             "x": {"type": "number", "description": "Target X coordinate"},
        #             "y": {"type": "number", "description": "Target Y coordinate (must be at ground level)"},
        #             "z": {"type": "number", "description": "Target Z coordinate"},
        #             "speed": {"type": "number", "description": "Movement speed (default: 0.03)", "default": 0.03},
        #             "relative": {"type": "boolean", "description": "If true, coordinates are relative to current position", "default": False}
        #         },
        #         "required": ["x", "y", "z"]
        #     }
        # ),
        Tool(
            name="bot_walk",
            description="Command the bot to walk directly to a position (bypasses A* pathfinding). Use this when pathfinding fails, especially for long distances beyond chunk loading range (~128 blocks). Bot walks in a straight line - will not avoid obstacles. Blocks until the bot reaches the destination.",
            inputSchema={
                "type": "object",
                "properties": {
                    "x": {"type": "number", "description": "Target X coordinate"},
                    "y": {"type": "number", "description": "Target Y coordinate"},
                    "z": {"type": "number", "description": "Target Z coordinate"},
                    "speed": {"type": "number", "description": "Movement speed (default: 0.03)", "default": 0.03},
                    "relative": {"type": "boolean", "description": "If true, coordinates are relative to current position", "default": False}
                },
                "required": ["x", "y", "z"]
            }
        ),
        Tool(
            name="bot_stop",
            description="Stop the bot's current movement.",
            inputSchema={
                "type": "object",
                "properties": {},
                "required": []
            }
        ),
        Tool(
            name="bot_blocks",
            description="Scan blocks around the bot in a given radius. Use visibility_filter to only return blocks visible via line-of-sight (like a player would see). Use filter to search for specific block types by keyword.",
            inputSchema={
                "type": "object",
                "properties": {
                    "radius": {"type": "integer", "description": "Scan radius in blocks (default: 3)", "default": 3},
                    "visibility_filter": {
                        "type": "string",
                        "description": "Filter blocks by visibility: 'none' (all blocks), 'visible' (line-of-sight), 'surface' (visible + exposed face). Default: 'surface'",
                        "enum": ["none", "visible", "surface"],
                        "default": "surface"
                    },
                    "filter": {
                        "type": "string",
                        "description": "Keyword filter for block codes (case-insensitive). Use comma to match multiple keywords, e.g. 'flint,stone' matches blocks containing 'flint' OR 'stone'."
                    }
                },
                "required": []
            }
        ),
        Tool(
            name="bot_entities",
            description="Get entities near the bot.",
            inputSchema={
                "type": "object",
                "properties": {
                    "radius": {"type": "number", "description": "Search radius (default: 10)", "default": 10}
                },
                "required": []
            }
        ),
        Tool(
            name="bot_break",
            description="Break a block at the specified position.",
            inputSchema={
                "type": "object",
                "properties": {
                    "x": {"type": "integer", "description": "Block X coordinate"},
                    "y": {"type": "integer", "description": "Block Y coordinate"},
                    "z": {"type": "integer", "description": "Block Z coordinate"}
                },
                "required": ["x", "y", "z"]
            }
        ),
        Tool(
            name="bot_place",
            description="Place a block at the specified position.",
            inputSchema={
                "type": "object",
                "properties": {
                    "x": {"type": "integer", "description": "Block X coordinate"},
                    "y": {"type": "integer", "description": "Block Y coordinate"},
                    "z": {"type": "integer", "description": "Block Z coordinate"},
                    "blockCode": {"type": "string", "description": "Block code to place (e.g., 'game:stone')"}
                },
                "required": ["x", "y", "z", "blockCode"]
            }
        ),
        Tool(
            name="players_list",
            description="Get a list of all online players.",
            inputSchema={
                "type": "object",
                "properties": {},
                "required": []
            }
        ),
        Tool(
            name="player_observe",
            description="Get detailed observation of a specific player.",
            inputSchema={
                "type": "object",
                "properties": {
                    "name": {"type": "string", "description": "Player name (case-insensitive)"}
                },
                "required": ["name"]
            }
        ),
        Tool(
            name="bot_chat",
            description="Send a chat message as the bot to all players in the game.",
            inputSchema={
                "type": "object",
                "properties": {
                    "message": {"type": "string", "description": "The message to send"},
                    "name": {"type": "string", "description": "Bot name to display (default: 'Bot')", "default": "Bot"}
                },
                "required": ["message"]
            }
        ),
        Tool(
            name="screenshot",
            description="Take a screenshot of the game.",
            inputSchema={
                "type": "object",
                "properties": {},
                "required": []
            }
        ),
        Tool(
            name="bot_inventory",
            description="Get the contents of the bot's inventory, including all slots and hand items.",
            inputSchema={
                "type": "object",
                "properties": {},
                "required": []
            }
        ),
        Tool(
            name="bot_collect",
            description="Pick up a loose surface item (flint, stones, sticks) at a specific position. Bot must be within 5 blocks of the target.",
            inputSchema={
                "type": "object",
                "properties": {
                    "x": {"type": "integer", "description": "Block X coordinate"},
                    "y": {"type": "integer", "description": "Block Y coordinate"},
                    "z": {"type": "integer", "description": "Block Z coordinate"}
                },
                "required": ["x", "y", "z"]
            }
        ),
        Tool(
            name="bot_inventory_drop",
            description="Drop an item from the bot's inventory. Spawns the item in the world at the bot's feet.",
            inputSchema={
                "type": "object",
                "properties": {
                    "slotIndex": {"type": "integer", "description": "Inventory slot index to drop from (0-based)"},
                    "itemCode": {"type": "string", "description": "Item code to search for and drop (alternative to slotIndex)"},
                    "quantity": {"type": "integer", "description": "Number of items to drop (default: 1)", "default": 1}
                },
                "required": []
            }
        ),
        Tool(
            name="bot_pickup",
            description="Pick up a dropped item entity near the bot. If no entityId specified, picks up the nearest item.",
            inputSchema={
                "type": "object",
                "properties": {
                    "entityId": {"type": "integer", "description": "Specific item entity ID to pick up (from bot_entities)"},
                    "maxDistance": {"type": "number", "description": "Maximum pickup distance (default: 5)", "default": 5}
                },
                "required": []
            }
        ),
        Tool(
            name="bot_pathfind",
            description="""Compute a safe path to a target position using A* pathfinding.

CRITICAL: The returned waypoints MUST be followed ONE AT A TIME in sequential order using bot_walk.
DO NOT skip waypoints or walk directly to the final destination - the path routes around cliffs,
ravines, water, and other hazards. Skipping waypoints will cause the bot to fall off cliffs and die.

CORRECT: Call bot_walk for waypoint[0], wait for completion, then waypoint[1], etc.
WRONG: Call bot_walk directly to the final waypoint - bot will walk off a cliff.

The pathfinder analyzes terrain and returns waypoints that:
- Respect step heights (max 1 block up, 1-2 blocks down)
- Avoid hazards (water, lava, deep holes)
- Ensure head clearance (no walking into ceilings)
- Route around obstacles the bot cannot climb

Returns partial paths when target is outside scan range - follow the partial path,
then call bot_pathfind again from the new position to continue.""",
            inputSchema={
                "type": "object",
                "properties": {
                    "x": {"type": "number", "description": "Target X coordinate"},
                    "y": {"type": "number", "description": "Target Y coordinate"},
                    "z": {"type": "number", "description": "Target Z coordinate"},
                    "radius": {"type": "integer", "description": "Scan radius for terrain analysis (default: 32, max: 32)", "default": 32}
                },
                "required": ["x", "y", "z"]
            }
        )
    ]


# Tools that have blocking wait loops and need to run in a thread
BLOCKING_TOOLS = {"bot_goto", "bot_walk"}


@server.call_tool()
async def call_tool(name: str, arguments: dict[str, Any]) -> list[TextContent]:
    """Handle tool calls."""
    try:
        # Run blocking tools in a background thread so MCP server stays responsive
        if name in BLOCKING_TOOLS:
            result = await asyncio.to_thread(execute_tool, name, arguments)
        else:
            result = execute_tool(name, arguments)
        return [TextContent(type="text", text=json.dumps(result, indent=2))]
    except URLError as e:
        error_result = {"error": f"Failed to connect to VS server: {e}"}
        return [TextContent(type="text", text=json.dumps(error_result, indent=2))]
    except Exception as e:
        error_result = {"error": str(e)}
        return [TextContent(type="text", text=json.dumps(error_result, indent=2))]


def execute_tool(name: str, arguments: dict[str, Any]) -> dict[str, Any]:
    """Execute a tool and return the result."""

    if name == "bot_status":
        return http_get("/status")

    elif name == "bot_spawn":
        return http_post("/bot/spawn", {
            "x": arguments["x"],
            "y": arguments["y"],
            "z": arguments["z"]
        })

    elif name == "bot_despawn":
        return http_post("/bot/despawn", {})

    elif name == "bot_observe":
        return http_get("/bot/observe")

    elif name == "bot_goto":
        # Get initial position for reporting
        initial_status = http_get("/bot/movement/status")
        if "error" in initial_status:
            return initial_status

        start_pos = Position(
            initial_status["position"]["x"],
            initial_status["position"]["y"],
            initial_status["position"]["z"]
        )

        # Issue goto command
        goto_result = http_post("/bot/goto", {
            "x": arguments["x"],
            "y": arguments["y"],
            "z": arguments["z"],
            "speed": arguments.get("speed", 0.03),
            "relative": arguments.get("relative", False)
        })

        if "error" in goto_result:
            return goto_result

        # Calculate actual target
        if arguments.get("relative", False):
            target = Position(
                start_pos.x + arguments["x"],
                start_pos.y + arguments["y"],
                start_pos.z + arguments["z"]
            )
        else:
            target = Position(arguments["x"], arguments["y"], arguments["z"])

        # Wait for movement to complete
        final_status = wait_for_movement_complete()

        if "error" in final_status:
            return final_status

        end_pos = Position(
            final_status["position"]["x"],
            final_status["position"]["y"],
            final_status["position"]["z"]
        )

        success = final_status.get("status") == "reached"

        return {
            "success": success,
            "status": final_status.get("status"),
            "statusMessage": final_status.get("statusMessage"),
            "startPosition": {"x": start_pos.x, "y": start_pos.y, "z": start_pos.z},
            "endPosition": {"x": end_pos.x, "y": end_pos.y, "z": end_pos.z},
            "targetPosition": {"x": target.x, "y": target.y, "z": target.z},
            "distanceToTarget": end_pos.distance_to(target)
        }

    elif name == "bot_walk":
        # Get initial position for reporting
        initial_status = http_get("/bot/movement/status")
        if "error" in initial_status:
            return initial_status

        start_pos = Position(
            initial_status["position"]["x"],
            initial_status["position"]["y"],
            initial_status["position"]["z"]
        )

        # Issue walk command (direct movement, bypasses pathfinding)
        walk_result = http_post("/bot/walk", {
            "x": arguments["x"],
            "y": arguments["y"],
            "z": arguments["z"],
            "speed": arguments.get("speed", 0.03),
            "relative": arguments.get("relative", False)
        })

        if "error" in walk_result:
            return walk_result

        # Calculate actual target
        if arguments.get("relative", False):
            target = Position(
                start_pos.x + arguments["x"],
                start_pos.y + arguments["y"],
                start_pos.z + arguments["z"]
            )
        else:
            target = Position(arguments["x"], arguments["y"], arguments["z"])

        # Wait for direct walking to complete
        final_status = wait_for_direct_walk_complete()

        if "error" in final_status:
            return final_status

        end_pos = Position(
            final_status["position"]["x"],
            final_status["position"]["y"],
            final_status["position"]["z"]
        )

        success = final_status.get("status") == "reached"

        return {
            "success": success,
            "status": final_status.get("status"),
            "statusMessage": final_status.get("statusMessage"),
            "startPosition": {"x": start_pos.x, "y": start_pos.y, "z": start_pos.z},
            "endPosition": {"x": end_pos.x, "y": end_pos.y, "z": end_pos.z},
            "targetPosition": {"x": target.x, "y": target.y, "z": target.z},
            "distanceToTarget": end_pos.distance_to(target),
            "note": "Used direct walk (bypassed A* pathfinding)"
        }

    elif name == "bot_stop":
        return http_post("/bot/stop", {})

    elif name == "bot_blocks":
        radius = arguments.get("radius", 3)
        visibility_filter = arguments.get("visibility_filter", "surface")
        keyword_filter = arguments.get("filter", None)

        # Get raw blocks from API
        result = http_get(f"/bot/blocks?radius={radius}")

        if "error" in result:
            return result

        # Get bot position for visibility calculation
        bot_obs = http_get("/bot/observe")
        if "error" in bot_obs:
            return result  # Fall back to unfiltered if can't get position

        observer_pos = bot_obs["bot"]["position"]
        blocks = result.get("blocks", [])

        # Apply visibility filtering
        if visibility_filter == "none":
            filtered = blocks
        elif visibility_filter == "visible":
            filtered = filter_visible_blocks(observer_pos, blocks)
        else:  # "surface" (default)
            filtered = get_visible_surface_blocks(observer_pos, blocks)

        # Apply keyword filtering if specified
        if keyword_filter:
            keywords = [kw.strip().lower() for kw in keyword_filter.split(",")]
            filtered = [
                block for block in filtered
                if any(kw in block.get("code", "").lower() for kw in keywords)
            ]

        return {
            "success": True,
            "botPosition": observer_pos,
            "radius": radius,
            "visibilityFilter": visibility_filter,
            "keywordFilter": keyword_filter,
            "totalBlocksScanned": len(blocks),
            "visibleBlockCount": len(filtered),
            "blocks": filtered
        }

    elif name == "bot_entities":
        radius = arguments.get("radius", 10)
        return http_get(f"/bot/entities?radius={radius}")

    elif name == "bot_break":
        return http_post("/bot/break", {
            "x": arguments["x"],
            "y": arguments["y"],
            "z": arguments["z"]
        })

    elif name == "bot_place":
        return http_post("/bot/place", {
            "x": arguments["x"],
            "y": arguments["y"],
            "z": arguments["z"],
            "blockCode": arguments["blockCode"]
        })

    elif name == "players_list":
        return http_get("/players")

    elif name == "player_observe":
        player_name = arguments["name"]
        return http_get(f"/player/{player_name}/observe")

    elif name == "bot_chat":
        return http_post("/bot/chat", {
            "message": arguments["message"],
            "name": arguments.get("name", "Bot")
        })

    elif name == "screenshot":
        return http_post("/screenshot", {})

    elif name == "bot_inventory":
        return http_get("/bot/inventory")

    elif name == "bot_collect":
        return http_post("/bot/collect", {
            "x": arguments["x"],
            "y": arguments["y"],
            "z": arguments["z"]
        })

    elif name == "bot_inventory_drop":
        data: dict[str, Any] = {}
        if "slotIndex" in arguments:
            data["slotIndex"] = arguments["slotIndex"]
        if "itemCode" in arguments:
            data["itemCode"] = arguments["itemCode"]
        if "quantity" in arguments:
            data["quantity"] = arguments["quantity"]
        return http_post("/bot/inventory/drop", data)

    elif name == "bot_pickup":
        data: dict[str, Any] = {}
        if "entityId" in arguments:
            data["entityId"] = arguments["entityId"]
        if "maxDistance" in arguments:
            data["maxDistance"] = arguments["maxDistance"]
        return http_post("/bot/pickup", data)

    elif name == "bot_pathfind":
        radius = arguments.get("radius", 32)

        # Get bot's current position
        bot_obs = http_get("/bot/observe")
        if "error" in bot_obs:
            return bot_obs

        current_pos = bot_obs["bot"]["position"]

        # Scan blocks around the bot (need all blocks, not just surface)
        blocks_result = http_get(f"/bot/blocks?radius={radius}")
        if "error" in blocks_result:
            return blocks_result

        blocks = blocks_result.get("blocks", [])

        # Define target position
        target_pos = {
            "x": arguments["x"],
            "y": arguments["y"],
            "z": arguments["z"]
        }

        # Compute safe path using Python-side pathfinding
        path_result = find_safe_path(current_pos, target_pos, blocks, scan_radius=radius)

        # Add context about bot position and target
        path_result["botPosition"] = current_pos
        path_result["targetPosition"] = target_pos
        path_result["scanRadius"] = radius
        path_result["blocksAnalyzed"] = len(blocks)

        return path_result

    else:
        return {"error": f"Unknown tool: {name}"}


async def main() -> None:
    """Run the MCP server."""
    async with stdio_server() as (read_stream, write_stream):
        await server.run(read_stream, write_stream, server.create_initialization_options())


def run() -> None:
    """Entry point for running the MCP server."""
    asyncio.run(main())


if __name__ == "__main__":
    run()
