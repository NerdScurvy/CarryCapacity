using System;
using System.Collections.Generic;
using System.Linq;
using CarryOn.API.Common;
using CarryOn.Common.Network;
using CarryOn.Utility;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.GameContent;

namespace CarryOn.Common
{
    /// <summary>
    ///   Takes care of core CarryCapacity handling, such as listening to input events,
    ///   picking up, placing and swapping blocks, as well as sending and handling messages.
    /// </summary>
    public class CarryHandler
    {
        private CurrentAction _action = CurrentAction.None;
        private CarrySlot? _targetSlot = null;
        private BlockPos _selectedBlock = null;
        private float _timeHeld = 0.0F;

        private CarrySystem System { get; }

        public CarryHandler(CarrySystem system)
            => System = system;

        public void InitClient()
        {
            System.ClientChannel.SetMessageHandler<LockSlotsMessage>(OnLockSlotsMessage);

            System.ClientAPI.Input.InWorldAction += OnEntityAction;
            System.ClientAPI.Event.RegisterGameTickListener(OnGameTick, 0);

            System.ClientAPI.Event.BeforeActiveSlotChanged +=
                (_) => OnBeforeActiveSlotChanged(System.ClientAPI.World.Player.Entity);
        }

        public void InitServer()
        {
            System.ServerChannel
                .SetMessageHandler<InteractMessage>(OnInteractMessage)
                .SetMessageHandler<PickUpMessage>(OnPickUpMessage)
                .SetMessageHandler<PlaceDownMessage>(OnPlaceDownMessage)
                .SetMessageHandler<SwapSlotsMessage>(OnSwapSlotsMessage);

            System.ServerAPI.Event.OnEntitySpawn += OnServerEntitySpawn;
            System.ServerAPI.Event.PlayerNowPlaying += OnServerPlayerNowPlaying;

            System.ServerAPI.Event.BeforeActiveSlotChanged +=
                (player, _) => OnBeforeActiveSlotChanged(player.Entity);
        }

        public void OnServerEntitySpawn(Entity entity)
        {
            // We handle player "spawning" in OnServerPlayerJoin.
            // If we send a LockSlotsMessage at this point, the client's player is still null.
            if (entity is EntityPlayer) return;

            // Set this again so walk speed modifiers and animations can be applied.
            foreach (var carried in entity.GetCarried())
                carried.Set(entity, carried.Slot);
        }

        public void OnServerPlayerNowPlaying(IServerPlayer player)
        {
            foreach (var carried in player.Entity.GetCarried())
                carried.Set(player.Entity, carried.Slot);
        }

        /// <summary> Returns the first "action" slot (either Hands or Shoulder)
        ///           that satisfies the specified function, or null if none. </summary>
        private static CarrySlot? FindActionSlot(System.Func<CarrySlot, bool> func)
        {
            if (func(CarrySlot.Hands)) return CarrySlot.Hands;
            if (func(CarrySlot.Shoulder)) return CarrySlot.Shoulder;
            return null;
        }

