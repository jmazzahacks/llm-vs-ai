"""
Safe pathfinding for Vintage Story bot navigation.

Computes safe waypoints toward a target position using A* pathfinding,
respecting terrain rules for step heights, hazards, and reversibility.
"""

import heapq
import math
from typing import Any


def _is_passable_block(code: str) -> bool:
    """
    Check if a block is passable (bot can walk through it).

    Some blocks like leaves are marked as solid in the API but the bot can
    actually walk through them. This prevents them from blocking pathfinding.

    Note: Woody leaf variants (e.g., birch leaves with -woody-) may have
    collision, so we exclude those.
    """
    code_lower = code.lower()

    # Exclude woody variants - these may have actual collision
    if "woody" in code_lower:
        return False

    # Regular leaves and branchy leaves are passable
    if "leaves" in code_lower:
        return True

    return False


def _build_terrain_data(
    blocks: list[dict[str, Any]],
    bot_y: int | None = None
) -> tuple[dict[tuple[int, int], int], set[tuple[int, int, int]], set[tuple[int, int, int]]]:
    """
    Build terrain data structures from block scan.

    Args:
        blocks: Raw block data from API
        bot_y: Bot's current Y position (used to prefer nearby surfaces in buildings)

    Returns:
        heightmap: (x, z) -> walkable surface y (prefers surface near bot_y if provided)
        solid_blocks: set of all solid block positions
        liquid_blocks: set of all liquid block positions
    """
    solid_blocks: set[tuple[int, int, int]] = set()
    liquid_blocks: set[tuple[int, int, int]] = set()

    for block in blocks:
        # Handle both flat and nested position formats
        if "worldPos" in block:
            pos = block["worldPos"]
            x, y, z = int(pos["x"]), int(pos["y"]), int(pos["z"])
        else:
            x, y, z = int(block["x"]), int(block["y"]), int(block["z"])

        code = block.get("code", "")
        is_solid = block.get("isSolid", True)
        is_liquid = "water" in code.lower() or "lava" in code.lower() or "liquid" in code.lower()

        # Check if block is passable (solid in API but walkable through)
        is_passable = _is_passable_block(code)

        if is_liquid:
            liquid_blocks.add((x, y, z))
        elif is_solid and not is_passable:
            solid_blocks.add((x, y, z))

    # Build heightmap: for each (x, z), find walkable surface
    # If bot_y is provided, prefer surfaces close to bot's level (for buildings with roofs)
    # Otherwise, use highest surface (outdoor terrain)
    heightmap: dict[tuple[int, int], int] = {}

    for x, y, z in solid_blocks:
        key = (x, z)
        above = (x, y + 1, z)

        # Walkable if block above is not solid (air or liquid counts as non-solid for surface)
        if above not in solid_blocks:
            if key not in heightmap:
                heightmap[key] = y
            elif bot_y is not None:
                # Prefer surface closest to bot's Y position (bot stands at surface_y + 1)
                current_dist = abs(heightmap[key] - (bot_y - 1))
                new_dist = abs(y - (bot_y - 1))
                if new_dist < current_dist:
                    heightmap[key] = y
            else:
                # No bot_y provided, use highest surface
                if y > heightmap[key]:
                    heightmap[key] = y

    return heightmap, solid_blocks, liquid_blocks


def _has_head_clearance(
    x: int,
    surface_y: int,
    z: int,
    solid_blocks: set[tuple[int, int, int]]
) -> bool:
    """Check if there's head clearance at a position (standing on surface_y)."""
    # Bot stands on surface_y, occupies y+1 and y+2
    head_pos = (x, surface_y + 2, z)
    return head_pos not in solid_blocks


def _get_walkable_neighbors(
    x: int,
    z: int,
    current_y: int,
    heightmap: dict[tuple[int, int], int],
    solid_blocks: set[tuple[int, int, int]],
    liquid_blocks: set[tuple[int, int, int]],
    allow_2_block_drop: bool = False
) -> list[tuple[int, int, int, int]]:
    """
    Get walkable neighbor positions.

    Args:
        x, z: Current horizontal position
        current_y: Current surface height (block bot stands on)
        heightmap: Walkable surface heights
        solid_blocks: Set of solid block positions
        liquid_blocks: Set of liquid block positions
        allow_2_block_drop: If True, allow 2-block drops (non-reversible)

    Returns:
        List of (nx, nz, ny, cost) tuples for valid moves
    """
    neighbors = []
    directions = [(1, 0), (-1, 0), (0, 1), (0, -1)]

    for dx, dz in directions:
        nx, nz = x + dx, z + dz
        key = (nx, nz)

        if key not in heightmap:
            continue

        ny = heightmap[key]

        # Check for liquid at destination (on or above surface)
        if (nx, ny, nz) in liquid_blocks or (nx, ny + 1, nz) in liquid_blocks:
            continue

        # Calculate height difference
        height_diff = ny - current_y

        # Can step up max 1 block
        if height_diff > 1:
            continue

        # Step down limits
        if height_diff < -1:
            if not allow_2_block_drop or height_diff < -2:
                continue
            # Check if it's a hole (gap with depth)
            # For a 1-wide gap: we can cross if there's ground at ny
            # But a 2+ deep hole at our level is dangerous

        # Check for head clearance at destination
        if not _has_head_clearance(nx, ny, nz, solid_blocks):
            continue

        # Check for head clearance during transition for step up
        if height_diff == 1:
            # When stepping up, we need clearance at y+2 at current pos
            if (x, current_y + 2, z) in solid_blocks:
                continue

        # Calculate movement cost (diagonal would be sqrt(2) but we only do cardinal)
        cost = 1
        if height_diff < 0:
            # Slight preference for staying at same level
            cost = 1.1 if height_diff == -1 else 1.3

        neighbors.append((nx, nz, ny, cost))

    return neighbors


