using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using GHPC.Weapons;
using HarmonyLib;
using UnityEngine;
using ShellInfo = GHPC.Weapons.ShotInfo;

namespace HitCam
{
    // The window can only be opened after the round has struck, so by then the flight itself is
    // already over and no replay can show it - unless it was drawn while it happened. The recorder
    // renders the shell from a chase camera during its fraction of a second in the air into a short
    // ring of textures, and the window plays those frames back at a slower rate before handing over
    // to the live orbit view. Only pixels are being held back: Time.timeScale and the physics step
    // are never touched, so the battle keeps running at full speed behind the window.
    internal sealed class Prerecorder
    {
        public sealed class Recording
        {
            public ShellInfo Shot;
            public RenderTexture[] Textures;
            public int[] Order;
        }

        // one RenderTexture per recorded frame is the whole cost of the feature, so the ring is
        // sized from the tail the window will actually keep instead of being a fixed archive
        const int MaxTailFrames = 24;
        const int MinTailFrames = 3;
        const float RecordInterval = 1f / 40f;
        const float MaxFlight = 3f;

        // off to the side rather than straight behind: from directly astern the lens is inside the
        // shooter's own turret and the shell sits between the camera and its target, so the whole
        // shot collapses into one dot. Parallel to the flight line instead. Only the last breath
        // before the strike is kept, so this is the moment of impact rather than a ride from the
        // muzzle - and the lens tracks the shell itself, because at 1500 m/s a 12 degree window aimed
        // anywhere other than at the round shows nothing but empty sky for the whole clip.
        const float SideOffset = 18f;
        const float UpOffset = 5f;

        static readonly FieldInfo ImpactTime = AccessTools.Field(typeof(LiveRound), "_impactTime");

        static LiveRound tracked;
        static ShellInfo trackedShot;
        static Camera cam;
        static GameObject root;
        static RenderTexture[] ring;
        static Recording held;

        static int writeIndex;
        static int filled;
        static float clock;
        static float age;
        static Vector3 lastPosition;
        static bool hasLastPosition;
        static int loggedRounds;

        // the player camera is found by a scene wide search, which is far too much to pay forty
        // times a second - the mask is taken once per round instead
        static int surfaceMaskCache;

        // called from the LiveRound.Init postfix, i.e. once per launched round
        public static void OnRoundLaunched(LiveRound round)
        {
            if (round == null || tracked != null) return;
            if (!HitCamMod.EnabledNow || !HitCamMod.PreRollOn) return;

            var info = round.ShotInfo;
            if (info == null || !info.IsPlayerShot) return;
            if (round.ParentRound != null || round.IsSpall) return;

            EnsureCamera();
            if (cam == null) return;

            tracked = round;
            trackedShot = info;
            surfaceMaskCache = ReplaySession.SurfaceMask();
            writeIndex = 0;
            filled = 0;
            clock = 0f;
            age = 0f;
            hasLastPosition = false;
            probeNext = HitCamMod.DebugLogging;
            if (++loggedRounds <= 20)
                HitCamMod.Log("prerec: following the round up " + round.GetInstanceID());
        }

        public static void Tick(float deltaTime)
        {
            var round = tracked;
            if (round == null) return;
            if (!HitCamMod.EnabledNow || !HitCamMod.PreRollOn) { Stop("mod off", false); return; }

            age += deltaTime;
            clock += deltaTime;
            if (clock >= RecordInterval)
            {
                clock = clock % RecordInterval;
                Render(round);
            }

            string ended = EndReason(round);
            if (ended != null) Stop(ended, true);
        }

        static void Stop(string reason, bool keepFrames)
        {
            // the caller may only ever ask for the kept tail, so the cap has to be decided here
            int all = filled;
            int frames = TailFrames(all);
            tracked = null;
            if (!keepFrames || frames < MinTailFrames || trackedShot == null || ring == null)
            {
                trackedShot = null;
                if (reason != null && loggedRounds <= 20) HitCamMod.Log("prerec: " + reason + " after " + frames + " frames");
                return;
            }

            // the ring is handed over whole, so the recorder has to allocate a fresh one for the
            // next round instead of writing under the window's feet
            var order = new int[frames];
            for (int i = 0; i < frames; i++)
                order[i] = (writeIndex - frames + i + slots * 2) % slots;

            // a recording nobody asked for must not hold onto its textures as well
            if (held != null) Recycle(held);

            held = new Recording { Shot = trackedShot, Textures = ring, Order = order };
            ring = null;
            trackedShot = null;
            if (loggedRounds <= 20)
                HitCamMod.Log("prerec: " + reason + " after " + all + " frames, kept " + frames
                    + ", held for playback");
        }