        public void OnEntityAction(EnumEntityAction action, bool on, ref EnumHandling handled)
        {
            // Only handle action if it's being activated rather than deactivated.
            if (!on) return;

            bool isInteract;
            switch (action)
            {
                // Right click (interact action) starts carry's pickup and place handling.
                case EnumEntityAction.InWorldRightMouseDown:
                    isInteract = true; break;
                // Other actions, which are prevented while holding something.
                case EnumEntityAction.InWorldLeftMouseDown:
                case EnumEntityAction.Sprint:
                    isInteract = false; break;
                default: return;
            }

            // If an action is currently ongoing, ignore the game's entity action.
            if (_action != CurrentAction.None)
            { handled = EnumHandling.PreventDefault; return; }

            var world = System.ClientAPI.World;
            var player = world.Player;
            var selection = player.CurrentBlockSelection;

            var carriedHands = player.Entity.GetCarried(CarrySlot.Hands);
            var carriedBack = player.Entity.GetCarried(CarrySlot.Back);
            var carriedShoulder = player.Entity.GetCarried(CarrySlot.Shoulder);
            var holdingAny = carriedHands ?? carriedShoulder;

            // If enabled allow player to hold control to focus on swapping to or from back
            var swapBackFocus = ModConfig.ClientConfig.HoldControlForBackSwapFocus && player.Entity.Controls.CtrlKey;

            // If something is being carried in-hand, prevent RMB, LMB and sprint.
            // If still holding RMB after an action completed, prevent the default action as well.
            if ((carriedHands != null) || (isInteract && (_timeHeld > 0.0F)))
                handled = EnumHandling.PreventDefault;

            // Only continue if player is starting an interaction (right click).
            if (!isInteract || (_timeHeld > 0.0F)) return;

            // If something's being held..
            if (holdingAny != null)
            {
                // ..and aiming at block, try to place it.
                if (selection != null && !swapBackFocus)
                {
                    // If carrying something in-hand, don't require empty hands.
                    // This shouldn't occur since nothing is supposed to go into
                    // an active slot while something is carried there. This is
                    // just in case, so a carried block can still be placed down.
                    if (!CanInteract(player.Entity, carriedHands != null))
                    {
                        selection = GetMultiblockOriginSelection(selection);

                        // Cannot pick up or put down - check for interact behavior
                        if (selection?.Block?.HasBehavior<BlockBehaviorCarryableInteract>() == true)
                        {
                            var interactBehavior = selection?.Block.GetBehavior<BlockBehaviorCarryableInteract>();
                            if(interactBehavior.CanInteract(player)){
                                _action = CurrentAction.Interact;
                                _selectedBlock = selection.Position;
                            }
                        }
                        else
                        {
                            handled = EnumHandling.PreventDefault;
                        }
                        return;
                    }

                    _selectedBlock = GetPlacedPosition(world, selection, holdingAny.Block);
                    if (_selectedBlock == null) return;

                    _action = CurrentAction.PlaceDown;
                    _targetSlot = holdingAny.Slot;
                }
                // If something's being held and aiming at nothing, try to put held block on back.
                else
                {
                    // Check to make sure that player is sneaking empty-handed,
                    // is not already carrying something in the back slot, and
                    // the currently held block can be equipped on the back.
                    if (!CanInteract(player.Entity, true) || (carriedBack != null) ||
                        (holdingAny.Behavior.Slots[CarrySlot.Back] == null))
                    {
                        return;
                    }

                    _action = CurrentAction.SwapBack;
                    _targetSlot = holdingAny.Slot;
                }
            }
            // If nothing's being held..
            else if (CanInteract(player.Entity, true))
            {
                if (selection != null) selection = GetMultiblockOriginSelection(selection);
                // ..and aiming at carryable block, try to pick it up.
                if ((selection?.Block != null) && (_targetSlot = FindActionSlot(slot => selection.Block.IsCarryable(slot))) != null && !swapBackFocus)
                {
                    _action = CurrentAction.PickUp;
                    _selectedBlock = selection.Position;
                }
                // ..and aiming at nothing or non-carryable block, try to grab block on back.
                else if ((carriedBack != null) &&
                         (_targetSlot = FindActionSlot(slot => carriedBack.Behavior.Slots[slot] != null)) != null)
                {
                    _action = CurrentAction.SwapBack;
                }
                else
                {
                    return;
                }
            }
            else
            {
                return;
            }

            // Run this once to for validation. May reset action to None.
            _timeHeld = 0.0F;
            OnGameTick(0.0F);
            // Prevent default action. Don't want to interact with blocks.
            handled = EnumHandling.PreventDefault;
        }

