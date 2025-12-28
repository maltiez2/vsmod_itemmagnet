using CombatOverhaul;
using CombatOverhaul.Armor;
using CombatOverhaul.DamageSystems;
using System.Text;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.API.Util;
using Vintagestory.GameContent;

namespace ItemMagnet;

public class ItemMagnetStats
{
    public bool NeedsFuel { get; set; } = true;
    public float FuelCapacityHours { get; set; } = 24f;
    public float FuelEfficiency { get; set; } = 1f;
    public string FuelAttribute { get; set; } = "nightVisionFuelHours";
    public bool ConsumeFuelWhileSleeping { get; set; } = false;
    public string[] RefuelBagWildcard { get; set; } = ["exoskeleton*"];

    public string[] Layers { get; set; } = [];
    public string[] Zones { get; set; } = [];
    public Dictionary<string, float> Resists { get; set; } = [];
    public Dictionary<string, float> FlatReduction { get; set; } = [];
    public Dictionary<string, float> StatsWhenTurnedOn { get; set; } = [];
    public Dictionary<string, float> StatsWhenTurnedOff { get; set; } = [];
}

public class ItemMagnet : ItemWearable, IFueledItem, IAffectsPlayerStats, ITogglableItem, IArmor
{
    public ArmorType ArmorType { get; private set; }
    public DamageResistData Resists { get; private set; } = new();
    public bool StatsChanged { get; set; } = false;
    public string HotKeyCode => "item-magnet-toggle";

    public override void OnLoaded(ICoreAPI api)
    {
        base.OnLoaded(api);

        _stats = Attributes.AsObject<ItemMagnetStats>();

        if (!_stats.Layers.Any() || !_stats.Zones.Any())
        {
            return;
        }

        ArmorType = new(_stats.Layers.Select(Enum.Parse<ArmorLayers>).Aggregate((first, second) => first | second), _stats.Zones.Select(Enum.Parse<DamageZone>).Aggregate((first, second) => first | second));
        Resists = new(
            _stats.Resists.ToDictionary(entry => Enum.Parse<EnumDamageType>(entry.Key), entry => entry.Value),
            _stats.FlatReduction.ToDictionary(entry => Enum.Parse<EnumDamageType>(entry.Key), entry => entry.Value));
    }

    public void AddFuelHours(IPlayer player, ItemSlot slot, double hours)
    {
        if (slot?.Itemstack?.Attributes == null) return;

        slot.Itemstack.Attributes.SetDouble("fuelHours", Math.Max(0.0, hours + GetFuelHours(player, slot)));

        if (GetFuelHours(player, slot) <= 0.0)
        {
            RefuelFromBag(player, slot);
        }

        slot.OnItemSlotModified(sinkStack: null);
    }
    public double GetFuelHours(IPlayer player, ItemSlot slot)
    {
        if (slot?.Itemstack?.Attributes == null) return 0;

        return Math.Max(0.0, slot.Itemstack.Attributes.GetDecimal(_fuelAttribute));
    }
    public bool ConsumeFuelWhenSleeping(IPlayer player, ItemSlot slot) => _stats.ConsumeFuelWhileSleeping;
    public void SetFuelHours(IPlayer player, ItemSlot slot, double fuelHours)
    {
        if (slot?.Itemstack?.Attributes == null) return;

        StatsChanged = true;
        fuelHours = GameMath.Clamp(fuelHours, 0, _stats.FuelCapacityHours);
        slot.Itemstack.Attributes.SetDouble("fuelHours", fuelHours);
        slot.MarkDirty();
    }
    public float GetStackFuel(ItemStack stack)
    {
        return (stack.ItemAttributes?[_stats.FuelAttribute].AsFloat() ?? 0f) * _stats.FuelEfficiency;
    }
    public void RefuelFromBag(IPlayer player, ItemSlot slot)
    {
        player.Entity.WalkInventory(fuelSlot =>
        {
            if (fuelSlot is ItemSlotBagContentWithWildcardMatch wildcardMatchSlot &&
                WildcardUtil.Match(_stats.RefuelBagWildcard, wildcardMatchSlot.SourceBag.Collectible.Code.Path) &&
                fuelSlot?.Empty == false &&
                GetStackFuel(fuelSlot.Itemstack) > 0)
            {
                if (AddFuelFromStack(player, slot, fuelSlot.Itemstack))
                {
                    fuelSlot.TakeOut(1);
                    fuelSlot.MarkDirty();
                    slot.MarkDirty();

                    return false;
                }
            }

            return true;
        });
    }
    public bool AddFuelFromStack(IPlayer player, ItemSlot slot, ItemStack stack)
    {
        float stackFuel = GetStackFuel(stack);
        double fuelHours = GetFuelHours(player, slot);
        if (stackFuel > 0f && fuelHours + (double)(stackFuel / 2f) < (double)_stats.FuelCapacityHours)
        {
            SetFuelHours(player, slot, (double)stackFuel + fuelHours);
            return true;
        }
        return false;
    }