        static int slots = MinTailFrames;

        static int TailFrameCount()
        {
            float seconds = HitCamMod.PreRollTailSeconds;
            if (seconds < 0.02f) seconds = 0.02f;
            if (seconds > 0.6f) seconds = 0.6f;
            int cap = Mathf.CeilToInt(seconds / RecordInterval);
            if (cap < MinTailFrames) cap = MinTailFrames;
            if (cap > MaxTailFrames) cap = MaxTailFrames;
            return cap;
        }

        // the flight is over in a fifth of a second, yet the ring runs for as long as the round
        // flies; the window only wants the moment of the strike, so the older slots are dropped
        // here rather than at playback
        static int TailFrames(int filledFrames)
        {
            int cap = TailFrameCount();
            return filledFrames > cap ? cap : filledFrames;
        }

        static string EndReason(LiveRound round)
        {
            // a pooled round is switched off rather than destroyed when it ends, and the launch can
            // also see it while it is still inactive, so only a real destruction or the flight
            // limits may cut the recording short
            if (round == null || round.gameObject == null) return "round gone";
            if (ImpactTime != null)
            {
                try
                {
                    // the first impact is exactly the frame the window must not show - everything
                    // after it comes from the live view
                    if ((float)ImpactTime.GetValue(round) > 0f) return "impact";
                }
                catch { return "impact time unreadable"; }
            }
            if (age > MaxFlight) return "flight too long";
            return null;
        }

        static void Render(LiveRound round)
        {
            try
            {
                if (!round.gameObject.activeInHierarchy) return;

                EnsureRing(HitCamMod.WindowWidth, HitCamMod.WindowHeight);
                if (ring == null) { Stop("no record buffer", false); return; }

                Vector3 position = round.transform.position;
                Vector3 travel = hasLastPosition && (position - lastPosition).sqrMagnitude > 0.0004f
                    ? position - lastPosition
                    : round.transform.forward;
                lastPosition = position;
                hasLastPosition = true;
                if (travel.sqrMagnitude < 0.0001f) return;
                travel.Normalize();

                Vector3 side = Vector3.Cross(Vector3.up, travel);
                if (side.sqrMagnitude < 0.0001f) side = Vector3.right;
                side.Normalize();

                cam.fieldOfView = Mathf.Clamp(HitCamMod.PreRollFieldOfView, 4f, 60f);
                cam.cullingMask = surfaceMaskCache | (1 << ProjectileLayer());

                root.transform.position = position + side * SideOffset + Vector3.up * UpOffset;
                root.transform.LookAt(position);

                RenderTexture frame = ring[writeIndex];
                bool probing = probeNext && HitCamMod.DebugLogging;
                if (probing) ClearTo(frame, Color.magenta);

                cam.targetTexture = frame;
                cam.Render();

                if (probing) Probe(frame, position);

                writeIndex = (writeIndex + 1) % slots;
                if (filled < slots) filled++;
            }
            catch (Exception e)
            {
                HitCamMod.Log("prerec render threw: " + e.Message);
                Stop("render error", false);
            }
        }

        // Every dumped recording frame so far came back byte-identical pure white, for different
        // slots of different rounds, which is what an untouched RenderTexture looks like rather than
        // a rendered picture. So one frame of one round in a debug build is cleared to magenta before
        // the camera runs: magenta surviving means the camera drew nothing at all, and anything else
        // is named by the nearest renderers the recorder's own mask would have let through.
        static bool probeNext;

