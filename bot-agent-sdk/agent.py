#!/usr/bin/env python
"""
Vintage Story AI Bot Agent

An autonomous agent that controls a bot in Vintage Story, receiving commands
from players in-game via chat and executing them using Claude.
"""

import asyncio
import sys
import httpx
from claude_agent_sdk import query, ClaudeAgentOptions


# Configuration
VSAI_SERVER_URL = "http://localhost:4560"
MCP_SERVER_PATH = "/Users/jason/Sync/code/Learn/VintageStory-AI/mcp-server"
GAMEPLAY_TIPS_PATH = "/Users/jason/Sync/code/Learn/VintageStory-AI/GAMEPLAY.md"

POLL_INTERVAL_SECONDS = 2

SYSTEM_PROMPT = """You are an AI bot playing Vintage Story, a survival/crafting game similar to Minecraft.

You control a bot entity in the game world using MCP tools. Players can give you commands via in-game chat.

## Your Capabilities
- Navigation: Use bot_goto to walk to locations (uses A* pathfinding)
- Mining/Building: Use bot_mine to break blocks, bot_place to place blocks
- Crafting: Use bot_knap for stone tools, bot_craft for recipes
- Tool use: Use bot_equip to equip items, bot_use_tool for harvesting
- Inventory: Use bot_inventory to check items, bot_pickup to collect drops
- Communication: Use bot_chat to talk to players
- Observation: Use bot_observe for your status, bot_blocks to scan surroundings

## Important Rules
1. Always announce what you're doing via bot_chat so players know your status
2. Use relative coordinates when chatting (subtract 512000 from X and Z)
3. If you get stuck, ask for help - don't keep trying the same thing
4. Check your inventory before crafting to know what you have
5. After harvesting or mining, use bot_pickup to collect dropped items

## Current Task
Process the player's command and execute it step by step. Announce your progress.
"""


def log(msg: str) -> None:
    """Print with immediate flush"""
    print(msg, flush=True)


def load_gameplay_tips() -> str:
    """Load gameplay tips from GAMEPLAY.md"""
    try:
        with open(GAMEPLAY_TIPS_PATH, "r") as f:
            return f.read()
    except FileNotFoundError:
        return ""


def build_mcp_config() -> dict:
    """Build MCP server configuration"""
    return {
        "vsai": {
            "command": f"{MCP_SERVER_PATH}/bin/python",
            "args": [f"{MCP_SERVER_PATH}/vsai_server.py"]
        }
    }


async def check_server_status() -> dict | None:
    """Check if the vsai server is running and get status"""
    try:
        async with httpx.AsyncClient() as client:
            response = await client.get(f"{VSAI_SERVER_URL}/status", timeout=5.0)
            if response.status_code == 200:
                return response.json()
    except (httpx.RequestError, httpx.TimeoutException):
        pass
    return None


async def check_inbox() -> list[dict]:
    """Poll bot_inbox for new messages from players (direct HTTP call)"""
    try:
        async with httpx.AsyncClient() as client:
            response = await client.get(
                f"{VSAI_SERVER_URL}/bot/inbox",
                params={"clear": "true"},
                timeout=5.0
            )
            if response.status_code == 200:
                data = response.json()
                return data.get("messages", [])
    except (httpx.RequestError, httpx.TimeoutException):
        pass
    return []


async def send_chat(message: str) -> None:
    """Send a chat message as the bot (direct HTTP call)"""
    try:
        async with httpx.AsyncClient() as client:
            await client.post(
                f"{VSAI_SERVER_URL}/bot/chat",
                json={"message": message},
                timeout=5.0
            )
    except (httpx.RequestError, httpx.TimeoutException):
        pass


async def execute_command(player: str, command: str) -> None:
    """Execute a player command using Claude"""
    gameplay_tips = load_gameplay_tips()

    prompt = f"""A player has given you a command in-game.

Player: {player}
Command: {command}

First, announce via bot_chat that you received the command and what you're going to do.
Then execute the command step by step, announcing your progress.
If you encounter problems, explain what happened and ask for help if needed.

## Gameplay Reference
{gameplay_tips}
"""

    log(f"\n{'='*60}")
    log(f"[{player}] {command}")
    log('='*60)

    async for message in query(
        prompt=prompt,
        options=ClaudeAgentOptions(
            system_prompt=SYSTEM_PROMPT,
            mcp_servers=build_mcp_config(),
            permission_mode="bypassPermissions"
        )
    ):
        if hasattr(message, "content"):
            content = message.content
            if isinstance(content, list):
                for block in content:
                    if hasattr(block, "text"):
                        log(block.text)
            else:
                log(content)
        elif hasattr(message, "result"):
            log(f"\n{message.result}")


async def main() -> None:
    log("Vintage Story AI Bot Agent")
    log("="*40)
    log(f"Server: {VSAI_SERVER_URL}")
    log(f"Poll interval: {POLL_INTERVAL_SECONDS}s")
    log("")

    # Check initial status
    log("Checking server status...")
    status = await check_server_status()

    if status is None:
        log("ERROR: Cannot connect to vsai server. Is Vintage Story running with the mod?")
        return

    log(f"Server: {status.get('status', 'unknown')}")
    log(f"Bot: {'active' if status.get('bot', {}).get('active') else 'not spawned'}")

    if status.get("bot", {}).get("active"):
        log("\nAnnouncing online status...")
        await send_chat("I'm online and ready for commands! Say 'Claude: <command>' to give me tasks.")
    else:
        log("\nBot is not spawned. Will spawn when commanded.")

    log("\nListening for player commands...")
    log("-" * 40)

    # Main loop
    while True:
        try:
            # Check if server is still available
            status = await check_server_status()
            if status is None:
                log("Lost connection to server. Retrying...")
                await asyncio.sleep(POLL_INTERVAL_SECONDS)
                continue

            # Poll for commands (even if bot not active - might be a spawn command)
            messages = await check_inbox()

            for msg in messages:
                player = msg.get("player", "Unknown")
                command = msg.get("message", "")

                if command:
                    await execute_command(player, command)

            await asyncio.sleep(POLL_INTERVAL_SECONDS)

        except KeyboardInterrupt:
            log("\n\nShutting down agent...")
            await send_chat("Going offline. Goodbye!")
            break
        except Exception as e:
            log(f"Error: {e}")
            await asyncio.sleep(POLL_INTERVAL_SECONDS)


if __name__ == "__main__":
    asyncio.run(main())
