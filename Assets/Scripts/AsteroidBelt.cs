using System;
using System.Threading.Tasks;
using UnityEngine;

/// <summary>
/// Renders 150k+ asteroids using baked orbit positions for GPU playback, with two LOD levels:
///   LOD0 — full mesh, for asteroids within lodDistance of the camera.
///   LOD1 — camera-facing billboard quad, for asteroids beyond lodDistance.
///
/// Setup:
///   1. Attach to an empty GameObject.
///   2. Assign orbitShader       (OrbitCompute.compute)
///   3. Assign asteroidMaterial  (AsteroidInstanced.shader)
///   4. Assign billboardMaterial (AsteroidBillboard.shader + impostor texture)
///   5. Assign asteroidPrototypes (asteroid_1 through asteroid_5 meshes)
///   6. Assign csvFile (gaia_solar_system_xyz_150kobjects.csv as TextAsset)
/// </summary>
public class AsteroidBelt : MonoBehaviour
{
    [Header("GPU Resources")]
    public ComputeShader orbitShader;
    public Material      asteroidMaterial;   // AsteroidInstanced — full mesh (LOD0)
    public Material      billboardMaterial;  // AsteroidBillboard — impostor quad (LOD1)

    [Header("Asteroid Meshes (asteroid_1 .. asteroid_5)")]
    public Mesh[] asteroidPrototypes;

    [Header("Data")]
    public TextAsset csvFile;

    [Header("Culling")]
    [Tooltip("Asteroids further than this distance (scene units) from the camera are excluded entirely.")]
    public float maxRenderDist = 1000f;

    [Header("LOD")]
    [Tooltip("Asteroids closer than this distance use the full mesh (LOD0). Beyond it they use the billboard (LOD1).")]
    public float lodDistance = 20f;

    // Number of world-space positions baked per orbit cycle.
    // Must match #define N_ORBIT_SAMPLES in OrbitCompute.compute.
    const int N_ORBIT_SAMPLES = 16;

    // ── GPU buffers ───────────────────────────────────────────────────────────

    // Per-prototype (LOD0 — full mesh)
    ComputeBuffer[]         _phaseBuffers;    // float2 {phase0, invPeriod} per asteroid
    ComputeBuffer[]         _orbitBuffers;    // baked positions, sample-major
    ComputeBuffer[]         _compactBuffers;  // AppendStructuredBuffer<float4>: visible LOD0 asteroids
    GraphicsBuffer[]        _argsBuffers;     // indirect args for LOD0 draw calls
    MaterialPropertyBlock[] _propBlocks;

    // Shared (LOD1 — billboard)
    ComputeBuffer         _compactBufferLOD1; // AppendStructuredBuffer<float4>: visible LOD1 asteroids
    GraphicsBuffer        _argsBufferLOD1;    // indirect args for the single LOD1 draw call
    MaterialPropertyBlock _propBlockLOD1;
    Mesh                  _billboardQuad;     // procedurally generated unit quad

    // Shared utilities
    ComputeBuffer _countBuffer;   // 1-element bridge: CopyCount → WriteInstanceCount

    int[] _counts;
    int   _totalCount;
    int   _kernel;
    int   _writeKernel;

    readonly Vector4[] _frustumPlanes = new Vector4[6];

    // Used only during CPU baking — never uploaded to GPU.
    struct BakeData
    {
        public float sma, ecc, m0;
        public float phase0;
        public float invPeriod;
        public float sinArgPeri, cosArgPeri;
        public float sinInc,     cosInc;
        public float sinLongAsc, cosLongAsc;
        public int   modelIndex;
    }

    const float ORBIT_SCALE    = 20f;
    const float EARTH_YEAR_SEC = 60f; // 60 seconds per Earth orbit
    const float TWO_PI         = 2f * Mathf.PI;

    // ── Lifecycle ─────────────────────────────────────────────────────────────

