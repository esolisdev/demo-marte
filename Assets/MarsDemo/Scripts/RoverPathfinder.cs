using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Planificador de rutas: rejilla de transitabilidad (pendiente + rocas + borde)
/// y búsqueda A* 8-conexa con coste que penaliza la pendiente.
/// No requiere nodos manuales: la rejilla se calcula a partir del terreno.
/// </summary>
public class RoverPathfinder
{
    public enum CellState : byte { Free, Slope, Obstacle, Border }

    public readonly int Width, Depth;
    public readonly float CellSize;
    public readonly float SlopeLimitDeg;

    readonly Terrain terrain;
    readonly Vector3 origin;
    readonly CellState[] state;
    readonly float[] slope;   // pendiente máxima de la celda (grados)
    readonly float[] height;  // altura del centro de la celda (m)
    readonly float slopeWeight;

    // Estadísticas de la última búsqueda (para el HUD y el reporte).
    public int LastExpanded { get; private set; }
    public float LastCost { get; private set; }
    public float LastLength { get; private set; }
    public float LastMs { get; private set; }
    public int FreeCells { get; private set; }

    public RoverPathfinder(Terrain terrain, float cellSize, float slopeLimitDeg, LayerMask obstacleMask,
                           float clearanceRadius, float borderMargin, float slopeWeight = 2f)
    {
        this.terrain = terrain;
        CellSize = cellSize;
        SlopeLimitDeg = slopeLimitDeg;
        this.slopeWeight = slopeWeight;
        origin = terrain.transform.position;
        TerrainData data = terrain.terrainData;
        Width = Mathf.FloorToInt(data.size.x / cellSize);
        Depth = Mathf.FloorToInt(data.size.z / cellSize);
        int n = Width * Depth;
        state = new CellState[n];
        slope = new float[n];
        height = new float[n];
        var steep = new bool[n];

        Physics.SyncTransforms(); // asegura que los colliders de las rocas están actualizados
        float half = cellSize * 0.5f;
        for (int iz = 0; iz < Depth; iz++)
        {
            for (int ix = 0; ix < Width; ix++)
            {
                int i = iz * Width + ix;
                float lx = (ix + 0.5f) * cellSize, lz = (iz + 0.5f) * cellSize;
                // Pendiente: máximo entre el centro y las 4 esquinas de la celda.
                float s = Steepness(data, lx, lz);
                s = Mathf.Max(s, Steepness(data, lx - half, lz - half));
                s = Mathf.Max(s, Steepness(data, lx + half, lz - half));
                s = Mathf.Max(s, Steepness(data, lx - half, lz + half));
                s = Mathf.Max(s, Steepness(data, lx + half, lz + half));
                slope[i] = s;
                steep[i] = s > slopeLimitDeg;
                Vector3 world = origin + new Vector3(lx, 0f, lz);
                height[i] = terrain.SampleHeight(world) + origin.y;
            }
        }

        int border = Mathf.Max(1, Mathf.CeilToInt(borderMargin / cellSize));
        for (int iz = 0; iz < Depth; iz++)
        {
            for (int ix = 0; ix < Width; ix++)
            {
                int i = iz * Width + ix;
                if (ix < border || iz < border || ix >= Width - border || iz >= Depth - border)
                {
                    state[i] = CellState.Border;
                    continue;
                }
                // Rocas: cápsula vertical con el radio de seguridad del rover.
                Vector3 p = new Vector3(origin.x + (ix + 0.5f) * cellSize, height[i], origin.z + (iz + 0.5f) * cellSize);
                if (Physics.CheckCapsule(p + Vector3.up * 0.2f, p + Vector3.up * 2f, clearanceRadius, obstacleMask, QueryTriggerInteraction.Ignore))
                {
                    state[i] = CellState.Obstacle;
                    continue;
                }
                // Pendiente dilatada una celda (≈ medio rover de margen).
                bool blocked = false;
                for (int dz = -1; dz <= 1 && !blocked; dz++)
                    for (int dx = -1; dx <= 1 && !blocked; dx++)
                        blocked = steep[(iz + dz) * Width + (ix + dx)];
                state[i] = blocked ? CellState.Slope : CellState.Free;
                if (!blocked) FreeCells++;
            }
        }
    }

    static float Steepness(TerrainData data, float lx, float lz)
    {
        return data.GetSteepness(Mathf.Clamp01(lx / data.size.x), Mathf.Clamp01(lz / data.size.z));
    }

    // ---------- Consultas ----------
    public bool WorldToCell(Vector3 world, out int ix, out int iz)
    {
        ix = Mathf.FloorToInt((world.x - origin.x) / CellSize);
        iz = Mathf.FloorToInt((world.z - origin.z) / CellSize);
        return ix >= 0 && iz >= 0 && ix < Width && iz < Depth;
    }

    public CellState StateAt(Vector3 world)
    {
        return WorldToCell(world, out int ix, out int iz) ? state[iz * Width + ix] : CellState.Border;
    }

