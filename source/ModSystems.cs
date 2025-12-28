using CombatOverhaul;
using HarmonyLib;
using ProtoBuf;
using System.Reflection;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.GameContent;

namespace ItemMagnet;
public class ItemMagnetModSystem : ModSystem
{
    [ProtoContract(ImplicitFields = ImplicitFields.AllPublic)]
    public class ItemMagnetTogglePacket
    {
        public byte Data { get; set; } = 0;
    }

    public override void Start(ICoreAPI api)
    {
        api.RegisterItemClass("ItemMagnet:ItemMagnet", typeof(ItemMagnet));

        _api = api;

        new Harmony("itemmagnet").Patch(
                    AccessTools.Method(typeof(EntityBehaviorCollectEntities), nameof(EntityBehaviorCollectEntities.OnGameTick)),
                    prefix: new HarmonyMethod(AccessTools.Method(typeof(ItemMagnetModSystem), nameof(OnGameTick)))
                );

        if (api is ICoreClientAPI clientApi)
        {
            clientApi.Input.RegisterHotKey("item-magnet-toggle", "Toggle item magnet", GlKeys.Z);
            clientApi.Input.SetHotKeyHandler("item-magnet-toggle", _ => clientApi.ModLoader.GetModSystem<CombatOverhaulSystem>().ToggleWearableItem(clientApi.World.Player, "item-magnet-toggle"));
        }
    }

    public override void Dispose()
    {
        new Harmony("itemmagnet").Unpatch(AccessTools.Method(typeof(EntityBehaviorCollectEntities), nameof(EntityBehaviorCollectEntities.OnGameTick)), HarmonyPatchType.Prefix, "itemmagnet");
    }


    private ICoreAPI? _api;

    private ItemSlot? GetMagnet(IServerPlayer player)
    {
        IInventory? inventory = player?.InventoryManager?.GetOwnInventory(GlobalConstants.characterInvClassName);

        if (inventory != null)
        {
            foreach (ItemSlot item in inventory)
            {
                if (item.Empty || item.Itemstack?.Item is not ItemMagnet) continue;

                return item;

                if (item.Itemstack.Item.Code.Domain == "itemmagnet")
                {
                    return item;
                }
            }
        }

        return null;
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
}