    void Start()
    {
        if (!ValidateInputs()) return;

        int protoCount = asteroidPrototypes.Length;

        var perProto = new System.Collections.Generic.List<BakeData>[protoCount];
        for (int i = 0; i < protoCount; i++)
            perProto[i] = new System.Collections.Generic.List<BakeData>();

        ParseCSV(csvFile.text, perProto);

        _counts         = new int[protoCount];
        _phaseBuffers   = new ComputeBuffer[protoCount];
        _orbitBuffers   = new ComputeBuffer[protoCount];
        _compactBuffers = new ComputeBuffer[protoCount];
        _argsBuffers    = new GraphicsBuffer[protoCount];
        _propBlocks     = new MaterialPropertyBlock[protoCount];

        asteroidMaterial.enableInstancing  = true;
        billboardMaterial.enableInstancing = true;

        _kernel      = orbitShader.FindKernel("ComputeOrbits");
        _writeKernel = orbitShader.FindKernel("WriteInstanceCount");
        _countBuffer = new ComputeBuffer(1, sizeof(uint));

        _totalCount = 0;
        for (int p = 0; p < protoCount; p++)
        {
            _counts[p] = perProto[p].Count;
            _totalCount += _counts[p];

            if (_counts[p] == 0) { Debug.LogWarning($"[AsteroidBelt] Prototype {p} has 0 asteroids."); continue; }

            var phaseData = new Vector2[_counts[p]];
            for (int i = 0; i < _counts[p]; i++)
                phaseData[i] = new Vector2(perProto[p][i].phase0, perProto[p][i].invPeriod);
            _phaseBuffers[p] = new ComputeBuffer(_counts[p], 2 * sizeof(float));
            _phaseBuffers[p].SetData(phaseData);

            Vector3[] positions = BakeOrbits(perProto[p], _counts[p]);
            _orbitBuffers[p] = new ComputeBuffer(_counts[p] * N_ORBIT_SAMPLES, 3 * sizeof(float));
            _orbitBuffers[p].SetData(positions);

            // LOD0 compact buffer: float4 per visible close asteroid (xyz = pos, w = scale)
            _compactBuffers[p] = new ComputeBuffer(_counts[p], 4 * sizeof(float), ComputeBufferType.Append);

            var drawCmd = new GraphicsBuffer.IndirectDrawIndexedArgs[1];
            drawCmd[0].indexCountPerInstance = asteroidPrototypes[p].GetIndexCount(0);
            drawCmd[0].instanceCount         = 0;
            drawCmd[0].startIndex            = asteroidPrototypes[p].GetIndexStart(0);
            drawCmd[0].baseVertexIndex       = asteroidPrototypes[p].GetBaseVertex(0);
            drawCmd[0].startInstance         = 0;
            _argsBuffers[p] = new GraphicsBuffer(
                GraphicsBuffer.Target.IndirectArguments | GraphicsBuffer.Target.Raw,
                1, GraphicsBuffer.IndirectDrawIndexedArgs.size);
            _argsBuffers[p].SetData(drawCmd);

            _propBlocks[p] = new MaterialPropertyBlock();
            _propBlocks[p].SetBuffer("_MatricesLOD0", _compactBuffers[p]);
        }

        // ── LOD1 shared billboard setup ───────────────────────────────────────
        // One compact buffer for ALL far asteroids across all prototypes — they
        // all render as the same billboard quad, so per-prototype separation isn't needed.
        _compactBufferLOD1 = new ComputeBuffer(_totalCount, 4 * sizeof(float), ComputeBufferType.Append);

        _billboardQuad = CreateBillboardQuad();
        var drawCmdLOD1 = new GraphicsBuffer.IndirectDrawIndexedArgs[1];
        drawCmdLOD1[0].indexCountPerInstance = _billboardQuad.GetIndexCount(0);
        drawCmdLOD1[0].instanceCount         = 0;
        drawCmdLOD1[0].startIndex            = _billboardQuad.GetIndexStart(0);
        drawCmdLOD1[0].baseVertexIndex       = _billboardQuad.GetBaseVertex(0);
        drawCmdLOD1[0].startInstance         = 0;
        _argsBufferLOD1 = new GraphicsBuffer(
            GraphicsBuffer.Target.IndirectArguments | GraphicsBuffer.Target.Raw,
            1, GraphicsBuffer.IndirectDrawIndexedArgs.size);
        _argsBufferLOD1.SetData(drawCmdLOD1);

        _propBlockLOD1 = new MaterialPropertyBlock();
        _propBlockLOD1.SetBuffer("_Matrices", _compactBufferLOD1);

        Debug.Log($"[AsteroidBelt] Ready — {_totalCount} asteroids, LOD split at {lodDistance} units.");
    }

