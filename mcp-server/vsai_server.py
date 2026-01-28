#!/usr/bin/env python3
"""
MCP Server for Vintage Story AI Bot Control.

Provides tools for LLMs to control an AI bot in Vintage Story.
"""

import asyncio
import json
import os
import time
from dataclasses import dataclass
from typing import Any
from urllib.error import URLError
from urllib.request import Request, urlopen

from mcp.server import Server
from mcp.server.stdio import stdio_server
from mcp.types import Tool, TextContent
from vintage_story_core import get_visible_surface_blocks

# Configuration
VS_API_BASE_URL = "http://localhost:4560"
BOT_NAME = os.environ.get("VSAI_BOT_NAME", "Claude")
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


def http_post(endpoint: str, data: dict[str, Any], timeout: int = 10) -> dict[str, Any]:
    """Make a POST request to the VS API with JSON body."""
    url = f"{VS_API_BASE_URL}{endpoint}"
    body = json.dumps(data).encode()
    req = Request(url, data=body, method="POST")
    req.add_header("Content-Type", "application/json")

    with urlopen(req, timeout=timeout) as response:
        return json.loads(response.read().decode())


def wait_for_movement_complete() -> dict[str, Any]:
    """
    Poll movement status until the bot reaches destination, gets stuck, or is interrupted.
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

        # Check for combat interrupt - return immediately so agent can respond
        interrupted_value = status.get("interrupted")
        if interrupted_value:
            interrupt_details = status.get("interruptDetails", {})
            print(f"[VSAI-PY] Interrupt detected! interrupted={interrupted_value}, details={interrupt_details}", flush=True)
            return {
                "error": f"Movement interrupted by combat! Attacked by {interrupt_details.get('attackerCode', 'unknown')}",
                "status": "interrupted",
                "position": status.get("position", {}),
                "statusMessage": f"Under attack by {interrupt_details.get('attackerCode', 'unknown')}",
                "interruptDetails": interrupt_details
            }

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

            # Check confirm for interrupt too (in case damage happened between polls)
            if confirm.get("interrupted"):
                interrupt_details = confirm.get("interruptDetails", {})
                return {
                    "error": f"Movement interrupted by combat! Attacked by {interrupt_details.get('attackerCode', 'unknown')}",
                    "status": "interrupted",
                    "position": confirm.get("position", {}),
                    "statusMessage": f"Under attack by {interrupt_details.get('attackerCode', 'unknown')}",
                    "interruptDetails": interrupt_details
                }

            if confirm.get("status") in terminal_states and not confirm.get("isActive", False):
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
        Tool(
            name="bot_goto",
            description="Command the bot to walk to a position using the game's built-in A* pathfinding (NavigateTo). Blocks until the bot reaches the destination or gets stuck. Works at long distances with the chunk loading fix. If stuck on terrain obstacles, try routing around them.",
            inputSchema={
                "type": "object",
                "properties": {
                    "x": {"type": "number", "description": "Target X coordinate"},
                    "y": {"type": "number", "description": "Target Y coordinate (must be at ground level)"},
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
            description="Scan blocks around the bot in a given radius. Only returns surface blocks (visible with exposed face). Use filter to search for specific block types by keyword.",
            inputSchema={
                "type": "object",
                "properties": {
                    "radius": {"type": "integer", "description": "Scan radius in blocks (default: 3)", "default": 3},
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
            name="bot_mine",
            description="Mine a block using equipped tool. Respects tool tier - drops only if tool tier >= block requirement. Bot should have appropriate tool equipped (pickaxe for stone/ore, axe for wood, etc.). Tool tiers: 0=none, 1=stone/flint, 2=copper, 3=bronze, 4=iron, 5=steel.",
            inputSchema={
                "type": "object",
                "properties": {
                    "x": {"type": "integer", "description": "Block X coordinate"},
                    "y": {"type": "integer", "description": "Block Y coordinate"},
                    "z": {"type": "integer", "description": "Block Z coordinate"},
                    "relative": {
                        "type": "boolean",
                        "default": False,
                        "description": "If true, coordinates are relative to bot position"
                    }
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
            name="bot_interact",
            description="Interact with a block (open/close doors, gates, trapdoors, activate levers, etc.). Simulates right-click on the block.",
            inputSchema={
                "type": "object",
                "properties": {
                    "x": {"type": "integer", "description": "Block X coordinate"},
                    "y": {"type": "integer", "description": "Block Y coordinate"},
                    "z": {"type": "integer", "description": "Block Z coordinate"},
                    "relative": {"type": "boolean", "description": "If true, coordinates are relative to bot position", "default": False}
                },
                "required": ["x", "y", "z"]
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
                    "name": {"type": "string", "description": f"Bot name to display (default: '{BOT_NAME}')", "default": BOT_NAME}
                },
                "required": ["message"]
            }
        ),
        Tool(
            name="bot_chat_location",
            description="Send a chat message with a location, automatically converting absolute coordinates to relative coordinates (subtracts 512000 from X and Z) so they match what players see on screen.",
            inputSchema={
                "type": "object",
                "properties": {
                    "message": {"type": "string", "description": "Message prefix (e.g., 'Found copper at')"},
                    "x": {"type": "number", "description": "Absolute X coordinate"},
                    "y": {"type": "number", "description": "Y coordinate (unchanged)"},
                    "z": {"type": "number", "description": "Absolute Z coordinate"},
                    "name": {"type": "string", "description": f"Bot name to display (default: '{BOT_NAME}')", "default": BOT_NAME}
                },
                "required": ["message", "x", "y", "z"]
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
            name="bot_inbox",
            description=f"Read messages sent to the bot by players. Players address the bot with '{BOT_NAME}: <message>' in chat.",
            inputSchema={
                "type": "object",
                "properties": {
                    "clear": {"type": "boolean", "description": "Clear inbox after reading (default: true)", "default": True},
                    "limit": {"type": "integer", "description": "Max messages to return (default: 50)", "default": 50}
                },
                "required": []
            }
        ),
        Tool(
            name="bot_knap",
            description="Knap a flint or stone tool. Bot must have flint or stone in inventory. Creates knapping surface, completes recipe instantly, gives output to bot.",
            inputSchema={
                "type": "object",
                "properties": {
                    "recipe": {
                        "type": "string",
                        "description": "Tool to make: 'axe', 'knife', 'shovel', 'hoe', 'spear', or 'arrowhead'"
                    }
                },
                "required": ["recipe"]
            }
        ),
        Tool(
            name="bot_craft",
            description="Craft an item using grid recipe. Bot must have all required ingredients in inventory. Use for combining knapped tool heads with sticks to make tools.",
            inputSchema={
                "type": "object",
                "properties": {
                    "recipe": {
                        "type": "string",
                        "description": "Output to craft (e.g., 'axe', 'knife', 'shovel', 'spear'). Matches recipes containing this name."
                    }
                },
                "required": ["recipe"]
            }
        ),
        Tool(
            name="bot_equip",
            description="Equip an item from inventory to the bot's hand. Swaps with any item currently in hand.",
            inputSchema={
                "type": "object",
                "properties": {
                    "slotIndex": {
                        "type": "integer",
                        "description": "Inventory slot index to equip from (0-based)"
                    },
                    "itemCode": {
                        "type": "string",
                        "description": "Item code to search for and equip (alternative to slotIndex, e.g., 'knife', 'axe')"
                    },
                    "hand": {
                        "type": "string",
                        "description": "Which hand to equip to: 'right' (default) or 'left'",
                        "default": "right"
                    }
                },
                "required": []
            }
        ),
        Tool(
            name="bot_use_tool",
            description="Use equipped tool on a block (left-click action). Simulates attack/harvest actions like using a knife on grass to get dry grass, or using an axe to strip bark. Bot must have appropriate tool equipped.",
            inputSchema={
                "type": "object",
                "properties": {
                    "x": {"type": "integer", "description": "Block X coordinate"},
                    "y": {"type": "integer", "description": "Block Y coordinate"},
                    "z": {"type": "integer", "description": "Block Z coordinate"},
                    "relative": {
                        "type": "boolean",
                        "default": False,
                        "description": "If true, coordinates are relative to bot position"
                    }
                },
                "required": ["x", "y", "z"]
            }
        ),
        Tool(
            name="bot_attack",
            description="Chase and attack a target entity with equipped melee weapon. Bot will pursue the target and attack until it dies or escapes. Use bot_entities first to find target IDs.",
            inputSchema={
                "type": "object",
                "properties": {
                    "entityId": {
                        "type": "integer",
                        "description": "Target entity ID (from bot_entities)"
                    },
                    "maxChaseDistance": {
                        "type": "number",
                        "default": 30,
                        "description": "Give up if target gets this far away (default: 30)"
                    }
                },
                "required": ["entityId"]
            }
        ),
        Tool(
            name="bot_harvest",
            description="Harvest a dead animal corpse to get meat, hides, and other drops. Bot must have a knife equipped and be within 5 blocks of the corpse. Use bot_entities first to find dead animals (alive=false). Drop rate is 40% since bot is not a player.",
            inputSchema={
                "type": "object",
                "properties": {
                    "entityId": {
                        "type": "integer",
                        "description": "Entity ID of the dead animal to harvest (from bot_entities)"
                    }
                },
                "required": ["entityId"]
            }
        )
    ]


# Tools that have blocking wait loops and need to run in a thread
BLOCKING_TOOLS = {"bot_goto", "bot_attack"}


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
            # Include current position for interrupted/error states
            pos = final_status.get("position", {})
            end_pos = Position(pos.get("x", 0), pos.get("y", 0), pos.get("z", 0))

            result = {
                "success": False,
                "status": final_status.get("status"),
                "statusMessage": final_status.get("statusMessage"),
                "error": final_status.get("error"),
                "startPosition": {"x": start_pos.x, "y": start_pos.y, "z": start_pos.z},
                "endPosition": {"x": end_pos.x, "y": end_pos.y, "z": end_pos.z},
                "targetPosition": {"x": target.x, "y": target.y, "z": target.z},
                "distanceToTarget": end_pos.distance_to(target)
            }

            # Include interrupt details if present
            if "interruptDetails" in final_status:
                result["interruptDetails"] = final_status["interruptDetails"]

            return result

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

    elif name == "bot_stop":
        return http_post("/bot/stop", {})

    elif name == "bot_blocks":
        radius = arguments.get("radius", 3)
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

        # Apply surface visibility filtering (always enabled)
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
            "visibilityFilter": "surface",
            "keywordFilter": keyword_filter,
            "totalBlocksScanned": len(blocks),
            "visibleBlockCount": len(filtered),
            "blocks": filtered
        }

    elif name == "bot_entities":
        radius = arguments.get("radius", 10)
        return http_get(f"/bot/entities?radius={radius}")

    elif name == "bot_mine":
        return http_post("/bot/mine", {
            "x": arguments["x"],
            "y": arguments["y"],
            "z": arguments["z"],
            "relative": arguments.get("relative", False)
        })

    elif name == "bot_place":
        return http_post("/bot/place", {
            "x": arguments["x"],
            "y": arguments["y"],
            "z": arguments["z"],
            "blockCode": arguments["blockCode"]
        })

    elif name == "bot_interact":
        return http_post("/bot/interact", {
            "x": arguments["x"],
            "y": arguments["y"],
            "z": arguments["z"],
            "relative": arguments.get("relative", False)
        })

    elif name == "players_list":
        return http_get("/players")

    elif name == "player_observe":
        player_name = arguments["name"]
        return http_get(f"/player/{player_name}/observe")

    elif name == "bot_chat":
        return http_post("/bot/chat", {
            "message": arguments["message"],
            "name": arguments.get("name", BOT_NAME)
        })

    elif name == "bot_chat_location":
        # Convert absolute coordinates to relative (subtract 512000 from X and Z)
        rel_x = int(arguments["x"] - 512000)
        rel_z = int(arguments["z"] - 512000)
        y = int(arguments["y"])

        # Format message with relative coordinates
        full_message = f"{arguments['message']} ({rel_x}, {y}, {rel_z})"

        return http_post("/bot/chat", {
            "message": full_message,
            "name": arguments.get("name", BOT_NAME)
        })

    elif name == "bot_inbox":
        clear = arguments.get("clear", True)
        limit = arguments.get("limit", 50)
        params = f"?limit={limit}&clear={'true' if clear else 'false'}"
        return http_get(f"/bot/inbox{params}")

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

    elif name == "bot_knap":
        return http_post("/bot/knap", {
            "recipe": arguments["recipe"]
        })

    elif name == "bot_craft":
        return http_post("/bot/craft", {
            "recipe": arguments["recipe"]
        })

    elif name == "bot_equip":
        data: dict[str, Any] = {}
        if "slotIndex" in arguments:
            data["slotIndex"] = arguments["slotIndex"]
        if "itemCode" in arguments:
            data["itemCode"] = arguments["itemCode"]
        if "hand" in arguments:
            data["hand"] = arguments["hand"]
        return http_post("/bot/equip", data)

    elif name == "bot_use_tool":
        return http_post("/bot/use_tool", {
            "x": arguments["x"],
            "y": arguments["y"],
            "z": arguments["z"],
            "relative": arguments.get("relative", False)
        })

    elif name == "bot_attack":
        # Longer timeout for combat operations (60 second combat + buffer)
        return http_post("/bot/attack", {
            "entityId": arguments["entityId"],
            "maxChaseDistance": arguments.get("maxChaseDistance", 30)
        }, timeout=65)

    elif name == "bot_harvest":
        return http_post("/bot/harvest", {
            "entityId": arguments["entityId"]
        })

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