def _heuristic(x1: int, z1: int, x2: int, z2: int) -> float:
    """Manhattan distance heuristic for A*."""
    return abs(x2 - x1) + abs(z2 - z1)


def _find_valid_start(
    bot_x: int,
    bot_y: int,
    bot_z: int,
    heightmap: dict[tuple[int, int], int]
) -> tuple[int, int, int] | None:
    """
    Find a valid starting position near the bot's location.

    The bot's Y coordinate is at standing level (air block where feet are),
    while heightmap stores surface Y (solid block below feet). Expected
    difference is ~1, but floor() rounding and terrain variation can cause
    the initial lookup to miss.

    Searches the exact position first, then nearby cells within 1 block.

    Returns:
        (x, surface_y, z) if found, None if no valid start found
    """
    # Search order: exact position first, then neighbors
    search_offsets = [
        (0, 0),   # Exact position
        (1, 0), (-1, 0), (0, 1), (0, -1),  # Cardinal neighbors
        (1, 1), (1, -1), (-1, 1), (-1, -1),  # Diagonal neighbors
    ]

    for dx, dz in search_offsets:
        check_x = bot_x + dx
        check_z = bot_z + dz
        key = (check_x, check_z)

        if key not in heightmap:
            continue

        surface_y = heightmap[key]

        # Bot stands at surface_y + 1 (air block)
        # Allow tolerance of 2 to handle terrain variation and rounding
        # Expected: bot_y = surface_y + 1, so difference should be ~1
        if abs(surface_y - bot_y) <= 2:
            return (check_x, surface_y, check_z)

    return None


def _astar(
    start: tuple[int, int, int],
    goal: tuple[int, int],
    heightmap: dict[tuple[int, int], int],
    solid_blocks: set[tuple[int, int, int]],
    liquid_blocks: set[tuple[int, int, int]],
    allow_2_block_drop: bool = False
) -> list[tuple[int, int, int]] | None:
    """
    A* pathfinding on the heightmap.

    Args:
        start: (x, y, z) starting position
        goal: (x, z) target horizontal position
        heightmap: Walkable surface heights
        solid_blocks: Set of solid block positions
        liquid_blocks: Set of liquid block positions
        allow_2_block_drop: Allow non-reversible 2-block drops

    Returns:
        List of (x, y, z) waypoints or None if no path found
    """
    # Find a valid starting position near the bot
    # This handles floor() rounding and terrain variation
    valid_start = _find_valid_start(start[0], start[1], start[2], heightmap)
    if valid_start is None:
        return None

    start_x, start_y, start_z = valid_start
    start_xz = (start_x, start_z)

    # Priority queue: (f_score, counter, x, z, y)
    counter = 0
    open_set: list[tuple[float, int, int, int, int]] = []
    heapq.heappush(open_set, (0, counter, start_xz[0], start_xz[1], start_y))

    came_from: dict[tuple[int, int], tuple[int, int, int]] = {}
    g_score: dict[tuple[int, int], float] = {start_xz: 0}
    f_score: dict[tuple[int, int], float] = {start_xz: _heuristic(start_xz[0], start_xz[1], goal[0], goal[1])}

    while open_set:
        _, _, cx, cz, cy = heapq.heappop(open_set)
        current = (cx, cz)

        if current == goal:
            # Reconstruct path
            path = [(cx, cy, cz)]
            while current in came_from:
                px, py, pz = came_from[current]
                path.append((px, py, pz))
                current = (px, pz)
            path.reverse()
            return path

        neighbors = _get_walkable_neighbors(
            cx, cz, cy, heightmap, solid_blocks, liquid_blocks, allow_2_block_drop
        )

        for nx, nz, ny, move_cost in neighbors:
            neighbor_xz = (nx, nz)
            tentative_g = g_score[current] + move_cost

            if neighbor_xz not in g_score or tentative_g < g_score[neighbor_xz]:
                came_from[neighbor_xz] = (cx, cy, cz)
                g_score[neighbor_xz] = tentative_g
                f = tentative_g + _heuristic(nx, nz, goal[0], goal[1])
                f_score[neighbor_xz] = f
                counter += 1
                heapq.heappush(open_set, (f, counter, nx, nz, ny))

    return None


