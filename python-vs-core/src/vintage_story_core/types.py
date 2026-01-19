"""
Core types for Vintage Story utilities.
"""

import math
from dataclasses import dataclass
from typing import Any


@dataclass
class Vec3:
    """3D vector/position."""

    x: float
    y: float
    z: float

    def __sub__(self, other: "Vec3") -> "Vec3":
        return Vec3(self.x - other.x, self.y - other.y, self.z - other.z)

    def __add__(self, other: "Vec3") -> "Vec3":
        return Vec3(self.x + other.x, self.y + other.y, self.z + other.z)

    def length(self) -> float:
        return math.sqrt(self.x * self.x + self.y * self.y + self.z * self.z)

    def normalize(self) -> "Vec3":
        length = self.length()
        if length == 0:
            return Vec3(0, 0, 0)
        return Vec3(self.x / length, self.y / length, self.z / length)

    def to_block_pos(self) -> tuple[int, int, int]:
        """Convert to integer block coordinates."""
        return (int(math.floor(self.x)), int(math.floor(self.y)), int(math.floor(self.z)))


@dataclass
class BlockInfo:
    """Information about a block in the world."""

    x: int
    y: int
    z: int
    code: str
    is_solid: bool = True
    is_liquid: bool = False

    @classmethod
    def from_dict(cls, data: dict[str, Any]) -> "BlockInfo":
        """Create BlockInfo from API response dict.

        Supports both flat format (x, y, z) and nested format (worldPos.x, worldPos.y, worldPos.z).
        """
        # Handle nested worldPos format from VS API
        if "worldPos" in data:
            world_pos = data["worldPos"]
            x = int(world_pos.get("x", 0))
            y = int(world_pos.get("y", 0))
            z = int(world_pos.get("z", 0))
        else:
            x = int(data.get("x", 0))
            y = int(data.get("y", 0))
            z = int(data.get("z", 0))

        code = data.get("code", "")
        is_air = code == "air" or code == "" or "air" in code.lower()
        is_liquid = "water" in code.lower() or "liquid" in code.lower()

        # Use isSolid from API if available, otherwise infer
        if "isSolid" in data:
            is_solid = data["isSolid"]
        else:
            is_solid = not is_air and not is_liquid

        return cls(
            x=x,
            y=y,
            z=z,
            code=code,
            is_solid=is_solid,
            is_liquid=is_liquid,
        )

    def to_tuple(self) -> tuple[int, int, int]:
        """Return block position as tuple."""
        return (self.x, self.y, self.z)
