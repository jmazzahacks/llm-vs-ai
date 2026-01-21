# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

This is `vintage-story-core`, a Python library providing core utilities for Vintage Story AI bot control. It's part of the larger VintageStory-AI project - read `/Users/jason/Sync/code/Learn/VintageStory-AI/LEARNINGS.md` for full project context.

## Build Commands

```bash
# Activate virtual environment (required before any Python command)
source bin/activate

# Install dev dependencies
pip install -r dev-requirements.txt

# Install package in editable mode
pip install -e .

# Run tests
pytest

# Type checking
mypy src/

# Format code
black src/

# Build and publish to PyPI
./build-publish.sh
```

## Architecture

### Module Structure

```
src/vintage_story_core/
├── __init__.py      # Public exports: filter_visible_blocks, get_visible_surface_blocks, find_safe_path, Vec3, BlockInfo
├── types.py         # Core data types (Vec3, BlockInfo)
├── visibility.py    # Line-of-sight filtering using Amanatides & Woo voxel traversal
└── pathfinding.py   # A* pathfinding with terrain rules
```

### Core Components

**types.py**: Data classes for 3D vectors and block information
- `Vec3` - 3D position/vector with arithmetic operations
- `BlockInfo` - Block data with position, code, solidity. Handles both flat and nested API response formats

**visibility.py**: Line-of-sight visibility filtering
- `filter_visible_blocks()` - Filter blocks to those visible via raycast from observer
- `get_visible_surface_blocks()` - Further filter to only exposed surface blocks
- Uses Amanatides & Woo's fast voxel traversal algorithm internally

**pathfinding.py**: Python-side A* pathfinding for safe navigation
- `find_safe_path()` - Compute waypoints toward a target respecting terrain rules
- Terrain rules: max 1-block step-up, 1-2 block step-down, hazard avoidance (water/lava)
- Returns partial paths when target is outside scan range
- Waypoints returned at standing position (Y+1 above surface)
- Passable block detection: treats leaves (non-woody) as walkable
- Head clearance checking: ensures bot won't hit ceilings

### Integration with VS API

This library processes block data from the C# mod's `/bot/blocks` endpoint. The MCP server (`mcp-server/vsai_server.py`) uses these functions to filter blocks before returning them to the LLM.

Block visibility filtering flow:
1. Bot scans blocks via HTTP API
2. MCP server receives block list
3. `get_visible_surface_blocks()` filters to visible blocks
4. Filtered blocks returned to LLM tool call
