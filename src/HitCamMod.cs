using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using GHPC;
using ShellInfo = GHPC.Weapons.ShotInfo;
using MelonLoader;
using UnityEngine;

[assembly: MelonInfo(typeof(HitCam.HitCamMod), "命中回放小窗", "1.0.4", "rhino2024")]
[assembly: MelonGame("Radian Simulations LLC", "GHPC")]

namespace HitCam
{
    public sealed class HitCamMod : MelonMod
    {
        sealed class Request
        {
            public ShellInfo Shot;
            public IUnit Victim;
        }

        static MelonPreferences_Category cat;
        static MelonPreferences_Entry<bool> eEnabled, eInterior, eInfoText, eFlightPath, eDebugLog;
        static MelonPreferences_Entry<bool> eBlackBackground, ePreRoll, eColorByHealth;
        static MelonPreferences_Entry<float> eDuration, eOrbitSpeed, eFov, eElevation, eDistanceScale, eFlightLeg;
        static MelonPreferences_Entry<float> ePreRollFov, ePreRollFps, ePreRollTail;
        static MelonPreferences_Entry<int> eWidth, eHeight, eMinCaliber, eMargin, eConfigRevision;
        static MelonPreferences_Entry<KeyCode> eToggle;

        static Request pending;
        static ShellInfo lastAccepted;
        static ReplaySession active;
        static StreamWriter log;
        static int loggedRejections;

        public static bool EnabledNow { get { return eEnabled != null && eEnabled.Value; } }
        public static float Duration { get { return eDuration.Value; } }
        public static float OrbitSpeed { get { return eOrbitSpeed.Value; } }
        public static float FieldOfView { get { return eFov.Value; } }
        public static float Elevation { get { return eElevation.Value; } }
        public static float DistanceScale { get { return eDistanceScale.Value; } }
        public static int WindowWidth { get { return eWidth.Value; } }
        public static int WindowHeight { get { return eHeight.Value; } }
        public static int Margin { get { return eMargin.Value; } }
        public static bool ShowInterior { get { return eInterior.Value; } }
        public static bool ShowInfoText { get { return eInfoText.Value; } }
        public static bool DebugLogging { get { return eDebugLog != null && eDebugLog.Value; } }
        public static bool ShowFlightPath { get { return eFlightPath.Value; } }
        public static float FlightLegMeters { get { return eFlightLeg.Value; } }
        public static bool BlackBackground { get { return eBlackBackground.Value; } }
        public static bool PreRollOn { get { return ePreRoll != null && ePreRoll.Value; } }
        public static float PreRollFieldOfView { get { return ePreRollFov.Value; } }
        public static float PreRollPlayFps { get { return ePreRollFps.Value; } }
        public static float PreRollTailSeconds { get { return ePreRollTail.Value; } }
        public static bool TintByHealth { get { return eColorByHealth.Value; } }

