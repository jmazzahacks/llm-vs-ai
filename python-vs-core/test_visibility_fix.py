#!/usr/bin/env python3
"""Quick test to verify loose stones are included in surface filter."""

from vintage_story_core import filter_visible_blocks, get_visible_surface_blocks

# Simulate bot at a position
observer_pos = {"x": 512020.5, "y": 125.0, "z": 511955.5}

# Simulate blocks like we'd get from the API
# A loose stone at y=125 sitting on soil at y=124
blocks = [
    # Solid soil blocks
    {"worldPos": {"x": 512020, "y": 124, "z": 511955}, "code": "soil-medium-none", "isSolid": True},
    {"worldPos": {"x": 512020, "y": 124, "z": 511956}, "code": "soil-medium-none", "isSolid": True},
    {"worldPos": {"x": 512021, "y": 124, "z": 511955}, "code": "soil-medium-none", "isSolid": True},
    {"worldPos": {"x": 512021, "y": 124, "z": 511956}, "code": "soil-medium-none", "isSolid": True},
    # Loose stone on top of soil (non-solid)
    {"worldPos": {"x": 512021, "y": 125, "z": 511956}, "code": "looseflints-shale-free", "isSolid": False},
    # Tallgrass (also non-solid)
    {"worldPos": {"x": 512020, "y": 125, "z": 511955}, "code": "tallgrass-short-free", "isSolid": False},
]

print("Testing filter_visible_blocks...")
visible = filter_visible_blocks(observer_pos, blocks)
print(f"  Input: {len(blocks)} blocks")
print(f"  Output: {len(visible)} visible blocks")
for b in visible:
    pos = b.get("worldPos", {})
    print(f"    - {b['code']} at ({pos.get('x')}, {pos.get('y')}, {pos.get('z')})")

print("\nTesting get_visible_surface_blocks...")
surface = get_visible_surface_blocks(observer_pos, blocks)
print(f"  Input: {len(blocks)} blocks")
print(f"  Output: {len(surface)} surface blocks")
for b in surface:
    pos = b.get("worldPos", {})
    print(f"    - {b['code']} at ({pos.get('x')}, {pos.get('y')}, {pos.get('z')})")

# Check if loose stone is in output
loose_in_visible = any("looseflints" in b["code"] for b in visible)
loose_in_surface = any("looseflints" in b["code"] for b in surface)

print(f"\nLoose flint in visible: {loose_in_visible}")
print(f"Loose flint in surface: {loose_in_surface}")

if loose_in_surface:
    print("\n✓ FIX WORKING: Loose stones now appear in surface filter!")
else:
    print("\n✗ FIX NOT WORKING: Loose stones still missing from surface filter")
