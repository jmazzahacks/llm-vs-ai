using System;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.GameContent;

namespace vsai;

/// <summary>
/// Data structure to capture combat interrupt information when the bot takes damage.
/// </summary>
public class CombatInterrupt
{
    public bool Interrupted { get; set; }
    public long AttackerEntityId { get; set; }
    public string AttackerCode { get; set; } = "";
    public float DamageReceived { get; set; }
    public float CurrentHealth { get; set; }
    public float MaxHealth { get; set; }
    public long Timestamp { get; set; }
}

/// <summary>
/// Custom entity class for the AI bot.
/// Extends EntityAgent but prevents persistence to save file.
/// </summary>
public class EntityAiBot : EntityAgent
{
    // Combat interrupt tracking
    private readonly object _interruptLock = new object();
    private CombatInterrupt? _lastInterrupt = null;

    /// <summary>
    /// Return false to prevent this entity from being saved with chunk data.
    /// This ensures orphaned bots don't accumulate across game sessions.
    /// </summary>
    public override bool StoreWithChunk => false;

    /// <summary>
    /// Keep bot active regardless of distance from players.
    /// This prevents the bot from becoming inactive or despawning
    /// when it travels far from the player during autonomous tasks.
    /// </summary>
    public override bool AlwaysActive => true;

    /// <summary>
    /// Prevent the bot from being despawned during chunk unload.
    /// Without this, the entity gets removed from LoadedEntities when
    /// its chunk unloads, even with AlwaysActive=true.
    /// This matches how EntityPlayer prevents despawning.
    /// </summary>
    public override bool ShouldDespawn => false;

    /// <summary>
    /// Allow the bot to exist beyond normally loaded chunk boundaries.
    /// Without this (default=false), the entity is removed from LoadedEntities
    /// when its chunk unloads, even with all other despawn prevention measures.
    /// This is the key property for long-distance autonomous operation.
    /// </summary>
    public override bool AllowOutsideLoadedRange => true;

    /// <summary>
    /// Right hand item slot - returns slot from botinventory behavior.
    /// </summary>
    public override ItemSlot RightHandItemSlot
    {
        get
        {
            var inventoryBehavior = GetBehavior<EntityBehaviorBotInventory>();
            return inventoryBehavior?.RightHandSlot!;
        }
    }

    /// <summary>
    /// Left hand item slot - returns slot from botinventory behavior.
    /// </summary>
    public override ItemSlot LeftHandItemSlot
    {
        get
        {
            var inventoryBehavior = GetBehavior<EntityBehaviorBotInventory>();
            return inventoryBehavior?.LeftHandSlot!;
        }
    }

    /// <summary>
    /// Initialize the entity with extended simulation range.
    /// Default SimulationRange is 128 blocks - we extend to 1000 to allow
    /// the bot to move autonomously at greater distances from the player.
    /// </summary>
    public override void Initialize(EntityProperties properties, ICoreAPI api, long InChunkIndex3d)
    {
        base.Initialize(properties, api, InChunkIndex3d);

        // Extend simulation range from default 128 to 1000 blocks
        SimulationRange = 1000;
    }

    /// <summary>
    /// Override to capture damage events for combat interrupt system.
    /// This allows long-running operations (like bot_goto) to detect when
    /// the bot is being attacked and return early.
    /// </summary>
    public override bool ReceiveDamage(DamageSource damageSource, float damage)
    {
        Api?.Logger.Debug($"[VSAI] ReceiveDamage called: damage={damage}, hasSource={damageSource.SourceEntity != null}");

        // Call base implementation first
        bool result = base.ReceiveDamage(damageSource, damage);

        Api?.Logger.Debug($"[VSAI] ReceiveDamage base returned: {result}");

        // Only record interrupt if damage was actually applied and we have a source entity
        if (result && damageSource.SourceEntity != null)
        {
            var healthBehavior = GetBehavior<EntityBehaviorHealth>();
            var attackerCode = damageSource.SourceEntity.Code?.Path ?? "unknown";

            Api?.Logger.Debug($"[VSAI] Setting interrupt: attacker={attackerCode}, damage={damage}");

            lock (_interruptLock)
            {
                _lastInterrupt = new CombatInterrupt
                {
                    Interrupted = true,
                    AttackerEntityId = damageSource.SourceEntity.EntityId,
                    AttackerCode = attackerCode,
                    DamageReceived = damage,
                    CurrentHealth = healthBehavior?.Health ?? 0,
                    MaxHealth = healthBehavior?.MaxHealth ?? 0,
                    Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                };
            }
        }

        return result;
    }

    /// <summary>
    /// Get pending combat interrupt without clearing it.
    /// Interrupt persists until ClearInterrupt() is called.
    /// </summary>
    public CombatInterrupt? GetInterrupt()
    {
        lock (_interruptLock)
        {
            return _lastInterrupt;
        }
    }

    /// <summary>
    /// Clear the pending combat interrupt.
    /// Called when starting a new movement command.
    /// </summary>
    public void ClearInterrupt()
    {
        lock (_interruptLock)
        {
            _lastInterrupt = null;
        }
    }

    /// <summary>
    /// Check for and clear any pending combat interrupt (legacy method).
    /// </summary>
    public CombatInterrupt? ConsumeInterrupt()
    {
        lock (_interruptLock)
        {
            var interrupt = _lastInterrupt;
            _lastInterrupt = null;
            return interrupt;
        }
    }

    /// <summary>
    /// Check if there's a pending interrupt without consuming it.
    /// </summary>
    public bool HasPendingInterrupt()
    {
        lock (_interruptLock)
        {
            return _lastInterrupt?.Interrupted ?? false;
        }
    }
}