    void Update()
    {
        if (_phaseBuffers == null) return;

        Camera  cam    = Camera.main;
        Vector3 camPos = cam != null ? cam.transform.position : Vector3.zero;
        if (cam != null) ExtractFrustumPlanes(cam, _frustumPlanes);

        orbitShader.SetFloat("_Time",          Time.time);
        orbitShader.SetVector("_CameraPos",    camPos);
        orbitShader.SetFloat("_MaxRenderDist", maxRenderDist);
        orbitShader.SetFloat("_LODDistance",   lodDistance);
        orbitShader.SetVectorArray("_FrustumPlanes", _frustumPlanes);

        Bounds bounds = new Bounds(Vector3.zero, Vector3.one * 10000f);

        // Reset the shared LOD1 buffer once before all per-prototype dispatches.
        _compactBufferLOD1.SetCounterValue(0);

        for (int p = 0; p < asteroidPrototypes.Length; p++)
        {
            if (_counts[p] == 0 || _phaseBuffers[p] == null) continue;

            _compactBuffers[p].SetCounterValue(0);

            orbitShader.SetBuffer(_kernel, "_PhaseData",       _phaseBuffers[p]);
            orbitShader.SetBuffer(_kernel, "_OrbitPositions",  _orbitBuffers[p]);
            orbitShader.SetBuffer(_kernel, "_MatricesLOD0",    _compactBuffers[p]);
            orbitShader.SetBuffer(_kernel, "_MatricesLOD1",    _compactBufferLOD1);
            orbitShader.SetInt("_AsteroidCount", _counts[p]);
            orbitShader.Dispatch(_kernel, Mathf.CeilToInt(_counts[p] / 64f), 1, 1);

            // Write LOD0 visible count into args buffer
            ComputeBuffer.CopyCount(_compactBuffers[p], _countBuffer, 0);
            orbitShader.SetBuffer(_writeKernel, "_VisibleCount", _countBuffer);
            orbitShader.SetBuffer(_writeKernel, "_ArgBuffer",    _argsBuffers[p]);
            orbitShader.Dispatch(_writeKernel, 1, 1, 1);

            Graphics.RenderMeshIndirect(
                new RenderParams(asteroidMaterial) { worldBounds = bounds, matProps = _propBlocks[p] },
                asteroidPrototypes[p], _argsBuffers[p]);
        }

        // Write LOD1 visible count and issue the single billboard draw call
        ComputeBuffer.CopyCount(_compactBufferLOD1, _countBuffer, 0);
        orbitShader.SetBuffer(_writeKernel, "_VisibleCount", _countBuffer);
        orbitShader.SetBuffer(_writeKernel, "_ArgBuffer",    _argsBufferLOD1);
        orbitShader.Dispatch(_writeKernel, 1, 1, 1);

        Graphics.RenderMeshIndirect(
            new RenderParams(billboardMaterial) { worldBounds = bounds, matProps = _propBlockLOD1 },
            _billboardQuad, _argsBufferLOD1);
    }

