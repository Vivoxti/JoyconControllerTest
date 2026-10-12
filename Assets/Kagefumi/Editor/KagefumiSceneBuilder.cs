using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;

namespace Kagefumi.EditorTools
{
    /// <summary>Builds the KAGEFUMI scene and its assets from scratch. Menu: Kagefumi ▸ Build Scene.</summary>
    public static class KagefumiSceneBuilder
    {
        const string Root = "Assets/Kagefumi";
        const string ScenePath = Root + "/Scenes/Kagefumi.unity";
        const string ControlsPath = Root + "/Input/KagefumiControls.inputactions";

        static readonly Color P1Color = new Color(0.97f, 0.33f, 0.3f);
        static readonly Color P2Color = new Color(0.22f, 0.56f, 1f);

        [MenuItem("Kagefumi/Build Scene")]
        public static void Build()
        {
            foreach (var folder in new[] { "Materials", "Meshes", "Textures", "Scenes", "Settings" })
                EnsureFolder($"{Root}/{folder}");

            // ── Assets ──
            var flagstones = BuildFlagstoneTexture();
            var plaza = Lit("Plaza", new Color(0.93f, 0.86f, 0.72f), 0.1f, flagstones, new Vector2(8f, 14f / 3f));
            var grass = Lit("Grass", new Color(0.33f, 0.4f, 0.3f), 0.05f);
            var curb = Lit("Curb", new Color(0.58f, 0.54f, 0.5f), 0.1f);
            var stone = Lit("Stone", new Color(0.66f, 0.65f, 0.62f), 0.1f);
            var vermilion = Lit("Vermilion", new Color(0.86f, 0.22f, 0.12f), 0.25f);
            var lacquer = Lit("Black Lacquer", new Color(0.08f, 0.07f, 0.07f), 0.4f);
            var metal = Lit("Lamp Metal", new Color(0.16f, 0.17f, 0.19f), 0.45f);
            var bulb = Lit("Lamp Bulb", new Color(1f, 0.9f, 0.7f), 0.5f, emission: new Color(1f, 0.78f, 0.45f) * 2f);
            var p1Body = Lit("P1 Body", P1Color, 0.35f);
            var p2Body = Lit("P2 Body", P2Color, 0.35f);
            var straw = Lit("Straw Hat", new Color(0.9f, 0.76f, 0.47f), 0.15f);
            var eye = Lit("Eye", new Color(0.05f, 0.05f, 0.07f), 0.8f);
            var fx = Unlit("FX Unlit");
            var kasa = BuildKasaMesh();
            var post = BuildPostProfile();
            var controls = AssetDatabase.LoadAssetAtPath<InputActionAsset>(ControlsPath);
            if (controls == null) Debug.LogError($"[Kagefumi] Missing input actions at {ControlsPath}");

            // ── Scene ──
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            SetupLighting();

            var camGo = new GameObject("Main Camera") { tag = "MainCamera" };
            var cam = camGo.AddComponent<Camera>();
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0.55f, 0.72f, 0.82f);
            cam.fieldOfView = 36f;
            cam.nearClipPlane = 0.3f;
            cam.farClipPlane = 150f;
            camGo.transform.position = new Vector3(0f, 22.5f, -15.5f);
            camGo.transform.LookAt(new Vector3(0f, 0f, 0.3f));
            camGo.AddComponent<AudioListener>();
            var camData = cam.GetUniversalAdditionalCameraData();
            camData.renderPostProcessing = true;
            camData.antialiasing = AntialiasingMode.SubpixelMorphologicalAntiAliasing;

            var volume = new GameObject("Post FX").AddComponent<Volume>();
            volume.isGlobal = true;
            volume.sharedProfile = post;

            var sunGo = new GameObject("Sun");
            var sun = sunGo.AddComponent<Light>();
            sun.type = LightType.Directional;
            sun.shadows = LightShadows.Soft;
            sun.shadowStrength = 0.92f;
            sun.intensity = 1.8f;
            sun.color = new Color(1f, 0.6f, 0.36f);
            sunGo.transform.rotation = Quaternion.Euler(18f, 280f, 0f);
            RenderSettings.sun = sun;