    public Dictionary<string, float> PlayerStats(ItemSlot slot, EntityPlayer player)
    {
        double fuelLeft = GetFuelHours(player.Player, slot);

        return ((fuelLeft > 0 || !_stats.NeedsFuel) && TurnedOn(player.Player, slot)) ? _stats.StatsWhenTurnedOn : _stats.StatsWhenTurnedOff;
    }

    public override int GetMergableQuantity(ItemStack sinkStack, ItemStack sourceStack, EnumMergePriority priority)
    {
        if (priority == EnumMergePriority.DirectMerge)
        {
            if (GetStackFuel(sourceStack) == 0f)
            {
                return base.GetMergableQuantity(sinkStack, sourceStack, priority);
            }

            return 1;
        }

        return base.GetMergableQuantity(sinkStack, sourceStack, priority);
    }
    public override void TryMergeStacks(ItemStackMergeOperation op)
    {
        if (op.CurrentPriority == EnumMergePriority.DirectMerge)
        {
            float stackFuel = GetStackFuel(op.SourceSlot.Itemstack);
            double fuelHours = GetFuelHours(op.ActingPlayer, op.SinkSlot);
            if (stackFuel > 0f && fuelHours + (double)(stackFuel / 2f) < (double)_stats.FuelCapacityHours)
            {
                SetFuelHours(op.ActingPlayer, op.SinkSlot, (double)stackFuel + fuelHours);
                op.MovedQuantity = 1;
                op.SourceSlot.TakeOut(1);
                op.SinkSlot.MarkDirty();
            }
            else if (api.Side == EnumAppSide.Client)
            {
                (api as ICoreClientAPI)?.TriggerIngameError(this, "maskfull", Lang.Get("ingameerror-mask-full")); // @TODO change error message
            }
        }
    }
    public override void GetHeldItemInfo(ItemSlot inSlot, StringBuilder dsc, IWorldAccessor world, bool withDebugInfo)
    {
        base.GetHeldItemInfo(inSlot, dsc, world, withDebugInfo);

        if (_stats.NeedsFuel)
        {
            double fuelHours = GetFuelHours((world as IClientWorldAccessor)?.Player, inSlot);
            dsc.AppendLine(Lang.Get("Has fuel for {0:0.#} hours", fuelHours));
            if (fuelHours <= 0.0)
            {
                dsc.AppendLine(Lang.Get("Add temporal gear to refuel"));
            }

            dsc.AppendLine();
        }

        dsc.AppendLine(Lang.Get("combatoverhaul:armor-layers-info", ArmorType.LayersToTranslatedString()));
        dsc.AppendLine(Lang.Get("combatoverhaul:armor-zones-info", ArmorType.ZonesToTranslatedString()));
        if (Resists.Resists.Values.Any(value => value != 0))
        {
            dsc.AppendLine(Lang.Get("combatoverhaul:armor-fraction-protection"));
            foreach ((EnumDamageType type, float level) in Resists.Resists.Where(entry => entry.Value > 0))
            {
                string damageType = Lang.Get($"combatoverhaul:damage-type-{type}");
                dsc.AppendLine($"  {damageType}: {level}");
            }
        }

        if (Resists.FlatDamageReduction.Values.Any(value => value != 0))
        {
            dsc.AppendLine(Lang.Get("combatoverhaul:armor-flat-protection"));
            foreach ((EnumDamageType type, float level) in Resists.FlatDamageReduction.Where(entry => entry.Value > 0))
            {
                string damageType = Lang.Get($"combatoverhaul:damage-type-{type}");
                dsc.AppendLine($"  {damageType}: {level}");
            }
        }

        if (_stats.StatsWhenTurnedOn.Values.Any(value => value != 0))
        {
            dsc.AppendLine(Lang.Get("combatoverhaul:stat-stats"));
            foreach ((string stat, float value) in _stats.StatsWhenTurnedOn)
            {
                if (value != 0f) dsc.AppendLine($"  {Lang.Get($"combatoverhaul:stat-{stat}")}: {value * 100:F1}%");
            }
        }

        dsc.AppendLine();
    }
    public override void OnHeldInteractStart(ItemSlot slot, EntityAgent byEntity, BlockSelection blockSel, EntitySelection entitySel, bool firstEvent, ref EnumHandHandling handHandling)
    {
        if (byEntity.Controls.ShiftKey)
        {
            base.OnHeldInteractStart(slot, byEntity, blockSel, entitySel, firstEvent, ref handHandling);
            return;
        }

        if (slot.Itemstack.Item == null) return;

        IPlayer? player = (byEntity as EntityPlayer)?.Player;
        if (player == null) return;

        ArmorInventory? inventory = GetGearInventory(byEntity) as ArmorInventory;
        if (inventory == null) return;

        string code = slot.Itemstack.Item.Code;

        try
        {
            IEnumerable<int> slots = inventory.GetSlotBlockingSlotsIndices(ArmorType);

            foreach (int index in slots)
            {
                ItemStack stack = inventory[index].TakeOutWhole();
                if (!player.InventoryManager.TryGiveItemstack(stack))
                {
                    byEntity.Api.World.SpawnItemEntity(stack, byEntity.ServerPos.AsBlockPos);
                }
                inventory[index].MarkDirty();
            }

            int slotIndex = inventory.GetFittingSlotIndex(ArmorType);
            inventory[slotIndex].TryFlipWith(slot);

            inventory[slotIndex].MarkDirty();
            slot.MarkDirty();

            handHandling = EnumHandHandling.PreventDefault;
        }
        catch (Exception exception)
        {
            api.Logger.Error($"[Exoskeleton] Error on equipping '{code}' that occupies {ArmorType}:\n{exception}");
        }
    }

