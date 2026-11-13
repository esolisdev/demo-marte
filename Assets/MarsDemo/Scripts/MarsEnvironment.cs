using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
#if UNITY_EDITOR
using UnityEditor;
using UnityEditor.SceneManagement;
#endif

/// <summary>
/// Entorno marciano: constantes físicas, gravedad, generación procedural del terreno
/// y de las rocas, y prueba de caída libre.
/// </summary>
[DefaultExecutionOrder(-1000)] // se ejecuta antes que el resto para fijar la gravedad
public class MarsEnvironment : MonoBehaviour
{
    // ---------- Física de Marte ----------
    /// <summary>Gravedad superficial de Marte: 3.71 m/s² (la Tierra tiene 9.81 m/s²).</summary>
    public const float MarsGravity = 3.71f;
    public const float EarthGravity = 9.81f;

    [Header("Física de Marte")]
    [Tooltip("Gravedad de Marte en m/s². Se aplica a Physics.gravity al iniciar la escena.")]
    public Vector3 gravity = new Vector3(0f, -MarsGravity, 0f);

    [Tooltip("Pendiente máxima (grados) que el rover puede afrontar. La usan la ruta y la validación del movimiento.")]
    public float maxSlopeDeg = 25f;

    [Tooltip("Margen de seguridad (grados) que se resta al planificar la ruta.")]
    public float planningSlopeMarginDeg = 3f;

    [Tooltip("Material físico del regolito: fricción dinámica 0.5, estática 0.6.")]
    public PhysicsMaterial regolithMaterial;

    // ---------- Terreno ----------
    [Header("Terreno")]
    public Terrain terrain;
    public int seed = 2026;
    [Tooltip("Lado del terreno en metros (máximo permitido por el enunciado: 300).")]
    public float terrainSize = 200f;
    public float terrainHeight = 40f;
    public int heightmapResolution = 513;
    public int alphamapResolution = 256;
    public float baseLevel = 8f;
    public float duneAmplitude = 3f;
    [Tooltip("Centro (x, z) del cráter profundo de paredes impracticables: su interior es inalcanzable.")]
    public Vector2 closedCraterCenter = new Vector2(144f, 60f);
    public float closedCraterRadius = 16f;

    // ---------- Rocas ----------
    [Header("Rocas (obstáculos con collider)")]
    public Transform obstaclesRoot;
    public int rockCount = 60;
    public Material rockMaterial;
    public string obstacleLayerName = "Obstacle";

    // ---------- Prueba de caída ----------
    [Header("Prueba de caída libre")]
    public float dropHeight = 1f;
    public bool DropRunning { get; private set; }
    public float LastDropMeasured { get; private set; } = -1f;
    public float LastDropInterpolated { get; private set; } = -1f;

    public static MarsEnvironment Instance { get; private set; }

    /// <summary>Pendiente límite usada al planificar (máxima menos margen).</summary>
    public float PlanningSlopeDeg => maxSlopeDeg - planningSlopeMarginDeg;
    /// <summary>Tiempo teórico de caída: t = √(2h/g).</summary>
    public float DropTheoretical => Mathf.Sqrt(2f * dropHeight / Mathf.Abs(gravity.y));
    public float DropOnEarth => Mathf.Sqrt(2f * dropHeight / EarthGravity);
    public float DynamicFriction => regolithMaterial != null ? regolithMaterial.dynamicFriction : 0.5f;
    public float StaticFriction => regolithMaterial != null ? regolithMaterial.staticFriction : 0.6f;
    public Vector3 Center => terrain.transform.position + new Vector3(terrainSize * 0.5f, 0f, terrainSize * 0.5f);

    void Awake()
    {
        Instance = this;
        // Gravedad de Marte: 3.71 m/s² hacia abajo (≈ 38 % de la terrestre).
        Physics.gravity = gravity;
    }

    // ---------- Consultas del terreno ----------
    public float HeightAt(float x, float z)
    {
        return terrain.SampleHeight(new Vector3(x, 0f, z)) + terrain.transform.position.y;
    }

    public Vector3 GroundPoint(float x, float z) => new Vector3(x, HeightAt(x, z), z);

    /// <summary>Pendiente del terreno en grados en el punto (x, z) del mundo.</summary>
    public float SteepnessAt(float x, float z)
    {
        ToUV(x, z, out float u, out float v);
        return terrain.terrainData.GetSteepness(u, v);
    }