    public bool IsWalkable(Vector3 world) => StateAt(world) == CellState.Free;

    public float SlopeAt(Vector3 world)
    {
        return WorldToCell(world, out int ix, out int iz) ? slope[iz * Width + ix] : 90f;
    }

    public Vector3 CellCenter(int ix, int iz)
    {
        int i = iz * Width + ix;
        return new Vector3(origin.x + (ix + 0.5f) * CellSize, height[i], origin.z + (iz + 0.5f) * CellSize);
    }

    Vector3 CellCenter(int id) => CellCenter(id % Width, id / Width);

    /// <summary>Celda transitable más cercana a un punto (búsqueda en anillos).</summary>
    public bool NearestWalkable(Vector3 world, float maxRadius, out Vector3 result)
    {
        result = world;
        if (!WorldToCell(world, out int cx, out int cz)) return false;
        int maxR = Mathf.CeilToInt(maxRadius / CellSize);
        for (int r = 0; r <= maxR; r++)
        {
            for (int dz = -r; dz <= r; dz++)
            {
                for (int dx = -r; dx <= r; dx++)
                {
                    if (Mathf.Max(Mathf.Abs(dx), Mathf.Abs(dz)) != r) continue;
                    int ix = cx + dx, iz = cz + dz;
                    if (ix < 0 || iz < 0 || ix >= Width || iz >= Depth) continue;
                    if (state[iz * Width + ix] != CellState.Free) continue;
                    result = CellCenter(ix, iz);
                    return true;
                }
            }
        }
        return false;
    }

    float GroundY(float x, float z) => terrain.SampleHeight(new Vector3(x, 0f, z)) + origin.y;

    // ---------- A* ----------
    /// <summary>
    /// Devuelve la ruta óptima (puntos sobre el terreno) o null si no existe.
    /// </summary>
    public List<Vector3> FindPath(Vector3 from, Vector3 to)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        LastExpanded = 0; LastCost = 0f; LastLength = 0f;
        if (!WorldToCell(from, out int sx, out int sz) || !WorldToCell(to, out int gx, out int gz)) return null;
        int start = sz * Width + sx, goal = gz * Width + gx;
        if (state[start] != CellState.Free || state[goal] != CellState.Free) return null;

        int n = Width * Depth;
        var g = new float[n];
        var parent = new int[n];
        var closed = new bool[n];
        for (int i = 0; i < n; i++) { g[i] = float.PositiveInfinity; parent[i] = -1; }

        var heap = new MinHeap(1024);
        g[start] = 0f;
        heap.Push(start, Heuristic(sx, sz, gx, gz));
        bool found = false;

        while (heap.Count > 0)
        {
            int cur = heap.Pop();
            if (closed[cur]) continue; // entrada obsoleta
            closed[cur] = true;
            LastExpanded++;
            if (cur == goal) { found = true; break; }

            int cx = cur % Width, cz = cur / Width;
            for (int dz = -1; dz <= 1; dz++)
            {
                for (int dx = -1; dx <= 1; dx++)
                {
                    if (dx == 0 && dz == 0) continue;
                    int nx = cx + dx, nz = cz + dz;
                    if (nx < 0 || nz < 0 || nx >= Width || nz >= Depth) continue;
                    int nb = nz * Width + nx;
                    if (closed[nb] || state[nb] != CellState.Free) continue;
                    // No se permite cortar esquinas entre celdas bloqueadas.
                    if (dx != 0 && dz != 0 &&
                        (state[cz * Width + nx] != CellState.Free || state[nz * Width + cx] != CellState.Free)) continue;

                    float dist = (dx != 0 && dz != 0 ? 1.41421356f : 1f) * CellSize;
                    float step = StepCost(cur, nb, dist);
                    if (step < 0f) continue; // desnivel entre celdas superior al límite
                    float ng = g[cur] + step;
                    if (ng < g[nb])
                    {
                        g[nb] = ng;
                        parent[nb] = cur;
                        heap.Push(nb, ng + Heuristic(nx, nz, gx, gz));
                    }
                }
            }
        }

        if (!found)
        {
            LastMs = (float)watch.Elapsed.TotalMilliseconds;
            return null; // lista abierta agotada: no existe ruta
        }

        // Reconstrucción de la ruta en celdas.
        var cells = new List<int>();
        for (int c = goal; c != -1; c = parent[c]) cells.Add(c);
        cells.Reverse();

