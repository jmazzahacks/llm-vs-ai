#!/usr/bin/env python3
"""
CLI tool to move the VS AI bot and monitor movement progress.
Simulates what an MCP tool would do - issue command, poll status, return results.

Usage:
    python bot_move.py <x> <y> <z> [--speed SPEED] [--relative] [--poll-interval MS]

Examples:
    python bot_move.py 100 72 100           # Move to absolute position
    python bot_move.py 5 0 5 --relative     # Move 5 blocks in X and Z
    python bot_move.py 100 72 100 --speed 0.06  # Move slower
"""

import argparse
import json
import sys
import time
from dataclasses import dataclass
from typing import Optional
from urllib.request import Request, urlopen
from urllib.error import URLError


BASE_URL = "http://localhost:4560"


@dataclass
class Position:
    x: float
    y: float
    z: float

    def distance_to(self, other: "Position") -> float:
        dx = self.x - other.x
        dy = self.y - other.y
        dz = self.z - other.z
        return (dx*dx + dy*dy + dz*dz) ** 0.5

    def __str__(self) -> str:
        return f"({self.x:.2f}, {self.y:.2f}, {self.z:.2f})"


@dataclass
class MovementSample:
    timestamp: float
    position: Position
    status: str
    is_active: bool


@dataclass
class MovementResult:
    success: bool
    start_position: Position
    end_position: Position
    target_position: Position
    final_status: str
    samples: list[MovementSample]
    total_distance: float
    elapsed_time: float
    error: Optional[str] = None


def http_get(endpoint: str) -> dict:
    """Make a GET request and return JSON response."""
    url = f"{BASE_URL}{endpoint}"
    req = Request(url, method="GET")
    req.add_header("Content-Type", "application/json")

    with urlopen(req, timeout=5) as response:
        return json.loads(response.read().decode())


def http_post(endpoint: str, data: dict) -> dict:
    """Make a POST request with JSON body and return JSON response."""
    url = f"{BASE_URL}{endpoint}"
    body = json.dumps(data).encode()
    req = Request(url, data=body, method="POST")
    req.add_header("Content-Type", "application/json")

    with urlopen(req, timeout=5) as response:
        return json.loads(response.read().decode())


def get_movement_status() -> dict:
    """Get current movement status from the bot."""
    return http_get("/bot/movement/status")


def issue_goto_command(x: float, y: float, z: float, speed: float, relative: bool) -> dict:
    """Issue a goto command to the bot."""
    data = {
        "x": x,
        "y": y,
        "z": z,
        "speed": speed,
        "relative": relative
    }
    return http_post("/bot/goto", data)


