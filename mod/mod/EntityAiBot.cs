using Vintagestory.API.Common;

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
}