    void OnDestroy()
    {
        if (_phaseBuffers   != null) foreach (var b in _phaseBuffers)   b?.Release();
        if (_orbitBuffers   != null) foreach (var b in _orbitBuffers)   b?.Release();
        if (_compactBuffers != null) foreach (var b in _compactBuffers) b?.Release();
        if (_argsBuffers    != null) foreach (var b in _argsBuffers)    b?.Dispose();
        _compactBufferLOD1?.Release();
        _argsBufferLOD1?.Dispose();
        _countBuffer?.Release();
        if (_billboardQuad != null) Destroy(_billboardQuad);
    }

    // ── Orbit baking ──────────────────────────────────────────────────────────

    static Vector3[] BakeOrbits(System.Collections.Generic.List<BakeData> asteroids, int count)
    {
        var positions = new Vector3[count * N_ORBIT_SAMPLES];

        Parallel.For(0, count, i =>
        {
            BakeData d = asteroids[i];

            for (int s = 0; s < N_ORBIT_SAMPLES; s++)
            {
                float M = s / (float)N_ORBIT_SAMPLES * TWO_PI;

                float E    = M + d.ecc * Mathf.Sin(M);
                float sinE = Mathf.Sin(E);
                float cosE = Mathf.Cos(E);
                float nu   = E + 2f * d.ecc * sinE;

                float r  = d.sma * (1f - d.ecc * cosE);
                float xO = r * Mathf.Cos(nu);
                float yO = r * Mathf.Sin(nu);

                float x1 = xO * d.cosArgPeri - yO * d.sinArgPeri;
                float y1 = xO * d.sinArgPeri + yO * d.cosArgPeri;
                float yW = y1 * d.cosInc;
                float zW = y1 * d.sinInc;
                float xF = x1 * d.cosLongAsc - yW * d.sinLongAsc;
                float yF = x1 * d.sinLongAsc + yW * d.cosLongAsc;

                positions[s * count + i] = new Vector3(xF, zW, yF);
            }
        });

        return positions;
    }

    // ── Billboard quad ────────────────────────────────────────────────────────

    static Mesh CreateBillboardQuad()
    {
        var mesh = new Mesh { name = "AsteroidBillboardQuad" };
        mesh.SetVertices(new[]
        {
            new Vector3(-0.5f, -0.5f, 0f),
            new Vector3( 0.5f, -0.5f, 0f),
            new Vector3( 0.5f,  0.5f, 0f),
            new Vector3(-0.5f,  0.5f, 0f),
        });
        mesh.SetUVs(0, new[]
        {
            new Vector2(0f, 0f), new Vector2(1f, 0f),
            new Vector2(1f, 1f), new Vector2(0f, 1f),
        });
        mesh.SetTriangles(new[] { 0, 2, 1, 0, 3, 2 }, 0);
        return mesh;
    }

    // ── CSV parsing ───────────────────────────────────────────────────────────

