using System;
using System.Collections.Generic;
using System.Linq;
using FireDrill.Core;

namespace FireDrill.Runtime
{
    /// <summary>One hand slot + bag slots, and the towel state (명세서 16.10). Plain C#; owned by ItemInteractions.
    /// Picking something up while the hand is full moves the held item to the bag, or drops it when the bag is full.</summary>
    public sealed class Inventory
    {
        private readonly Func<string, string> _kindOf;   // object id → snake_case kind
        private readonly string[] _bag;

        public string Hand { get; private set; }
        public IReadOnlyList<string> Bag => _bag;
        public bool TowelWet { get; private set; }
        public bool TowelEquipped { get; private set; }

        public event Action Changed;

        public Inventory(int bagSlots, Func<string, string> kindOf)
        {
            _bag = new string[bagSlots];
            _kindOf = kindOf;
        }

        public IEnumerable<string> All => (Hand != null ? new[] { Hand } : Array.Empty<string>()).Concat(_bag.Where(id => id != null));
        public bool Has(string objectId) => All.Contains(objectId);
        public string FindKind(string kind) => All.FirstOrDefault(id => _kindOf(id) == kind);
        public bool HasTowel => FindKind("towel") != null;
        public string HandKind => Hand != null ? _kindOf(Hand) : null;

        /// <summary>Towel state that the exposure model sees: only an equipped towel protects.</summary>
        public TowelState Towel => !TowelEquipped ? TowelState.None : TowelWet ? TowelState.Wet : TowelState.Dry;

        public void Reset()
        {
            Hand = null;
            Array.Clear(_bag, 0, _bag.Length);
            TowelWet = TowelEquipped = false;
            Changed?.Invoke();
        }

        /// <summary>Puts <paramref name="objectId"/> in the hand. Returns the item that fell to the floor, or null.</summary>
        public string Pickup(string objectId)
        {
            if (Has(objectId)) return null;
            string dropped = null;
            if (Hand != null)
            {
                int free = Array.IndexOf(_bag, null);
                if (free >= 0) _bag[free] = Hand;
                else dropped = Release(Hand);
            }
            Hand = objectId;
            Changed?.Invoke();
            return dropped;
        }

        /// <summary>Exchanges the hand with a fixed bag slot. An empty slot can store the held item.</summary>
        public void SwapWithBag(int index)
        {
            if (index < 0 || index >= _bag.Length || (Hand == null && _bag[index] == null)) return;
            var stored = _bag[index];
            _bag[index] = Hand;
            Hand = stored;
            Changed?.Invoke();
        }

        /// <summary>Drops the held item (G). Returns its id, or null when the hand is empty.</summary>
        public string Drop()
        {
            if (Hand == null) return null;
            var id = Release(Hand);
            Changed?.Invoke();
            return id;
        }

        /// <summary>Faucet: the towel becomes wet (it can be anywhere in the inventory).</summary>
        public bool WetTowel()
        {
            if (!HasTowel) return false;
            TowelWet = true;
            Changed?.Invoke();
            return true;
        }

        /// <summary>T: put the towel to the nose and mouth or take it away. Returns the new state, null without a towel.</summary>
        public bool? ToggleTowel()
        {
            if (!HasTowel) return null;
            TowelEquipped = !TowelEquipped;
            Changed?.Invoke();
            return TowelEquipped;
        }

        private string Release(string id)
        {
            if (Hand == id) Hand = null;
            if (_kindOf(id) == "towel") TowelEquipped = false; // it stays wet if it was
            return id;
        }
    }
}