def move_and_monitor(
    target_x: float,
    target_y: float,
    target_z: float,
    speed: float = 0.03,
    relative: bool = False,
    poll_interval_ms: int = 100
) -> MovementResult:
    """
    Issue a move command and monitor until completion.
    Returns a MovementResult with full movement trace.
    """
    samples: list[MovementSample] = []
    start_time = time.time()

    # Get initial status/position
    try:
        initial_status = get_movement_status()
    except URLError as e:
        return MovementResult(
            success=False,
            start_position=Position(0, 0, 0),
            end_position=Position(0, 0, 0),
            target_position=Position(target_x, target_y, target_z),
            final_status="error",
            samples=[],
            total_distance=0,
            elapsed_time=0,
            error=f"Failed to connect to VS server: {e}"
        )

    if "error" in initial_status:
        return MovementResult(
            success=False,
            start_position=Position(0, 0, 0),
            end_position=Position(0, 0, 0),
            target_position=Position(target_x, target_y, target_z),
            final_status="error",
            samples=[],
            total_distance=0,
            elapsed_time=0,
            error=initial_status["error"]
        )

    start_pos = Position(
        initial_status["position"]["x"],
        initial_status["position"]["y"],
        initial_status["position"]["z"]
    )

    # Calculate actual target if relative
    if relative:
        actual_target = Position(
            start_pos.x + target_x,
            start_pos.y + target_y,
            start_pos.z + target_z
        )
    else:
        actual_target = Position(target_x, target_y, target_z)

    # Issue the move command
    print(f"Starting position: {start_pos}")
    print(f"Target position: {actual_target}")
    print(f"Distance: {start_pos.distance_to(actual_target):.2f} blocks")
    print(f"Speed: {speed}")
    print()

    try:
        goto_result = issue_goto_command(target_x, target_y, target_z, speed, relative)
    except URLError as e:
        return MovementResult(
            success=False,
            start_position=start_pos,
            end_position=start_pos,
            target_position=actual_target,
            final_status="error",
            samples=[],
            total_distance=0,
            elapsed_time=time.time() - start_time,
            error=f"Failed to issue goto command: {e}"
        )

    if "error" in goto_result:
        return MovementResult(
            success=False,
            start_position=start_pos,
            end_position=start_pos,
            target_position=actual_target,
            final_status="error",
            samples=[],
            total_distance=0,
            elapsed_time=time.time() - start_time,
            error=goto_result["error"]
        )

    print("Movement started, polling status...")
    print("-" * 50)

    # Poll until movement completes
    poll_interval_sec = poll_interval_ms / 1000.0
    last_position: Optional[Position] = None
    total_distance = 0.0

    terminal_states = {"idle", "reached", "stuck", "no_task"}

    while True:
        try:
            status = get_movement_status()
        except URLError as e:
            print(f"  Warning: Failed to get status: {e}")
            time.sleep(poll_interval_sec)
            continue

        if "error" in status:
            print(f"  Error: {status['error']}")
            break

        current_pos = Position(
            status["position"]["x"],
            status["position"]["y"],
            status["position"]["z"]
        )
        current_status = status["status"]
        is_active = status.get("isActive", False)

        # Record sample
        sample = MovementSample(
            timestamp=time.time() - start_time,
            position=current_pos,
            status=current_status,
            is_active=is_active
        )
        samples.append(sample)

        # Calculate distance traveled since last sample
        if last_position is not None:
            total_distance += last_position.distance_to(current_pos)
        last_position = current_pos

        # Print progress
        distance_to_target = current_pos.distance_to(actual_target)
        print(f"  [{sample.timestamp:5.2f}s] {current_pos} | status={current_status} | to_target={distance_to_target:.2f}")

        # Check for terminal state
        if current_status in terminal_states and not is_active:
            # Give it one more poll to confirm
            time.sleep(poll_interval_sec)
            confirm_status = get_movement_status()
            if confirm_status.get("status") in terminal_states and not confirm_status.get("isActive", False):
                break

        time.sleep(poll_interval_sec)

    elapsed_time = time.time() - start_time
    end_pos = samples[-1].position if samples else start_pos
    final_status = samples[-1].status if samples else "unknown"

    print("-" * 50)
    print()

    success = final_status == "reached"

    return MovementResult(
        success=success,
        start_position=start_pos,
        end_position=end_pos,
        target_position=actual_target,
        final_status=final_status,
        samples=samples,
        total_distance=total_distance,
        elapsed_time=elapsed_time
    )


def print_result(result: MovementResult) -> None:
    """Print a formatted summary of the movement result."""
    print("=" * 50)
    print("MOVEMENT RESULT")
    print("=" * 50)

    if result.error:
        print(f"ERROR: {result.error}")
        return

    status_emoji = "✓" if result.success else "✗"
    print(f"Status: {status_emoji} {result.final_status}")
    print(f"Start:  {result.start_position}")
    print(f"End:    {result.end_position}")
    print(f"Target: {result.target_position}")
    print()
    print(f"Distance to target: {result.end_position.distance_to(result.target_position):.2f} blocks")
    print(f"Total traveled:     {result.total_distance:.2f} blocks")
    print(f"Elapsed time:       {result.elapsed_time:.2f}s")
    print(f"Samples collected:  {len(result.samples)}")

    if result.elapsed_time > 0 and result.total_distance > 0:
        avg_speed = result.total_distance / result.elapsed_time
        print(f"Average speed:      {avg_speed:.2f} blocks/s")


def main() -> int:
    parser = argparse.ArgumentParser(
        description="Move the VS AI bot and monitor progress",
        formatter_class=argparse.RawDescriptionHelpFormatter,
        epilog=__doc__
    )
    parser.add_argument("x", type=float, help="Target X coordinate")
    parser.add_argument("y", type=float, help="Target Y coordinate")
    parser.add_argument("z", type=float, help="Target Z coordinate")
    parser.add_argument("--speed", type=float, default=0.03,
                        help="Movement speed (default: 0.03)")
    parser.add_argument("--relative", action="store_true",
                        help="Interpret coordinates as relative to current position")
    parser.add_argument("--poll-interval", type=int, default=100,
                        help="Status polling interval in milliseconds (default: 100)")

    args = parser.parse_args()

    result = move_and_monitor(
        target_x=args.x,
        target_y=args.y,
        target_z=args.z,
        speed=args.speed,
        relative=args.relative,
        poll_interval_ms=args.poll_interval
    )

    print_result(result)

    return 0 if result.success else 1


if __name__ == "__main__":
    sys.exit(main())