        var path = Smooth(cells, g, from, to);
        LastCost = g[goal];
        for (int i = 1; i < path.Count; i++) LastLength += Vector3.Distance(path[i - 1], path[i]);
        LastMs = (float)watch.Elapsed.TotalMilliseconds;
        return path;
    }

    /// <summary>Coste de pasar de una celda a su vecina; negativo si el desnivel es excesivo.</summary>
    float StepCost(int a, int b, float dist)
    {
        float dh = height[b] - height[a];
        if (Mathf.Atan2(Mathf.Abs(dh), dist) * Mathf.Rad2Deg > SlopeLimitDeg) return -1f;
        float d3 = Mathf.Sqrt(dist * dist + dh * dh);
        float t = 0.5f * (slope[a] + slope[b]) / SlopeLimitDeg;
        return d3 * (1f + slopeWeight * t * t); // penaliza la pendiente: prefiere rutas llanas
    }

    /// <summary>Distancia octil horizontal: admisible porque el coste nunca es menor.</summary>
    float Heuristic(int x, int z, int gx, int gz)
    {
        int dx = Mathf.Abs(x - gx), dz = Mathf.Abs(z - gz);
        return CellSize * (Mathf.Max(dx, dz) + 0.41421356f * Mathf.Min(dx, dz));
    }

    // ---------- Suavizado por línea de visión ----------
    List<Vector3> Smooth(List<int> cells, float[] g, Vector3 from, Vector3 to)
    {
        int m = cells.Count - 1;
        var pts = new Vector3[cells.Count];
        for (int i = 0; i <= m; i++) pts[i] = CellCenter(cells[i]);
        pts[0] = new Vector3(from.x, GroundY(from.x, from.z), from.z);
        pts[m] = new Vector3(to.x, GroundY(to.x, to.z), to.z);

        var result = new List<Vector3> { pts[0] };
        int anchor = 0;
        while (anchor < m)
        {
            int k = anchor + 1;
            // Avanza mientras el tramo recto sea transitable y no más caro que la ruta de rejilla.
            while (k < m && SegmentOk(pts[anchor], pts[k + 1], (g[cells[k + 1]] - g[cells[anchor]]) * 1.05f + 0.01f)) k++;
            result.Add(pts[k]);
            anchor = k;
        }
        return result;
    }

    bool SegmentOk(Vector3 a, Vector3 b, float maxCost)
    {
        float distXZ = Vector2.Distance(new Vector2(a.x, a.z), new Vector2(b.x, b.z));
        int steps = Mathf.Max(1, Mathf.CeilToInt(distXZ / (CellSize * 0.25f)));
        float cost = 0f;
        Vector3 prev = a;
        for (int i = 1; i <= steps; i++)
        {
            float t = i / (float)steps;
            float x = Mathf.Lerp(a.x, b.x, t), z = Mathf.Lerp(a.z, b.z, t);
            var p = new Vector3(x, GroundY(x, z), z);
            if (!WorldToCell(p, out int ix, out int iz)) return false;
            int id = iz * Width + ix;
            if (state[id] != CellState.Free) return false;
            float r = slope[id] / SlopeLimitDeg;
            cost += Vector3.Distance(prev, p) * (1f + slopeWeight * r * r);
            if (cost > maxCost) return false;
            prev = p;
        }
        return true;
    }

    /// <summary>Remuestrea la ruta cada 'step' metros y la pega al terreno (para dibujarla).</summary>
    public List<Vector3> Densify(List<Vector3> path, float step, float yOffset)
    {
        var result = new List<Vector3>();
        for (int i = 0; i < path.Count - 1; i++)
        {
            Vector3 a = path[i], b = path[i + 1];
            int steps = Mathf.Max(1, Mathf.CeilToInt(Vector3.Distance(a, b) / step));
            for (int s = 0; s < steps; s++)
            {
                float t = s / (float)steps;
                float x = Mathf.Lerp(a.x, b.x, t), z = Mathf.Lerp(a.z, b.z, t);
                result.Add(new Vector3(x, GroundY(x, z) + yOffset, z));
            }
        }
        Vector3 last = path[path.Count - 1];
        result.Add(new Vector3(last.x, GroundY(last.x, last.z) + yOffset, last.z));
        return result;
    }

    // ---------- Montículo binario mínimo (cola de prioridad) ----------
    class MinHeap
    {
        int[] ids;
        float[] keys;
        public int Count { get; private set; }

        public MinHeap(int capacity)
        {
            ids = new int[capacity];
            keys = new float[capacity];
        }

        public void Push(int id, float key)
        {
            if (Count == ids.Length)
            {
                System.Array.Resize(ref ids, Count * 2);
                System.Array.Resize(ref keys, Count * 2);
            }
            int i = Count++;
            while (i > 0)
            {
                int p = (i - 1) / 2;
                if (keys[p] <= key) break;
                ids[i] = ids[p]; keys[i] = keys[p];
                i = p;
            }
            ids[i] = id; keys[i] = key;
        }

        public int Pop()
        {
            int top = ids[0];
            Count--;
            int lastId = ids[Count];
            float lastKey = keys[Count];
            int i = 0;
            while (true)
            {
                int c = 2 * i + 1;
                if (c >= Count) break;
                if (c + 1 < Count && keys[c + 1] < keys[c]) c++;
                if (keys[c] >= lastKey) break;
                ids[i] = ids[c]; keys[i] = keys[c];
                i = c;
            }
            ids[i] = lastId; keys[i] = lastKey;
            return top;
        }
    }
}