    public Vector3 NormalAt(float x, float z)
    {
        ToUV(x, z, out float u, out float v);
        return terrain.terrainData.GetInterpolatedNormal(u, v);
    }

    void ToUV(float x, float z, out float u, out float v)
    {
        Vector3 p = terrain.transform.position;
        Vector3 s = terrain.terrainData.size;
        u = Mathf.Clamp01((x - p.x) / s.x);
        v = Mathf.Clamp01((z - p.z) / s.z);
    }

    // =====================================================================
    //  Generación procedural (determinista por semilla)
    // =====================================================================
    struct Feature
    {
        public Vector2 pos;
        public float radius;   // radio de influencia en metros
        public float height;   // altura (montaña) o profundidad (cráter)
        public float rim;      // altura del borde (solo cráter)
        public bool crater;
        public float noiseOffset;
    }

    [ContextMenu("Regenerar terreno y rocas")]
    public void Generate()
    {
        EnsureAssets();
        GenerateTerrain();
        GenerateRocks();
#if UNITY_EDITOR
        if (!Application.isPlaying)
        {
            EditorUtility.SetDirty(this);
            EditorSceneManager.MarkSceneDirty(gameObject.scene);
            AssetDatabase.SaveAssets();
        }
#endif
    }

    List<Feature> BuildFeatures(System.Random rng)
    {
        var list = new List<Feature>();
        Vector2 center = new Vector2(terrainSize * 0.5f, terrainSize * 0.5f);

        // Cráter cerrado: profundo y con paredes de más de 30° en todo el perímetro.
        list.Add(new Feature
        {
            pos = closedCraterCenter, radius = closedCraterRadius,
            height = 0.45f * closedCraterRadius, rim = 0.06f * closedCraterRadius, crater = true
        });

        // Tres elevaciones: dos montañas escarpadas y una colina suave (transitable).
        float[] heights = { 20f, 14f, 7f };
        float[] radii = { 30f, 26f, 34f };
        for (int i = 0; i < heights.Length; i++)
            TryPlace(list, rng, center, radii[i], heights[i], 0f, false);

        // Siete cráteres someros (transitables) con borde elevado.
        for (int i = 0; i < 7; i++)
        {
            float r = Range(rng, 6f, 16f);
            TryPlace(list, rng, center, r, 0.10f * r, 0.03f * r, true);
        }
        return list;
    }

    void TryPlace(List<Feature> list, System.Random rng, Vector2 center, float radius, float height, float rim, bool crater)
    {
        float extent = crater ? radius * 1.6f : radius;
        for (int attempt = 0; attempt < 200; attempt++)
        {
            float margin = extent + 8f;
            var pos = new Vector2(Range(rng, margin, terrainSize - margin), Range(rng, margin, terrainSize - margin));
            // Se deja libre el centro del mapa (zona de inicio del rover).
            if (Vector2.Distance(pos, center) < extent + 18f) continue;
            bool overlaps = false;
            foreach (var f in list)
            {
                float fe = f.crater ? f.radius * 1.6f : f.radius;
                if (Vector2.Distance(pos, f.pos) < (extent + fe) * 0.9f) { overlaps = true; break; }
            }
            if (overlaps) continue;
            list.Add(new Feature { pos = pos, radius = radius, height = height, rim = rim, crater = crater, noiseOffset = Range(rng, 0f, 500f) });
            return;
        }
    }

    static float Range(System.Random rng, float a, float b) => a + (float)rng.NextDouble() * (b - a);

