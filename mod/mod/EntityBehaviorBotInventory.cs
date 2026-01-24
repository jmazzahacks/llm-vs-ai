using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Datastructures;

namespace vsai;

/// <summary>
/// Custom inventory behavior for the AI bot with expanded slot count and hand slots.
/// Uses InventoryGeneric which allows any item in any slot, unlike SeraphInventory
/// which reserves slots 0-14 for equipment.
/// Hand items are synced via WatchedAttributes to allow client-side rendering.
/// </summary>
public class EntityBehaviorBotInventory : EntityBehavior
{
    /// <summary>
    /// The inventory storage - uses InventoryGeneric for unrestricted slots.
    /// </summary>
    public InventoryGeneric Inventory { get; private set; } = null!;

    /// <summary>
    /// Right hand slot for equipped items.
    /// </summary>
    public ItemSlot RightHandSlot { get; private set; } = null!;

    /// <summary>
    /// Left hand slot for equipped items.
    /// </summary>
    public ItemSlot LeftHandSlot { get; private set; } = null!;

    /// <summary>
    /// Default number of inventory slots for the bot.
    /// </summary>
    public const int DefaultSlotCount = 32;

    /// <summary>
    /// WatchedAttributes keys for hand item sync.
    /// </summary>
    public const string RightHandAttrKey = "rightHandItem";
    public const string LeftHandAttrKey = "leftHandItem";

    private int _slotCount = DefaultSlotCount;
    private InventoryGeneric _handInventory = null!;

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

        // Create separate hand inventory for right and left hand (2 slots)
        string handInstanceId = $"bothands-{entity.EntityId}";
        _handInventory = new InventoryGeneric(
            2,
            "bothands",
            handInstanceId,
            entity.Api
        );

        // Slot 0 = right hand, Slot 1 = left hand
        RightHandSlot = _handInventory[0];
        LeftHandSlot = _handInventory[1];

        // Register slot change handlers to sync to WatchedAttributes (server side only)
        if (entity.Api.Side == EnumAppSide.Server)
        {
            _handInventory.SlotModified += OnHandSlotModified;
        }
    }

    /// <summary>
    /// Called when a hand slot is modified on the server.
    /// Syncs the item to WatchedAttributes for client visibility.
    /// </summary>
    private void OnHandSlotModified(int slotIndex)
    {
        SyncHandSlotToWatchedAttributes(slotIndex);
    }

    /// <summary>
    /// Syncs a hand slot to WatchedAttributes for client-side rendering.
    /// </summary>
    public void SyncHandSlotToWatchedAttributes(int slotIndex)
    {
        if (entity.Api.Side != EnumAppSide.Server) return;

        string attrKey = slotIndex == 0 ? RightHandAttrKey : LeftHandAttrKey;
        ItemSlot slot = slotIndex == 0 ? RightHandSlot : LeftHandSlot;

        if (slot.Empty)
        {
            // Remove the attribute if slot is empty
            entity.WatchedAttributes.RemoveAttribute(attrKey);
        }
        else
        {
            // Store the itemstack directly using SetItemstack
            entity.WatchedAttributes.SetItemstack(attrKey, slot.Itemstack);
        }

        entity.WatchedAttributes.MarkPathDirty(attrKey);
    }

    /// <summary>
    /// Syncs both hand slots to WatchedAttributes.
    /// Call this after equipping items via the API.
    /// </summary>
    public void SyncAllHandSlots()
    {
        SyncHandSlotToWatchedAttributes(0);
        SyncHandSlotToWatchedAttributes(1);
    }
}