    public bool TurnedOn(IPlayer player, ItemSlot slot) => slot.Itemstack.Attributes.GetAsBool(_stateAttribute, false);
    public void TurnOn(IPlayer player, ItemSlot slot)
    {
        slot.Itemstack.Attributes.SetBool(_stateAttribute, true);
        slot.MarkDirty();
    }
    public void TurnOff(IPlayer player, ItemSlot slot)
    {
        slot.Itemstack.Attributes.SetBool(_stateAttribute, false);
        slot.MarkDirty();
    }
    public void Toggle(IPlayer player, ItemSlot slot)
    {
        slot.Itemstack.Attributes.SetBool(_stateAttribute, !slot.Itemstack.Attributes.GetAsBool(_stateAttribute, false));
        DummySlot temporarySlot = new();
        slot.TryPutInto(player.Entity.Api.World, temporarySlot);
        temporarySlot.TryPutInto(player.Entity.Api.World, slot);
        slot.MarkDirty();
    }

    private const string _fuelAttribute = "fuelHours";
    private ItemMagnetStats _stats = new();
    private const string _stateAttribute = "item-magnet-state";

    private static InventoryBase? GetGearInventory(Entity entity)
    {
        return entity.GetBehavior<EntityBehaviorPlayerInventory>()?.Inventory;
    }
}