using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;

namespace vsai;

/// <summary>
/// Custom entity class for the AI bot.
/// Extends EntityAgent but prevents persistence to save file.
/// </summary>
public class EntityAiBot : EntityAgent
{
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
}