    void ParseCSV(string text, System.Collections.Generic.List<BakeData>[] perProto)
    {
        string[] lines  = text.Split('\n');
        string[] header = lines[0].Trim().Split(',');

        int iSma  = Array.IndexOf(header, "semi_major_axis_AU");
        int iEcc  = Array.IndexOf(header, "eccentricity");
        int iInc  = Array.IndexOf(header, "inclination_deg");
        int iAsc  = Array.IndexOf(header, "long_asc_node");
        int iPeri = Array.IndexOf(header, "arg_perihelion");
        int iM0   = Array.IndexOf(header, "mean_anomaly");

        if (iSma < 0 || iEcc < 0 || iInc < 0 || iAsc < 0 || iPeri < 0 || iM0 < 0)
        {
            Debug.LogError("[AsteroidBelt] CSV missing expected columns. Check header row.");
            return;
        }

        int totalParsed = 0;
        int maxCol = Mathf.Max(iSma, iEcc, iInc, iAsc, iPeri, iM0);

        for (int i = 1; i < lines.Length; i++)
        {
            string line = lines[i].Trim();
            if (string.IsNullOrEmpty(line)) continue;

            string[] cols = line.Split(',');
            if (cols.Length <= maxCol) continue;

            if (!float.TryParse(cols[iSma],  out float smaAU))  continue;
            if (!float.TryParse(cols[iEcc],  out float ecc))    continue;
            if (!float.TryParse(cols[iInc],  out float incDeg)) continue;
            if (!float.TryParse(cols[iAsc],  out float asc))    continue;
            if (!float.TryParse(cols[iPeri], out float peri))   continue;
            if (!float.TryParse(cols[iM0],   out float m0))     continue;

            ecc = Mathf.Clamp(ecc, 0f, 0.99f);

            float incRad   = incDeg * Mathf.Deg2Rad;
            float period   = EARTH_YEAR_SEC * Mathf.Pow(smaAU, 1.5f);
            int   modelIdx = totalParsed % perProto.Length;

            perProto[modelIdx].Add(new BakeData
            {
                sma        = smaAU * ORBIT_SCALE,
                ecc        = ecc,
                m0         = m0,
                phase0     = m0 / TWO_PI,
                invPeriod  = 1f / Mathf.Max(0.1f, period),
                sinArgPeri = Mathf.Sin(peri),
                cosArgPeri = Mathf.Cos(peri),
                sinInc     = Mathf.Sin(incRad),
                cosInc     = Mathf.Cos(incRad),
                sinLongAsc = Mathf.Sin(asc),
                cosLongAsc = Mathf.Cos(asc),
                modelIndex = modelIdx
            });

            totalParsed++;
        }

        Debug.Log($"[AsteroidBelt] Parsed {totalParsed} asteroids from CSV.");
    }

    // ── Frustum plane extraction ──────────────────────────────────────────────

    static void ExtractFrustumPlanes(Camera cam, Vector4[] planes)
    {
        // GL.GetGPUProjectionMatrix converts Camera.projectionMatrix (which Unity always
        // returns in OpenGL convention) into the platform-specific GPU matrix.
        // On Metal (Apple Silicon) this applies a Y-axis flip and reversed-Z, so without
        // this call the top/bottom planes are inverted and near/far are swapped — producing
        // the "asteroids disappearing in a circle" symptom on Mac.
        Matrix4x4 proj = GL.GetGPUProjectionMatrix(cam.projectionMatrix, false);
        Matrix4x4 vp   = proj * cam.worldToCameraMatrix;

        Vector4 r0 = vp.GetRow(0), r1 = vp.GetRow(1),
                r2 = vp.GetRow(2), r3 = vp.GetRow(3);

        planes[0] = r3 + r0; // left
        planes[1] = r3 - r0; // right
        planes[2] = r3 + r1; // bottom
        planes[3] = r3 - r1; // top

        // Metal and Vulkan use a reversed depth buffer (Z=1 at near, Z=0 at far).
        // GL.GetGPUProjectionMatrix already bakes the Z-reversal into the matrix, so
        // the Gribb-Hartmann near/far assignment must be swapped on these platforms.
        if (SystemInfo.usesReversedZBuffer)
        {
            planes[4] = r3 - r2; // near
            planes[5] = r3 + r2; // far
        }
        else
        {
            planes[4] = r3 + r2; // near
            planes[5] = r3 - r2; // far
        }
    }

    // ── Validation ────────────────────────────────────────────────────────────

    bool ValidateInputs()
    {
        if (orbitShader == null)      { Debug.LogError("[AsteroidBelt] orbitShader not assigned.");      return false; }
        if (asteroidMaterial == null) { Debug.LogError("[AsteroidBelt] asteroidMaterial not assigned."); return false; }
        if (billboardMaterial == null){ Debug.LogError("[AsteroidBelt] billboardMaterial not assigned."); return false; }
        if (csvFile == null)          { Debug.LogError("[AsteroidBelt] csvFile not assigned.");           return false; }
        if (asteroidPrototypes == null || asteroidPrototypes.Length == 0)
                                      { Debug.LogError("[AsteroidBelt] No asteroid prototypes assigned."); return false; }
        return true;
    }
}
