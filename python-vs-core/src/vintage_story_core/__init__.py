"""
Core utilities for Vintage Story AI bot control.
"""

__version__ = "0.0.1"

from .visibility import filter_visible_blocks, get_visible_surface_blocks
from .types import Vec3, BlockInfo

__all__ = [
    "filter_visible_blocks",
    "get_visible_surface_blocks",
    "Vec3",
    "BlockInfo",
]
