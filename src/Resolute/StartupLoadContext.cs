using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using BepInEx.Logging;
using Cysharp.Threading.Tasks;

namespace Resolute
{
    // Cooperative startup only. Unity objects never leave the game thread.
    // A null context in the existing diagnostic adapters performs no waits.
    internal sealed class StartupLoadContext
    {
        private const double FrameBudgetMilliseconds = 8;
        private readonly CancellationToken cancellation;
        private readonly ManualLogSource log;
        private readonly int thread;
        private readonly Stopwatch total = Stopwatch.StartNew(), slice = Stopwatch.StartNew();
        private readonly List<object> phases = new List<object>();
        private bool forceFrame = true;
        internal string Title { get; private set; } = "Preparing Resolute";
        internal string Detail { get; private set; } = "Waiting for game assets";
        internal int Stage { get; private set; }
        internal const int StageCount = 5;
        internal bool Finished { get; private set; }
        internal bool Failed { get; private set; }
        internal int YieldedFrames { get; private set; }
        internal double LongestWorkSliceMilliseconds { get; private set; }

        internal StartupLoadContext(CancellationToken cancellation, ManualLogSource log)
        { this.cancellation = cancellation; this.log = log; thread = Thread.CurrentThread.ManagedThreadId; }

        internal void Phase(int stage, string title)
        {
            Stage = stage; Title = title; Detail = ""; forceFrame = true;
            phases.Add(new { stage, title, elapsedMilliseconds = total.ElapsedMilliseconds });
            log?.LogInfo("Resolute loading " + stage + "/" + StageCount + ": " + title + ".");
        }

        internal async UniTask Step(string label)
        {
            cancellation.ThrowIfCancellationRequested();
            if (Thread.CurrentThread.ManagedThreadId != thread)
                throw new InvalidOperationException("Resolute startup work left the game thread.");
            Detail = label;
            if (!forceFrame && slice.Elapsed.TotalMilliseconds < FrameBudgetMilliseconds) return;
            LongestWorkSliceMilliseconds = Math.Max(LongestWorkSliceMilliseconds, slice.Elapsed.TotalMilliseconds);
            forceFrame = false;
            await UniTask.NextFrame(cancellationToken: cancellation);
            YieldedFrames++;
            slice.Restart();
        }

        internal void Complete()
        {
            LongestWorkSliceMilliseconds = Math.Max(LongestWorkSliceMilliseconds, slice.Elapsed.TotalMilliseconds);
            Finished = true; Title = "Resolute ready"; Detail = "Both ship outfits are available";
            log?.LogInfo("Resolute loading complete: " + total.ElapsedMilliseconds + " ms, " + YieldedFrames +
                " frame yields; longest measured work slice " + LongestWorkSliceMilliseconds.ToString("F1") + " ms.");
        }

        internal void Fail(Exception error)
        {
            Finished = true; Failed = true; Title = "Resolute could not load";
            Detail = "Resolute is unavailable. Check the game log for details.";
            log?.LogError("Resolute startup failed during " + Stage + "/" + StageCount + ": " + error);
        }

        internal object Capture() => new { Stage, stageCount = StageCount, Title, Detail, Finished, Failed,
            YieldedFrames, LongestWorkSliceMilliseconds, elapsedMilliseconds = total.ElapsedMilliseconds, phases = phases.ToArray() };
    }
}