        public void OnGameTick(float deltaTime)
        {
            var interactHeld = System.ClientAPI.Input.MouseButton.Right;
            if (!interactHeld) { CancelInteraction(true); return; }

            if (_action == CurrentAction.None) return;
            var world = System.ClientAPI.World;
            var player = world.Player;

            // TODO: Only allow close blocks to be picked up.
            // TODO: Don't allow the block underneath to change?

            if (_action != CurrentAction.Interact && !CanInteract(player.Entity, (_action != CurrentAction.PlaceDown) || (_targetSlot != CarrySlot.Hands)))
            { CancelInteraction(); return; }

            var carriedTarget = _targetSlot.HasValue ? player.Entity.GetCarried(_targetSlot.Value) : null;
            var holdingAny = player.Entity.GetCarried(CarrySlot.Hands)
                             ?? player.Entity.GetCarried(CarrySlot.Shoulder);
            BlockSelection selection = null;
            BlockBehaviorCarryable carryBehavior = null;
            BlockBehaviorCarryableInteract interactBehavior = null;
            switch (_action)
            {
                case CurrentAction.Interact:
                case CurrentAction.PickUp:
                case CurrentAction.PlaceDown:

                    // Ensure the player hasn't in the meantime
                    // picked up / placed down something somehow.
                    if ((_action == CurrentAction.PickUp) == (holdingAny != null))
                    { CancelInteraction(); return; }

                    selection = (_action == CurrentAction.PlaceDown) ? player.CurrentBlockSelection : GetMultiblockOriginSelection(player.CurrentBlockSelection);

                    var position = (_action == CurrentAction.PlaceDown)
                        ? GetPlacedPosition(world, player?.CurrentBlockSelection, carriedTarget.Block)
                        : selection?.Position;

                    // Make sure the player is still looking at the same block.
                    if (_selectedBlock != position)
                    { CancelInteraction(); return; }

                    if (_action == CurrentAction.Interact)
                    {
                        interactBehavior = selection?.Block.GetBehavior<BlockBehaviorCarryableInteract>();
                        break;
                    }
                    // Get the block behavior from either the block
                    // to be picked up or the currently carried block.
                    carryBehavior = (_action == CurrentAction.PickUp)
                        ? selection?.Block?.GetBehaviorOrDefault(BlockBehaviorCarryable.Default)
                        : carriedTarget?.Behavior;
                    break;

                case CurrentAction.SwapBack:

                    var carriedBack = player.Entity.GetCarried(CarrySlot.Back);
                    // Ensure that the player hasn't in the meantime
                    // put something in that slot / on their back.
                    if ((carriedTarget != null) == (carriedBack != null))
                    { CancelInteraction(); return; }

                    carryBehavior = (carriedTarget != null) ? carriedTarget.Behavior : carriedBack.Behavior;
                    // Make sure the block to swap can still be put in that slot.
                    if (carryBehavior.Slots[_targetSlot.Value] == null) return;

                    break;

                default: return;
            }

            float requiredTime;
            if (_action == CurrentAction.Interact)
            {
                requiredTime = interactBehavior?.InteractDelay ?? CarrySystem.InteractSpeedDefault;
            }
            else
            {
                requiredTime = carryBehavior?.InteractDelay ?? CarrySystem.PickUpSpeedDefault;
                switch (_action)
                {
                    case CurrentAction.PlaceDown: requiredTime *= CarrySystem.PlaceSpeedDefault; break;
                    case CurrentAction.SwapBack: requiredTime *= CarrySystem.SwapSpeedDefault; break;
                }
            }

            _timeHeld += deltaTime;
            var progress = _timeHeld / requiredTime;
            System.HudOverlayRenderer.CircleProgress = progress;
            if (progress <= 1.0F) return;

            switch (_action)
            {
                case CurrentAction.Interact:
                    if (selection?.Block?.OnBlockInteractStart(world, player, selection) == true)
                        System.ClientChannel.SendPacket(new InteractMessage(selection.Position));
                    break;
                case CurrentAction.PickUp:
                    if (player.Entity.Carry(selection.Position, _targetSlot.Value))
                        System.ClientChannel.SendPacket(new PickUpMessage(selection.Position, _targetSlot.Value));
                    break;
                case CurrentAction.PlaceDown:
                    if (PlaceDown(player, carriedTarget, selection, out var placedAt))
                        System.ClientChannel.SendPacket(new PlaceDownMessage(_targetSlot.Value, selection, placedAt));
                    break;
                case CurrentAction.SwapBack:
                    if (player.Entity.Swap(_targetSlot.Value, CarrySlot.Back))
                        System.ClientChannel.SendPacket(new SwapSlotsMessage(CarrySlot.Back, _targetSlot.Value));
                    break;
            }

            CancelInteraction();
        }