        public override void OnInitializeMelon()
        {
            cat = MelonPreferences.CreateCategory("HitCam", "命中回放小窗");
            eEnabled = cat.CreateEntry<bool>("Enabled", true, "Master switch");
            eDuration = cat.CreateEntry<float>("Duration", 3f, "Seconds the replay window stays open");
            eWidth = cat.CreateEntry<int>("WindowWidth", 640, "Replay texture width");
            eHeight = cat.CreateEntry<int>("WindowHeight", 360, "Replay texture height");
            eMargin = cat.CreateEntry<int>("Margin", 24, "Distance from the top right corner, in pixels");
            eMinCaliber = cat.CreateEntry<int>("MinCaliberMm", 40, "Hide the window for shells below this caliber");
            eOrbitSpeed = cat.CreateEntry<float>("OrbitSpeed", 26f, "Camera orbit speed, degrees per second");
            eElevation = cat.CreateEntry<float>("Elevation", 10f, "Camera elevation angle, degrees above the target");
            eDistanceScale = cat.CreateEntry<float>("DistanceScale", 0.72f, "Multiplier on how far the replay camera sits from the target");
            eFov = cat.CreateEntry<float>("FieldOfView", 24f, "Replay camera field of view");
            eInterior = cat.CreateEntry<bool>("ShowInterior", true, "Highlight the struck vehicle's AarVisual interior art in the window");
            eInfoText = cat.CreateEntry<bool>("ShowInfoText", true, "Show ammo / distance / penetration caption");
            eFlightPath = cat.CreateEntry<bool>("ShowFlightPath", false, "Show the long gun-to-target flight trace line");
            eFlightLeg = cat.CreateEntry<float>("FlightLegMeters", 25f, "Trace legs longer than this are the flight path and get hidden");
            eBlackBackground = cat.CreateEntry<bool>("BlackBackground", true, "Hide terrain, trees and sky in the window so only the struck vehicle and its after-effects are left, on black");
            ePreRoll = cat.CreateEntry<bool>("PreRoll", true, "Record the shell while it is still in the air and play those frames back slowly before the live orbit view. The world itself never slows down.");
            ePreRollFps = cat.CreateEntry<float>("PreRollFps", 12f, "Recorded frames shown per second in the window. Lower is slower motion; the flight is recorded at 40 fps.");
            ePreRollFov = cat.CreateEntry<float>("PreRollFov", 30f, "Field of view of the in-flight recorder camera, degrees. The lens tracks the shell, so this is how much of the surroundings stays in the frame around it");
            ePreRollTail = cat.CreateEntry<float>("PreRollTail", 0.2f, "How much of the flight the window plays back, seconds counted back from the strike. The rest of the shot is dropped, so the clip is the moment of impact rather than a ride from the muzzle.");
            eColorByHealth = cat.CreateEntry<bool>("ColorByHealth", true, "Tint the lit interior modules and the crew by their remaining health: green when whole, red when hurt, black-red when destroyed");
            // F9 belongs to the perf probe, and both mods read their own key with plain Input, so sharing
            // it silently switched this mod off whenever the player pressed F9 to watch frame timings.
            eToggle = cat.CreateEntry<KeyCode>("ToggleKey", KeyCode.F12, "Toggles the mod at runtime");
            eDebugLog = cat.CreateEntry<bool>("DebugLog", false, "Write diagnostic lines to Bin/Mods/HitCam.debug.log; only turn this on when something is wrong");
            eConfigRevision = cat.CreateEntry<int>("ConfigRevision", 0, "Do not edit; tracks one-time config migrations");

            // 1.0.0 shipped an 800x450 window. It costs a second full-scene render, so existing
            // configs move to the smaller default once instead of keeping the old size forever.
            if (eConfigRevision.Value < 1)
            {
                if (eWidth.Value == 800 && eHeight.Value == 450)
                {
                    eWidth.Value = 640;
                    eHeight.Value = 360;
                }
                eConfigRevision.Value = 1;
                try { cat.SaveToFile(false); } catch (Exception e) { Log("saving the config migration threw: " + e.Message); }
            }

            if (eDebugLog.Value) OpenLog();
            MelonLogger.Msg("Hit Cam loaded (debug log " + (eDebugLog.Value ? "on" : "off") + ")");
            Log("Hit Cam initialised (patch target: LiveRound.doDestroy)  enabled=" + eEnabled.Value
                + "  toggle key=" + eToggle.Value);
            SelfCheck();
        }

        // silent failure is the worst outcome here: both members are private, so a
        // game update that renames them disables the mod without any exception
        void SelfCheck()
        {
            if (HarmonyLib.AccessTools.Field(typeof(GHPC.Weapons.LiveRound), "_didDestroy") == null)
                Log("SELF-CHECK FAIL: LiveRound._didDestroy missing, hook will never fire");
            if (HarmonyLib.AccessTools.Method(typeof(AarController), "createShotTrace") == null)
                Log("SELF-CHECK FAIL: AarController.createShotTrace missing, no impact visuals");
            if (AarController.Instance == null)
                Log("note: no AarController in scene yet (normal until a mission loads)");
        }

