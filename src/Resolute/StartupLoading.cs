using System;
using System.Collections;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Cysharp.Threading.Tasks;
using HarmonyLib;
using UnityEngine;

namespace Resolute
{
    [HarmonyPatch(typeof(Encyclopedia), nameof(Encyclopedia.Preload))]
    internal static class ResoluteStartupPreloadPatch
    {
        private static void Postfix(CancellationToken cancel, ref UniTask __result)
        { __result = Complete(__result, cancel); }

        internal static async UniTask Complete(UniTask native, CancellationToken cancel)
        {
            await native;
            cancel.ThrowIfCancellationRequested();
            if (Plugin.Instance != null)
                await Plugin.Instance.EnsureRegisteredAsync(Encyclopedia.i, cancel);
        }
    }

    // Native networking sends numeric encyclopedia indices. Keep final mod
    // registration ordered even when bundle scan speed differs between PCs.
    // Blueprinter's scan and patch work still run through their normal flow.
    [HarmonyPatch]
    internal static class ResoluteBlueprinterRegistrationOrderPatch
    {
        private static MethodBase TargetMethod()
        {
            Type type = AccessTools.TypeByName("Blueprinter.PatchRunner");
            MethodInfo method = type != null ? AccessTools.Method(type, "ApplyAllPatchesCoroutine", Type.EmptyTypes) : null;
            return method != null && method.ReturnType == typeof(IEnumerator) ? method : null;
        }
        private static bool Prepare() => TargetMethod() != null;
        private static void Postfix(ref IEnumerator __result)
        {
            if (__result != null && Plugin.Instance != null && !Plugin.Instance.SynchronousStartup)
                __result = WaitForRegistration(__result);
        }
        internal static IEnumerator WaitForRegistration(IEnumerator routine)
        {
            try
            {
                while (Plugin.Instance != null && !MainMenu.ApplicationIsQuitting && routine.MoveNext())
                    yield return routine.Current;

                // Do not start registration here: an empty/fast Blueprinter
                // load can arrive while native AfterLoad is still initializing
                // its collections. Encyclopedia.Preload owns Resolute's build.
                while (Plugin.Instance != null && !MainMenu.ApplicationIsQuitting &&
                    (Plugin.Instance.Loading == null || !Plugin.Instance.Loading.Finished))
                    yield return null;
            }
            finally { (routine as IDisposable)?.Dispose(); }
        }
    }

    // Keep Blueprinter's normal bundle scan, patches and existing loading UI
    // running alongside native preloads and Resolute. Only its temporary
    // mission must wait: opening it earlier destroys the awaiting main menu.
    [HarmonyPatch]
    internal static class ResoluteBlueprinterStartupOrderPatch
    {
        private static MethodBase TargetMethod()
        {
            Type type = AccessTools.TypeByName("Blueprinter.Plugin");
            MethodInfo method = type != null ? AccessTools.Method(type, "LoadAdditionalAssets", Type.EmptyTypes) : null;
            return method != null && method.ReturnType == typeof(Task<GameObject>) ? method : null;
        }
        private static bool Prepare() => TargetMethod() != null;
        private static bool invokingOriginal;
        private static bool NativeMenuReady => MainMenu.State == MainMenu.LoadingState.Loaded &&
            GameManager.gameState == GameState.Menu && SteamManager.ClientInitialized;

        private static bool Prefix(object __instance, ref Task<GameObject> __result)
        {
            if (invokingOriginal || Plugin.Instance == null || Plugin.Instance.SynchronousStartup || NativeMenuReady) return true;
            __result = WaitForMenu(() => {
                // Scope the bypass to reflection dispatch, never across await.
                // The original async method retains its own error handling.
                invokingOriginal = true;
                try { return (Task<GameObject>)((MethodInfo)TargetMethod()).Invoke(__instance, null); }
                finally { invokingOriginal = false; }
            }, () => __instance is UnityEngine.Object owner && owner != null);
            return false;
        }
        internal static async Task<GameObject> WaitForMenu(Func<Task<GameObject>> load, Func<bool> ownerAlive)
        {
            try
            {
                while (Plugin.Instance != null && ownerAlive() && !MainMenu.ApplicationIsQuitting &&
                    MainMenu.State != MainMenu.LoadingState.Loaded)
                    await UniTask.NextFrame();
                if (Plugin.Instance == null || !ownerAlive() || MainMenu.ApplicationIsQuitting) return null;

                // State becomes Loaded just before synchronous Steam/menu
                // setup. Resume on the next frame so that setup can finish.
                await UniTask.NextFrame();
                if (Plugin.Instance == null || !ownerAlive() || MainMenu.ApplicationIsQuitting) return null;
                if (!NativeMenuReady)
                {
                    Plugin.Instance.LogStartupWarning("Blueprinter additional assets skipped because native menu initialization did not finish.");
                    return null;
                }
                return await load();
            }
            catch (Exception error)
            {
                // Blueprinter consumes Task.Result without a catch. Match its
                // original null-on-failure contract instead of faulting it.
                Plugin.Instance?.LogStartupWarning("Blueprinter additional asset startup failed: " + error);
                return null;
            }
        }
    }

}