        public void CancelInteraction(bool resetTimeHeld = false)
        {
            _action = CurrentAction.None;
            _targetSlot = null;
            System.HudOverlayRenderer.CircleVisible = false;
            if (resetTimeHeld) _timeHeld = 0.0F;
        }

        public EnumHandling OnBeforeActiveSlotChanged(EntityAgent entity)
        {
            // If the player is carrying something in their hands,
            // prevent them from changing their active hotbar slot.
            return (entity.GetCarried(CarrySlot.Hands) != null)
                ? EnumHandling.PreventDefault
                : EnumHandling.PassThrough;
        }

        public void OnLockSlotsMessage(LockSlotsMessage message)
        {
            var player = System.ClientAPI.World.Player;
            var hotbar = player.InventoryManager.GetHotbarInventory();
            for (var i = 0; i < hotbar.Count; i++)
            {
                if (message.HotbarSlots?.Contains(i) == true)
                    LockedItemSlot.Lock(hotbar[i]);
                else LockedItemSlot.Restore(hotbar[i]);
            }
        }

        public void SendLockSlotsMessage(IServerPlayer player)
        {
            var hotbar = player.InventoryManager.GetHotbarInventory();
            var slots = Enumerable.Range(0, hotbar.Count).Where(i => hotbar[i] is LockedItemSlot).ToList();
            System.ServerChannel.SendPacket(new LockSlotsMessage(slots), player);
        }
        public static void SendLockSlotsMessage(EntityPlayer player)
        {
            if ((player == null) || (player.World.PlayerByUid(player.PlayerUID) is not IServerPlayer serverPlayer)) return;
            var system = player.World.Api.ModLoader.GetModSystem<CarrySystem>();
            system.CarryHandler.SendLockSlotsMessage(serverPlayer);
        }

        private void OnInteractMessage(IServerPlayer player, InteractMessage message)
        {
            var world = player.Entity.World;
            var block = world.BlockAccessor.GetBlock(message.Position);

            // Check block has interact behavior serverside
            if (block?.HasBlockBehavior<BlockBehaviorCarryableInteract>() == true)
            {
                var behavior = block.GetBehavior<BlockBehaviorCarryableInteract>();

                if(behavior.CanInteract(player)){
                    var blockSelection = player.CurrentBlockSelection.Clone();
                    blockSelection.Position = message.Position;
                    blockSelection.Block = block;
                    // TODO: add event hook here
                    block?.OnBlockInteractStart(world, player, blockSelection);
                }
            }
        }

        public void OnPickUpMessage(IServerPlayer player, PickUpMessage message)
        {
            // FIXME: Do at least some validation of this data.

            var carried = player.Entity.GetCarried(message.Slot);
            if ((message.Slot == CarrySlot.Back) || (carried != null) ||
                !CanInteract(player.Entity, true) ||
                !player.Entity.Carry(message.Position, message.Slot))
            {
                InvalidCarry(player, message.Position);
            }
        }

        public void OnPlaceDownMessage(IServerPlayer player, PlaceDownMessage message)
        {
            // FIXME: Do at least some validation of this data.

            var carried = player.Entity.GetCarried(message.Slot);
            if ((message.Slot == CarrySlot.Back) || (carried == null) ||
                !CanInteract(player.Entity, message.Slot != CarrySlot.Hands) ||
                !PlaceDown(player, carried, message.Selection, out var placedAt))
            {
                InvalidCarry(player, message.PlacedAt);
            }
            // If succeeded, but by chance the client's projected placement isn't
            // the same as the server's, re-sync the block at the client's position.
            else if (placedAt != message.PlacedAt)
            {
                player.Entity.World.BlockAccessor.MarkBlockDirty(message.PlacedAt);
            }
        }

