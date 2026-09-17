namespace Resolute
{
    // Command authorization belongs to the base command executor. This bridge
    // reserves ordinary Pike formations and preserves the selected objective;
    // it never grants sensor visibility, resets a seeker or fires a weapon.
    internal static class ResoluteManualPikeOrders
    {
        internal static bool Prepare(Ship owner, Unit target, int count, int orderId, int[] replaceOrderIds = null)
            => ResoluteStrikeOrders.CommitManual(owner, target, count, orderId, replaceOrderIds);

        internal static void RegisterLaunched(Ship owner, Missile missile, Unit commandedTarget, int orderId)
            => ResoluteStrikeOrders.RegisterManual(owner, missile, commandedTarget, orderId);

        internal static bool PreparePosition(Ship owner, GlobalPosition point, int count, int orderId, int[] replaceOrderIds = null)
            => ResoluteStrikeOrders.CommitManualPosition(owner, point, count, orderId, replaceOrderIds);

        internal static void RegisterLaunchedPosition(Ship owner, Missile missile, GlobalPosition point, int orderId)
            => ResoluteStrikeOrders.RegisterManualPosition(owner, missile, point, orderId);

        internal static void CancelPending(Ship owner, int orderId = 0)
            => ResoluteStrikeOrders.CancelManual(owner, orderId);

        internal static bool IsManualMissile(Missile missile)
            => ResoluteStrikeOrders.IsManualMissile(missile);

        internal static bool AllowsTarget(Missile missile, Unit target)
            => ResoluteStrikeOrders.ManualTargetAllowed(missile, target);
    }
}