    /// <summary>Altura del terreno en metros para un punto local (x, z).</summary>
    float HeightFunction(float x, float z, List<Feature> feats, float ox, float oz)
    {
        // 1) Base ondulada: ruido fractal (fBm) de 4 octavas.
        float amp = 1f, freq = 1f / 70f, sum = 0f, norm = 0f;
        for (int o = 0; o < 4; o++)
        {
            sum += amp * (Mathf.PerlinNoise(x * freq + ox, z * freq + oz) * 2f - 1f);
            norm += amp;
            amp *= 0.45f;
            freq *= 2.1f;
        }
        float h = baseLevel + duneAmplitude * sum / norm;

        // 2) Montañas (perfil gaussiano) y 3) cráteres (cuenco parabólico con borde).
        foreach (var f in feats)
        {
            float r = Vector2.Distance(new Vector2(x, z), f.pos);
            if (f.crater)
            {
                if (r > f.radius * 3f) continue;
                float t = r / f.radius;
                if (t <= 1f) h += -f.height * (1f - t * t) + f.rim * t * t;
                else h += f.rim * Mathf.Exp(-(r - f.radius) / (0.35f * f.radius));
            }
            else
            {
                if (r > f.radius * 2f) continue;
                float sigma = f.radius / 2.2f;
                float mod = 0.85f + 0.3f * Mathf.PerlinNoise(x * 0.05f + f.noiseOffset, z * 0.05f + f.noiseOffset);
                h += f.height * mod * Mathf.Exp(-(r * r) / (2f * sigma * sigma));
            }
        }
        return h;
    }

    public void GenerateTerrain()
    {
        TerrainData data = terrain.terrainData;
        data.heightmapResolution = heightmapResolution; // antes que size: cambiarla reinicia el tamaño
        data.size = new Vector3(terrainSize, terrainHeight, terrainSize);

        var rng = new System.Random(seed);
        float ox = Range(rng, 0f, 1000f), oz = Range(rng, 0f, 1000f);
        List<Feature> feats = BuildFeatures(rng);

        int res = data.heightmapResolution;
        var heights = new float[res, res]; // [z, x], normalizado 0..1
        for (int iz = 0; iz < res; iz++)
        {
            float z = iz / (float)(res - 1) * terrainSize;
            for (int ix = 0; ix < res; ix++)
            {
                float x = ix / (float)(res - 1) * terrainSize;
                heights[iz, ix] = Mathf.Clamp01(HeightFunction(x, z, feats, ox, oz) / terrainHeight);
            }
        }
        data.SetHeights(0, 0, heights);

        // Pintado: regolito en llano, roca oscura donde la pendiente supera el límite de ruta.
        data.alphamapResolution = alphamapResolution;
        int ares = data.alphamapResolution;
        var alpha = new float[ares, ares, 2];
        float limit = PlanningSlopeDeg;
        for (int iz = 0; iz < ares; iz++)
        {
            for (int ix = 0; ix < ares; ix++)
            {
                float steep = data.GetSteepness((ix + 0.5f) / ares, (iz + 0.5f) / ares);
                float rock = Mathf.InverseLerp(limit - 3f, limit + 1f, steep);
                alpha[iz, ix, 0] = 1f - rock;
                alpha[iz, ix, 1] = rock;
            }
        }
        data.SetAlphamaps(0, 0, alpha);

        var col = terrain.GetComponent<TerrainCollider>();
        col.terrainData = data;
        col.sharedMaterial = regolithMaterial;
        terrain.Flush();
    }

    public void GenerateRocks()
    {
        // Elimina las rocas anteriores.
        for (int i = obstaclesRoot.childCount - 1; i >= 0; i--)
        {
            var child = obstaclesRoot.GetChild(i).gameObject;
            if (Application.isPlaying) Destroy(child); else DestroyImmediate(child);
        }

        var rng = new System.Random(seed + 7);
        int layer = LayerMask.NameToLayer(obstacleLayerName);
        Vector3 center = Center;
        var placed = new List<Vector4>(); // x, z, radio

        for (int i = 0; i < rockCount; i++)
        {
            for (int attempt = 0; attempt < 40; attempt++)
            {
                float x = Range(rng, 6f, terrainSize - 6f) + terrain.transform.position.x;
                float z = Range(rng, 6f, terrainSize - 6f) + terrain.transform.position.z;
                float radius = Range(rng, 0.4f, 1.6f);
                if (SteepnessAt(x, z) > 15f) continue;                                   // solo en zonas llanas
                if (Vector2.Distance(new Vector2(x, z), new Vector2(center.x, center.z)) < 8f) continue; // zona de inicio libre
                bool overlaps = false;
                foreach (var p in placed)
                    if (Vector2.Distance(new Vector2(x, z), new Vector2(p.x, p.y)) < radius + p.z + 1f) { overlaps = true; break; }
                if (overlaps) continue;

                bool sphere = rng.NextDouble() < 0.6;
                GameObject rock = GameObject.CreatePrimitive(sphere ? PrimitiveType.Sphere : PrimitiveType.Cube);
                rock.name = "Rock_" + i.ToString("00");
                rock.transform.SetParent(obstaclesRoot, false);
                float y = HeightAt(x, z);
                if (sphere)
                {
                    // Escala uniforme para que el SphereCollider coincida con la malla.
                    rock.transform.localScale = Vector3.one * (radius * 2f);
                    rock.transform.position = new Vector3(x, y + radius * 0.6f, z);
                }
                else
                {
                    rock.transform.localScale = new Vector3(radius * Range(rng, 1.2f, 2f), radius * Range(rng, 0.8f, 1.6f), radius * Range(rng, 1.2f, 2f));
                    rock.transform.position = new Vector3(x, y + radius * 0.3f, z);
                    rock.transform.rotation = Quaternion.Euler(Range(rng, -20f, 20f), Range(rng, 0f, 360f), Range(rng, -20f, 20f));
                }
                rock.layer = layer;
                rock.isStatic = true;
                rock.GetComponent<Renderer>().sharedMaterial = rockMaterial;
                rock.GetComponent<Collider>().sharedMaterial = regolithMaterial;
                placed.Add(new Vector4(x, z, radius * (sphere ? 1f : 1.5f), 0f));
                break;
            }
        }
    }

