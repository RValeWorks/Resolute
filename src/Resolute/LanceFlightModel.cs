using System;

namespace Resolute
{
    // Nuclear Option game-physics calibration, not measured aircraft data.
    // These are forces, areas and finite propellant budgets. No velocity is
    // assigned by this model and there is no terminal Mach-six speed command.
    internal static class LanceFlightModel
    {
        internal const double G = 9.80665, SeaLevelSound = 340.294;
        internal const double BoostReferenceSpeed = 1701.470, CruiseReferenceSpeed = 2722.352;
        internal const double BoostDistance = 30000, CruiseHeight = 30000;
        internal const double LaunchMass = 2000, BoosterFuel = 1000, BoosterDryMass = 150, CruiseFuel = 300;
        internal const double BoostThrust = 60000, BoostExhaustSpeed = 4000;
        internal const double NominalCruiseDensity = .0049485303; // Captured NO atmosphere at 30 km.
        internal const double CruiseRamDrag = .00265, CruiseExhaustSpeed = 16000;
        internal const double CruiseDragArea = .015, BoostDragArea = .11;
        internal const double CruiseStaticThrust = (CruiseRamDrag + .5 * NominalCruiseDensity * CruiseDragArea) * CruiseReferenceSpeed * CruiseReferenceSpeed;
        // Effective game control area: calibrated against NO's thinner upper
        // atmosphere to retain the authored 25g boost / 40g flight envelope.
        // It is not the physical area of the approved rendered model.
        internal const double ControlArea = 24, MaximumLiftCoefficient = 1.8, InducedDragFactor = .003;
        internal const double BoostG = 25, FlightG = 40, LoadSlew = 240;
        internal const double MinimumRange = 25 * 1852, MaximumRange = 500 * 1852;

        internal static double Clamp(double x, double lo, double hi) => Math.Max(lo, Math.Min(hi, x));
        internal static double Smooth(double x) { x = Clamp(x, 0, 1); return x * x * (3 - 2 * x); }
        internal static bool Finite(double x) => !double.IsNaN(x) && !double.IsInfinity(x);
        internal static double Pressure(double density, double speed) => .5 * Math.Max(0, density) * speed * speed;
        internal static double LiftLimit(double density, double speed, double mass, bool boost)
            => Math.Min(mass * G * (boost ? BoostG : FlightG), Pressure(density, speed) * ControlArea * MaximumLiftCoefficient);
        internal static double Drag(double density, double speed, double lift, bool boost)
        {
            double q = Pressure(density, speed);
            return q * (boost ? BoostDragArea : CruiseDragArea) +
                (q > .01 ? InducedDragFactor * lift * lift / (q * ControlArea) : 0);
        }
        internal static double CruiseThrust(double density, double airspeed)
        {
            // Inlet mass flow and engine/ram drag form a smooth thrust map.
            // Its equilibrium varies with density and maneuver drag; the
            // nominal Mach-eight design point is never a velocity clamp.
            double flow = Clamp(Math.Pow(Math.Max(0, density) / NominalCruiseDensity, .3), 0, 2);
            return Math.Max(0, CruiseStaticThrust - CruiseRamDrag * airspeed * airspeed) * flow;
        }
        internal static double FuelUsed(double thrust, double exhaustSpeed, double remaining, double dt)
            => Math.Min(Math.Max(0, remaining), Math.Max(0, thrust) / exhaustSpeed * Math.Max(0, dt));
        internal static bool SeparationReady(double horizontal, double altitude, double speed)
            => horizontal >= BoostDistance && Math.Abs(altitude - CruiseHeight) <= 300 && speed >= BoostReferenceSpeed * .99;
        internal static double BoostHeight(double launchHeight, double horizontal)
        {
            double u = Clamp(horizontal / BoostDistance, 0, 1);
            return launchHeight + (CruiseHeight - launchHeight) * (2 * u - u * u);
        }
        internal static double BoostSlope(double launchHeight, double horizontal)
            => (CruiseHeight - launchHeight) / BoostDistance * 2 * (1 - Clamp(horizontal / BoostDistance, 0, 1));
        internal static double DescentHeight(double horizontalRange, double targetHeight)
            => targetHeight + Math.Max(0, CruiseHeight - targetHeight) * Smooth(horizontalRange / 120000);
        internal static double DescentSlope(double horizontalRange, double targetHeight)
        {
            double u = Clamp(horizontalRange / 120000, 0, 1);
            return -Math.Max(0, CruiseHeight - targetHeight) / 120000 * 6 * u * (1 - u);
        }
        internal static double EvasiveG(double range, double timeToTarget, bool warned)
        {
            double urgency = warned ? 1 : Smooth((450 * 1852 - range) / (350 * 1852));
            double ceiling = 40 - 34 * Smooth((12 - timeToTarget) / 7);
            double arrival = Smooth(timeToTarget / 5);
            return Math.Min(12 + 28 * urgency, ceiling) * arrival * arrival;
        }
    }
}
