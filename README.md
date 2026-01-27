# VintageStory-AI

**A fun experiment** in creating an AI-controlled bot for [Vintage Story](https://www.vintagestory.at/) - think [Voyager](https://github.com/MineDojo/Voyager) but for VS!

> **Disclaimer:** This is a hobby project and learning experiment. It's janky, incomplete, and mostly exists because I wanted to see if I could get Claude to survive in Vintage Story. Spoiler: it dies a lot.

## What is this?

This project lets an LLM (Claude) control a bot entity in Vintage Story through natural language. The bot can:

- Walk around using A* pathfinding
- Collect loose items (flint, sticks, stones)
- Mine blocks with appropriate tools
- Knap flint tools (knife, axe, spear, etc.)
- Craft items by combining materials
- Fight hostile mobs (with varying success)
- Interact with blocks (doors, gates, levers)
- Respond to combat interrupts when attacked

## Architecture

```
┌─────────────────────┐
│   Claude / LLM      │  <- The "brain" making decisions
└──────────┬──────────┘
           │ MCP Protocol
           v
┌─────────────────────┐
│  MCP Server         │  <- Python server exposing VS actions as tools
│  (vsai_server.py)   │
└──────────┬──────────┘
           │ HTTP (localhost:4560)
           v
┌─────────────────────┐
│  C# Mod (vsai)      │  <- Vintage Story mod with HTTP API
└──────────┬──────────┘
           │
           v
┌─────────────────────┐
│  Bot Entity in VS   │  <- A humanoid entity walking around your world
└─────────────────────┘
```

## Features

### MCP Tools Available

| Tool | Description |
|------|-------------|
| `bot_spawn` | Spawn the bot at a location |
| `bot_despawn` | Remove the bot from the world |
| `bot_observe` | Get bot's current state (position, health, etc.) |
| `bot_goto` | Walk to a location using pathfinding |
| `bot_blocks` | Scan nearby blocks |
| `bot_entities` | Find nearby entities |
| `bot_mine` | Mine a block |
| `bot_collect` | Pick up loose surface items |
| `bot_pickup` | Pick up dropped item entities |
| `bot_inventory` | Check inventory contents |
| `bot_equip` | Equip an item to hand |
| `bot_knap` | Knap a flint/stone tool |
| `bot_craft` | Craft an item using grid recipes |
| `bot_attack` | Chase and attack an entity |
| `bot_chat` | Send a chat message in-game |
| `bot_interact` | Interact with blocks (doors, etc.) |
| `bot_use_tool` | Use equipped tool on a block |

### Combat Interrupt System

The bot can detect when it's being attacked during movement and return early, allowing the LLM to make defensive decisions (fight back, flee, etc.).

## Project Structure

```
VintageStory-AI/
├── mod/                    # C# Vintage Story mod
│   └── mod/
│       ├── VsaiModSystem.cs    # Main mod with HTTP API
│       ├── EntityAiBot.cs      # Custom bot entity class
│       └── assets/vsai/        # Entity definitions, patches
├── mcp-server/             # Python MCP server
│   └── vsai_server.py          # Tool definitions for Claude
├── bot-agent-sdk/          # Experimental autonomous agent
├── LEARNINGS.md            # Development notes and gotchas
└── GAMEPLAY.md             # VS gameplay mechanics reference
```

## Setup

### Requirements

- Vintage Story (tested on 1.20.x)
- .NET SDK (for building the mod)
- Python 3.11+ (for MCP server)
- Claude Code CLI or similar MCP-compatible client

### Building the Mod

```bash
cd mod
VINTAGE_STORY="/path/to/Vintage Story.app" dotnet build mod/mod.csproj
cp -r mod/bin/Debug/Mods/vsai/* ~/path/to/VintageStoryData/Mods/vsai/
```

### Running

1. Start Vintage Story and load a world
2. The mod starts an HTTP server on port 4560
3. Connect Claude Code (or your MCP client) with the MCP server configured
4. Start chatting with Claude about what you want the bot to do!

## Learnings

See [LEARNINGS.md](LEARNINGS.md) for a detailed record of development discoveries, including:

- How VS entity physics and pathfinding work
- JSON patching for mod compatibility
- Combat and tool tier systems
- What works and what definitely doesn't

## Why?

Because it's fun! And because I wanted to explore:

- How well an LLM can play a survival game
- The challenges of embodied AI in a voxel world
- VS modding and the MCP protocol

## Status

This is an active experiment. Things that work-ish:

- Basic movement and pathfinding
- Simple resource gathering
- Tool crafting
- Combat (bot usually loses)

Things that need work:

- Long-term planning and memory
- Efficient exploration strategies
- Not dying to drifters at night

## License

MIT - Do whatever you want with this code. If you make something cool, let me know!

## Acknowledgments

- [Vintage Story](https://www.vintagestory.at/) - An amazing game
- [Voyager](https://github.com/MineDojo/Voyager) - Inspiration for the concept
- [Anthropic](https://www.anthropic.com/) - For Claude and the MCP protocol
