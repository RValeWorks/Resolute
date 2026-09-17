using System;

namespace Resolute
{
    // Read once during plugin startup. Normal play needs neither diagnostic
    // observer components nor flight-event snapshots. The separate recorder
    // bridge remains available; opt in before launching a recording session.
    internal static class ResoluteDiagnostics
    {
        internal static bool Enabled { get; private set; }

        internal static void Configure(bool requested, string[] arguments)
        {
            Enabled = requested;
            if (arguments == null) return;
            foreach (string argument in arguments)
            {
                switch ((argument ?? string.Empty).ToLowerInvariant())
                {
                    case "--resolute-diagnostics":
                    case "--resolute-audit":
                    case "--resolute-smoke":
                    case "--resolute-trial":
                    case "--resolute-encyclopedia":
                        Enabled = true;
                        return;
                }
            }
        }
    }
}
