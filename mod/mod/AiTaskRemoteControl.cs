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
    private string _status = "idle";  // idle, moving, reached, stuck
    private string _statusMessage = "";
    private Vec3d? _lastTargetPos;

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
        // Check for pending movement command
        if (_pendingTarget != null && _status != "moving")
        {
            _lastTargetPos = _pendingTarget.Clone();
            _status = "moving";
            _statusMessage = $"Moving to ({_pendingTarget.X:F1}, {_pendingTarget.Y:F1}, {_pendingTarget.Z:F1})";

            pathTraverser.NavigateTo(_pendingTarget, _moveSpeed, 0.5f, OnGoalReached, OnStuck);
            _pendingTarget = null;
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
}
