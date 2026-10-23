using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using GHPC;
using ShellInfo = GHPC.Weapons.ShotInfo;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;

namespace HitCam
{
    internal sealed class ReplaySession
    {
        const int MaxCaptionLines = 4;
        const float RenderInterval = 1f / 30f;
        const float ChildTraceInterval = 0.25f;

        static readonly MethodInfo CreateShotTrace =
            AccessTools.Method(typeof(AarController), "createShotTrace");
        static readonly FieldInfo ShotTraceList =
            AccessTools.Field(typeof(AarController), "_shotTraces");
        static readonly FieldInfo MainShotTraceList =
            AccessTools.Field(typeof(AarController), "_mainShotTraces");
        static readonly FieldInfo StartShotTrace =
            AccessTools.Field(typeof(AarController), "_startShotTrace");
        static readonly FieldInfo SessionShots =
            AccessTools.Field(typeof(AarController), "SessionShots");
        static readonly FieldInfo VisualRenderers =
            AccessTools.Field(typeof(AarVisual), "_renderers");
        static readonly MethodInfo VisualMaterials =
            AccessTools.Method(typeof(AarVisual), "updateMaterials");

        sealed class VisualState
        {
            public AarVisual Visual;
            public bool Highlighted;
            public bool Hidden;
            public bool HideUntilAar;
            public bool PreserveLayerUntilAar;
            public AarVisual.AarVisualRenderMode RenderMode;
        }

        readonly ShellInfo shot;
        readonly Transform pivot;
        readonly GameObject cameraRoot;
        readonly Camera replayCamera;
        readonly GameObject xrayRoot;
        readonly Camera xrayCamera;
        readonly RenderTexture texture;
        GameObject uiRoot;
        RawImage picture;
        Text captionText;

        Prerecorder.Recording preRoll;
        int preIndex;
        float preClock;

        readonly List<TintGroup> tints = new List<TintGroup>();
        int tintedGroups;
        int crewGroups;
        bool colourPropertyFound = true;

        readonly List<VisualState> states = new List<VisualState>();
        readonly List<GameObject> suppressedTraces = new List<GameObject>();
        readonly List<GameObject> createdTraces = new List<GameObject>();

        Vector3 centre;
        Bounds interiorBounds;
        float lastBeat;
        int renderFailures;
        int frames;
        float worstRenderMs;
        float radius = 14f;
        float aimUp = 1.2f;
        float victimExtent = 8f;
        float yaw;
        float elapsed;
        float renderClock;
        bool tracesCreated;
        bool visualsApplied;
        readonly HashSet<ShellInfo> tracedChildren = new HashSet<ShellInfo>();
        float childTraceClock;
        int processedTraces;
        int suppressCursor;

        ReplaySession(ShellInfo target, IUnit victim)
        {
            shot = target;
            pivot = victim.transform;

            texture = new RenderTexture(HitCamMod.WindowWidth, HitCamMod.WindowHeight, 24);
            texture.antiAliasing = 1;
            texture.Create();
            int live = LiveViewMask();
            int surface = SurfaceMask();

            // exactly the two-pass stack the game builds for its own AAR: the world pass draws
            // everything the player can already see, the second pass clears only the depth buffer
            // and paints the AAR layer over it. That is why the AAR looks see-through without the
            // hull ever becoming transparent - and why no exterior renderer has to be touched
            cameraRoot = new GameObject("HitCam_ReplayCamera");
            replayCamera = AddCamera(cameraRoot, CameraClearFlags.SolidColor, surface, 100f);

            xrayRoot = new GameObject("HitCam_XrayCamera");
            xrayRoot.transform.SetParent(cameraRoot.transform, false);
            xrayCamera = AddCamera(xrayRoot, CameraClearFlags.Depth, InteriorMask(), 101f);

            BuildVictimFraming(victim);
            // a short far plane is the layer-free way to lose the neighbouring tanks and the distant
            // scenery: they share the vehicle's own layers, so the culling mask cannot separate them
            float far = Mathf.Clamp(radius * 3f + 30f, 45f, 300f);
            replayCamera.farClipPlane = far;
            xrayCamera.farClipPlane = far;
            AimCamera();
            SweepLiveTraces();
            CreateTraces(victim);
            if (!HitCamMod.ShowFlightPath) SuppressFlightPath();
            if (HitCamMod.ShowInterior) CollectVisuals(victim, live);
            int preserved = HitCamMod.BlackBackground ? PreserveVehicleArt() : 0;
            // lit once for the whole window rather than twice per frame
            ApplyVisuals();
            BuildTints();
            BuildUi();
            StartPreRoll();
            PrimeFirstFrame();

            HitCamMod.Log("background: player camera " + live + " minus environment, far "
                + far.ToString("0") + "m, drawn mask " + surface + ", " + preserved
                + " hull renderers moved off Default");
            LogNearbyLayers();
        }

        Camera AddCamera(GameObject root, CameraClearFlags clear, int mask, float depth)
        {
            var camera = root.AddComponent<Camera>();
            camera.clearFlags = clear;
            camera.backgroundColor = Color.black;
            camera.orthographic = false;
            camera.fieldOfView = HitCamMod.FieldOfView;
            camera.nearClipPlane = 0.1f;
            camera.farClipPlane = 1500f;
            camera.depth = depth;
            camera.cullingMask = mask;
            camera.allowHDR = false;
            camera.allowMSAA = false;
            camera.targetTexture = texture;
            // never enabled: the window is drawn by our own Render() calls, so the game's camera
            // stack and its timing are untouched
            camera.enabled = false;
            return camera;
        }

        public static ReplaySession TryStart(ShellInfo shot, IUnit victim)
        {
            if (shot == null || victim == null || victim.transform == null) return null;
            if (AarController.Instance == null || CreateShotTrace == null)
            {
                HitCamMod.Log("AarController.createShotTrace unavailable");
                return null;
            }

            try { return new ReplaySession(shot, victim); }
            catch (Exception e)
            {
                HitCamMod.Log("session threw: " + e);
                return null;
            }
        }

        void BuildVictimFraming(IUnit victim)
        {
            centre = pivot.position;
            yaw = Mathf.Atan2(shot.StartPosition.x - centre.x, shot.StartPosition.z - centre.z) * Mathf.Rad2Deg;

            // colliders track the real hull; renderer bounds blow up with muzzle flash
            // particles and the oversized quads mod vehicles ship with
            var bounds = new Bounds(centre, Vector3.zero);
            bool measured = false;

            var colliders = victim.transform.GetComponentsInChildren<Collider>();
            for (int i = 0; i < colliders.Length; i++)
            {
                if (colliders[i].bounds.size.magnitude > 60f) continue;
                bounds.Encapsulate(colliders[i].bounds);
                measured = true;
            }
            if (!measured)
            {
                var renderers = victim.transform.GetComponentsInChildren<Renderer>();
                for (int i = 0; i < renderers.Length; i++)
                {
                    if (!renderers[i].enabled || renderers[i].bounds.size.magnitude > 30f) continue;
                    bounds.Encapsulate(renderers[i].bounds);
                    measured = true;
                }
            }
            if (!measured) bounds.size = new Vector3(9f, 4f, 9f);

            // generous on top and bottom: the follow roots the turret art and the crew hang off
            // sit above the hull collider envelope
            bounds.Expand(new Vector3(2.5f, 4f, 2.5f));
            interiorBounds = bounds;

            victimExtent = Mathf.Max(bounds.size.x, bounds.size.z);
            radius = Mathf.Clamp(victimExtent * 1.9f * HitCamMod.DistanceScale, 6f, 40f);
            aimUp = Mathf.Clamp(bounds.center.y - pivot.position.y, 0.4f, 3f);
        }

        // The AAR interior is entirely AarVisual driven (measured in the real AAR: 0 AarModel in
        // scene and in the assets, 386 AarVisual). Highlighting one does the whole job the way the
        // game does it: it enables its renderers, swaps in the coloured AarMaterial, and moves the
        // object to the AAR layer. Outside the AAR the PreserveLayerUntilAar guard makes that last
        // step a no-op, so the pieces stay on their raycast-only layer - invisible to every camera
        // in the game, visible to ours.
        void CollectVisuals(IUnit victim, int liveMask)
        {
            var roots = InteriorRoots(victim);
            artRoots.Clear();
            for (int i = 0; i < roots.Count; i++) artRoots.Add(roots[i]);
            var seen = new HashSet<AarVisual>();
            interiorLayers = (1 << XrayLayer()) | (1 << AarLayer());

            AddVisuals(victim.transform.GetComponentsInChildren<AarVisual>(true), seen, liveMask);
            for (int i = 0; i < roots.Count; i++)
                AddVisuals(roots[i].GetComponentsInChildren<AarVisual>(true), seen, liveMask);

            int fromHierarchy = states.Count;
            var proximity = NearbyVisuals();
            AddVisuals(proximity, seen, liveMask);
            int fromProximity = states.Count - fromHierarchy;

            int revealed = RevealHiddenLayers(roots, liveMask);

            xrayCamera.cullingMask |= interiorLayers;
            xrayCamera.cullingMask &= ~liveMask;

            var layers = new StringBuilder();
            var listed = new HashSet<int>();
            int mask = interiorLayers;
            for (int bit = 0; bit < 32; bit++)
                if ((mask & (1 << bit)) != 0 && listed.Add(bit)) layers.Append(bit).Append(' ');

            var rootNames = new StringBuilder();
            for (int i = 0; i < roots.Count && i < 4; i++)
                rootNames.Append(roots[i].name).Append(' ');

            HitCamMod.Log("xray: " + states.Count + " of " + candidates + " AarVisual highlighted ("
                + fromHierarchy + " by parent, " + fromProximity + " by position, " + exteriorSkips
                + " exterior skipped), " + movedLayers + " renderers moved to layer " + XrayLayer()
                + ", " + stuckLayers + " kept on their layer (collider carrier), "
                + revealed + " already-lit renderers on hidden layers, from "
                + roots.Count + " roots [" + rootNames + "], xray pass on layers "
                + layers + " (mask " + xrayCamera.cullingMask + ")");

            if (!probedUnits.Contains(victim))
            {
                probedUnits.Add(victim);
                HitCamMod.Log("xray box " + interiorBounds + ": "
                    + AarProbe.DescribeVisuals(proximity, 3));
                ProbeScene(victim, shot, roots);
            }
        }