            // Arena: a 24 × 14 stone plaza in a park.
            var env = new GameObject("Environment").transform;
            Prim(PrimitiveType.Cube, "Plaza", env, new Vector3(0f, -0.1f, 0f), new Vector3(24f, 0.2f, 14f), plaza);
            Prim(PrimitiveType.Cube, "Park", env, new Vector3(0f, -0.21f, 0f), new Vector3(120f, 0.2f, 120f), grass, castShadows: false);
            Prim(PrimitiveType.Cube, "Curb N", env, new Vector3(0f, 0.15f, 7.15f), new Vector3(24.6f, 0.3f, 0.3f), curb);
            Prim(PrimitiveType.Cube, "Curb S", env, new Vector3(0f, 0.15f, -7.15f), new Vector3(24.6f, 0.3f, 0.3f), curb);
            Prim(PrimitiveType.Cube, "Curb W", env, new Vector3(-12.15f, 0.15f, 0f), new Vector3(0.3f, 0.3f, 14f), curb);
            Prim(PrimitiveType.Cube, "Curb E", env, new Vector3(12.15f, 0.15f, 0f), new Vector3(0.3f, 0.3f, 14f), curb);

            BuildTorii(env, new Vector3(0f, 0f, 1.2f), vermilion, lacquer);
            foreach (var p in new[] { new Vector3(-5.2f, 0f, -3.4f), new Vector3(5.2f, 0f, -3.4f), new Vector3(-5.6f, 0f, 4.2f), new Vector3(5.6f, 0f, 4.2f) })
                BuildStoneLantern(env, p, stone);

            var lampPositions = new[] { new Vector3(-9.6f, 0f, 4.8f), new Vector3(9.6f, 0f, 4.8f), new Vector3(0f, 0f, -5.4f) };
            var lamps = new Light[lampPositions.Length];
            var bulbs = new Renderer[lampPositions.Length];
            for (int i = 0; i < lampPositions.Length; i++)
                BuildStreetLamp(env, lampPositions[i], metal, bulb, out lamps[i], out bulbs[i]);

            // Systems
            var cycle = new GameObject("Day Cycle").AddComponent<DayCycle>();
            cycle.sun = sun;
            cycle.lamps = lamps;
            cycle.lampBulbs = bulbs;
            cycle.skyCamera = cam;

            var p1 = BuildPlayer(0, new Vector3(-7f, 0f, -0.5f), Vector3.right, P1Color, p1Body, straw, eye, kasa);
            var p2 = BuildPlayer(1, new Vector3(7f, 0f, -0.5f), Vector3.left, P2Color, p2Body, straw, eye, kasa);

