using System;
using System.Collections.Generic;
using System.Text;
using Cairo;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace vsai;

/// <summary>
/// Custom map layer that displays the AI bot on the minimap and world map.
/// Only tracks entities with code "aibot", ignoring all other entities.
/// </summary>
public class AiBotMapLayer : MapLayer
{
    private readonly ICoreClientAPI _capi;
    private readonly Dictionary<long, EntityMapComponent> _components = new();
    private LoadedTexture? _botTexture;

    // Cyan/turquoise color for bot marker (distinct from player white)
    private const double MarkerColorR = 0.0;
    private const double MarkerColorG = 0.87;
    private const double MarkerColorB = 1.0;
    private const int MarkerSize = 16;

    public override string Title => "AI Bot";
    public override EnumMapAppSide DataSide => EnumMapAppSide.Client;
    public override string LayerGroupCode => "entities";

    public AiBotMapLayer(ICoreAPI api, IWorldMapManager mapSink) : base(api, mapSink)
    {
        _capi = (ICoreClientAPI)api;
    }

    public override void OnLoaded()
    {
        // Create the bot marker texture
        _botTexture = GenerateBotTexture();

        // Register for entity spawn/despawn events
        _capi.Event.OnEntitySpawn += OnEntitySpawn;
        _capi.Event.OnEntityDespawn += OnEntityDespawn;

        // Also check for any existing aibot entities that were spawned before map loaded
        foreach (var entity in _capi.World.LoadedEntities.Values)
        {
            OnEntitySpawn(entity);
        }

        _capi.Logger.Notification("[VSAI] AiBotMapLayer loaded");
    }

    public override void Dispose()
    {
        // Unregister event handlers
        _capi.Event.OnEntitySpawn -= OnEntitySpawn;
        _capi.Event.OnEntityDespawn -= OnEntityDespawn;

        // Dispose all map components
        foreach (var component in _components.Values)
        {
            component.Dispose();
        }
        _components.Clear();

        // Dispose the texture
        _botTexture?.Dispose();
        _botTexture = null;

        base.Dispose();
    }

    private void OnEntitySpawn(Entity entity)
    {
        // Only track aibot entities
        if (entity.Code?.Path != "aibot") return;

        // Don't add duplicates
        if (_components.ContainsKey(entity.EntityId)) return;

        if (_botTexture == null)
        {
            _capi.Logger.Warning("[VSAI] Bot texture not initialized, cannot create map component");
            return;
        }

        // Create map component for this entity
        var component = new EntityMapComponent(_capi, _botTexture, entity);
        _components[entity.EntityId] = component;

        _capi.Logger.Notification($"[VSAI] Added aibot (ID: {entity.EntityId}) to minimap");
    }

    private void OnEntityDespawn(Entity entity, EntityDespawnData despawnData)
    {
        // Only track aibot entities
        if (entity.Code?.Path != "aibot") return;

        if (_components.TryGetValue(entity.EntityId, out var component))
        {
            component.Dispose();
            _components.Remove(entity.EntityId);
            _capi.Logger.Notification($"[VSAI] Removed aibot (ID: {entity.EntityId}) from minimap");
        }
    }

    public override void OnMapOpenedClient()
    {
        // Called when the map is opened - refresh entity positions
        foreach (var component in _components.Values)
        {
            // EntityMapComponent auto-updates position from entity
        }
    }

    public override void Render(GuiElementMap mapElem, float dt)
    {
        // Find the ONE valid active bot to display (most recently spawned)
        Entity? activeBot = null;
        long highestEntityId = 0;

        foreach (var entity in _capi.World.LoadedEntities.Values)
        {
            if (entity.Code?.Path == "aibot"
                && entity.Alive
                && entity.State == EnumEntityState.Active
                && entity.EntityId > highestEntityId)
            {
                activeBot = entity;
                highestEntityId = entity.EntityId;
            }
        }

        // Clear all tracked components
        foreach (var component in _components.Values)
        {
            component.Dispose();
        }
        _components.Clear();

        // Only add the one active bot
        if (activeBot != null && _botTexture != null)
        {
            var component = new EntityMapComponent(_capi, _botTexture, activeBot);
            _components[activeBot.EntityId] = component;
            component.Render(mapElem, dt);
        }
    }

    public override void OnMouseMoveClient(MouseEvent args, GuiElementMap mapElem, StringBuilder hoverText)
    {
        // Check if mouse is hovering over any bot marker
        foreach (var component in _components.Values)
        {
            component.OnMouseMove(args, mapElem, hoverText);
        }
    }

    public override void OnMouseUpClient(MouseEvent args, GuiElementMap mapElem)
    {
        foreach (var component in _components.Values)
        {
            component.OnMouseUpOnElement(args, mapElem);
        }
    }

    /// <summary>
    /// Generate a colored circle texture for the bot marker using Cairo.
    /// </summary>
    private LoadedTexture GenerateBotTexture()
    {
        var surface = new ImageSurface(Format.Argb32, MarkerSize, MarkerSize);
        var ctx = new Context(surface);

        // Clear background
        ctx.SetSourceRGBA(0, 0, 0, 0);
        ctx.Paint();

        // Draw outer glow/border (darker cyan)
        ctx.SetSourceRGBA(MarkerColorR * 0.5, MarkerColorG * 0.5, MarkerColorB * 0.5, 0.8);
        ctx.Arc(MarkerSize / 2.0, MarkerSize / 2.0, MarkerSize / 2.0 - 1, 0, 2 * Math.PI);
        ctx.Fill();

        // Draw inner circle (bright cyan)
        ctx.SetSourceRGBA(MarkerColorR, MarkerColorG, MarkerColorB, 1.0);
        ctx.Arc(MarkerSize / 2.0, MarkerSize / 2.0, MarkerSize / 2.0 - 3, 0, 2 * Math.PI);
        ctx.Fill();

        // Draw center dot (white)
        ctx.SetSourceRGBA(1, 1, 1, 1);
        ctx.Arc(MarkerSize / 2.0, MarkerSize / 2.0, 2, 0, 2 * Math.PI);
        ctx.Fill();

        // Upload to GPU
        int textureId = _capi.Gui.LoadCairoTexture(surface, true);
        var texture = new LoadedTexture(_capi, textureId, MarkerSize, MarkerSize);

        // Cleanup Cairo resources
        ctx.Dispose();
        surface.Dispose();

        return texture;
    }
}