        static void Probe(RenderTexture frame, Vector3 shell)
        {
            probeNext = false;
            try
            {
                Color mean = AverageColour(frame);
                Vector3 from = root.transform.position;
                Vector3 forward = root.transform.forward;
                HitCamMod.Log("prerec probe: mean " + mean.r.ToString("F2") + "," + mean.g.ToString("F2")
                    + "," + mean.b.ToString("F2") + "," + mean.a.ToString("F2")
                    + " | shell off-axis " + Vector3.Angle(forward, shell - from).ToString("F1")
                    + " deg at " + Vector3.Distance(from, shell).ToString("F0") + "m"
                    + " | fov " + cam.fieldOfView.ToString("F0") + " near " + cam.nearClipPlane.ToString("F1")
                    + " far " + cam.farClipPlane.ToString("F0")
                    + " | mask 0x" + cam.cullingMask.ToString("X")
                    + " | drawn nearby: " + NearestDrawn(from, cam.cullingMask));
            }
            catch (Exception e)
            {
                HitCamMod.Log("prerec probe threw: " + e.Message);
            }
        }

        // Two controls taken at the moment the window wants to show a recorded frame, because three
        // rounds of guessing have each been falsified by the next log and the only way to stop guessing
        // is to measure something that cannot be misread: clear a scratch texture through GL to a
        // colour that has no grey channel and read it back (if that read comes white the read path
        // itself is blind and no pixel judgement means anything), then render this camera into a
        // texture built exactly like a ring slot and read that back too.
        public static void CapturePulse(out Color glClearRead, out Color cameraRead)
        {
            glClearRead = new Color(-1f, -1f, -1f, -1f);
            cameraRead = new Color(-1f, -1f, -1f, -1f);
            var control = new RenderTexture(16, 16, 0);
            control.Create();
            try { ClearTo(control, new Color(1f, 0f, 1f, 1f)); glClearRead = AverageColour(control); }
            catch (Exception e) { HitCamMod.Log("prerec control threw: " + e.Message); }
            finally { control.Release(); UnityEngine.Object.Destroy(control); }

            if (cam == null) return;
            var scratch = new RenderTexture(HitCamMod.WindowWidth, HitCamMod.WindowHeight, 16);
            scratch.antiAliasing = 1;
            scratch.Create();
            RenderTexture previousTarget = cam.targetTexture;
            try
            {
                cam.targetTexture = scratch;
                cam.Render();
                cameraRead = AverageColour(scratch);
            }
            catch (Exception e) { HitCamMod.Log("prerec pulse threw: " + e.Message); }
            finally
            {
                cam.targetTexture = previousTarget;
                scratch.Release();
                UnityEngine.Object.Destroy(scratch);
            }
        }

        public static Color AverageColourOf(RenderTexture source)
        {
            return AverageColour(source);
        }


        static void ClearTo(RenderTexture target, Color colour)
        {
            if (target == null) return;
            var previous = RenderTexture.active;
            try
            {
                Graphics.SetRenderTarget(target);
                GL.Clear(true, true, colour);
            }
            finally
            {
                Graphics.SetRenderTarget(previous);
                RenderTexture.active = previous;
            }
        }

        static Color AverageColour(RenderTexture source)
        {
            var tiny = new RenderTexture(1, 1, 0);
            tiny.Create();
            var read = new Texture2D(1, 1, TextureFormat.RGBA32, false);
            var previous = RenderTexture.active;
            try
            {
                Graphics.Blit(source, tiny);
                RenderTexture.active = tiny;
                read.ReadPixels(new Rect(0, 0, 1, 1), 0, 0);
                read.Apply();
                return read.GetPixel(0, 0);
            }
            finally
            {
                RenderTexture.active = previous;
                UnityEngine.Object.Destroy(read);
                tiny.Release();
                UnityEngine.Object.Destroy(tiny);
            }
        }

