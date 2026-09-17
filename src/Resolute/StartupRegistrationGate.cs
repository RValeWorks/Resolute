using System;
using System.Threading;
using Cysharp.Threading.Tasks;

namespace Resolute
{
    // Multiple preload callers share one build. A caller's canceled wait must
    // not dispose assets still being prepared for another caller.
    internal sealed class StartupRegistrationGate
    {
        private UniTask shared;
        internal bool Started { get; private set; }
        internal bool Finished { get; private set; }
        internal Exception Failure { get; private set; }

        internal UniTask Ensure(Func<UniTask> work, CancellationToken cancellation)
        {
            if (!Started)
            {
                Started = true;
                shared = Run(work).Preserve();
            }
            return shared.AttachExternalCancellation(cancellation);
        }

        private async UniTask Run(Func<UniTask> work)
        {
            try { await work(); }
            catch (Exception error) { Failure = error; throw; }
            finally { Finished = true; }
        }
    }
}
