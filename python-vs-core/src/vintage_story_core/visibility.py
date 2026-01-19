"""
Line-of-sight visibility filtering for voxel worlds.

Uses Amanatides & Woo's fast voxel traversal algorithm to determine
which blocks are visible from an observer position.
"""

import math
from typing import Any

from .types import Vec3, BlockInfo


def _sign(x: float) -> int:
    """Return sign of x: -1, 0, or 1."""
    if x > 0:
        return 1
    elif x < 0:
        return -1
    return 0


def _voxel_traversal(
    origin: Vec3,
    target_block: tuple[int, int, int],
    solid_blocks: set[tuple[int, int, int]],
    max_distance: float,
) -> bool:
    """
    Check if a ray from origin can reach target_block without hitting solid blocks.

    Uses Amanatides & Woo's fast voxel traversal algorithm.

    Args:
        origin: Ray starting position (observer's eye position)
        target_block: Block coordinates (x, y, z) to check visibility of
        solid_blocks: Set of solid block positions that block vision
        max_distance: Maximum ray distance

    Returns:
        True if target_block is visible from origin
    """
    target = Vec3(target_block[0] + 0.5, target_block[1] + 0.5, target_block[2] + 0.5)

    direction = target - origin
    distance = direction.length()

    if distance == 0:
        return True

    if distance > max_distance:
        return False

    direction = direction.normalize()

    current_x = int(math.floor(origin.x))
    current_y = int(math.floor(origin.y))
    current_z = int(math.floor(origin.z))

    step_x = _sign(direction.x)
    step_y = _sign(direction.y)
    step_z = _sign(direction.z)

    if direction.x != 0:
        if step_x > 0:
            t_max_x = (math.floor(origin.x) + 1 - origin.x) / direction.x
        else:
            t_max_x = (origin.x - math.floor(origin.x)) / -direction.x
        t_delta_x = abs(1.0 / direction.x)
    else:
        t_max_x = float("inf")
        t_delta_x = float("inf")

    if direction.y != 0:
        if step_y > 0:
            t_max_y = (math.floor(origin.y) + 1 - origin.y) / direction.y
        else:
            t_max_y = (origin.y - math.floor(origin.y)) / -direction.y
        t_delta_y = abs(1.0 / direction.y)
    else:
        t_max_y = float("inf")
        t_delta_y = float("inf")

    if direction.z != 0:
        if step_z > 0:
            t_max_z = (math.floor(origin.z) + 1 - origin.z) / direction.z
        else:
            t_max_z = (origin.z - math.floor(origin.z)) / -direction.z
        t_delta_z = abs(1.0 / direction.z)
    else:
        t_max_z = float("inf")
        t_delta_z = float("inf")

    traveled = 0.0

    while traveled < distance:
        if (current_x, current_y, current_z) == target_block:
            return True

        if traveled > 0 and (current_x, current_y, current_z) in solid_blocks:
            return False

        if t_max_x < t_max_y:
            if t_max_x < t_max_z:
                traveled = t_max_x
                t_max_x += t_delta_x
                current_x += step_x
            else:
                traveled = t_max_z
                t_max_z += t_delta_z
                current_z += step_z
        else:
            if t_max_y < t_max_z:
                traveled = t_max_y
                t_max_y += t_delta_y
                current_y += step_y
            else:
                traveled = t_max_z
                t_max_z += t_delta_z
                current_z += step_z

    return (current_x, current_y, current_z) == target_block


def filter_visible_blocks(
    observer_pos: dict[str, float],
    blocks: list[dict[str, Any]],
    eye_height: float = 1.5,
) -> list[dict[str, Any]]:
    """
    Filter a list of blocks to only those visible from the observer position.

    Includes:
    - Solid blocks visible via line-of-sight
    - Liquid blocks visible via line-of-sight
    - Non-solid blocks resting on solid ground (loose stones, plants, etc.)

    Args:
        observer_pos: Dict with x, y, z of observer (bot) position
        blocks: List of block dicts from the API (with x, y, z, code fields)
        eye_height: Height offset for observer's eyes (default 1.5)

    Returns:
        List of visible block dicts
    """
    eye = Vec3(
        observer_pos["x"],
        observer_pos["y"] + eye_height,
        observer_pos["z"],
    )

    block_infos: list[BlockInfo] = []
    solid_blocks: set[tuple[int, int, int]] = set()

    for block_data in blocks:
        info = BlockInfo.from_dict(block_data)
        block_infos.append(info)
        if info.is_solid:
            solid_blocks.add(info.to_tuple())

    max_distance = 0.0
    for info in block_infos:
        block_center = Vec3(info.x + 0.5, info.y + 0.5, info.z + 0.5)
        dist = (block_center - eye).length()
        if dist > max_distance:
            max_distance = dist

    visible_blocks: list[dict[str, Any]] = []

    for i, info in enumerate(block_infos):
        target = info.to_tuple()

        # Skip non-solid blocks unless they rest on solid ground
        if not info.is_solid and not info.is_liquid:
            # Check if there's a solid block directly below
            block_below = (info.x, info.y - 1, info.z)
            if block_below not in solid_blocks:
                continue

        if _voxel_traversal(eye, target, solid_blocks, max_distance):
            visible_blocks.append(blocks[i])

    return visible_blocks


def get_visible_surface_blocks(
    observer_pos: dict[str, float],
    blocks: list[dict[str, Any]],
    eye_height: float = 1.5,
    include_liquids: bool = True,
) -> list[dict[str, Any]]:
    """
    Get only the visible surface blocks (blocks with at least one exposed face).

    This is more restrictive than filter_visible_blocks - it only returns
    blocks that are both visible AND have an exposed face (not buried).

    Includes:
    - Solid blocks with at least one exposed (non-solid neighbor) face
    - Liquid blocks (if include_liquids=True)
    - Non-solid blocks resting on solid ground (loose stones, plants, etc.)

    Args:
        observer_pos: Dict with x, y, z of observer position
        blocks: List of block dicts from the API
        eye_height: Height offset for observer's eyes
        include_liquids: Whether to include liquid blocks in output

    Returns:
        List of visible surface block dicts
    """
    visible = filter_visible_blocks(observer_pos, blocks, eye_height)

    solid_positions: set[tuple[int, int, int]] = set()

    for block_data in blocks:
        info = BlockInfo.from_dict(block_data)
        if info.is_solid:
            solid_positions.add(info.to_tuple())

    surface_blocks: list[dict[str, Any]] = []
    neighbor_offsets = [
        (1, 0, 0),
        (-1, 0, 0),
        (0, 1, 0),
        (0, -1, 0),
        (0, 0, 1),
        (0, 0, -1),
    ]

    for block_data in visible:
        info = BlockInfo.from_dict(block_data)
        pos = info.to_tuple()

        # Liquids are always surface blocks
        if info.is_liquid:
            if include_liquids:
                surface_blocks.append(block_data)
            continue

        # Non-solid blocks resting on ground are always surface blocks
        # (they already passed the "on solid ground" check in filter_visible_blocks)
        if not info.is_solid:
            surface_blocks.append(block_data)
            continue

        # For solid blocks, check if they have an exposed face
        has_exposed_face = False
        for dx, dy, dz in neighbor_offsets:
            neighbor = (pos[0] + dx, pos[1] + dy, pos[2] + dz)
            if neighbor not in solid_positions:
                has_exposed_face = True
                break

        if has_exposed_face:
            surface_blocks.append(block_data)

    return surface_blocks
