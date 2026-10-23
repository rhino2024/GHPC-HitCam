using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using GHPC;
using HarmonyLib;
using UnityEngine;

namespace HitCam
{
    // Passive observer: records how the game itself makes the interior visible during the real
    // post-battle AAR, so the live replay window can copy the mechanism instead of guessing it.
    // Reads state only - no AAR method is patched, called, or reordered.
    internal static class AarProbe
    {
        static bool wasInAar;
        static float cooldown;
        static int dumps;

        public static void Tick(float deltaTime)
        {
            // the probe exists to fill the debug log; with the log off it must cost nothing
            if (!HitCamMod.DebugLogging) return;

            cooldown -= deltaTime;

            bool nowInAar = SafeInAar();
            if (nowInAar != wasInAar)
            {
                wasInAar = nowInAar;
                cooldown = 0f;
                if (nowInAar) dumps = 0;
                HitCamMod.Log("aarwatch: InAar -> " + nowInAar);
            }

            if (Input.GetKeyDown(KeyCode.F10)) Dump("manual");

            // a few samples spread over the first seconds of the AAR: models, layers and camera
            // masks are rebuilt as the replay starts, one snapshot can land between steps
            if (!nowInAar || cooldown > 0f || dumps >= 6) return;
            cooldown = 1.2f;
            Dump("aar t" + (dumps * 1.2f).ToString("0"));
        }

        static bool SafeInAar()
        {
            try { return AarController.InAar; }
            catch { return false; }
        }

        static void Dump(string why)
        {
            dumps++;
            try
            {
                HitCamMod.Log("==== aarwatch dump (" + why + ") ====");
                DumpLayers();
                DumpCameras();
                DumpAarModels();
                DumpAarVisuals();
            }
            catch (Exception e)
            {
                HitCamMod.Log("aarwatch threw: " + e.Message);
            }
        }

        static void DumpLayers()
        {
            var manager = ConstantsAndInfoManager.Instance;
            var sb = new StringBuilder();
            if (manager != null)
            {
                var fields = manager.GetType().GetFields(BindingFlags.Public | BindingFlags.Instance);
                for (int i = 0; i < fields.Length; i++)
                {
                    var field = fields[i];
                    if (field.FieldType != typeof(int)) continue;
                    if (field.Name.IndexOf("layer", StringComparison.OrdinalIgnoreCase) < 0) continue;
                    sb.Append(field.Name).Append('=').Append(field.GetValue(manager)).Append(' ');
                }
            }
            HitCamMod.Log("aarwatch manager: " + sb);
            HitCamMod.Log("aarwatch names: "
                + Describe(0) + ' ' + Describe(8) + ' ' + Describe(15) + ' ' + Describe(21));
        }

        static string Describe(int layer)
        {
            return layer + "=\"" + LayerMask.LayerToName(layer) + '"';
        }

        static void DumpCameras()
        {
            var cams = UnityEngine.Object.FindObjectsOfType<Camera>();
            var sb = new StringBuilder();
            for (int i = 0; i < cams.Length && i < 12; i++)
            {
                var c = cams[i];
                if (c == null) continue;
                sb.Append(c.name).Append(":m").Append(c.cullingMask)
                    .Append(":d").Append(c.depth.ToString("0"))
                    .Append(":c").Append(c.clearFlags)
                    .Append(":rt").Append(c.targetTexture != null ? 1 : 0)
                    .Append(":on").Append(c.enabled ? 1 : 0)
                    .Append(":act").Append(c.gameObject.activeInHierarchy ? 1 : 0).Append(' ');
            }
            HitCamMod.Log("aarwatch cameras (" + cams.Length + "): " + sb);
        }

        static void DumpAarModels()
        {
            var assets = Resources.FindObjectsOfTypeAll<AarModel>();
            var live = UnityEngine.Object.FindObjectsOfType<AarModel>();
            int active = 0, visible = 0;
            for (int i = 0; i < live.Length; i++)
            {
                if (live[i] == null) continue;
                if (live[i].gameObject.activeInHierarchy) active++;
                if (CountEnabled(RendererListOf(live[i]), true) > 0) visible++;
            }
            HitCamMod.Log("aarwatch AarModel: scene=" + live.Length + " assets=" + assets.Length
                + " activeInHierarchy=" + active + " rendererVisible=" + visible);

            AarModel sample = null;
            if (live.Length > 0) sample = live[0];
            else if (assets.Length > 0) sample = assets[0];
            if (sample == null) return;

            var renderers = RendererListOf(sample);
            HitCamMod.Log("aarwatch AarModel sample: " + PathOf(sample.transform)
                + " layer=" + sample.gameObject.layer + " renderers=" + renderers.Length
                + " enabled=" + CountEnabled(renderers, true) + " mats: " + MaterialsOf(renderers, 3));
        }

