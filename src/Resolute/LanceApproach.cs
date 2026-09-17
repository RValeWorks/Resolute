using UnityEngine;

namespace Resolute
{
    // Own navigation only. Input points/velocity come from the native seeker;
    // this does not introduce a target track, force velocity or change a fuze.
    internal static class LanceApproach
    {
        internal static Vector3 Navigate(LanceManeuverState s, Vector3 nativeLead, Vector3 targetOffset,
            Vector3 velocity, Vector3 targetVelocity, float altitude, float density, float mass)
        {
            float speed = velocity.magnitude;
            Vector3 flat = nativeLead; flat.y = 0f;
            if (flat.sqrMagnitude < .001f) flat = Vector3.ProjectOnPlane(s.Before, Vector3.up);
            if (flat.sqrMagnitude < .001f) flat = Vector3.forward;
            flat.Normalize();
            float horizontal = new Vector3(targetOffset.x, 0f, targetOffset.z).magnitude;
            s.Depression = Mathf.Atan2(-targetOffset.y, Mathf.Max(.001f, horizontal)) * Mathf.Rad2Deg;
            // Retain a gravity allowance in the feasibility prediction. Actual
            // force, density, drag and mass integration remain authoritative.
            s.AvailableG = Mathf.Max(.1f, (float)LanceFlightModel.LiftLimit(density, speed, mass, false) /
                (Mathf.Max(1f, mass) * (float)LanceFlightModel.G) - 1f);
            bool recover = NeedsRecovery(s, nativeLead, velocity, targetVelocity);
            // Allow the finite turn into a sixty-degree arrival. Waiting until
            // a 55-degree sightline while still level produced 73-82-degree
            // impacts: sightline angle is not the eventual flight-path angle.
            float height = Mathf.Max(0f, -nativeLead.y);
            float nativeHorizontal = new Vector3(nativeLead.x, 0f, nativeLead.z).magnitude;
            float turnRadius = speed * speed / (s.AvailableG * (float)LanceFlightModel.G);
            float entryRange = height / 1.7320508f + .9f * turnRadius + .5f * speed;
            if (!s.Diving && nativeHorizontal <= entryRange)
            {
                s.Diving = true;
                s.DiveStartRange = Mathf.Max(1f, nativeHorizontal);
                s.DiveStartHeight = height;
                s.DiveStartSlope = Mathf.Clamp(-velocity.y /
                    Mathf.Max(1f, new Vector3(velocity.x, 0f, velocity.z).magnitude), 0f, 4f);
            }
            // Recovery is temporary. Resume loaded evasion once sufficient
            // margin returns; a bank-in correction must not disable it for the
            // entire descent. Hysteresis prevents toggling at the boundary.
            if (s.Diving && recover) s.TerminalRecovery = true;
            else if (s.Diving && s.RecoveryDemandG < s.AvailableG * .95f) s.TerminalRecovery = false;
            if (s.Diving)
            {
                // Hermite approach ends at the freshly updated native lead
                // point with a sixty-degree slope. It retains the entry slope
                // and uses forces to follow the curve, never setting altitude.
                float r = Mathf.Clamp(nativeHorizontal, 0f, s.DiveStartRange);
                float endSlope = 1.7320508f, start = s.DiveStartRange;
                float a = (s.DiveStartSlope + endSlope - 2f * s.DiveStartHeight / start) / (start * start);
                float b = (3f * s.DiveStartHeight / start - s.DiveStartSlope - 2f * endSlope) / start;
                float wanted = r * (endSlope + r * (b + r * a));
                float ahead = Mathf.Max(0f, r - new Vector3(velocity.x, 0f, velocity.z).magnitude * .4f);
                float slope = endSlope + ahead * (2f * b + 3f * a * ahead);
                float down = slope + (height - wanted) / Mathf.Max(400f, speed * .5f);
                Vector3 path = (flat - Vector3.up * down).normalized;
                // The final recovery margin resolves any remaining path error
                // onto the real native aimpoint, while the established dive
                // slope supplies the arrival angle. No synthetic impact point.
                float finalLead = s.TerminalRecovery ? Mathf.SmoothStep(0f, 1f,
                    Mathf.InverseLerp(s.RecoveryHorizon + .5f, 1f, s.TimeToTarget)) : 0f;
                return Vector3.Slerp(path, nativeLead.normalized, finalLead).normalized;
            }
            // Cruise around 27.5 km, with vertical-velocity damping. The 25-30
            // km band leaves room for loaded diagonal maneuvers on either side.
            float heightError = 27500f - altitude - velocity.y * 2f;
            return (flat + Vector3.up * Mathf.Clamp(heightError / 6000f, -.3f, .3f)).normalized;
        }

        internal static bool NeedsRecovery(LanceManeuverState s, Vector3 lead, Vector3 velocity, Vector3 targetVelocity)
        {
            // Exactly 1.5 seconds of continued evasion before the curvature
            // check. Do not extend this margin by assuming that the old pull
            // also continues throughout a subsequent recovery bank/load-up.
            // Actual steering still retains its finite roll and load limits;
            // this check is not a guarantee of a recoverable interception.
            s.RecoveryHorizon = 1.5f;
            s.RecoveryDemandG = 0f;
            Vector3 acceleration = s.SteeringG * (float)LanceFlightModel.G;
            // Check now and through 1.5 further seconds of the current pull.
            // The bounded-curvature circle is recomputed from current physics,
            // not a fixed seconds-to-impact taper or a terminal speed command.
            for (int i = 0; i <= 4; i++)
            {
                float t = s.RecoveryHorizon * i / 4f;
                Vector3 delta = lead + (targetVelocity - velocity) * t - acceleration * (.5f * t * t);
                Vector3 futureVelocity = velocity + acceleration * t;
                float distance = delta.magnitude;
                if (distance < 1f || Vector3.Dot(delta, futureVelocity) <= 0f) return true;
                float sine = Vector3.Cross(futureVelocity.normalized, delta / distance).magnitude;
                float needed = 2f * futureVelocity.sqrMagnitude * sine / (distance * (float)LanceFlightModel.G);
                s.RecoveryDemandG = Mathf.Max(s.RecoveryDemandG, needed);
            }
            return s.RecoveryDemandG >= s.AvailableG;
        }
    }
}
