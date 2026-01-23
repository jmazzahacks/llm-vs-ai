using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Datastructures;

namespace vsai;

/// <summary>
/// Custom inventory behavior for the AI bot with expanded slot count.
/// Uses InventoryGeneric which allows any item in any slot, unlike SeraphInventory
/// which reserves slots 0-14 for equipment.
/// </summary>
public class EntityBehaviorBotInventory : EntityBehavior
{
    /// <summary>
    /// The inventory storage - uses InventoryGeneric for unrestricted slots.
    /// </summary>
    public InventoryGeneric Inventory { get; private set; } = null!;

    /// <summary>
    /// Default number of inventory slots for the bot.
    /// </summary>
    public const int DefaultSlotCount = 32;

    private int _slotCount = DefaultSlotCount;

    public EntityBehaviorBotInventory(Entity entity) : base(entity)
    {
    }

    public override string PropertyName() => "botinventory";

    public override void Initialize(EntityProperties properties, JsonObject attributes)
    {
        base.Initialize(properties, attributes);

        // Allow slot count to be configured via JSON attributes
        if (attributes != null && attributes["slotCount"].Exists)
        {
            _slotCount = attributes["slotCount"].AsInt(DefaultSlotCount);
        }

        // Create a unique instance ID for this entity's inventory
        string instanceId = $"botinv-{entity.EntityId}";

        // Create the inventory with all general-purpose slots
        Inventory = new InventoryGeneric(
            _slotCount,
            "botinventory",
            instanceId,
            entity.Api
        );
    }
}