        static string NearestDrawn(Vector3 from, int mask)
        {
            Renderer[] all;
            try { all = GameObject.FindObjectsOfType<Renderer>(); }
            catch { return "unreadable"; }

            var nearest = new List<KeyValuePair<float, string>>(7);
            for (int i = 0; i < all.Length; i++)
            {
                var renderer = all[i];
                if (renderer == null || !renderer.enabled || !renderer.gameObject.activeInHierarchy) continue;
                int layer = renderer.gameObject.layer;
                if (layer < 0 || layer > 31 || (mask & (1 << layer)) == 0) continue;
                float d = Vector3.Distance(from, renderer.bounds.center);
                if (d > 300f) continue;
                if (nearest.Count == 6 && d >= nearest[5].Key) continue;
                nearest.Add(new KeyValuePair<float, string>(d, renderer.name + "(" + layer + ")@"
                    + d.ToString("F0") + "m"));
                nearest.Sort(delegate(KeyValuePair<float, string> a, KeyValuePair<float, string> b)
                { return a.Key.CompareTo(b.Key); });
                if (nearest.Count > 6) nearest.RemoveAt(6);
            }

            if (nearest.Count == 0) return "nothing";
            var sb = new StringBuilder();
            for (int i = 0; i < nearest.Count; i++)
            {
                if (i > 0) sb.Append(' ');
                sb.Append(nearest[i].Value);
            }
            return sb.ToString();
        }

        // the window asks for the frames of the shot it is about to show
        public static Recording Claim(ShellInfo shot)
        {
            var recording = held;
            if (recording == null) return null;
            if (recording.Shot != shot)
            {
                Recycle(recording);
                held = null;
                return null;
            }
            held = null;
            return recording;
        }

        public static void Release(Recording recording)
        {
            if (recording == null) return;
            Recycle(recording);
        }

        static void Recycle(Recording recording)
        {
            if (recording == null || recording.Textures == null) return;
            if (ring == null)
            {
                // only a ring of the current window size is worth keeping, EnsureRing would
                // rebuild it anyway
                ring = recording.Textures;
                recording.Textures = null;
                return;
            }
            // a second ring is already in use - give the memory back rather than hold two
            DestroyTextures(recording.Textures);
            recording.Textures = null;
        }

        static void EnsureCamera()
        {
            if (cam != null) return;
            root = new GameObject("HitCam_Recorder");
            cam = root.AddComponent<Camera>();
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = Color.black;
            cam.orthographic = false;
            cam.nearClipPlane = 0.1f;
            // the same reason the window tightens its own far plane: everything the environment layers
            // do not hide lives within a few tens of metres, and a long reach lets distant geometry in
            cam.farClipPlane = 90f;
            cam.allowHDR = false;
            cam.allowMSAA = false;
            // never enabled, exactly like the two window cameras: the recorder drives its own renders
            cam.enabled = false;
        }

        static void EnsureRing(int width, int height)
        {
            int want = TailFrameCount();
            if (ring != null && ring.Length == want
                && ring[0] != null && ring[0].width == width && ring[0].height == height) return;
            DestroyRing();
            ring = new RenderTexture[want];
            for (int i = 0; i < want; i++)
            {
                var texture = new RenderTexture(width, height, 16);
                texture.antiAliasing = 1;
                texture.Create();
                ring[i] = texture;
            }
            slots = want;
            writeIndex = 0;
            filled = 0;
            HitCamMod.Log("prerec: ring of " + want + " at " + width + "x" + height);
        }

        static void DestroyRing()
        {
            if (ring == null) return;
            for (int i = 0; i < ring.Length; i++)
            {
                if (ring[i] == null) continue;
                ring[i].Release();
                UnityEngine.Object.Destroy(ring[i]);
            }
            ring = null;
        }

        // the shells are kept off every game camera by their own layer; pointing the recorder back
        // at it is what makes the dart visible in the window
        static int ProjectileLayer()
        {
            int layer = LayerMask.NameToLayer("Projectiles Only");
            return layer >= 0 && layer < 32 ? layer : 7;
        }

        public static void Abandon()
        {
            tracked = null;
            trackedShot = null;
            if (held != null)
            {
                DestroyTextures(held.Textures);
                held = null;
            }
            DestroyRing();
            if (root != null) UnityEngine.Object.Destroy(root);
            root = null;
            cam = null;
            writeIndex = 0;
            filled = 0;
            hasLastPosition = false;
        }

        static void DestroyTextures(RenderTexture[] textures)
        {
            if (textures == null) return;
            for (int i = 0; i < textures.Length; i++)
            {
                if (textures[i] == null) continue;
                textures[i].Release();
                UnityEngine.Object.Destroy(textures[i]);
            }
        }
    }
}
