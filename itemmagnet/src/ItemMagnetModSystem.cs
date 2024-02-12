using HarmonyLib;
using Newtonsoft.Json.Linq;
using ProtoBuf;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.Common;
using Vintagestory.GameContent;

#pragma warning disable S3011 // Reflection should not be used to increase accessibility of classes, methods, or fields

namespace ItemMagnet;
public class ItemMagnetModSystem : ModSystem
{
    public HashSet<string> WearableStats = new();
    private ICoreAPI? mApi;
    private IClientNetworkChannel? mClientNetworkChannel;

    [ProtoContract(ImplicitFields = ImplicitFields.AllPublic)]
    public struct ItemMagnetTogglePacket
    {
    }

    public override void Start(ICoreAPI api)
    {
        api.RegisterItemClass("ItemWearableWithStats", typeof(ItemWearableWithStats));

        mApi = api;

        new Harmony("itemmagnet").Patch(
                    AccessTools.Method(typeof(EntityBehaviorCollectEntities), nameof(EntityBehaviorCollectEntities.OnGameTick)),
                    prefix: new HarmonyMethod(AccessTools.Method(typeof(ItemMagnetModSystem), nameof(OnGameTick)))
                );

        new Harmony("itemmagnet").Patch(
                    typeof(ModSystemWearableStats).GetMethod("updateWearableStats", AccessTools.all),
                    postfix: new HarmonyMethod(AccessTools.Method(typeof(ItemMagnetModSystem), nameof(UpdateWearableStats)))
                );

        if (api is ICoreServerAPI serverApi)
        {
            serverApi.Network.RegisterChannel("itemmagnet")
                .RegisterMessageType<ItemMagnetTogglePacket>()
                .SetMessageHandler<ItemMagnetTogglePacket>(ToggleMagnetServer);
        }
        if (api is ICoreClientAPI clientApi)
        {
            mClientNetworkChannel = clientApi.Network.RegisterChannel("itemmagnet")
                .RegisterMessageType<ItemMagnetTogglePacket>();

            clientApi.Input.RegisterHotKey("itemmagnettoggle", "Toggle item magnet", GlKeys.Z);
            clientApi.Input.SetHotKeyHandler("itemmagnettoggle", ToggleMagnetClient);
        }
    }

    public override void Dispose()
    {
        new Harmony("itemmagnet").Unpatch(AccessTools.Method(typeof(EntityBehaviorCollectEntities), nameof(EntityBehaviorCollectEntities.OnGameTick)), HarmonyPatchType.Prefix, "itemmagnet");
        new Harmony("itemmagnet").Unpatch(typeof(ModSystemWearableStats).GetMethod("updateWearableStats", AccessTools.all), HarmonyPatchType.Postfix, "itemmagnet");
    }

    private bool ToggleMagnetClient(KeyCombination hotkey)
    {
        if (mClientNetworkChannel != null)
        {
            mClientNetworkChannel.SendPacket(new ItemMagnetTogglePacket());
        }

        return true;
    }

    private ItemSlot? GetMagnet(IServerPlayer player)
    {
        IInventory? inventory = player?.InventoryManager?.GetOwnInventory(GlobalConstants.characterInvClassName);

        if (inventory != null)
        {
            foreach (ItemSlot item in inventory)
            {
                if (item.Empty || item.Itemstack?.Item is not ItemWearableWithStats) continue;

                return item;

                if (item.Itemstack.Item.Code.Domain == "itemmagnet")
                {
                    return item;
                }
            }
        }

        return null;
    }

    private void ToggleMagnetServer(IServerPlayer player, ItemMagnetTogglePacket packet)
    {
        
        ItemSlot? magnet = GetMagnet(player);
        
        
        if (magnet == null) return;

        if (magnet.Itemstack.Attributes.HasAttribute("otherStatModifiers"))
        {
            magnet.Itemstack.Attributes.RemoveAttribute("otherStatModifiers");
        }
        else
        {
            magnet.Itemstack.Attributes.GetOrAddTreeAttribute("otherStatModifiers").SetFloat("pickupradius", 0);
            magnet.Itemstack.Attributes.GetOrAddTreeAttribute("otherStatModifiers").SetFloat("pickupspeed", 0);
        }

        magnet.MarkDirty();

        UpdateWearableStats(player.InventoryManager.GetOwnInventory(GlobalConstants.characterInvClassName), player);
    }