        // the interior and the crew do not live under the unit transform (measured: only 2
        // AarVisual there), so anything sitting inside the struck hull has to be found by asking
        // where it is rather than who owns it
        AarVisual[] NearbyVisuals()
        {
            var all = UnityEngine.Object.FindObjectsOfType<AarVisual>();
            var close = new List<AarVisual>();
            for (int i = 0; i < all.Length; i++)
            {
                var visual = all[i];
                if (visual == null) continue;
                if (interiorBounds.Contains(visual.transform.position)) close.Add(visual);
            }
            return close.ToArray();
        }

        // the crew is not AarVisual art: their bodies stay enabled the whole battle, they simply
        // sit on a layer the player camera refuses to render, which is why nobody ever sees them
        // through the hull. Pointing our second pass at those layers is all it takes.
        int RevealHiddenLayers(List<Transform> roots, int liveMask)
        {
            int count = 0;
            for (int i = 0; i < roots.Count; i++)
            {
                var renderers = roots[i].GetComponentsInChildren<Renderer>(true);
                for (int r = 0; r < renderers.Length; r++)
                {
                    var renderer = renderers[r];
                    if (renderer == null || !renderer.enabled) continue;
                    if (!renderer.gameObject.activeInHierarchy) continue;
                    if (renderer is ParticleSystemRenderer) continue;
                    int layer = renderer.gameObject.layer;
                    if ((liveMask & (1 << layer)) != 0) continue;
                    interiorLayers |= 1 << layer;
                    count++;
                }
            }
            return count;
        }

        void AddVisuals(AarVisual[] found, HashSet<AarVisual> seen, int liveMask)
        {
            for (int i = 0; i < found.Length; i++)
            {
                var visual = found[i];
                if (visual == null || seen.Contains(visual)) continue;
                seen.Add(visual);
                candidates++;

                var renderers = RenderersOf(visual);

                // the giveaway for exterior art is not its layer but whether anything of it is
                // drawn right now: dormant interior art (measured: the driver's body sits on
                // layer 0) draws nothing until we light it, so it can never be the outer hull
                if ((liveMask & (1 << visual.gameObject.layer)) != 0 && CountOn(renderers) > 0)
                {
                    exteriorSkips++;
                    continue;
                }

                for (int r = 0; r < renderers.Length; r++)
                    if (renderers[r] != null) interiorLayers |= 1 << renderers[r].gameObject.layer;

                states.Add(new VisualState
                {
                    Visual = visual,
                    Highlighted = visual.Highlighted,
                    Hidden = visual.Hidden,
                    HideUntilAar = visual.HideUntilAar,
                    PreserveLayerUntilAar = visual.PreserveLayerUntilAar,
                    RenderMode = visual.RenderMode,
                });
                PushToXrayLayer(renderers);
            }
        }

        // the overlay pass only clears depth, so whatever it renders paints over the hull - but it
        // can only render layers the player camera does not use. Art already sitting on a player
        // visible layer has to be stepped aside onto Xray Only for the length of the window and
        // moved back on close; objects carrying their own collider stay put, moving those would
        // silently change what incoming rounds can hit
        void PushToXrayLayer(Renderer[] renderers)
        {
            int target = XrayLayer();
            for (int r = 0; r < renderers.Length; r++)
            {
                var renderer = renderers[r];
                if (renderer == null || !moved.Add(renderer)) continue;
                var gameObject = renderer.gameObject;
                if (gameObject.layer == target) continue;
                // an object with its own collider is part of the hit model: moving it would change
                // what incoming rounds can strike, so it has to stay on its layer and take its
                // chances with depth occlusion instead
                if (gameObject.GetComponent<Collider>() != null)
                {
                    stuckLayers++;
                    continue;
                }
                layerMoves.Add(new LayerMove { Object = gameObject, Layer = gameObject.layer });
                gameObject.layer = target;
                movedLayers++;
            }
        }

        void RestoreLayers()
        {
            for (int i = layerMoves.Count - 1; i >= 0; i--)
            {
                var move = layerMoves[i];
                if (move.Object != null) move.Object.layer = move.Layer;
            }
            layerMoves.Clear();
            moved.Clear();
        }

        // measured from this window's own layer histogram: a struck unit's hull, turret, gun and
        // wheel art sits on Default(0) - the very layer a map also scatters its bushes on - while
        // Main Body(14) holds no renderer at all around a vehicle. So switching Default off to lose
        // the grass erases the target with it. The way to have both is to step the victim's own art
        // onto Main Body for the length of the window: that layer is inside the player camera's mask
        // too, so the live view never notices the move, and RestoreLayers puts it back on close.
        // Only the vehicle's own visual roots are walked - a scene wide sweep by position used to
        // pick up spall clouds and tracer cylinders as well, and those came along as armour.
        int PreserveVehicleArt()
        {
            int target = MainBodyLayer();
            if (target < 0) return 0;

            var roots = new List<Transform>();
            AddFollowRoot(roots, pivot);
            for (int i = 0; i < artRoots.Count; i++) AddFollowRoot(roots, artRoots[i]);
            for (int i = 0; i < states.Count; i++)
                if (states[i].Visual != null) AddFollowRoot(roots, states[i].Visual.transform);

            int count = 0;
            for (int i = 0; i < roots.Count; i++)
                count += StepOffDefault(roots[i].GetComponentsInChildren<Renderer>(true), target);
            return count;
        }

        void AddFollowRoot(List<Transform> roots, Transform t)
        {
            var root = FollowRootOf(t);
            if (root != null && !roots.Contains(root)) roots.Add(root);
        }

        // the unit's own hierarchy is proof enough; everything else found in the scene has to sit
        // inside the struck hull's box, so a bush two metres away is never mistaken for armour
        int StepOffDefault(Renderer[] found, int target)
        {
            int count = 0;
            int stuck = 0;
            for (int i = 0; i < found.Length; i++)
            {
                var renderer = found[i];
                if (renderer == null || !renderer.enabled) continue;
                if (renderer is ParticleSystemRenderer) continue;
                var gameObject = renderer.gameObject;
                if (gameObject.layer != 0 || !gameObject.activeInHierarchy) continue;
                if (!moved.Add(renderer)) continue;
                // a collider carrier decides what an incoming round can strike, so it stays put
                if (gameObject.GetComponent<Collider>() != null) { stuck++; continue; }
                layerMoves.Add(new LayerMove { Object = gameObject, Layer = 0 });
                gameObject.layer = target;
                count++;
            }
            if (count > 0 || stuck > 0)
                HitCamMod.Log("hull art: " + count + " renderers off Default onto layer " + target
                    + ", " + stuck + " left in place (collider carrier)");
            return count;
        }

        static int CountOn(Renderer[] renderers)
        {
            int n = 0;
            for (int i = 0; i < renderers.Length; i++)
                if (renderers[i] != null && renderers[i].enabled) n++;
            return n;
        }

        sealed class LayerMove
        {
            public GameObject Object;
            public int Layer;
        }

        readonly List<LayerMove> layerMoves = new List<LayerMove>();
        readonly List<Transform> artRoots = new List<Transform>();
        readonly HashSet<Renderer> moved = new HashSet<Renderer>();
        int movedLayers;
        int stuckLayers;
        int exteriorSkips;

        int interiorLayers;
        int candidates;

        static Renderer[] RenderersOf(AarVisual visual)
        {
            var list = VisualRenderers != null ? VisualRenderers.GetValue(visual) as System.Collections.IEnumerable : null;
            var found = new List<Renderer>();
            if (list != null)
                foreach (var item in list)
                {
                    var renderer = item as Renderer;
                    if (renderer != null) found.Add(renderer);
                }
            if (found.Count > 0) return found.ToArray();
            return visual.GetComponentsInChildren<Renderer>(true);
        }

        // the interior art and the crew do NOT live under the unit's transform (measured: only 2
        // AarVisual there, both machine gun parts). They sit under the per-part follow roots,
        // HULL FOLLOW and TURRET FOLLOW, which the armour handles of the shot hang inside
        List<Transform> InteriorRoots(IUnit victim)
        {
            var roots = new List<Transform>();
            var frames = shot.AllShotFrames;
            if (frames != null)
                for (int i = 0; i < frames.Count; i++)
                    AddRoot(roots, frames[i].HitArmor);
            return roots;
        }

        static void AddRoot(List<Transform> roots, IArmor armor)
        {
            if (armor == null) return;
            var root = FollowRootOf(armor.Transform);
            if (root != null && !roots.Contains(root)) roots.Add(root);
        }

        static Transform FollowRootOf(Transform t)
        {
            if (t == null) return null;
            Transform follow = null;
            Transform aar = null;
            var walk = t;
            int hops = 0;
            while (walk != null && hops < 14)
            {
                // climb all the way: the outermost follow root carries both the AAR subtree and
                // the crew, which live as siblings rather than inside Hull AAR
                if (IsFollowName(walk.name)) follow = walk;
                else if (aar == null && IsAarName(walk.name)) aar = walk;
                walk = walk.parent;
                hops++;
            }
            if (follow != null) return follow;
            if (aar != null) return aar;
            return t;
        }