            var game = new GameObject("Kagefumi Game").AddComponent<KagefumiGame>();
            game.players = new[] { p1, p2 };
            game.dayCycle = cycle;
            game.gameCamera = cam;
            game.controls = controls;
            game.fxMaterial = fx;
            game.spawnPoints = new[] { p1.transform.position, p2.transform.position };

            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene, ScenePath);
            AddSceneToBuild();
            AssetDatabase.SaveAssets();
            Debug.Log($"[Kagefumi] Scene built at {ScenePath}");
        }

        // ───────────────────────────── Props ─────────────────────────────

        static void BuildTorii(Transform parent, Vector3 position, Material red, Material black)
        {
            var torii = new GameObject("Torii Gate").transform;
            torii.SetParent(parent, false);
            torii.localPosition = position;
            Prim(PrimitiveType.Cylinder, "Pillar L", torii, new Vector3(-1.7f, 1.75f, 0f), new Vector3(0.44f, 1.75f, 0.44f), red);
            Prim(PrimitiveType.Cylinder, "Pillar R", torii, new Vector3(1.7f, 1.75f, 0f), new Vector3(0.44f, 1.75f, 0.44f), red);
            Prim(PrimitiveType.Cylinder, "Base L", torii, new Vector3(-1.7f, 0.15f, 0f), new Vector3(0.6f, 0.15f, 0.6f), black);
            Prim(PrimitiveType.Cylinder, "Base R", torii, new Vector3(1.7f, 0.15f, 0f), new Vector3(0.6f, 0.15f, 0.6f), black);
            Prim(PrimitiveType.Cube, "Nuki", torii, new Vector3(0f, 2.85f, 0f), new Vector3(4.3f, 0.22f, 0.26f), red);
            Prim(PrimitiveType.Cube, "Kasagi", torii, new Vector3(0f, 3.55f, 0f), new Vector3(5.2f, 0.3f, 0.5f), red);
            Prim(PrimitiveType.Cube, "Kasagi Cap", torii, new Vector3(0f, 3.78f, 0f), new Vector3(5.7f, 0.16f, 0.6f), black);
        }

        static void BuildStoneLantern(Transform parent, Vector3 position, Material stone)
        {
            var lantern = new GameObject("Stone Lantern").transform;
            lantern.SetParent(parent, false);
            lantern.localPosition = position;
            Prim(PrimitiveType.Cylinder, "Post", lantern, new Vector3(0f, 1.05f, 0f), new Vector3(0.8f, 1.05f, 0.8f), stone);
            Prim(PrimitiveType.Cube, "Firebox", lantern, new Vector3(0f, 2.35f, 0f), new Vector3(0.95f, 0.6f, 0.95f), stone);
            var roof = Prim(PrimitiveType.Cube, "Roof", lantern, new Vector3(0f, 2.8f, 0f), new Vector3(1.5f, 0.24f, 1.5f), stone);
            roof.transform.localRotation = Quaternion.Euler(0f, 45f, 0f);
            Prim(PrimitiveType.Sphere, "Finial", lantern, new Vector3(0f, 3.05f, 0f), new Vector3(0.35f, 0.35f, 0.35f), stone);
        }

        static void BuildStreetLamp(Transform parent, Vector3 position, Material metal, Material bulbMat, out Light light, out Renderer bulb)
        {
            // The lamp hangs from an arm that reaches towards the plaza, so the pole doesn't shade its own light.
            var lamp = new GameObject("Street Lamp").transform;
            lamp.SetParent(parent, false);
            lamp.localPosition = position;
            var inward = new Vector3(-position.x, 0f, -position.z).normalized;
            lamp.localRotation = Quaternion.LookRotation(inward);
            Prim(PrimitiveType.Cylinder, "Pole", lamp, new Vector3(0f, 2.5f, 0f), new Vector3(0.16f, 2.5f, 0.16f), metal);
            Prim(PrimitiveType.Cube, "Arm", lamp, new Vector3(0f, 4.95f, 0.5f), new Vector3(0.1f, 0.1f, 1.1f), metal, keepCollider: false, castShadows: false);
            Prim(PrimitiveType.Cube, "Hood", lamp, new Vector3(0f, 5.0f, 1.1f), new Vector3(0.7f, 0.14f, 0.7f), metal, keepCollider: false, castShadows: false);
            bulb = Prim(PrimitiveType.Sphere, "Bulb", lamp, new Vector3(0f, 4.82f, 1.1f), Vector3.one * 0.34f, bulbMat, keepCollider: false, castShadows: false)
                .GetComponent<Renderer>();

            var lightGo = new GameObject("Lamp Light");
            lightGo.transform.SetParent(lamp, false);
            lightGo.transform.localPosition = new Vector3(0f, 4.76f, 1.1f);
            lightGo.transform.rotation = Quaternion.Euler(90f, 0f, 0f);
            light = lightGo.AddComponent<Light>();
            light.type = LightType.Spot;
            light.spotAngle = 150f;
            light.innerSpotAngle = 110f;
            light.range = 24f;
            light.intensity = 60f;
            light.color = new Color(1f, 0.78f, 0.5f);
            light.shadows = LightShadows.Soft;
            light.shadowStrength = 0.95f;
            light.enabled = false; // the DayCycle turns lamps on at dusk
        }

        static ShadowPlayer BuildPlayer(int index, Vector3 position, Vector3 facing, Color color, Material bodyMat, Material hatMat, Material eyeMat, Mesh kasa)
        {
            var root = new GameObject($"Player {index + 1}");
            root.transform.position = position;

            var cc = root.AddComponent<CharacterController>();
            cc.height = 1.6f;
            cc.radius = 0.4f;
            cc.center = new Vector3(0f, 0.82f, 0f);
            cc.stepOffset = 0.05f;
            cc.slopeLimit = 30f;
            cc.skinWidth = 0.03f;
            cc.minMoveDistance = 0f;

            var body = new GameObject("Body").transform;
            body.SetParent(root.transform, false);
            body.localRotation = Quaternion.LookRotation(facing);

            var torso = Prim(PrimitiveType.Capsule, "Torso", body, new Vector3(0f, 0.72f, 0f), new Vector3(0.72f, 0.72f, 0.72f), bodyMat);
            var torsoCol = torso.GetComponent<CapsuleCollider>();
            torsoCol.isTrigger = true;

            var hat = new GameObject("Kasa Hat");
            hat.transform.SetParent(body, false);
            hat.transform.localPosition = new Vector3(0f, 1.36f, 0f);
            hat.AddComponent<MeshFilter>().sharedMesh = kasa;
            hat.AddComponent<MeshRenderer>().sharedMaterial = hatMat;
            var hatCol = hat.AddComponent<MeshCollider>();
            hatCol.sharedMesh = kasa;
            hatCol.convex = true;
            hatCol.isTrigger = true;

            Prim(PrimitiveType.Sphere, "Eye L", body, new Vector3(-0.13f, 1.08f, 0.31f), Vector3.one * 0.12f, eyeMat, keepCollider: false, castShadows: false);
            Prim(PrimitiveType.Sphere, "Eye R", body, new Vector3(0.13f, 1.08f, 0.31f), Vector3.one * 0.12f, eyeMat, keepCollider: false, castShadows: false);

            var player = root.AddComponent<ShadowPlayer>();
            player.index = index;
            player.color = color;
            player.body = body;
            player.shadowColliders = new Collider[] { torsoCol, hatCol };
            return player;
        }

        // ───────────────────────────── Helpers ─────────────────────────────

        static GameObject Prim(PrimitiveType type, string name, Transform parent, Vector3 localPos, Vector3 scale, Material mat,
                               bool keepCollider = true, bool castShadows = true)
        {
            var go = GameObject.CreatePrimitive(type);
            go.name = name;
            go.transform.SetParent(parent, false);
            go.transform.localPosition = localPos;
            go.transform.localScale = scale;
            var r = go.GetComponent<MeshRenderer>();
            r.sharedMaterial = mat;
            r.shadowCastingMode = castShadows ? ShadowCastingMode.On : ShadowCastingMode.Off;
            if (!keepCollider) Object.DestroyImmediate(go.GetComponent<Collider>());
            return go;
        }

        static void SetupLighting()
        {
            const string path = Root + "/Settings/KagefumiLighting.lighting";
            var settings = AssetDatabase.LoadAssetAtPath<LightingSettings>(path);
            if (settings == null)
            {
                settings = new LightingSettings { name = "KagefumiLighting" };
                AssetDatabase.CreateAsset(settings, path);
            }
            settings.bakedGI = false;
            settings.realtimeGI = false;
            Lightmapping.lightingSettings = settings;

            RenderSettings.skybox = null;
            RenderSettings.fog = false;
            RenderSettings.ambientMode = AmbientMode.Flat;
            RenderSettings.ambientLight = new Color(0.5f, 0.4f, 0.45f);
        }

        static Material Lit(string name, Color color, float smoothness, Texture texture = null, Vector2? tiling = null, Color? emission = null)
        {
            var mat = LoadOrCreateMaterial(name, "Universal Render Pipeline/Lit");
            mat.SetColor("_BaseColor", color);
            mat.SetFloat("_Smoothness", smoothness);
            if (texture != null)
            {
                mat.SetTexture("_BaseMap", texture);
                mat.SetTextureScale("_BaseMap", tiling ?? Vector2.one);
            }
            if (emission.HasValue)
            {
                mat.EnableKeyword("_EMISSION");
                mat.SetColor("_EmissionColor", emission.Value);
                mat.globalIlluminationFlags = MaterialGlobalIlluminationFlags.RealtimeEmissive;
            }
            EditorUtility.SetDirty(mat);
            return mat;
        }

        static Material Unlit(string name)
        {
            // Sprites/Default: unlit, alpha-blended, vertex-colored, double-sided — ideal for rings and particles.
            var mat = LoadOrCreateMaterial(name, "Sprites/Default");
            EditorUtility.SetDirty(mat);
            return mat;
        }

        static Material LoadOrCreateMaterial(string name, string shaderName)
        {
            string path = $"{Root}/Materials/{name}.mat";
            var shader = Shader.Find(shaderName);
            var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (mat == null)
            {
                mat = new Material(shader);
                AssetDatabase.CreateAsset(mat, path);
            }
            else if (mat.shader != shader)
            {
                mat.shader = shader;
            }
            return mat;
        }

        static Texture2D BuildFlagstoneTexture()
        {
            const string path = Root + "/Textures/Flagstones.png";
            const int size = 256, tile = 128;
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
            var rng = new System.Random(7);
            var tileShade = new float[4, 4];
            for (int a = 0; a < 4; a++)
            for (int b = 0; b < 4; b++)
                tileShade[a, b] = 0.9f + (float)rng.NextDouble() * 0.1f;

            for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                int row = y / tile;
                int sx = (x + (row % 2 == 1 ? tile / 2 : 0)) % size; // running bond
                int lx = sx % tile, ly = y % tile;
                int edge = Mathf.Min(Mathf.Min(lx, ly), Mathf.Min(tile - 1 - lx, tile - 1 - ly));
                float grout = edge < 2 ? 0.74f : edge < 4 ? 0.88f : 1f;
                float grain = 0.97f + (float)rng.NextDouble() * 0.03f;
                float v = tileShade[sx / tile, row] * grout * grain;
                tex.SetPixel(x, y, new Color(v, v, v, 1f));
            }
            tex.Apply();
            File.WriteAllBytes(path, tex.EncodeToPNG());
            Object.DestroyImmediate(tex);
            AssetDatabase.ImportAsset(path);

            var importer = (TextureImporter)AssetImporter.GetAtPath(path);
            importer.wrapMode = TextureWrapMode.Repeat;
            importer.mipmapEnabled = true;
            importer.anisoLevel = 4;
            importer.SaveAndReimport();
            return AssetDatabase.LoadAssetAtPath<Texture2D>(path);
        }

        /// <summary>A conical straw hat (kasa): its wide brim gives every shadow a readable silhouette.</summary>
        static Mesh BuildKasaMesh()
        {
            const string path = Root + "/Meshes/Kasa.asset";
            const int segments = 28;
            const float radius = 0.6f, height = 0.34f, underside = 0.03f;

            var verts = new Vector3[segments * 6];
            var tris = new int[segments * 6];
            for (int i = 0; i < segments; i++)
            {
                float a0 = i * Mathf.PI * 2f / segments, a1 = (i + 1) * Mathf.PI * 2f / segments;
                var r0 = new Vector3(Mathf.Cos(a0) * radius, 0f, Mathf.Sin(a0) * radius);
                var r1 = new Vector3(Mathf.Cos(a1) * radius, 0f, Mathf.Sin(a1) * radius);
                int v = i * 6;
                // top cone
                verts[v] = new Vector3(0f, height, 0f); verts[v + 1] = r1; verts[v + 2] = r0;
                // underside, slightly concave
                verts[v + 3] = new Vector3(0f, underside, 0f); verts[v + 4] = r0; verts[v + 5] = r1;
                for (int k = 0; k < 6; k++) tris[v + k] = v + k;
            }

            var mesh = AssetDatabase.LoadAssetAtPath<Mesh>(path);
            if (mesh == null)
            {
                mesh = new Mesh { name = "Kasa" };
                AssetDatabase.CreateAsset(mesh, path);
            }
            mesh.Clear();
            mesh.vertices = verts;
            mesh.triangles = tris;
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            EditorUtility.SetDirty(mesh);
            return mesh;
        }

        static VolumeProfile BuildPostProfile()
        {
            const string path = Root + "/Settings/KagefumiPost.asset";
            if (AssetDatabase.LoadAssetAtPath<VolumeProfile>(path) != null) AssetDatabase.DeleteAsset(path);

            var profile = ScriptableObject.CreateInstance<VolumeProfile>();
            AssetDatabase.CreateAsset(profile, path);

            var bloom = profile.Add<Bloom>();
            bloom.threshold.Override(1f);
            bloom.intensity.Override(0.9f);
            bloom.scatter.Override(0.7f);

            var tonemap = profile.Add<Tonemapping>();
            tonemap.mode.Override(TonemappingMode.Neutral);

            var vignette = profile.Add<Vignette>();
            vignette.intensity.Override(0.3f);
            vignette.smoothness.Override(0.45f);

            var grade = profile.Add<ColorAdjustments>();
            grade.contrast.Override(10f);
            grade.saturation.Override(12f);

            foreach (var component in profile.components)
            {
                component.name = component.GetType().Name;
                component.hideFlags = HideFlags.HideInInspector | HideFlags.HideInHierarchy;
                AssetDatabase.AddObjectToAsset(component, profile);
            }
            EditorUtility.SetDirty(profile);
            return profile;
        }

        static void AddSceneToBuild()
        {
            var others = EditorBuildSettings.scenes.Where(s => s.path != ScenePath);
            EditorBuildSettings.scenes = new[] { new EditorBuildSettingsScene(ScenePath, true) }.Concat(others).ToArray();
        }

        static void EnsureFolder(string path)
        {
            if (AssetDatabase.IsValidFolder(path)) return;
            var parent = Path.GetDirectoryName(path)?.Replace('\\', '/');
            if (!string.IsNullOrEmpty(parent)) EnsureFolder(parent);
            AssetDatabase.CreateFolder(parent, Path.GetFileName(path));
        }
    }
}