    private sealed class Field<TValue, TInstance>
    {
        public TValue? Value
        {
            get
            {
                return (TValue?)mFieldInfo?.GetValue(mInstance);
            }
            set
            {
                mFieldInfo?.SetValue(mInstance, value);
            }
        }

        private readonly FieldInfo? mFieldInfo;
        private readonly TInstance mInstance;

        public Field(Type from, string field, TInstance instance)
        {
            mInstance = instance;
            mFieldInfo = from.GetField(field, BindingFlags.NonPublic | BindingFlags.Instance);
        }
    }

    private static bool OnGameTick(EntityBehaviorCollectEntities __instance, float deltaTime)
    {
        if (__instance.entity.State != 0 || !__instance.entity.Alive)
        {
            return false;
        }

        float pickupradius = __instance.entity.Stats.GetBlended("pickupradius");
        float pickupspeed = __instance.entity.Stats.GetBlended("pickupspeed");

        Field<int, EntityBehaviorCollectEntities> waitTicks = new(typeof(EntityBehaviorCollectEntities), "waitTicks", __instance);
        Field<int, EntityBehaviorCollectEntities> lastCollectedEntityIndex = new(typeof(EntityBehaviorCollectEntities), "lastCollectedEntityIndex", __instance);
        Field<Vec3d, EntityBehaviorCollectEntities> tmp = new(typeof(EntityBehaviorCollectEntities), "tmp", __instance);
        Field<float, EntityBehaviorCollectEntities> itemsPerSecond = new(typeof(EntityBehaviorCollectEntities), "itemsPerSecond", __instance);
        Field<float, EntityBehaviorCollectEntities> unconsumedDeltaTime = new(typeof(EntityBehaviorCollectEntities), "unconsumedDeltaTime", __instance);

        IPlayer? player = (__instance.entity as EntityPlayer)?.Player;
        IServerPlayer? serverPlayer = player as IServerPlayer;

        if (serverPlayer != null && serverPlayer.ItemCollectMode == 1)
        {
            EntityAgent? agent = __instance.entity as EntityAgent;
            if (agent != null && !agent.Controls.Sneak)
            {
                return false;
            }
        }

        if (__instance.entity.IsActivityRunning("invulnerable"))
        {
            waitTicks.Value = 3;
        }
        else
        {
            if (waitTicks.Value-- > 0 || (player != null && player.WorldData.CurrentGameMode == EnumGameMode.Spectator))
            {
                return false;
            }

            tmp.Value?.Set(__instance.entity.ServerPos.X, __instance.entity.ServerPos.Y + __instance.entity.SelectionBox.Y1 + (double)(__instance.entity.SelectionBox.Y2 / 2f), __instance.entity.ServerPos.Z);
            Entity?[] entitiesAround = __instance.entity.World.GetEntitiesAround(tmp.Value, 0.5f + pickupradius, 0.5f + pickupradius, (foundEntity) => foundEntity.CanCollect(__instance.entity));
            if (entitiesAround.Length == 0)
            {
                unconsumedDeltaTime.Value = 0f;
                return false;
            }

            deltaTime = Math.Min(1f, deltaTime + unconsumedDeltaTime.Value);
            while (deltaTime - 1f / itemsPerSecond.Value * pickupspeed > 0f)
            {
                Entity? entity = null;
                int i;
                for (i = 0; i < entitiesAround.Length; i++)
                {
                    if (entitiesAround[i] != null && i >= lastCollectedEntityIndex.Value)
                    {
                        entity = entitiesAround[i];
                        break;
                    }
                }

                if (entity == null)
                {
                    entity = entitiesAround[0];
                    i = 0;
                }

                if (entity == null)
                {
                    return false;
                }

                if (!__instance.OnFoundCollectible(entity))
                {
                    lastCollectedEntityIndex.Value = (lastCollectedEntityIndex.Value + 1) % entitiesAround.Length;
                }
                else
                {
                    entitiesAround[i] = null;
                }

                deltaTime -= 1f / (itemsPerSecond.Value * pickupspeed);
            }

            unconsumedDeltaTime.Value = deltaTime;
        }

        return false;
    }

