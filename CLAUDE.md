# Claude Code Instructions for VintageStory-AI

## Project Overview
This project is building an AI-controlled bot for Vintage Story, similar to Voyager for Minecraft. The goal is to have an LLM control a bot entity in the game via an HTTP API.

## Important: Knowledge Base
**Read `/Users/jason/Sync/code/Learn/VintageStory-AI/LEARNINGS.md` at the start of each session.**

This document contains accumulated knowledge about:
- What works and doesn't work for bot control
- VS modding patterns and gotchas
- API endpoints implemented
- Open questions being investigated

**Update LEARNINGS.md whenever you discover something important** - this is our persistent memory across sessions.

## Project Structure
```
VintageStory-AI/
├── mod/                    # C# Vintage Story mod
│   ├── mod/
│   │   ├── VsaiModSystem.cs   # Main mod code with HTTP API
│   │   ├── modinfo.json       # Mod metadata
│   │   └── mod.csproj         # Project file
│   └── mod.sln
├── LEARNINGS.md            # Accumulated knowledge (READ THIS)
└── CLAUDE.md               # This file
```

## Development Workflow

### Building the mod
```bash
cd /Users/jason/Sync/code/Learn/VintageStory-AI/mod
VINTAGE_STORY="/Applications/Vintage Story.app" dotnet build mod/mod.csproj
```

### Deploying to VS
```bash
cp -r mod/bin/Debug/Mods/vsai/* ~/Library/Application\ Support/VintageStoryData/Mods/vsai/
```

### Testing
VS must be restarted after deploying new mod code. Test endpoints with curl:
```bash
curl http://localhost:4560/status
curl -X POST http://localhost:4560/bot/spawn -H "Content-Type: application/json" -d '{"entityCode": "chicken-rooster"}'
```

## Current Focus
We're working on getting real walking movement (not teleportation) for bot entities using the VS pathfinding system (PathTraverser).
