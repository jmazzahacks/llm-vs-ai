using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace vsai;

/// <summary>
/// Custom renderer for the AI bot that renders held items in hands.
/// Extends EntityShapeRenderer and implements custom held item rendering.
/// Reads hand items from WatchedAttributes which are synced from server.
/// </summary>
public class EntityAiBotRenderer : EntityShapeRenderer
{
    // Dummy slot used for getting render info
    private DummySlot? _dummySlot;

    public EntityAiBotRenderer(Entity entity, ICoreClientAPI api) : base(entity, api)
    {
        api.Logger.Notification("[VSAI] EntityAiBotRenderer created for entity " + entity.EntityId);
        _dummySlot = new DummySlot();
    }

    public override void DoRender3DOpaque(float dt, bool isShadowPass)
    {
        base.DoRender3DOpaque(dt, isShadowPass);

        // Render held items after the entity shape
        RenderBotHeldItem(dt, true, isShadowPass);  // Right hand
        RenderBotHeldItem(dt, false, isShadowPass); // Left hand
    }

    /// <summary>
    /// Custom held item rendering for the bot.
    /// Reads from WatchedAttributes which are synced from server.
    /// Uses the base class RenderItem method for proper matrix handling.
    /// </summary>
    private void RenderBotHeldItem(float dt, bool rightHand, bool isShadowPass)
    {
        // Read hand item from WatchedAttributes (synced from server)
        string attrKey = rightHand
            ? EntityBehaviorBotInventory.RightHandAttrKey
            : EntityBehaviorBotInventory.LeftHandAttrKey;

        // Get itemstack from WatchedAttributes and resolve it
        ItemStack? stack = entity.WatchedAttributes.GetItemstack(attrKey);
        if (stack == null) return;

        stack.ResolveBlockOrItem(entity.World);
        if (stack.Collectible == null) return;

        // Get the attachment point for this hand
        string attachmentCode = rightHand ? "RightHand" : "LeftHand";
        AttachmentPointAndPose? apap = entity.AnimManager?.Animator?.GetAttachmentPointPose(attachmentCode);
        if (apap == null)
        {
            return;
        }

        // Use dummy slot to get render info
        if (_dummySlot == null) _dummySlot = new DummySlot();
        _dummySlot.Itemstack = stack;

        // Get render info for the held item (third person view)
        ItemRenderInfo renderInfo = capi.Render.GetItemStackRenderInfo(_dummySlot, EnumItemRenderTarget.HandTp, dt);
        if (renderInfo?.ModelRef == null)
        {
            return;
        }

        // Use the base class RenderItem method which handles all the matrix math correctly
        RenderItem(dt, isShadowPass, stack, apap, renderInfo);
    }
}