        public override void OnUpdate()
        {
            AarProbe.Tick(Time.unscaledDeltaTime);

            if (Input.GetKeyDown(eToggle.Value))
            {
                eEnabled.Value = !eEnabled.Value;
                eEnabled.Save();
                Log("toggled " + (eEnabled.Value ? "ON" : "OFF"));
                if (!eEnabled.Value)
                {
                    StopActive();
                    Prerecorder.Abandon();
                }
            }

            // the flight only lasts a fraction of a second, so the recorder has to run every frame
            // of the shot, long before it is known whether the round will strike a vehicle
            Prerecorder.Tick(Time.unscaledDeltaTime);

            // a newer hit takes over the open window instead of queueing behind its remaining seconds
            if (pending != null && eEnabled.Value)
            {
                var request = pending;
                pending = null;
                if (active != null)
                {
                    active.Dispose();
                    active = null;
                    Log("window replaced by a newer hit");
                }
                active = ReplaySession.TryStart(request.Shot, request.Victim);
                Log(active != null ? "window opened" : "window FAILED to open");
                return;
            }

            if (active != null && !active.Update(Time.unscaledDeltaTime))
            {
                active.Dispose();
                active = null;
                Log("window closed");
            }
        }

        public override void OnSceneWasLoaded(int buildIndex, string sceneName)
        {
            StopActive();
            pending = null;
            lastAccepted = null;
            Prerecorder.Abandon();
            LiveRound_DoUpdateImpl.Forget();
            ReplaySession.ForgetProbes();
            Log("scene loaded: " + sceneName);
        }

        void StopActive()
        {
            if (active == null) return;
            active.Dispose();
            active = null;
        }

        internal static void OnShotTerminated(ShellInfo shot)
        {
            if (shot == null) return;
            // the report and the destroy trigger can both see one round
            if (shot == lastAccepted) return;

            string rejection = Reject(shot);
            IUnit victim = rejection == null ? FindVictim(shot) : null;
            if (victim == null && rejection == null) rejection = "no hostile vehicle struck";

            if (rejection != null)
            {
                if (loggedRejections++ < 40) Log("skip (" + loggedRejections + "): " + rejection + " | " + Describe(shot));
                return;
            }

            Log("hit accepted -> " + Describe(shot) + " vs " + victim.FriendlyName);
            lastAccepted = shot;
            pending = new Request { Shot = shot, Victim = victim };
        }

        static string Reject(ShellInfo shot)
        {
            if (!eEnabled.Value) return "mod disabled";
            if (shot.ParentShot != null) return "child shot (spall)";
            if (!shot.IsPlayerShot) return "not the player's round";
            if (!shot.IsHit) return "no hit recorded";
            if (shot.TypeInfo == null) return "ammo type missing";
            float caliber = shot.TypeInfo.GetFinalCaliber();
            if (caliber < eMinCaliber.Value) return "caliber " + caliber.ToString("0") + "mm < " + eMinCaliber.Value;
            return null;
        }

        static IUnit FindVictim(ShellInfo shot)
        {
            if (shot.HitUnits == null) return null;
            var shooter = shot.Shooter;
            for (int i = 0; i < shot.HitUnits.Count; i++)
            {
                var unit = shot.HitUnits[i];
                if (unit == null || unit.Chassis == null) continue;
                if (shooter != null && unit.Allegiance == shooter.Allegiance) continue;
                return unit;
            }
            return null;
        }

        static string Describe(ShellInfo shot)
        {
            if (shot == null) return "<null>";
            string ammo = shot.TypeInfo != null ? shot.TypeInfo.Name : "?";
            return ammo + " d=" + shot.Distance.ToString("0") + "m pen=" + shot.ActualPen.ToString("0") + "mm";
        }

        static void OpenLog()
        {
            try
            {
                string dir = Path.Combine(Path.GetDirectoryName(Application.dataPath), "Mods");
                log = new StreamWriter(Path.Combine(dir, "HitCam.debug.log"), true, new UTF8Encoding(false)) { AutoFlush = true };
            }
            catch { log = null; }
        }

        internal static void Log(object message)
        {
            if (log == null) return;
            try { log.WriteLine(DateTime.Now.ToString("HH:mm:ss.fff") + " " + message); }
            catch { }
        }
    }
}
