using System.Collections.Generic;
using System.Reflection;
using GHPC.Weapons;
using HarmonyLib;
using UnityEngine;

namespace HitCam
{
    // The shot data is final when the game has finished reporting the shot, which LiveRound does in
    // DoUpdateImpl once impactTime + 0.5 s has passed. doDestroy - the older trigger - can sit seconds
    // longer on that same round, because it only runs on detonation, on the speed and age limits or at
    // mission end, so the replay is taken from the report instead and doDestroy is kept as the fallback
    // for a round that never reaches it.
    [HarmonyPatch(typeof(LiveRound), "DoUpdateImpl")]
    internal static class LiveRound_DoUpdateImpl
    {
        static readonly FieldInfo ImpactTime = AccessTools.Field(typeof(LiveRound), "_impactTime");
        static readonly FieldInfo Reported = AccessTools.Field(typeof(LiveRound), "_reported");
        static readonly HashSet<int> Fired = new HashSet<int>();

        internal static void Forget()
        {
            Fired.Clear();
        }

        // the round is gone, so its id has to be free again for a pooled round that reuses it
        internal static void ForgetRound(int id)
        {
            Fired.Remove(id);
        }

        static void Postfix(LiveRound __instance)
        {
            if (__instance == null || ImpactTime == null || Reported == null) return;

            float impact;
            bool reported;
            try
            {
                impact = (float)ImpactTime.GetValue(__instance);
                reported = (bool)Reported.GetValue(__instance);
            }
            catch { return; }

            // exactly the game's own report gate: 0.5 s after the first impact, timed on the same clock
            if (!reported || impact <= 0f) return;
            if (Time.timeSinceLevelLoad - impact < 0.5f) return;

            int id = __instance.GetInstanceID();
            if (Fired.Contains(id)) return;
            Fired.Add(id);

            var info = __instance.ShotInfo;
            if (info != null && info.Story == null) info.Story = __instance.Story;
            HitCamMod.OnShotTerminated(info);
        }
    }

    // Init is the launch: by the time it returns the round has its ID, its ShotInfo and the
    // IsPlayerShot flag filled in, which is the earliest moment the flight can be recorded
    [HarmonyPatch(typeof(LiveRound), "Init")]
    internal static class LiveRound_Init
    {
        static void Postfix(LiveRound __instance)
        {
            Prerecorder.OnRoundLaunched(__instance);
        }
    }

    // Fallback for the rounds that were destroyed before their report window closed, e.g. when the
    // mission ends. _didDestroy going false -> true is the one signal that the round really ended.
    [HarmonyPatch(typeof(LiveRound), "doDestroy")]
    internal static class LiveRound_DoDestroy
    {
        static readonly FieldInfo DidDestroy = AccessTools.Field(typeof(LiveRound), "_didDestroy");

        static void Prefix(LiveRound __instance, out bool __state)
        {
            __state = DidDestroy == null || (bool)DidDestroy.GetValue(__instance);
        }

        static void Postfix(LiveRound __instance, bool __state)
        {
            if (__instance == null) return;
            int id = __instance.GetInstanceID();
            LiveRound_DoUpdateImpl.ForgetRound(id);
            if (__state) return;
            if (DidDestroy == null || !(bool)DidDestroy.GetValue(__instance)) return;
            HitCamMod.OnShotTerminated(__instance.ShotInfo);
        }
    }
}