        static Renderer[] RendererListOf(AarModel model)
        {
            var field = AccessTools.Field(typeof(AarModel), "_renderers");
            var list = field != null ? field.GetValue(model) as System.Collections.IEnumerable : null;
            var found = new List<Renderer>();
            if (list != null)
                foreach (var item in list)
                {
                    var renderer = item as Renderer;
                    if (renderer != null) found.Add(renderer);
                }
            if (found.Count > 0) return found.ToArray();
            return model.GetComponentsInChildren<Renderer>(true);
        }

        static void DumpAarVisuals()
        {
            var visuals = UnityEngine.Object.FindObjectsOfType<AarVisual>();
            HitCamMod.Log("aarwatch AarVisual: " + visuals.Length + " in scene | "
                + DescribeVisuals(visuals, 4));
        }

        // one AarVisual is what actually decides whether a piece of interior art gets drawn and on
        // which layer, so its private snapshot fields belong in every dump
        internal static string DescribeVisuals(AarVisual[] visuals, int max)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < visuals.Length && i < max; i++)
            {
                var v = visuals[i];
                if (v == null) continue;
                var type = v.GetType();
                sb.Append(PathOf(v.transform)).Append("{")
                    .Append("mode=").Append(Read(type, v, "RenderMode"))
                    .Append(",switchMat=").Append(Read(type, v, "SwitchMaterials"))
                    .Append(",preserve=").Append(Read(type, v, "PreserveLayerUntilAar"))
                    .Append(",hideUntilAar=").Append(Read(type, v, "HideUntilAar"))
                    .Append(",orig=").Append(Read(type, v, "_origLayer"))
                    .Append(",aar=").Append(Read(type, v, "_aarLayer"))
                    .Append(",xray=").Append(Read(type, v, "_xrayLayer"))
                    .Append(",hidden=").Append(Read(type, v, "_hidden"))
                    .Append(",lit=").Append(Read(type, v, "_highlighted"))
                    .Append(",layer=").Append(v.gameObject.layer)
                    .Append(",active=").Append(v.gameObject.activeInHierarchy ? 1 : 0)
                    .Append(",on=").Append(CountEnabled(RenderersOf(v), true))
                    .Append("} ");
            }
            return sb.Length == 0 ? "<none>" : sb.ToString();
        }

        static Renderer[] RenderersOf(AarVisual visual)
        {
            var specific = visual.SpecificRenderers;
            if (specific != null && specific.Length > 0) return specific;
            return visual.GetComponentsInChildren<Renderer>(true);
        }

        static object Read(Type type, object target, string name)
        {
            var field = type.GetField(name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.NonPublic);
            if (field != null) return field.GetValue(target);
            var property = type.GetProperty(name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.NonPublic);
            return property != null ? property.GetValue(target, null) : null;
        }

        static string MaterialsOf(Renderer[] renderers, int max)
        {
            var sb = new StringBuilder();
            var seen = new HashSet<string>();
            for (int i = 0; i < renderers.Length && seen.Count < max; i++)
            {
                var r = renderers[i];
                if (r == null) continue;
                Material material = null;
                try { material = r.sharedMaterial; }
                catch { }
                if (material == null) continue;
                string label = material.name + ":" + (material.shader != null ? material.shader.name : "?");
                if (seen.Add(label)) sb.Append(label).Append("; ");
            }
            return sb.Length == 0 ? "<none>" : sb.ToString();
        }

        static int CountEnabled(Renderer[] renderers, bool enabled)
        {
            int n = 0;
            for (int i = 0; i < renderers.Length; i++)
                if (renderers[i] != null && renderers[i].enabled == enabled) n++;
            return n;
        }

        static string PathOf(Transform t)
        {
            if (t == null) return "<null>";
            var sb = new StringBuilder(t.name);
            var walk = t.parent;
            int hops = 0;
            while (walk != null && hops < 8)
            {
                sb.Insert(0, walk.name + "/");
                walk = walk.parent;
                hops++;
            }
            return sb.ToString();
        }
    }
}
