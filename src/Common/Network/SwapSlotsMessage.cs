using CarryOn.API.Common;
using ProtoBuf;
using System;

namespace CarryOn.Common.Network
{
    [ProtoContract(ImplicitFields = ImplicitFields.AllFields)]
    public class SwapSlotsMessage
    {
        public CarrySlot First { get; }
        public CarrySlot Second { get; }

        private SwapSlotsMessage() { }

        public SwapSlotsMessage(CarrySlot first, CarrySlot second)
        {
            if (first == second) throw new ArgumentException("Slots can't be the same");
            First = first;
            Second = second;
        }
    }
}
