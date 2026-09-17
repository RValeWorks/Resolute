using System.Collections.Generic;

namespace Resolute
{
    internal sealed class ResoluteContactNumberLedger
    {
        private readonly Dictionary<uint, uint> numbers = new Dictionary<uint, uint>();
        private uint next = 1001;
        internal uint Get(uint identity)
        {
            if (identity == 0) return 0;
            if (!numbers.TryGetValue(identity, out uint number))
            {
                number = next++;
                numbers.Add(identity, number);
            }
            return number;
        }
    }
}