        public void OnSwapSlotsMessage(IServerPlayer player, SwapSlotsMessage message)
        {
            if ((message.First == message.Second) || (message.First != CarrySlot.Back) ||
                !CanInteract(player.Entity, true) ||
                !player.Entity.Swap(message.First, message.Second))
            {
                player.Entity.WatchedAttributes.MarkPathDirty(CarriedBlock.AttributeId);
            }
        }

        /// <summary>
        ///   Returns whether the specified entity has the required prerequisites
        ///   to interact using CarryOn: Must be sneaking with an empty hand.
        ///   Also tests for whether a valid hotbar slot is currently selected.
        /// </summary>
        private static bool CanInteract(EntityAgent entity, bool requireEmptyHanded)
        {
            if (!entity.Controls.Sneak) return false;

            var isEmptyHanded = entity.RightHandItemSlot.Empty && entity.LeftHandItemSlot.Empty;
            if (!isEmptyHanded && requireEmptyHanded) return false;

            if (entity is not EntityPlayer entityPlayer) return true;
            var activeHotbarSlot = entityPlayer.Player.InventoryManager.ActiveHotbarSlotNumber;
            return (activeHotbarSlot >= 0) && (activeHotbarSlot < 10);
        }

        public static bool PlaceDown(IPlayer player, CarriedBlock carried,
                                     BlockSelection selection, out BlockPos placedAt)
        {
            var clickedBlock = player.Entity.World.BlockAccessor.GetBlock(selection.Position);

            // Clone the selection, because we don't
            // want to affect what is sent to the server.
            selection = selection.Clone();

            if (clickedBlock.IsReplacableBy(carried.Block))
            {
                selection.Face = BlockFacing.UP;
                selection.HitPosition.Y = 0.5;
            }
            else
            {
                selection.Position.Offset(selection.Face);
                selection.DidOffset = true;
            }

            placedAt = selection.Position;
            return player.PlaceCarried(selection, carried.Slot);
        }

        /// <summary> Called when a player picks up or places down an invalid block,
        ///           requiring it to get notified about the action being rejected. </summary>
        private void InvalidCarry(IServerPlayer player, BlockPos pos)
        {
            player.Entity.World.BlockAccessor.MarkBlockDirty(pos);
            player.Entity.WatchedAttributes.MarkPathDirty(CarriedBlock.AttributeId);
            player.Entity.WatchedAttributes.MarkPathDirty("stats/walkspeed");
            SendLockSlotsMessage(player);
        }

        /// <summary> Returns the position that the specified block would
        ///           be placed at for the specified block selection. </summary>
        private static BlockPos GetPlacedPosition(
            IWorldAccessor world, BlockSelection selection, Block block)
        {
            if (selection == null) return null;
            var position = selection.Position.Copy();
            var clickedBlock = world.BlockAccessor.GetBlock(position);
            if (!clickedBlock.IsReplacableBy(block))
            {
                position.Offset(selection.Face);
                var replacedBlock = world.BlockAccessor.GetBlock(position);
                if (!replacedBlock.IsReplacableBy(block)) return null;
            }
            return position;
        }

        /// <summary>Get the block position for the main block within for a multiblock structure</summary>
        private BlockPos GetMultiblockOrigin(BlockPos position, BlockMultiblock multiblock)
        {
            if(position == null) return null;

            if(multiblock != null){
                var multiPosition = position.Copy();
                multiPosition.Add(multiblock.OffsetInv);
                return multiPosition;
            }
            return position;
        }

        /// <summary>Create a new block selection pointing to the main block within a multiblock structure</summary>
        private BlockSelection GetMultiblockOriginSelection(BlockSelection blockSelection)
        {
            if (blockSelection?.Block is BlockMultiblock multiblock)
            {
                var world = System.Api.World;
                var position = GetMultiblockOrigin(blockSelection.Position, multiblock);
                var block = world.BlockAccessor.GetBlock(position);
                var selection = blockSelection.Clone();
                selection.Position = position;
                selection.Block = block;

                return selection;
            }
            return blockSelection;
        }

        private enum CurrentAction
        {
            None,
            PickUp,
            PlaceDown,
            SwapBack,
            Interact
        }
    }
}