def _find_edge_goal(
    start: tuple[int, int],
    target: tuple[int, int],
    heightmap: dict[tuple[int, int], int],
    scan_radius: int
) -> tuple[int, int] | None:
    """
    Find the best reachable position closest to target.

    When target is outside scan radius, find edge position in that direction.
    """
    if not heightmap:
        return None

    # Calculate direction to target
    dx = target[0] - start[0]
    dz = target[1] - start[1]
    dist = math.sqrt(dx * dx + dz * dz)

    if dist == 0:
        return start

    # Normalize direction
    dx_norm = dx / dist
    dz_norm = dz / dist

    # Find walkable positions and pick the one furthest in target direction
    best_pos = None
    best_score = float("-inf")

    for pos in heightmap.keys():
        # Score by projection onto target direction
        px = pos[0] - start[0]
        pz = pos[1] - start[1]
        score = px * dx_norm + pz * dz_norm

        if score > best_score:
            best_score = score
            best_pos = pos

    return best_pos


def find_safe_path(
    current_pos: dict[str, float],
    target_pos: dict[str, float],
    blocks: list[dict[str, Any]],
    scan_radius: int = 16
) -> dict[str, Any]:
    """
    Compute safe waypoints toward target within scanned area.

    Args:
        current_pos: Dict with x, y, z of bot's current position
        target_pos: Dict with x, y, z of target position
        blocks: Raw block data from /bot/blocks API
        scan_radius: Scan radius used (for determining edge-of-scan goals)

    Returns:
        {
            "success": bool,
            "waypoints": [{"x": int, "y": int, "z": int}, ...],
            "reached_target": bool,
            "distance_to_target": float,
            "reason": str  # if success=False
        }
    """
    if not blocks:
        return {
            "success": False,
            "waypoints": [],
            "reached_target": False,
            "distance_to_target": float("inf"),
            "reason": "No block data provided"
        }

    # Parse positions
    start_x = int(math.floor(current_pos["x"]))
    start_y = int(math.floor(current_pos["y"]))
    start_z = int(math.floor(current_pos["z"]))

    target_x = int(math.floor(target_pos["x"]))
    target_z = int(math.floor(target_pos["z"]))

    # Build terrain data (pass bot_y to prefer surfaces at bot's level in buildings)
    heightmap, solid_blocks, liquid_blocks = _build_terrain_data(blocks, bot_y=start_y)

    if not heightmap:
        return {
            "success": False,
            "waypoints": [],
            "reached_target": False,
            "distance_to_target": float("inf"),
            "reason": "No walkable surfaces found in block data"
        }

    # Determine goal position
    target_xz = (target_x, target_z)
    goal_xz = target_xz

    # Check if target is in heightmap (reachable area)
    target_in_scan = target_xz in heightmap

    if not target_in_scan:
        # Find edge position closest to target
        edge_goal = _find_edge_goal((start_x, start_z), target_xz, heightmap, scan_radius)
        if edge_goal:
            goal_xz = edge_goal
        else:
            return {
                "success": False,
                "waypoints": [],
                "reached_target": False,
                "distance_to_target": float("inf"),
                "reason": "Cannot determine path toward target"
            }

    # Try pathfinding with reversible moves first
    start = (start_x, start_y, start_z)
    path = _astar(start, goal_xz, heightmap, solid_blocks, liquid_blocks, allow_2_block_drop=False)

    # If no path with safe moves, try allowing 2-block drops
    if path is None:
        path = _astar(start, goal_xz, heightmap, solid_blocks, liquid_blocks, allow_2_block_drop=True)

    if path is None:
        return {
            "success": False,
            "waypoints": [],
            "reached_target": False,
            "distance_to_target": math.sqrt((target_x - start_x) ** 2 + (target_z - start_z) ** 2),
            "reason": "No safe path found"
        }

    # Convert path to waypoints
    # IMPORTANT: Path Y values are surface block Y (what bot stands ON)
    # Waypoints need to be at standing position Y+1 (where bot's feet are)
    # This ensures bot_walk targets the correct elevation for step-ups
    waypoints = [{"x": p[0], "y": p[1] + 1, "z": p[2]} for p in path]

    # Calculate final distance to original target
    final_pos = path[-1]
    distance_to_target = math.sqrt(
        (target_x - final_pos[0]) ** 2 + (target_z - final_pos[2]) ** 2
    )

    reached_target = target_in_scan and goal_xz == target_xz and len(path) > 0

    return {
        "success": True,
        "waypoints": waypoints,
        "reached_target": reached_target,
        "distance_to_target": distance_to_target,
        "reason": ""
    }