    private static void UpdateWearableStats(IInventory inv, IServerPlayer player)
    {
        OtherStatModifiers stats;

        try
        {
            stats = new(player.Entity.Api.ModLoader.GetModSystem<ItemMagnetModSystem>().WearableStats);
        }
        catch (Exception exception)
        {
            player.Entity.Api.Logger.Error($"Error while updating wearable stats:\n{exception}");
            return;
        }

        foreach (ItemSlot item in inv)
        {
            if (item.Empty || item.Itemstack.Item is not ItemWearableWithStats statsItem) continue;

            OtherStatModifiers? statModifiers = statsItem.OtherStatModifiers;

            if (statModifiers == null || item.Itemstack.Collectible.GetRemainingDurability(item.Itemstack) == 0 && item.Itemstack.Collectible.GetMaxDurability(item.Itemstack) > 0) continue;

            if (item.Itemstack.Attributes.HasAttribute("otherStatModifiers"))
            {
                OtherStatModifiers modifiers = new(item.Itemstack.Attributes.GetTreeAttribute("otherStatModifiers"));
                stats.Add(statModifiers, true, modifiers);
            }
            else
            {
                stats.Add(statModifiers, true);
            }
        }

        EntityPlayer entity = player.Entity;

        foreach ((string stat, float value) in stats.Stats)
        {
            entity.Stats.Set(stat, "wearablestats", value, persistent: true);
        }
    }
}

public class OtherStatModifiers
{
    public Dictionary<string, float> Stats { get; } = new();

    public OtherStatModifiers(HashSet<string> stats)
    {
        foreach (string stat in stats)
        {
            Stats.Add(stat, 0);
        }
    }
    public OtherStatModifiers(ICoreAPI api, JsonObject jsonObject)
    {
        if (jsonObject.Token is not JObject stats) return;

        HashSet<string> statsRegistry = api.ModLoader.GetModSystem<ItemMagnetModSystem>().WearableStats;

        foreach ((string stat, JToken? value) in stats)
        {
            if (value is not JValue statValue || statValue.Type != JTokenType.Float && statValue.Type != JTokenType.Integer) continue;

            if (statValue.Type == JTokenType.Float)
            {
                JsonObject floatValue = new(statValue);
                Stats.Add(stat, floatValue.AsFloat());
                if (!statsRegistry.Contains(stat)) statsRegistry.Add(stat);
            }
            else if (statValue.Type == JTokenType.Integer)
            {
                JsonObject intValue = new(statValue);
                Stats.Add(stat, intValue.AsInt());
                if (!statsRegistry.Contains(stat)) statsRegistry.Add(stat);
            }
        }
    }
    public OtherStatModifiers(ITreeAttribute attributes)
    {
        foreach ((var stat, _) in attributes)
        {
            Stats.Add(stat, attributes.GetFloat(stat, 1));
        }
    }

    public void Add(OtherStatModifiers modifiers, bool onlyExisted = false, OtherStatModifiers? overwritten = null)
    {
        foreach ((string? stat, float value) in modifiers.Stats)
        {
            if (Stats.ContainsKey(stat))
            {
                if (overwritten?.Stats?.ContainsKey(stat) == true) continue;
                Stats[stat] += value;
            }
            else if (!onlyExisted)
            {
                Stats.Add(stat, value);
            }
        }

        if (overwritten == null) return;

        foreach ((string? stat, float value) in overwritten.Stats)
        {
            if (Stats.ContainsKey(stat))
            {
                Stats[stat] += value;
            }
            else if (!onlyExisted)
            {
                Stats.Add(stat, value);
            }
        }
    }
}

public class ItemWearableWithStats : ItemWearable
{
    public OtherStatModifiers? OtherStatModifiers { get; set; }

    public override void OnLoaded(ICoreAPI api)
    {
        base.OnLoaded(api);

        JsonObject? jsonObject = Attributes?["otherStatModifiers"];
        if (jsonObject != null && jsonObject.Exists)
        {
            try
            {
                OtherStatModifiers = new(api, jsonObject);
            }
            catch (Exception e)
            {
                api.World.Logger.Error("Failed loading otherStatModifiers for item/block {0}. Will ignore.", Code);
                api.World.Logger.Error(e);
                StatModifers = null;
            }
        }
    }
}

#pragma warning restore S3011 // Reflection should not be used to increase accessibility of classes, methods, or fields
