using System;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace vsai;

public class AiTaskRemoteControl : AiTaskBase
{
    // Movement state shared with HTTP server
    private Vec3d? _pendingTarget;
    private float _moveSpeed = 0.03f;
    private string _status = "idle";  // idle, moving, reached, stuck, direct_walking
    private string _statusMessage = "";
    private Vec3d? _lastTargetPos;

    // Direct walk state (bypasses pathfinding)
    private Vec3d? _directWalkTarget;
    private float _directWalkSpeed = 0.03f;  // Motion units per tick (matches normal walk speed)
    private const float DirectWalkArrivalThreshold = 0.5f;

    // Stuck detection for direct walk
    private Vec3d? _directWalkLastPos;
    private int _directWalkStuckTicks = 0;
    private const int DirectWalkStuckThreshold = 30;  // ~1.5 seconds at 20 ticks/sec
    private const double DirectWalkMinMovement = 0.05;  // Minimum movement per check to not be "stuck"

    public AiTaskRemoteControl(EntityAgent entity, JsonObject taskConfig, JsonObject aiConfig)
        : base(entity, taskConfig, aiConfig)
    {
        // Load default speed from config if provided
        if (taskConfig != null)
        {
            _moveSpeed = taskConfig["movespeed"].AsFloat(0.03f);
        }
    }

    public override bool ShouldExecute()
    {
        // Always be the active task
        return true;
    }

    public override void StartExecute()
    {
        base.StartExecute();
        _status = "idle";
        _statusMessage = "Ready for commands";
    }

    public override bool ContinueExecute(float dt)
    {
        // Check for pending pathfinding movement command
        if (_pendingTarget != null && _status != "moving")
        {
            _lastTargetPos = _pendingTarget.Clone();
            _status = "moving";
            _statusMessage = $"Moving to ({_pendingTarget.X:F1}, {_pendingTarget.Y:F1}, {_pendingTarget.Z:F1})";

            pathTraverser.NavigateTo(_pendingTarget, _moveSpeed, 0.5f, OnGoalReached, OnStuck);
            _pendingTarget = null;
        }

        // Handle direct walking (bypasses A* pathfinding, uses WalkTowards for straight-line movement)
        if (_directWalkTarget != null)
        {
            var currentPos = entity.ServerPos.XYZ;
            var targetPos = _directWalkTarget;

            // Calculate horizontal distance to target
            double dx = targetPos.X - currentPos.X;
            double dz = targetPos.Z - currentPos.Z;
            double horizontalDist = Math.Sqrt(dx * dx + dz * dz);

            if (horizontalDist < DirectWalkArrivalThreshold)
            {
                // Arrived at target
                _directWalkTarget = null;
                _directWalkLastPos = null;
                _directWalkStuckTicks = 0;
                pathTraverser.Stop();
                _status = "reached";
                _statusMessage = "Arrived at destination (direct walk)";
            }
            else
            {
                // Stuck detection: check if we've moved since last tick
                if (_directWalkLastPos != null)
                {
                    double movedX = currentPos.X - _directWalkLastPos.X;
                    double movedZ = currentPos.Z - _directWalkLastPos.Z;
                    double movedDist = Math.Sqrt(movedX * movedX + movedZ * movedZ);

                    if (movedDist < DirectWalkMinMovement)
                    {
                        _directWalkStuckTicks++;

                        if (_directWalkStuckTicks >= DirectWalkStuckThreshold)
                        {
                            // We're stuck - give up
                            _directWalkTarget = null;
                            _directWalkLastPos = null;
                            _directWalkStuckTicks = 0;
                            pathTraverser.Stop();
                            _status = "stuck";
                            _statusMessage = $"Stuck at ({currentPos.X:F1}, {currentPos.Y:F1}, {currentPos.Z:F1}) - obstacle blocking path";
                            return true;
                        }
                    }
                    else
                    {
                        // We moved, reset stuck counter
                        _directWalkStuckTicks = 0;
                    }
                }
                _directWalkLastPos = currentPos.Clone();

                // Use WalkTowards for straight-line movement (no A* pathfinding)
                // This needs to be called each tick to keep walking
                pathTraverser.WalkTowards(_directWalkTarget, _directWalkSpeed, 0.5f, null, null);
                _statusMessage = $"Direct walking to target, {horizontalDist:F1} blocks away";
            }
        }

        return true;  // Always keep this task running
    }

    private void OnGoalReached()
    {
        _status = "reached";
        _statusMessage = "Arrived at destination";
    }

    private void OnStuck()
    {
        _status = "stuck";
        var pos = entity.ServerPos.XYZ;
        _statusMessage = $"Stuck at ({pos.X:F1}, {pos.Y:F1}, {pos.Z:F1})";
    }

    public override void FinishExecute(bool cancelled)
    {
        pathTraverser.Stop();
        base.FinishExecute(cancelled);
    }

    // === Public API for HTTP server ===

    /// <summary>
    /// Set a new movement target. The task will start moving on the next tick.
    /// </summary>
    public void SetTarget(Vec3d target, float speed = 0.03f)
    {
        _pendingTarget = target.Clone();
        _moveSpeed = speed;
        _status = "pending";
        _statusMessage = "Target received";
    }

    /// <summary>
    /// Stop current movement.
    /// </summary>
    public void Stop()
    {
        pathTraverser.Stop();
        _pendingTarget = null;
        _status = "idle";
        _statusMessage = "Stopped";
    }

    /// <summary>
    /// Get current movement status.
    /// </summary>
    public string GetStatus() => _status;

    /// <summary>
    /// Get detailed status message.
    /// </summary>
    public string GetStatusMessage() => _statusMessage;

    /// <summary>
    /// Get the last target position.
    /// </summary>
    public Vec3d? GetLastTarget() => _lastTargetPos;

    /// <summary>
    /// Check if pathTraverser is actively moving.
    /// </summary>
    public bool IsActive() => pathTraverser.Active;

    /// <summary>
    /// Start direct walking to a target (bypasses A* pathfinding).
    /// This allows movement even when chunks aren't loaded for pathfinding.
    /// </summary>
    public void SetDirectWalkTarget(Vec3d target, float speed = 0.03f)
    {
        // Stop any current pathfinding
        pathTraverser.Stop();
        _pendingTarget = null;

        _directWalkTarget = target.Clone();
        _directWalkSpeed = speed;
        _directWalkLastPos = null;  // Reset stuck detection
        _directWalkStuckTicks = 0;
        _status = "direct_walking";
        _statusMessage = $"Direct walking to ({target.X:F1}, {target.Y:F1}, {target.Z:F1})";
    }

    /// <summary>
    /// Stop direct walking.
    /// </summary>
    public void StopDirectWalk()
    {
        _directWalkTarget = null;
        pathTraverser.Stop();
    }

    /// <summary>
    /// Check if currently direct walking.
    /// </summary>
    public bool IsDirectWalking() => _directWalkTarget != null;
}
