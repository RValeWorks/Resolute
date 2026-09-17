using UnityEngine;

namespace Resolute
{
    internal static class RcsJetLayout
    {
        internal static void Get(int index, float center, float bankDistance, float radius, out Vector3 point, out Vector3 direction)
        {
            if (index < 8)
            {
                float angle = (45f + (index % 4) * 90f) * Mathf.Deg2Rad;
                direction = new Vector3(Mathf.Cos(angle), Mathf.Sin(angle), 0f);
                point = direction * radius + Vector3.forward * (center + (index < 4 ? bankDistance : -bankDistance));
                return;
            }
            int pair = index - 8;
            float side = pair < 2 ? -1f : 1f;
            float vertical = pair % 2 == 0 ? 1f : -1f;
            float spacing = Mathf.Min(.045f, radius * .18f);
            point = new Vector3(side * Mathf.Sqrt(radius * radius - spacing * spacing), vertical * spacing, center);
            direction = new Vector3(side, -vertical, 0f).normalized;
        }
    }
}