    /// <summary>Crea (solo en el Editor) el terreno, el material físico y las texturas si faltan.</summary>
    void EnsureAssets()
    {
#if UNITY_EDITOR
        const string dir = "Assets/MarsDemo/Generated";
        if (!AssetDatabase.IsValidFolder("Assets/MarsDemo")) AssetDatabase.CreateFolder("Assets", "MarsDemo");
        if (!AssetDatabase.IsValidFolder(dir)) AssetDatabase.CreateFolder("Assets/MarsDemo", "Generated");

        if (regolithMaterial == null)
        {
            // Regolito: fricción moderada, sin rebote.
            regolithMaterial = new PhysicsMaterial("Regolith")
            {
                dynamicFriction = 0.5f,
                staticFriction = 0.6f,
                bounciness = 0f,
                frictionCombine = PhysicsMaterialCombine.Average,
                bounceCombine = PhysicsMaterialCombine.Minimum
            };
            AssetDatabase.CreateAsset(regolithMaterial, dir + "/Regolith.asset");
        }

        if (rockMaterial == null)
        {
            rockMaterial = new Material(Shader.Find("Universal Render Pipeline/Lit")) { name = "MarsRock" };
            rockMaterial.SetColor("_BaseColor", new Color(0.30f, 0.16f, 0.11f));
            rockMaterial.SetFloat("_Smoothness", 0.1f);
            AssetDatabase.CreateAsset(rockMaterial, dir + "/MarsRock.mat");
        }

        if (terrain == null)
        {
            var data = new TerrainData();
            data.heightmapResolution = heightmapResolution;
            data.size = new Vector3(terrainSize, terrainHeight, terrainSize);
            AssetDatabase.CreateAsset(data, dir + "/MarsTerrainData.asset");
            GameObject go = Terrain.CreateTerrainGameObject(data);
            go.name = "Terrain";
            go.transform.SetParent(transform, false);
            terrain = go.GetComponent<Terrain>();
            // Material de terreno por defecto del asset URP activo.
            var rp = GraphicsSettings.currentRenderPipeline;
            if (rp != null && rp.defaultTerrainMaterial != null) terrain.materialTemplate = rp.defaultTerrainMaterial;
        }

        if (terrain.terrainData.terrainLayers == null || terrain.terrainData.terrainLayers.Length < 2)
        {
            terrain.terrainData.terrainLayers = new[]
            {
                CreateLayer(dir, "Regolith", new Color(0.70f, 0.36f, 0.20f), 11),
                CreateLayer(dir, "SteepRock", new Color(0.33f, 0.17f, 0.12f), 23)
            };
        }

        if (obstaclesRoot == null)
        {
            var root = new GameObject("Obstacles");
            root.transform.SetParent(transform, false);
            obstaclesRoot = root.transform;
        }
#endif
    }

#if UNITY_EDITOR
    static TerrainLayer CreateLayer(string dir, string name, Color baseColor, int noiseSeed)
    {
        // Textura procedural pequeña: color base con variación de ruido.
        const int size = 64;
        var tex = new Texture2D(size, size, TextureFormat.RGB24, true) { name = name + "Tex", wrapMode = TextureWrapMode.Repeat };
        var rng = new System.Random(noiseSeed);
        var px = new Color[size * size];
        for (int i = 0; i < px.Length; i++)
        {
            float k = 0.85f + 0.3f * (float)rng.NextDouble();
            px[i] = new Color(baseColor.r * k, baseColor.g * k, baseColor.b * k);
        }
        tex.SetPixels(px);
        tex.Apply(true);
        AssetDatabase.CreateAsset(tex, dir + "/" + name + "Tex.asset");

        var layer = new TerrainLayer { name = name, diffuseTexture = tex, tileSize = new Vector2(6f, 6f), smoothness = 0f, metallic = 0f };
        AssetDatabase.CreateAsset(layer, dir + "/" + name + ".terrainlayer");
        return layer;
    }
#endif