        static bool IsFollowName(string name)
        {
            if (name == null) return false;
            return name.EndsWith("FOLLOW", StringComparison.OrdinalIgnoreCase)
                || name.EndsWith("Follow", StringComparison.Ordinal);
        }

        static bool IsAarName(string name)
        {
            if (name == null) return false;
            return name.IndexOf("AAR", StringComparison.OrdinalIgnoreCase) >= 0
                || name.IndexOf("Xray", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        // AarVisual.UpdateVisibility, IL line by line:
        //   enabled = !Hidden
        //          && (RenderMode != HighlightedOnly || Highlighted)
        //          && (RenderMode != XrayOnly || !PlayerCam.HideAarCam)
        //          && (HideUntilAar ? AarController.InAar : true)
        // So Highlighted alone cannot light everything: this window's own probe counted 55 of 477
        // sampled visuals with HideUntilAar set (the interior armour pieces) and 184 in XrayOnly
        // mode, and every one of them stays dark during play while the post-mission AAR - where
        // InAar and HideAarCam have both flipped - shows them. Open all four gates for the length
        // of the window and put the snapshots back on close. PlayerCam.HideAarCam is deliberately
        // not touched: that would hang an x-ray overlay on the player's own view.
        void ApplyVisuals()
        {
            if (visualsApplied) return;
            // flagged first: a throwing setter partway through must still get the restore pass
            visualsApplied = true;
            int failed = 0;
            int forced = 0;
            for (int i = 0; i < states.Count; i++)
            {
                var visual = states[i].Visual;
                if (visual == null) continue;
                try
                {
                    bool needed = false;
                    if (visual.Hidden) { visual.Hidden = false; needed = true; }
                    if (visual.HideUntilAar) { visual.HideUntilAar = false; needed = true; }
                    // updateLayers() bails out on this while the mission is live, which is exactly
                    // what keeps our Xray Only layer moves from being overwritten back onto the AAR
                    // layer (15) - a layer the player's own camera renders
                    if (!visual.PreserveLayerUntilAar)
                    {
                        visual.PreserveLayerUntilAar = true;
                        needed = true;
                    }
                    if (visual.RenderMode != AarVisual.AarVisualRenderMode.Always)
                    {
                        // the setter runs UpdateVisibility, which reads the player camera for
                        // XrayOnly pieces - that is the one thing that can throw here
                        visual.RenderMode = AarVisual.AarVisualRenderMode.Always;
                        needed = true;
                    }
                    visual.Highlighted = true;
                    if (needed) forced++;
                }
                catch (Exception e)
                {
                    if (failed++ == 0) HitCamMod.Log("highlight threw: " + e.Message);
                }
            }
            if (failed > 0) HitCamMod.Log("highlight: " + failed + " of " + states.Count + " refused");
            if (forced > 0)
                HitCamMod.Log("highlight: " + forced + " of " + states.Count
                    + " needed the AAR-only gates opened (HideUntilAar / XrayOnly / Hidden)");
        }

        void RestoreVisuals()
        {
            if (!visualsApplied) return;
            visualsApplied = false;

            for (int i = states.Count - 1; i >= 0; i--)
            {
                var state = states[i];
                var visual = state.Visual;
                if (visual == null) continue;
                if (visual.RenderMode != state.RenderMode) visual.RenderMode = state.RenderMode;
                if (visual.HideUntilAar != state.HideUntilAar) visual.HideUntilAar = state.HideUntilAar;
                if (visual.PreserveLayerUntilAar != state.PreserveLayerUntilAar)
                    visual.PreserveLayerUntilAar = state.PreserveLayerUntilAar;
                if (visual.Hidden != state.Hidden) visual.Hidden = state.Hidden;
                if (visual.Highlighted == state.Highlighted) continue;
                visual.Highlighted = state.Highlighted;
                // UpdateVisibility only refreshes materials while the piece is enabled, so the
                // colour swap needs its own nudge on the way back out
                if (VisualMaterials != null)
                {
                    try { VisualMaterials.Invoke(visual, null); }
                    catch { }
                }
            }
        }

        static readonly HashSet<IUnit> probedUnits = new HashSet<IUnit>();

        static void ProbeScene(IUnit victim, ShellInfo shot, List<Transform> roots)
        {
            var t = victim.transform;
            HitCamMod.Log("probe unit: " + t.name + " root=" + t.root.name + " kids=" + t.childCount
                + " aarModel=" + t.GetComponentsInChildren<AarModel>(true).Length
                + " aarVisual=" + t.GetComponentsInChildren<AarVisual>(true).Length
                + " renderer=" + t.GetComponentsInChildren<Renderer>(true).Length);

            var cams = UnityEngine.Object.FindObjectsOfType<Camera>();
            var masks = new StringBuilder();
            for (int i = 0; i < cams.Length && i < 10; i++)
                masks.Append(cams[i].name).Append(":m").Append(cams[i].cullingMask)
                    .Append(":rt").Append(cams[i].targetTexture != null ? 1 : 0).Append(' ');
            HitCamMod.Log("probe cameras: " + masks);

            var visuals = victim.transform.GetComponentsInChildren<AarVisual>(true);
            HitCamMod.Log("probe unit visuals: " + AarProbe.DescribeVisuals(visuals, 3));

            for (int i = 0; i < roots.Count && i < 6; i++)
                HitCamMod.Log("probe root path: " + Path(roots[i]));

            for (int i = 0; i < roots.Count && i < 6; i++)
            {
                var rootVisuals = roots[i].GetComponentsInChildren<AarVisual>(true);
                var renderers = roots[i].GetComponentsInChildren<Renderer>(true);
                int on = 0;
                int sleeping = 0;
                var hist = new StringBuilder();
                var perLayer = new Dictionary<int, int>();
                for (int r = 0; r < renderers.Length; r++)
                {
                    if (renderers[r] == null) continue;
                    if (renderers[r].enabled)
                    {
                        // an enabled renderer under an inactive ancestor still draws nothing, so
                        // the two cases need opposite fixes
                        if (renderers[r].gameObject.activeInHierarchy) on++;
                        else sleeping++;
                    }
                    int layer = renderers[r].gameObject.layer;
                    int n;
                    perLayer.TryGetValue(layer, out n);
                    perLayer[layer] = n + 1;
                }
                foreach (var pair in perLayer)
                    hist.Append(pair.Key).Append('x').Append(pair.Value).Append(' ');

                HitCamMod.Log("probe armor root: " + Path(roots[i]) + " visuals="
                    + AarProbe.DescribeVisuals(rootVisuals, 2) + " [" + rootVisuals.Length
                    + " aarVisual, " + renderers.Length + " renderers, " + on + " live, "
                    + sleeping + " enabled but inactive, layers " + hist + "]");
            }
        }

        static string Path(Transform t)
        {
            if (t == null) return "<null>";
            var sb = new StringBuilder(t.name);
            var walk = t.parent;
            int hops = 0;
            while (walk != null && hops < 20)
            {
                sb.Insert(0, walk.name + "/");
                walk = walk.parent;
                hops++;
            }
            return sb.ToString();
        }

        // diagnostics are per vehicle type, but the references belong to the old mission
        public static void ForgetProbes()
        {
            probedUnits.Clear();
        }

        static Camera MainViewCamera()
        {
            Camera best = null;
            int bestBits = -1;
            var cams = UnityEngine.Object.FindObjectsOfType<Camera>();
            for (int i = 0; i < cams.Length; i++)
            {
                var cam = cams[i];
                if (cam == null || cam.targetTexture != null) continue;
                if (cam.name.StartsWith("HitCam")) continue;
                int bits = BitCount(cam.cullingMask);
                if (bits > bestBits) { bestBits = bits; best = cam; }
            }
            return best;
        }

        static int BitCount(int mask)
        {
            int n = 0;
            while (mask != 0) { mask &= mask - 1; n++; }
            return n;
        }

        static int LiveViewMask()
        {
            var cam = MainViewCamera();
            return cam != null ? cam.cullingMask : ~0;
        }

        // what the world pass of the window is allowed to draw - the recorder camera uses the same
        // answer, so the pre-roll and the live view are cut from the same cloth
        internal static int SurfaceMask()
        {
            int live = LiveViewMask();
            return HitCamMod.BlackBackground ? live & ~EnvironmentMask() : live;
        }

        // The layers that carry the battlefield rather than a vehicle. Default is the one that
        // matters: a GHPC map scatters its bushes and trees about as plain objects on Default, and
        // a first attempt that only switched off Vegetation(3), Terrain(18) and Sky(27) still showed
        // grass, since the terrain's own layer was already off and the props were not. Switching off
        // Default loses nothing of the target either - the vehicle is Main Body(14) and its interior
        // art is stepped onto Xray Only(21) for the length of the window by PushToXrayLayer.
        static readonly int[] EnvironmentLayerNumbers = { 0, 3, 4, 5, 16, 18, 25, 26, 27 };

        static int EnvironmentMask()
        {
            int mask = 0;
            for (int i = 0; i < EnvironmentLayerNumbers.Length; i++)
                mask |= 1 << EnvironmentLayerNumbers[i];
            return mask;
        }

        // once per window: which layers the map actually puts around the target. This is how a
        // guess about where the foliage lives is checked against a map that was never measured.
        void LogNearbyLayers()
        {
            if (!HitCamMod.DebugLogging) return;
            var counts = new Dictionary<int, int>();
            var sample = new Dictionary<int, string>();
            Renderer[] all;
            try { all = GameObject.FindObjectsOfType<Renderer>(); }
            catch { return; }

            float reach = radius + 12f;
            float reachSquared = reach * reach;
            for (int i = 0; i < all.Length; i++)
            {
                var renderer = all[i];
                if (renderer == null || !renderer.enabled || !renderer.gameObject.activeInHierarchy) continue;
                Vector3 center;
                try { center = renderer.bounds.center; }
                catch { continue; }
                if ((center - centre).sqrMagnitude > reachSquared) continue;

                int layer = renderer.gameObject.layer;
                int seen;
                counts.TryGetValue(layer, out seen);
                counts[layer] = seen + 1;
                if (seen == 0)
                {
                    var parent = renderer.transform.parent;
                    sample[layer] = renderer.name + (parent != null ? " <" + parent.name + ">" : "");
                }
            }

            var builder = new StringBuilder("nearby layers within " + reach.ToString("0") + "m: ");
            foreach (var pair in counts)
            {
                int layer = pair.Key;
                bool drawn = (SurfaceMask() & (1 << layer)) != 0;
                builder.Append(LayerMask.LayerToName(layer)).Append('(').Append(layer).Append(")x")
                    .Append(pair.Value).Append(drawn ? "[drawn] " : " ");
            }
            foreach (var pair in sample)
                if ((SurfaceMask() & (1 << pair.Key)) != 0)
                    builder.Append(" | on ").Append(LayerMask.LayerToName(pair.Key)).Append(" e.g. ").Append(pair.Value);
            HitCamMod.Log(builder.ToString());
        }

        static int MainBodyLayer()
        {
            int layer = LayerMask.NameToLayer("Main Body");
            return layer >= 0 && layer < 32 ? layer : 14;
        }

        static int AarLayer()
        {
            var manager = ConstantsAndInfoManager.Instance;
            return manager != null && manager.AarLayer > 0 ? manager.AarLayer : 15;
        }

        static int XrayLayer()        {
            var manager = ConstantsAndInfoManager.Instance;
            return manager != null ? manager.XrayOnlyLayer : 21;
        }

        // before any AarVisual is known: the layers no game camera renders, where the AAR art
        // waits during battle, plus the one the shot traces get pushed onto
        static int InteriorMask()
        {
            int raycast = LayerMask.NameToLayer("Raycast Only");
            return (1 << XrayLayer()) | (1 << AarLayer()) | (raycast >= 0 ? 1 << raycast : 1 << 8);
        }

        void CreateTraces(IUnit victim)
        {
            AddTraceSet(shot, false);
            if (!tracesCreated) return;

            // the game's own recursion (below) already painted whatever spall children existed in
            // SessionShots at this instant, so they must be marked as done rather than traced twice
            foreach (var child in ChildShots())
                tracedChildren.Add(child);

            var names = new StringBuilder();
            for (int i = 0; i < createdTraces.Count && i < 6; i++)
                names.Append(createdTraces[i].name).Append(' ');
            HitCamMod.Log("traces added: " + createdTraces.Count + " [" + names + "]"
                + (createdTraces.Count > 0 ? " layer " + createdTraces[0].layer : "")
                + ", " + tracedChildren.Count + " spall shot(s) already filed");
            ProcessNewTraces();
        }

        IEnumerable<ShellInfo> ChildShots()
        {
            if (SessionShots == null || AarController.Instance == null)
                return EmptyChildren;
            var shots = SessionShots.GetValue(AarController.Instance) as IEnumerable<ShellInfo>;
            if (shots == null) return EmptyChildren;
            _childrenBuffer.Clear();
            foreach (var s in shots)
                if (s != null && ReferenceEquals(s.ParentShot, shot)) _childrenBuffer.Add(s);
            return _childrenBuffer;
        }

        static readonly List<ShellInfo> EmptyChildren = new List<ShellInfo>();
        readonly List<ShellInfo> _childrenBuffer = new List<ShellInfo>();

        // createShotTrace(main, false) ends with
        //   foreach (s in SessionShots.Where(s => s.ParentShot == main)) createShotTrace(s, true);
        // which is where the AAR's extra spall lines and impact marks come from. A shot is only
        // filed into SessionShots when its LiveRound is destroyed (NotifyShotTerminated), and the
        // spall rounds die a beat AFTER the parent shell - so at window-open the table is still
        // empty for them and the window showed only the main round's after-effects, while the
        // post-mission AAR reads the table after everything has died and shows all of them. Poll
        // with the game's own predicate and trace each child exactly once.
        void TraceLateSpall()
        {
            if (!tracesCreated) return;
            int made = 0;
            foreach (var child in ChildShots())
            {
                if (!tracedChildren.Add(child)) continue;
                made += AddTraceSet(child, true);
            }
            if (made == 0) return;
            ProcessNewTraces();
            if (!HitCamMod.ShowFlightPath) SuppressFlightPath();
            HitCamMod.Log("late spall traces: +" + made + " object(s), "
                + tracedChildren.Count + " child shot(s) traced, "
                + createdTraces.Count + " traces in all");
        }

        // traces arrive in batches (the main round, then every spall child that dies later), and
        // each batch only needs its own layer move and parenting - both are per-object world-space
        // operations that a repeated pass over the older batches would waste on nothing
        void ProcessNewTraces()
        {
            int from = processedTraces;
            MoveToXrayLayer(from);
            AttachTracesToVictim(from);
            processedTraces = createdTraces.Count;
        }

        int AddTraceSet(ShellInfo source, bool isSpall)
        {
            // the field hands out the game's live List, so only the count survives the invoke
            int before = TraceCount();
            try
            {
                CreateShotTrace.Invoke(AarController.Instance, new object[] { source, isSpall });
                tracesCreated = true;
            }
            catch (Exception e)
            {
                HitCamMod.Log("createShotTrace failed: " + (e.InnerException != null ? e.InnerException.Message : e.Message));
                return 0;
            }

            var live = ReadTraceList();
            if (live == null) return 0;
            int added = 0;
            for (int i = before; i < live.Count; i++)
                if (live[i] != null)
                {
                    createdTraces.Add(live[i]);
                    added++;
                }
            return added;
        }

        // the traces are built in world space at the moment of the strike while the battle keeps
        // running, so a target that is still driving away leaves its own flight lines behind and the
        // window shows a vehicle with stray marks hanging in the air where it used to be. The
        // game's AAR can freeze the mission here; we may not touch timeScale, so instead the lines
        // are hung off the victim and travel with it. Only the short impact legs survive this - the
        // long flight line is switched off by SuppressFlightPath.
        void AttachTracesToVictim(int from)
        {
            if (pivot == null) return;
            int attached = 0;
            for (int i = from; i < createdTraces.Count; i++)
            {
                var go = createdTraces[i];
                if (go == null) continue;
                go.transform.SetParent(pivot, true);
                attached++;
            }
            HitCamMod.Log("traces parented to victim " + pivot.name + " (" + attached
                + " of " + createdTraces.Count + ")");
        }

        // the traces land on the AAR layer, which the player's camera renders, so the world view
        // would show stray lines for the length of the window. Xray Only is excluded there and is
        // part of our second pass, so only the window keeps them.
        void MoveToXrayLayer(int from)
        {
            int layer = XrayLayer();
            for (int i = from; i < createdTraces.Count; i++)
            {
                var go = createdTraces[i];
                if (go == null) continue;
                // shot traces carry only mesh filters and renderers, no colliders, so moving the
                // whole object is safe; the rings ShotTraceVisual.Start creates afterwards copy
                // this layer from their parent, so they come along too
                go.layer = layer;
                var renderers = go.GetComponentsInChildren<Renderer>(true);
                for (int r = 0; r < renderers.Length; r++)
                    if (renderers[r] != null) renderers[r].gameObject.layer = layer;
            }
        }

        int TraceCount()
        {
            var list = ReadTraceList();
            return list == null ? 0 : list.Count;
        }

        // an earlier build leaked every trace it made, so old flight lines piled up in the
        // live scene; during play only we create traces, so emptying the lists is safe
        static void SweepLiveTraces()
        {
            if (AarController.InAar) return;
            var all = ReadTraceList(ShotTraceList);
            if (all == null) return;

            int swept = 0;
            for (int i = 0; i < all.Count; i++)
                if (all[i] != null)
                {
                    UnityEngine.Object.Destroy(all[i]);
                    swept++;
                }

            all.Clear();
            var main = ReadTraceList(MainShotTraceList);
            if (main != null) main.Clear();
            if (StartShotTrace != null && AarController.Instance != null)
                StartShotTrace.SetValue(AarController.Instance, null);

            if (swept > 0) HitCamMod.Log("swept " + swept + " leftover traces from the live scene");
        }

        static List<GameObject> ReadTraceList(FieldInfo listField)
        {
            if (listField == null || AarController.Instance == null) return null;
            return listField.GetValue(AarController.Instance) as List<GameObject>;
        }

        List<GameObject> ReadTraceList()
        {
            return ReadTraceList(ShotTraceList);
        }

        // createShotTrace only names its gun-to-target line "startLegTrace" when the first
        // recorded frame is already more than one trace step away; for the shots we replay it
        // instead stretches the FIRST "hitLegTrace" from the muzzle to the target, so the
        // giveaway is the leg length, not the name
        void SuppressFlightPath()
        {
            float cutoff = HitCamMod.FlightLegMeters;
            for (int i = suppressCursor; i < createdTraces.Count; i++)
            {
                var go = createdTraces[i];
                if (go == null) continue;
                var visual = go.GetComponent<ShotTraceVisual>();
                if (visual == null || visual.EndPoints == null || visual.EndPoints.Length == 0) continue;
                if (Vector3.Distance(visual.StartPoint, visual.EndPoints[0]) < cutoff) continue;
                go.SetActive(false);
                suppressedTraces.Add(go);
            }
            suppressCursor = createdTraces.Count;
            HitCamMod.Log("flight line: " + suppressedTraces.Count + " of " + createdTraces.Count
                + " traces hidden, cutoff " + cutoff.ToString("0") + "m");
        }

        // not AarController.ClearShotTraces(): that also walks every AarModel in the mission,
        // which is far too much to disturb during live play
        void RemoveOwnTraces()
        {
            for (int i = 0; i < createdTraces.Count; i++)
                if (createdTraces[i] != null) UnityEngine.Object.Destroy(createdTraces[i]);

            TrimDestroyed(ShotTraceList);
            TrimDestroyed(MainShotTraceList);

            if (StartShotTrace != null && AarController.Instance != null
                && StartShotTrace.GetValue(AarController.Instance) == null)
                StartShotTrace.SetValue(AarController.Instance, null);
        }

        static void TrimDestroyed(FieldInfo listField)
        {
            if (listField == null || AarController.Instance == null) return;
            var list = listField.GetValue(AarController.Instance) as List<GameObject>;
            if (list == null) return;
            list.RemoveAll(IsDestroyed);
        }

        static bool IsDestroyed(GameObject go)
        {
            return go == null;
        }

        void BuildUi()
        {
            uiRoot = new GameObject("HitCam_Window");
            var canvas = uiRoot.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 900;

            int margin = HitCamMod.Margin;
            int width = HitCamMod.WindowWidth;
            int height = HitCamMod.WindowHeight;

            picture = AddGraphic<RawImage>(margin, margin, width, height);
            picture.texture = texture;
            picture.color = Color.white;
            picture.raycastTarget = false;
            // a RawImage with nothing in its texture yet is exactly the white block the window used
            // to open with, so the picture only appears once a real frame is in the texture
            picture.enabled = false;

            if (!HitCamMod.ShowInfoText) return;

            Font font = null;
            try { font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf"); }
            catch { }
            if (font == null)
            {
                try { font = Resources.GetBuiltinResource<Font>("Arial.ttf"); } catch { }
            }
            if (font == null) return;

            var caption = BuildCaption();
            var lines = caption.Split('\n');
            BuildHealthIndex();

            // the report line goes in as the game wrote it and its colour comes from the component,
            // never from a <color> tag inside the string: the translator matches on the whole line,
            // so markup would leave the player looking at English
            captionText = AddGraphic<Text>(margin, margin + height + 4, width, 18);
            captionText.font = font;
            captionText.text = lines[0];
            captionText.alignment = TextAnchor.UpperCenter;
            captionText.fontSize = 15;
            captionText.lineSpacing = 1.15f;
            captionText.color = Color.white;
            captionText.raycastTarget = false;
            captionText.horizontalOverflow = HorizontalWrapMode.Wrap;
            captionText.verticalOverflow = VerticalWrapMode.Overflow;
            captionText.resizeTextForBestFit = true;
            captionText.resizeTextMinSize = 9;
            captionText.resizeTextMaxSize = 15;
            for (int i = 1; i < lines.Length; i++) AddCaptionLine(lines[i], i, margin, height, width);
            HitCamMod.Log("caption: " + caption.Replace("\n", "  //  "));
        }

        readonly List<CaptionLine> captionLines = new List<CaptionLine>();

        struct CaptionLine
        {
            public string Label;
            public Text Text;
        }

        Text AddCaptionLine(string label, int index, int margin, int pictureHeight, int width)
        {
            var text = AddGraphic<Text>(margin, margin + pictureHeight + 4 + index * 18, width, 18);
            text.font = captionText.font;
            text.alignment = TextAnchor.UpperCenter;
            text.fontSize = 15;
            text.lineSpacing = 1.15f;
            text.raycastTarget = false;
            text.horizontalOverflow = HorizontalWrapMode.Wrap;
            text.verticalOverflow = VerticalWrapMode.Overflow;
            text.resizeTextForBestFit = true;
            text.resizeTextMinSize = 9;
            text.resizeTextMaxSize = 15;
            text.text = label;
            int matched;
            float fraction = HealthOfLine(label, out matched);
            text.color = HealthTextColour(fraction);
            captionLines.Add(new CaptionLine { Label = label, Text = text });
            if (HitCamMod.DebugLogging)
                HitCamMod.Log("caption colour: \"" + label + "\" -> " + fraction.ToString("0.00")
                    + " over " + matched + " parts");
            return text;
        }

        // one Text per line, so a line's red can deepen while the damage spreads without ever
        // rewriting the string the translator works from
        void RefreshCaptionColours()
        {
            for (int i = 0; i < captionLines.Count; i++)
            {
                var line = captionLines[i];
                if (line.Text == null) continue;
                line.Text.color = HealthTextColour(HealthOfLine(line.Label));
            }
        }

        void SetCaptionVisible(bool visible)
        {
            if (captionText != null) captionText.enabled = visible;
            for (int i = 0; i < captionLines.Count; i++)
                if (captionLines[i].Text != null) captionLines[i].Text.enabled = visible;
        }

        // part name (lowercased) -> every part carrying it, rather than a snapshot of its health: the
        // replay keeps damaging the vehicle after the window opens, so the line has to redden as the
        // damage lands and reading through the component is how it stays live
        readonly Dictionary<string, List<GHPC.Equipment.IDestructible>> healthIndex =
            new Dictionary<string, List<GHPC.Equipment.IDestructible>>();

        void BuildHealthIndex()
        {
            // measured: the struck unit's transform holds no DestructibleComponent at all - the
            // modules, armour and bodies sit under the HULL/TURRET FOLLOW roots the shot's armour
            // handles hang inside, the same roots the interior art is drawn from
            var roots = InteriorRoots(null);
            if (pivot != null && !roots.Contains(pivot)) roots.Add(pivot);

            var names = new StringBuilder();
            int parts = 0;
            for (int r = 0; r < roots.Count; r++)
            {
                if (roots[r] == null) continue;
                if (r < 4) names.Append(roots[r].name).Append(' ');
                GHPC.Equipment.DestructibleComponent[] modules = null;
                try { modules = roots[r].GetComponentsInChildren<GHPC.Equipment.DestructibleComponent>(true); }
                catch { }
                if (modules == null) continue;
                for (int i = 0; i < modules.Length; i++)
                {
                    var module = modules[i];
                    if (module == null) continue;
                    parts++;
                    IndexHealth(module.Name, module);
                    IndexHealth(module.gameObject.name, module);
                }
            }
            IndexArmourNames();
            HitCamMod.Log("caption colour: " + healthIndex.Count + " names over " + parts + " parts in "
                + roots.Count + " roots [" + names + "]");
        }

        // a report line names the armour section, which has no health of its own; the module that
        // section belongs to does, and the shot already tells us which section was struck
        void IndexArmourNames()
        {
            var frames = shot == null ? null : shot.AllShotFrames;
            if (frames == null) return;
            for (int i = 0; i < frames.Count; i++)
            {
                var armor = frames[i].HitArmor;
                if (armor == null) continue;
                var module = NearestModule(armor.Transform);
                if (module != null) IndexHealth(armor.Name, module);
            }
        }

        void IndexHealth(string name, GHPC.Equipment.IDestructible part)
        {
            if (string.IsNullOrEmpty(name)) return;
            string key = name.ToLowerInvariant();
            if (key.Length < 3) return;
            List<GHPC.Equipment.IDestructible> list;
            if (!healthIndex.TryGetValue(key, out list))
            {
                list = new List<GHPC.Equipment.IDestructible>();
                healthIndex[key] = list;
            }
            if (!list.Contains(part)) list.Add(part);
        }

        // "Penetrated upper glacis applique armor" names the part inside a sentence, so the part name
        // has to sit inside the line - and when several parts match, the worst off one colours the line
        float HealthOfLine(string line)
        {
            int ignored;
            return HealthOfLine(line, out ignored);
        }

        float HealthOfLine(string line, out int matched)
        {
            matched = 0;
            if (line == null) return 1f;
            float worst = -1f;
            foreach (var pair in healthIndex)
            {
                if (line.IndexOf(pair.Key, StringComparison.OrdinalIgnoreCase) < 0) continue;
                var parts = pair.Value;
                for (int i = 0; i < parts.Count; i++)
                {
                    float fraction;
                    try { fraction = Mathf.Clamp01(parts[i].HealthPercent * 0.01f); }
                    catch { continue; }
                    matched++;
                    if (worst < 0f || fraction < worst) worst = fraction;
                }
            }
            return worst < 0f ? 1f : worst;
        }

        static Color HealthTextColour(float fraction)
        {
            float f = Mathf.Clamp01(fraction);
            return new Color(1f, f, f);
        }

        T AddGraphic<T>(int marginX, int offsetY, int width, int height) where T : Graphic
        {
            var go = new GameObject(typeof(T).Name + "_HitCam");
            go.transform.SetParent(uiRoot.transform, false);
            var graphic = go.AddComponent<T>();
            var rect = graphic.rectTransform;
            rect.anchorMin = new Vector2(1f, 1f);
            rect.anchorMax = new Vector2(1f, 1f);
            rect.pivot = new Vector2(1f, 1f);
            rect.anchoredPosition = new Vector2(-marginX, -offsetY);
            rect.sizeDelta = new Vector2(width, height);
            return graphic;
        }

        // XUnity AutoTranslator hooks UnityEngine.UI.Text.set_text, so any line the game itself
        // uses comes out in the player's language, but words we invent ("DAMAGE", "pen") are not in
        // its registry and stay English. Hence: numbers and the ammo designator only, plus the
        // game's own story lines - the kill / damage result is already stated by those lines.
        string BuildCaption()
        {
            string ammo = shot.TypeInfo != null ? shot.TypeInfo.Name : "SHELL";
            string head = ammo + "   " + shot.Distance.ToString("0") + " m   " + shot.ActualPen.ToString("0") + " mm";

            string modules = DescribeDamage();
            return modules.Length == 0 ? head : head + "\n" + modules;
        }

        // LiveRound.AddEvent feeds every shot narration line into ShotStory, whose builder the
        // game itself prints in the AAR: those lines already name the armour section and the
        // module the round struck, so replaying them borrows the game's vocabulary verbatim.
        string DescribeDamage()
        {
            var labels = new List<string>();
            var seen = new HashSet<string>();

            string story = shot.Story != null ? shot.Story.FinalString : null;
            if (!string.IsNullOrEmpty(story))
            {
                var lines = story.Split('\n');
                // newest events first: "Penetrated …" / "Hit …" end the list, launch-time
                // fuze chatter starts it
                for (int i = lines.Length - 1; i >= 0 && labels.Count < MaxCaptionLines; i--)
                    AddLabel(labels, seen, lines[i].Trim());
                labels.Reverse();
            }

            if (labels.Count == 0)
            {
                var frames = shot.AllShotFrames;
                if (frames == null) return string.Empty;
                for (int i = 0; i < frames.Count && labels.Count < MaxCaptionLines; i++)
                {
                    var armor = frames[i].HitArmor;
                    if (armor != null) AddLabel(labels, seen, armor.Name);
                }
            }

            if (labels.Count == 0) return string.Empty;
            return string.Join("\n", labels.ToArray());
        }

        static void AddLabel(List<string> labels, HashSet<string> seen, string label)
        {
            if (label == null || label.Length == 0) return;
            if (labels.Count >= MaxCaptionLines || !seen.Add(label)) return;
            labels.Add(label);
        }

        // returns false once the window is done and the caller may tear it down
        // the shot terminates a beat before the game cuts to the AAR screen, so aborting on
        // InAar used to kill the window before its first frame was ever on screen
        public bool Update(float deltaTime)
        {
            if (pivot == null) return false;

            // the after-effects are still being filed in while the window plays - see TraceLateSpall
            childTraceClock += deltaTime;
            if (childTraceClock >= ChildTraceInterval)
            {
                childTraceClock = 0f;
                try { TraceLateSpall(); }
                catch (Exception e) { HitCamMod.Log("late spall pass threw: " + e.Message); }
            }

            // the recorded flight is shown first and spends none of the live seconds, so the window
            // keeps its full Duration of after-effects after the shell has struck
            if (preRoll != null) { PlayPreRoll(deltaTime); return true; }

            elapsed += deltaTime;
            // once the game cuts to its own AAR the after-effects are on the full screen anyway,
            // and the AAR begins rewriting the very AarVisual state we hold (it moves layers freely
            // while InAar) - hand the vehicle back rather than fight it frame against frame
            if (frames > 0 && SafeInAar())
            {
                HitCamMod.Log("AAR took over after " + frames + " frames, window handed back");
                return false;
            }
            // a heartbeat proves whether the game was still running frames while the window was
            // open, which is the only way to tell our own stall from anybody else's afterwards
            if (elapsed - lastBeat >= 1f)
            {
                HitCamMod.Log("window alive: t=" + elapsed.ToString("0.0") + " frame=" + Time.frameCount
                    + " visuals=" + states.Count);
                lastBeat = elapsed;
            }
            if (elapsed >= HitCamMod.Duration) return false;

            // the window is a second full-scene render; 30 Hz is smooth enough for a slow orbit
            // and stops the extra draw work from scaling with the game's frame rate
            renderClock += deltaTime;
            if (frames > 0 && renderClock < RenderInterval * 0.95f) return true;
            yaw += HitCamMod.OrbitSpeed * renderClock;
            renderClock = 0f;
            AimCamera();
            RenderFrame();
            return true;
        }

        static bool SafeInAar()
        {
            try { return AarController.InAar; }
            catch { return false; }
        }

        void StartPreRoll()
        {
            if (!HitCamMod.PreRollOn) return;
            preRoll = Prerecorder.Claim(shot);
            if (preRoll == null || preRoll.Textures == null || preRoll.Order == null) { preRoll = null; return; }

            // the damage report would give the ending away before the shell has even arrived
            SetCaptionVisible(false);
            preIndex = 0;
            // primed so the first recorded frame is on screen straight away instead of a beat later
            preClock = 1f / Mathf.Clamp(HitCamMod.PreRollPlayFps, 1f, 60f);
            HitCamMod.Log("prerec: " + preRoll.Order.Length + " frames queued, played at "
                + HitCamMod.PreRollPlayFps.ToString("0") + " fps");
        }

        void PlayPreRoll(float deltaTime)
        {
            float step = 1f / Mathf.Clamp(HitCamMod.PreRollPlayFps, 1f, 60f);
            preClock += deltaTime;
            if (preClock < step) return;
            preClock = preClock % step;

            RenderTexture source = null;
            if (preIndex < preRoll.Order.Length)
            {
                int slot = preRoll.Order[preIndex];
                if (slot >= 0 && slot < preRoll.Textures.Length) source = preRoll.Textures[slot];
            }
            preIndex++;
            if (source == null) { EndPreRoll(); return; }

            try
            {
                Graphics.Blit(source, texture);
                ShowWindow();
                if (preIndex == 2) DumpTexture(source, "a_recorded_mid");
            }
            catch (Exception e)
            {
                HitCamMod.Log("prerec blit threw: " + e.Message);
                EndPreRoll();
            }
        }

        void EndPreRoll()
        {
            HitCamMod.Log("prerec: " + preIndex + " frames shown, switching to the live view");
            Prerecorder.Release(preRoll);
            preRoll = null;
            SetCaptionVisible(true);
            renderClock = 0f;
        }

        // War Thunder colours the X-ray view by what is left of each module. The game has no such
        // mapping - AarVisual only swaps in one authored material per piece - so the health is read
        // from the DestructibleComponent the piece hangs under and painted onto a private copy of
        // that material, which leaves the game's own AAR colours untouched.
        sealed class TintGroup
        {
            public bool Ready;
            public GHPC.Equipment.DestructibleComponent Module;
            public GHPC.Humans.IHuman Crew;
            public readonly List<Renderer> Renderers = new List<Renderer>();
            public readonly List<Material[]> Copies = new List<Material[]>();
        }

        static readonly FieldInfo CrewMembersList =
            AccessTools.Field(typeof(GHPC.Crew.CrewManager), "CrewMembers");

        void BuildTints()
        {
            if (!HitCamMod.TintByHealth) return;

            var unresolved = new List<AarVisual>();
            for (int i = 0; i < states.Count; i++)
            {
                var visual = states[i].Visual;
                if (visual == null) continue;
                var module = NearestModule(visual.transform);
                if (module == null) { unresolved.Add(visual); continue; }
                AddToGroup(module, visual);
            }

            if (unresolved.Count > 0) ResolveByReverseWalk(unresolved);

            foreach (var group in byModule.Values) tints.Add(group);
            tintedGroups = tints.Count;
            if (unresolved.Count > 0) ResolveCrewVisuals(unresolved);

            var names = new StringBuilder();
            for (int i = 0; i < tints.Count && i < 5; i++)
                names.Append(tintName(tints[i])).Append(' ');
            HitCamMod.Log("health colour: " + tintedGroups + " modules plus " + crewGroups
                + " crew over " + states.Count + " visuals, " + unresolved.Count
                + " found no owner, first [" + names + "]");
        }

        static string tintName(TintGroup group)
        {
            if (group.Module != null) return group.Module.name;
            if (group.Crew != null) return "crew";
            return "?";
        }

        readonly Dictionary<GHPC.Equipment.DestructibleComponent, TintGroup> byModule =
            new Dictionary<GHPC.Equipment.DestructibleComponent, TintGroup>();

        void AddToGroup(GHPC.Equipment.DestructibleComponent module, AarVisual visual)
        {
            TintGroup group;
            if (!byModule.TryGetValue(module, out group))
            {
                group = new TintGroup { Module = module };
                byModule.Add(module, group);
            }

            var renderers = RenderersOf(visual);
            for (int r = 0; r < renderers.Length; r++)
                if (renderers[r] != null && !group.Renderers.Contains(renderers[r]))
                    group.Renderers.Add(renderers[r]);
        }

        // the art does not always hang *under* its module - the crew bodies and some racks sit next
        // to the component that owns them - so ask every module what it contains and let the
        // deepest one win, otherwise the hull would claim the whole vehicle
        void ResolveByReverseWalk(List<AarVisual> unresolved)
        {
            var roots = new List<Transform>();
            if (pivot != null) roots.Add(pivot);
            var frames = shot.AllShotFrames;
            if (frames != null)
                for (int i = 0; i < frames.Count; i++)
                    AddRoot(roots, frames[i].HitArmor);

            var chosen = new Dictionary<AarVisual, GHPC.Equipment.DestructibleComponent>();
            for (int r = 0; r < roots.Count; r++)
            {
                if (roots[r] == null) continue;
                var modules = roots[r].GetComponentsInChildren<GHPC.Equipment.DestructibleComponent>(true);
                for (int m = 0; m < modules.Length; m++)
                {
                    var module = modules[m];
                    if (module == null) continue;
                    var visuals = module.GetComponentsInChildren<AarVisual>(true);
                    for (int v = 0; v < visuals.Length; v++)
                    {
                        var visual = visuals[v];
                        if (visual == null || !unresolved.Contains(visual)) continue;
                        GHPC.Equipment.DestructibleComponent existing;
                        if (chosen.TryGetValue(visual, out existing))
                        {
                            if (existing == module) continue;
                            // only a module inside the one already found may take the piece over
                            if (!module.transform.IsChildOf(existing.transform)) continue;
                        }
                        chosen[visual] = module;
                    }
                }
            }

            foreach (var pair in chosen) AddToGroup(pair.Value, pair.Key);
            foreach (var visual in chosen.Keys) unresolved.Remove(visual);
            HitCamMod.Log("health colour: " + chosen.Count + " pieces matched by their module subtree");
        }

        // crew bodies sit outside every DestructibleComponent; the game joins a crewman to his art
        // through CrewManager.CrewMembers[].AllAarVisuals, which is a plain object array
        void ResolveCrewVisuals(List<AarVisual> unresolved)
        {
            if (pivot == null || CrewMembersList == null) return;
            GHPC.Crew.CrewManager[] managers;
            try { managers = pivot.GetComponentsInChildren<GHPC.Crew.CrewManager>(true); }
            catch { return; }

            int pieces = 0;
            for (int i = 0; i < managers.Length && unresolved.Count > 0; i++)
            {
                Array members;
                try { members = CrewMembersList.GetValue(managers[i]) as Array; }
                catch { continue; }
                if (members == null) continue;

                for (int m = 0; m < members.Length; m++)
                {
                    var member = members.GetValue(m);
                    if (member == null) continue;
                    var visuals = ReadObjectField<AarVisual[]>(member, "AllAarVisuals");
                    var human = ReadObjectField<GHPC.Humans.IHuman>(member, "Human");
                    if (visuals == null || human == null) continue;

                    bool took = false;
                    for (int v = 0; v < visuals.Length; v++)
                    {
                        var visual = visuals[v];
                        if (visual == null || !unresolved.Remove(visual)) continue;
                        AddCrewGroup(human, visual);
                        took = true;
                        pieces++;
                    }
                    if (took) crewGroups++;
                }
            }
            HitCamMod.Log("health colour: " + pieces + " crew pieces over " + crewGroups + " crewmen");
        }

        static T ReadObjectField<T>(object target, string name) where T : class
        {
            try
            {
                var field = AccessTools.Field(target.GetType(), name);
                return field == null ? null : field.GetValue(target) as T;
            }
            catch { return null; }
        }

        void AddCrewGroup(GHPC.Humans.IHuman human, AarVisual visual)
        {
            TintGroup group = null;
            for (int i = 0; i < tints.Count; i++)
                if (tints[i].Crew == human) { group = tints[i]; break; }
            if (group == null)
            {
                group = new TintGroup { Crew = human };
                tints.Add(group);
            }

            var renderers = RenderersOf(visual);
            for (int r = 0; r < renderers.Length; r++)
                if (renderers[r] != null && !group.Renderers.Contains(renderers[r]))
                    group.Renderers.Add(renderers[r]);
        }

        // a crewman has no percentage, the game grades him in five steps
        static float CrewHealthFraction(GHPC.Humans.IHuman human)
        {
            // the dead are the case the player is looking for, and a killed human has been seen to
            // still report a graded status rather than Dead
            try { if (human.IsDead) return 0f; } catch { }
            switch (human.GetOverallStatus())
            {
                case GHPC.Humans.HealthStatus.Dead: return 0f;
                case GHPC.Humans.HealthStatus.Critical: return 0.1f;
                case GHPC.Humans.HealthStatus.HeavilyWounded: return 0.25f;
                case GHPC.Humans.HealthStatus.Wounded: return 0.45f;
                default: return 1f;
            }
        }

        // the module and its art are only joined by the transform hierarchy (measured: AarVisual has
        // no back-reference to what it draws), so the nearest DestructibleComponent above it is it
        static GHPC.Equipment.DestructibleComponent NearestModule(Transform t)
        {
            var walk = t;
            int hops = 0;
            while (walk != null && hops < 12)
            {
                var module = walk.GetComponent<GHPC.Equipment.DestructibleComponent>();
                if (module != null) return module;
                walk = walk.parent;
                hops++;
            }
            return null;
        }

        void ApplyTint()
        {
            if (tints.Count == 0) return;
            for (int i = 0; i < tints.Count; i++)
            {
                var group = tints[i];
                float percent;
                if (group.Module != null)
                {
                    try { percent = Mathf.Clamp01(group.Module.HealthPercent * 0.01f); }
                    catch { continue; }
                }
                else if (group.Crew != null)
                {
                    try { percent = CrewHealthFraction(group.Crew); }
                    catch { continue; }
                }
                else continue;

                // a module at full health keeps the game's own material: copying and repainting it
                // would recolour the interior where nothing was hurt
                if (percent >= 0.999f) continue;

                if (!group.Ready && !CopyMaterials(group)) continue;
                var colour = HealthColour(percent);

                for (int r = 0; r < group.Renderers.Count; r++)
                {
                    var renderer = group.Renderers[r];
                    var copies = group.Copies[r];
                    if (renderer == null || copies == null) continue;
                    for (int m = 0; m < copies.Length; m++)
                        if (copies[m] != null) SetColour(copies[m], colour);

                    // the game's own updateMaterials writes the originals back whenever it re-lights
                    // a piece; putting our array back is what keeps the colour on screen
                    if (copies.Length > 0 && copies[0] != null && renderer.sharedMaterial != copies[0])
                        renderer.sharedMaterials = copies;
                }
            }
        }

        bool CopyMaterials(TintGroup group)
        {
            for (int r = 0; r < group.Renderers.Count; r++)
            {
                var renderer = group.Renderers[r];
                Material[] copies = null;
                if (renderer != null)
                {
                    try
                    {
                        var source = renderer.sharedMaterials;
                        if (source != null && source.Length > 0)
                        {
                            copies = new Material[source.Length];
                            for (int i = 0; i < source.Length; i++)
                            {
                                if (source[i] == null) continue;
                                var copy = new Material(source[i]);
                                tintMaterials.Add(copy);
                                copies[i] = copy;
                                if (group.Crew != null) DescribeCrewMaterial(source[i], copy);
                            }
                            renderer.sharedMaterials = copies;
                        }
                    }
                    catch (Exception e)
                    {
                        if (tintedFailures++ == 0) HitCamMod.Log("health colour: copy threw: " + e.Message);
                    }
                }
                group.Copies.Add(copies);
            }
            group.Ready = true;
            return group.Copies.Count > 0;
        }

        static readonly string[] ColourProperties = {
            "_Color", "_BaseColor", "BaseColor", "_Tint", "Tint", "_TintColor", "_EmissionColor"
        };

        readonly List<Material> tintMaterials = new List<Material>();
        int tintedFailures;
        bool crewMaterialLogged;

        // one property is not enough: the art sets differ and each reads its colour from wherever it
        // likes, and on a dark body mesh _Color alone is a multiplication of nearly nothing, which
        // is why a killed crewman used to stay the same shade. Every knob the material has is turned
        // instead, and emission - which needs its keyword on - is the one that actually shows.
        void SetColour(Material material, Color colour)
        {
            bool any = false;
            for (int i = 0; i < ColourProperties.Length; i++)
            {
                string name = ColourProperties[i];
                if (!material.HasProperty(name)) continue;
                if (name == "_EmissionColor")
                {
                    // a glow reads on a mesh whose own texture is nearly black, where a plain
                    // multiplication of _Color is invisible - so the emission is driven harder
                    material.SetColor(name, new Color(
                        Mathf.Clamp01(colour.r * 1.7f), Mathf.Clamp01(colour.g * 1.7f),
                        Mathf.Clamp01(colour.b * 1.7f), 1f));
                    try { material.EnableKeyword("_EMISSION"); } catch { }
                }
                else material.SetColor(name, colour);
                any = true;
            }
            if (material.HasProperty("_Color")) material.color = colour;
            if (any) return;

            if (colourPropertyFound)
            {
                colourPropertyFound = false;
                HitCamMod.Log("health colour: no colour property on the AAR material, tinting off");
            }
        }

        // what the crew's material actually is, logged once: without this the choice between
        // _Color, _TintColor and _EmissionColor is a guess
        void DescribeCrewMaterial(Material source, Material copy)
        {
            if (crewMaterialLogged || source == null) return;
            crewMaterialLogged = true;
            var builder = new StringBuilder("crew material: " + source.name + " shader ");
            try { builder.Append(source.shader != null ? source.shader.name : "none"); }
            catch { builder.Append("?"); }
            try
            {
                builder.Append(" | copy props:");
                if (copy == null) { builder.Append(" none"); }
                else
                {
                    for (int i = 0; i < ColourProperties.Length; i++)
                        if (copy.HasProperty(ColourProperties[i]))
                            builder.Append(' ').Append(ColourProperties[i]).Append('=')
                                .Append(copy.GetColor(ColourProperties[i]).ToString("F2"));
                }
            }
            catch { builder.Append(" (unreadable)"); }
            HitCamMod.Log(builder.ToString());
        }

        static readonly float[] HealthStops = { 0f, 0.12f, 0.3f, 0.55f, 0.78f, 1f };
        static readonly Color[] HealthRamp = {
            new Color(0.04f, 0.005f, 0.005f, 1f),   // nothing left: black with a red core
            new Color(0.28f, 0.012f, 0.012f, 1f),
            new Color(0.78f, 0.05f, 0.04f, 1f),     // red
            new Color(0.94f, 0.44f, 0.04f, 1f),     // orange
            new Color(0.86f, 0.86f, 0.10f, 1f),     // yellow
            new Color(0.12f, 0.72f, 0.18f, 1f),     // whole: green
        };

        static Color HealthColour(float t)
        {
            t = Mathf.Clamp01(t);
            for (int i = 1; i < HealthStops.Length; i++)
            {
                if (t > HealthStops[i]) continue;
                float k = (t - HealthStops[i - 1]) / (HealthStops[i] - HealthStops[i - 1]);
                return Color.Lerp(HealthRamp[i - 1], HealthRamp[i], Mathf.Clamp01(k));
            }
            return HealthRamp[HealthRamp.Length - 1];
        }

        void DestroyTints()
        {
            for (int i = 0; i < tintMaterials.Count; i++)
                if (tintMaterials[i] != null) UnityEngine.Object.Destroy(tintMaterials[i]);
            tintMaterials.Clear();
            tints.Clear();
        }

        // a freshly created RenderTexture holds uninitialised memory and the canvas already shows it
        // during the frame the window is built in, which is the white beat before the first real
        // image. Fill it here instead: one live render right away, and only then the pre-roll - and
        // the pre-roll has to prove it holds a picture before it is allowed to cover that live frame.
        void PrimeFirstFrame()
        {
            // the live view is the one path proven to reach the screen, so the window opens on it no
            // matter what; if a validated pre-roll follows it is replaced in this same tick
            RenderFrame();
            ShowWindow();
            if (HitCamMod.DebugLogging) DumpTexture(texture, "z_live_control");
            if (preRoll == null) return;
            try
            {
                if (preRoll.Order == null || preRoll.Order.Length == 0
                    || preRoll.Textures == null || preRoll.Textures.Length == 0)
                {
                    AbandonPreRoll("empty recording");
                    return;
                }
                int slot = preRoll.Order[0];
                if (slot < 0 || slot >= preRoll.Textures.Length || preRoll.Textures[slot] == null)
                {
                    AbandonPreRoll("slot missing");
                    return;
                }

                Color onScreen = AverageColour(texture);
                Color banked = AverageColour(preRoll.Textures[slot]);
                Color control, cameraPulse;
                Prerecorder.CapturePulse(out control, out cameraPulse);
                HitCamMod.Log("first frame: onScreen " + Describe(onScreen) + " | banked " + Describe(banked)
                    + " | control " + Describe(control) + " | pulse " + Describe(cameraPulse));

                // the control decides whether any of these numbers can be believed: a GL clear to
                // magenta that does not read back magenta means this machine's pixel read path cannot
                // see render textures at all, in which case "white" was never evidence about the
                // recording and the recording stays hidden rather than trusted on a blind reading
                bool readsWork = control.r > 0.9f && control.g < 0.1f && control.b > 0.9f;
                if (!readsWork)
                {
                    AbandonPreRoll("read-back unproven");
                    return;
                }
                if (IsFlat(banked))
                {
                    AbandonPreRoll("recording is flat");
                    return;
                }

                DumpTexture(preRoll.Textures[slot], "a_recorded");
                Graphics.Blit(preRoll.Textures[slot], texture);
                DumpTexture(texture, "b_window");
                // frame 0 is on screen now, so the playback continues with the next one instead of
                // showing the same still twice
                preIndex = 1;
            }
            catch (Exception e)
            {
                HitCamMod.Log("first frame skipped: " + e.Message);
                if (preRoll != null) AbandonPreRoll("error");
            }
        }

        // A never-rendered RenderTexture reads back as flat white on this machine, a clear nobody
        // drew into as flat black, and the probe's own magenta as magenta. None of the three is a
        // picture of the struck vehicle, so none of them may cover the live frame.
        static bool IsFlat(Color c)
        {
            return CloseTo(c, 1f, 1f, 1f) || CloseTo(c, 0f, 0f, 0f) || CloseTo(c, 1f, 0f, 1f);
        }

        static bool CloseTo(Color c, float r, float g, float b)
        {
            const float tolerance = 0.04f;
            return Mathf.Abs(c.r - r) < tolerance && Mathf.Abs(c.g - g) < tolerance
                && Mathf.Abs(c.b - b) < tolerance;
        }

        // The pre-roll is dropped, the window simply keeps playing the live orbit view it already
        // opened on - which is the picture the user asked for instead of a white second.
        void AbandonPreRoll(string why)
        {
            if (preRoll == null) return;
            Prerecorder.Release(preRoll);
            preRoll = null;
            SetCaptionVisible(true);
            renderClock = 0f;
            HitCamMod.Log("prerec skipped: " + why);
        }

        static Color AverageColour(RenderTexture source)
        {
            return Prerecorder.AverageColourOf(source);
        }

        static string Describe(Color c)
        {
            return c.r.ToString("F2") + "," + c.g.ToString("F2") + "," + c.b.ToString("F2") + ","
                + c.a.ToString("F2");
        }

        void ShowWindow()
        {
            if (picture != null) picture.enabled = true;
        }

        // no pre-roll to show: one live render right away, so the window never opens on an empty
        // texture
        void ShowLive()
        {
            RenderFrame();
            ShowWindow();
            DumpTexture(texture, "c_live");
        }

        // debug builds only: write the actual pixels out so the complaint about a white window can be
        // answered from the picture rather than from guesses. A handful of files per mission is enough
        // to tell a white recording from a white UI.
        static int dumpsLeft = 9;

        void DumpTexture(RenderTexture source, string tag)
        {
            if (!HitCamMod.DebugLogging || source == null || dumpsLeft <= 0) return;
            dumpsLeft--;
            try
            {
                var tex = new Texture2D(source.width, source.height, TextureFormat.RGBA32, false);
                var previous = RenderTexture.active;
                RenderTexture.active = source;
                tex.ReadPixels(new Rect(0, 0, source.width, source.height), 0, 0);
                tex.Apply();
                RenderTexture.active = previous;
                byte[] bytes = tex.EncodeToPNG();
                UnityEngine.Object.Destroy(tex);
                if (bytes == null || bytes.Length == 0) return;
                string directory = System.IO.Path.Combine(
                    System.IO.Path.GetDirectoryName(UnityEngine.Application.dataPath), "Mods");
                string path = System.IO.Path.Combine(directory, "HitCam.frame" + dumpsLeft + "." + tag + ".png");
                System.IO.File.WriteAllBytes(path, bytes);
                HitCamMod.Log("frame written: " + path + " (" + bytes.Length + " B)");
            }
            catch (Exception e)
            {
                HitCamMod.Log("frame write failed: " + e.Message);
            }
        }

        void RenderFrame()
        {
            // no material swapping in here: the visuals stay lit for the whole window, because
            // replacing sharedMaterials twice per frame for a few hundred renderers is the one
            // thing in this design that can stall the main thread
            try
            {
                // the damage keeps spreading while the window is open, so the colour is re-read for
                // every rendered frame rather than fixed at open
                ApplyTint();
                RefreshCaptionColours();
                float t0 = Time.realtimeSinceStartup;
                replayCamera.Render();
                xrayCamera.Render();
                float ms = (Time.realtimeSinceStartup - t0) * 1000f;
                frames++;
                if (ms > worstRenderMs) worstRenderMs = ms;
                renderFailures = 0;
            }
            catch (Exception e)
            {
                if (++renderFailures == 1 || renderFailures == 5)
                    HitCamMod.Log("render threw (" + renderFailures + "): " + e.Message);
            }

            if (renderFailures < 5) return;
            HitCamMod.Log("render failed " + renderFailures + " times in a row, window aborted");
            elapsed = HitCamMod.Duration;
        }

        // explicit trig instead of Quaternion.Euler: Unity pitches +X *upward*, so an
        // Euler-built -radius offset sat the lens below the hull and into the terrain
        void AimCamera()
        {
            centre = pivot.position;
            Vector3 aim = centre + Vector3.up * aimUp;

            float pitch = Mathf.Clamp(HitCamMod.Elevation, 6f, 70f) * Mathf.Deg2Rad;
            float yawRad = yaw * Mathf.Deg2Rad;
            Vector3 pos = aim + new Vector3(
                Mathf.Sin(yawRad) * radius * Mathf.Cos(pitch),
                radius * Mathf.Sin(pitch),
                Mathf.Cos(yawRad) * radius * Mathf.Cos(pitch));

            // even at the lowest allowed angle the lens stays clear of the ground plane
            pos.y = Mathf.Max(pos.y, centre.y + 2f);

            cameraRoot.transform.position = pos;
            cameraRoot.transform.LookAt(aim);
        }

        public void Dispose()
        {
            try
            {
                HitCamMod.Log("window done: " + frames + " frames, worst render "
                    + worstRenderMs.ToString("0.0") + " ms, " + states.Count + " visuals lit");
                if (preRoll != null) { Prerecorder.Release(preRoll); preRoll = null; }
                RestoreVisuals();
                RestoreLayers();
                DestroyTints();

                if (uiRoot != null) UnityEngine.Object.Destroy(uiRoot);
                if (cameraRoot != null) UnityEngine.Object.Destroy(cameraRoot);
                if (texture != null)
                {
                    texture.Release();
                    UnityEngine.Object.Destroy(texture);
                }

                if (tracesCreated) RemoveOwnTraces();

                states.Clear();
                suppressedTraces.Clear();
                createdTraces.Clear();
            }
            catch (Exception e)
            {
                HitCamMod.Log("dispose threw: " + e.Message);
            }
        }
    }
}
