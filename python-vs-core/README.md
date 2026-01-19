# vintage-story-core

Core utilities for Vintage Story AI bot control.

## Installation

```bash
pip install vintage-story-core
```

## Features

### Line-of-Sight Visibility Filtering

Filter blocks to only those visible from an observer position using Amanatides & Woo's fast voxel traversal algorithm.

```python
from vintage_story_core import filter_visible_blocks, get_visible_surface_blocks

# observer_pos from bot observation
observer_pos = {"x": 512127.5, "y": 123.0, "z": 511858.5}

# blocks from /bot/blocks API endpoint
blocks = [
    {"x": 512127, "y": 122, "z": 511858, "code": "game:soil-medium-none"},
    {"x": 512128, "y": 122, "z": 511858, "code": "game:rock-granite"},
    # ...
]

# Get all visible blocks (blocks the observer can see via raycast)
visible = filter_visible_blocks(observer_pos, blocks)

# Get only visible surface blocks (visible + have exposed face)
surface = get_visible_surface_blocks(observer_pos, blocks)
```

## Development

### Setup

```bash
# Create virtual environment
python -m venv .

# Activate virtual environment
source bin/activate

# Install dependencies
pip install -r dev-requirements.txt
pip install -e .
```

### Building and Publishing

```bash
./build-publish.sh
```

## License

MIT

## Author

Jason Byteforge (jmazzahacks@gmail.com)