    // =====================================================================
    //  Prueba de caída libre: un objeto soltado desde 1 m tarda √(2h/g) ≈ 0.73 s en Marte
    // =====================================================================
    public void RunDropTest(Vector3 near)
    {
        if (!DropRunning && Application.isPlaying) StartCoroutine(DropRoutine(near));
    }

    IEnumerator DropRoutine(Vector3 near)
    {
        DropRunning = true;

        // Busca el punto más llano de los alrededores para que la altura sea exacta.
        Vector3 spot = near;
        float best = float.MaxValue;
        var rng = new System.Random(1);
        for (int i = 0; i < 80; i++)
        {
            float x = Mathf.Clamp(near.x + Range(rng, -12f, 12f), 3f, terrainSize - 3f);
            float z = Mathf.Clamp(near.z + Range(rng, -12f, 12f), 3f, terrainSize - 3f);
            float s = SteepnessAt(x, z);
            if (s < best) { best = s; spot = new Vector3(x, 0f, z); }
        }

        var col = terrain.GetComponent<TerrainCollider>();
        if (!col.Raycast(new Ray(new Vector3(spot.x, 500f, spot.z), Vector3.down), out RaycastHit hit, 1000f))
        {
            DropRunning = false;
            yield break;
        }

        yield return new WaitForFixedUpdate(); // se alinea con el paso de física

        const float radius = 0.2f;
        GameObject go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        go.name = "DropTestSphere";
        go.transform.localScale = Vector3.one * (radius * 2f);
        go.transform.position = hit.point + Vector3.up * (dropHeight + radius); // base a 'dropHeight' del suelo
        var rb = go.AddComponent<Rigidbody>();
        rb.mass = 1f;
        rb.linearDamping = 0f;   // atmósfera marciana ≈ 1 % de la terrestre: sin arrastre
        rb.angularDamping = 0f;
        rb.collisionDetectionMode = CollisionDetectionMode.Continuous;
        Physics.SyncTransforms();

        float dt = Time.fixedDeltaTime;
        float t0 = Time.fixedTime;
        float prevVy = 0f, prevGap = dropHeight;
        float measured = -1f, interpolated = -1f;
        while (true)
        {
            yield return new WaitForFixedUpdate();
            float elapsed = Time.fixedTime - t0;
            float vy = rb.linearVelocity.y;
            float gap = rb.position.y - radius - hit.point.y;
            // Contacto: la velocidad deja de crecer hacia abajo o la esfera toca el suelo.
            if (vy > prevVy + 1e-3f || gap <= 0.002f)
            {
                measured = elapsed;
                float vStep = Mathf.Abs(prevVy + gravity.y * dt);
                float frac = vStep > 1e-5f ? Mathf.Clamp01(prevGap / (vStep * dt)) : 1f;
                interpolated = elapsed - dt + frac * dt; // instante del impacto dentro del último paso
                break;
            }
            if (elapsed > 10f) break;
            prevVy = vy;
            prevGap = gap;
        }

        LastDropMeasured = measured;
        LastDropInterpolated = interpolated;
        Debug.Log($"[Marte] Caída libre desde {dropHeight:F2} m con g = {Mathf.Abs(gravity.y):F2} m/s²: " +
                  $"medido {measured:F3} s (paso de física de {dt:F3} s), interpolado {interpolated:F3} s; " +
                  $"teórico √(2h/g) = {DropTheoretical:F3} s; en la Tierra serían {DropOnEarth:F3} s.");
        Destroy(go, 4f);
        DropRunning = false;
    }
}
