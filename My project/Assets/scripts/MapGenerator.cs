using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using System.Linq;
using System;

/// <summary>
/// Generates a city layout using a Voronoi diagram for the main road network backbone.
/// Connects additional points identified by Perlin noise to the network using MST.
/// Aims for a grid-like structure, connects to map edges, and minimizes building demolition.
/// Optimized with Coroutines for smoother generation on larger maps.
/// </summary>
public class MapGenerator : MonoBehaviour
{
    #region Public Inspector Variables

    [Header("Prefabs & Parent")]
    public GameObject roadPrefab;
    public GameObject residentialPrefab;
    public GameObject commercialPrefab;
    public GameObject industrialPrefab;
    public Transform mapParent;

    [Header("Map Dimensions")]
    public int mapWidth = 50;
    public int mapHeight = 50;

    // --- Voronoi Options ---
    [Header("Voronoi Road Network Options")]
    [Tooltip("Approximate spacing between Voronoi site points. Smaller = denser network.")]
    public int voronoiSiteSpacing = 15;
    [Tooltip("Maximum random offset (jitter) applied to Voronoi sites. Lower values promote grid-like appearance.")]
    public int voronoiSiteJitter = 3;

    // --- Perlin Noise Branch Options ---
    [Header("Noise Branch Connection Options")]
    [Tooltip("Scale of Perlin noise used to find branch points.")]
    public float branchNoiseScale = 10.0f;
    [Tooltip("Noise threshold for selecting points to connect via MST. Higher = fewer points selected.")]
    [Range(0.0f, 1.0f)] public float noiseBranchThreshold = 0.8f; // Probability control

    // --- Edge Connection ---
    [Header("Map Edge Connection")]
    [Tooltip("Ensure roads connect to all four map edges.")]
    public bool ensureEdgeConnections = true;
    [Tooltip("How far from the edge center to search for the nearest road/site to connect.")]
    public int edgeConnectionSearchRadius = 5;

    // --- Common Settings ---
    [Header("Performance & Features")]
    [Tooltip("Offset for Perlin noise sampling.")]
    public Vector2 noiseOffset = Vector2.zero; // Randomized in Start()
    [Tooltip("Maximum GameObjects activated per frame during async generation.")]
    public int objectsPerFrame = 200;
    [Tooltip("Yield Coroutine Frequency: Lower value = yield more often (smoother but potentially slower total time).")]
    [Range(100, 5000)] public int yieldBatchSize = 500; // How many items/tiles to process before yielding
    [Tooltip("Generate city over multiple frames? If false, generation will block the main thread.")]
    public bool asyncGeneration = true; // Master switch for yielding

    #endregion

    #region Private Variables
    private enum ZoneType { Empty, Residential, Commercial, Industrial, Road }
    private ZoneType[,] map;
    private Queue<GameObject> generationQueue = new Queue<GameObject>();
    private Dictionary<Vector2Int, GameObject> trackedObjects = new Dictionary<Vector2Int, GameObject>();

    private readonly List<Vector2Int> directions; // Cardinal
    private readonly List<Vector2Int> allDirections; // Cardinal + Diagonal

    private List<Vector2Int> voronoiSites = new List<Vector2Int>();
    private HashSet<Vector2Int> roadTiles = new HashSet<Vector2Int>(); // Master set of all road tiles

    // For MST connection
    private List<Vector2Int> noisePoints = new List<Vector2Int>();

    #endregion

    // Constructor
    public MapGenerator()
    {
        directions = new List<Vector2Int> { Vector2Int.right, Vector2Int.left, Vector2Int.up, Vector2Int.down };
        allDirections = new List<Vector2Int> {
            Vector2Int.right, Vector2Int.left, Vector2Int.up, Vector2Int.down,
            new Vector2Int(1, 1), new Vector2Int(1, -1), new Vector2Int(-1, 1), new Vector2Int(-1, -1)
        };
    }

    #region Unity Lifecycle Methods
    IEnumerator Start()
    {
        if (mapParent == null) { GameObject p = new GameObject("GeneratedCity"); mapParent = p.transform; Debug.LogWarning("Created Map Parent 'GeneratedCity'."); }
        ClearPreviousGeneration();
        if (mapWidth <= 0 || mapHeight <= 0)
        {
            Debug.LogError("Map dimensions must be positive.");
            yield break;
        }
        map = new ZoneType[mapWidth, mapHeight];
        noiseOffset = new Vector2(UnityEngine.Random.Range(0f, 10000f), UnityEngine.Random.Range(0f, 10000f));
        Debug.Log($"Using Noise Offset: {noiseOffset}");

        // --- Start Generation ---
        float startTime = Time.realtimeSinceStartup;
        Debug.Log("Starting City Generation...");

        yield return StartCoroutine(GenerateCity());

        float endTime = Time.realtimeSinceStartup;
        Debug.Log($"City Generation Complete. Final tracked object count: {trackedObjects.Count}");
        Debug.Log($"Total Generation Time: {(endTime - startTime) * 1000:F2} ms");
    }

    void OnValidate()
    {
        if (voronoiSiteSpacing < 1) voronoiSiteSpacing = 1;
        if (mapWidth < 1) mapWidth = 1;
        if (mapHeight < 1) mapHeight = 1;
        if (edgeConnectionSearchRadius < 1) edgeConnectionSearchRadius = 1;
        if (yieldBatchSize < 10) yieldBatchSize = 10; // Prevent excessively frequent yields
    }
    #endregion

    #region Main Generation Pipeline (with internal yielding)
    IEnumerator GenerateCity()
    {
        // --- Step 1: Generate Voronoi Sites ---
        Debug.Log("Step 1: Generating Voronoi Sites...");
        yield return StartCoroutine(GenerateVoronoiSites()); // This coroutine handles its own yielding
        Debug.Log($"Step 1 Complete. Found {voronoiSites.Count} Voronoi sites.");
        if (voronoiSites.Count < 2) { Debug.LogError("Insufficient Voronoi sites. Stopping."); yield break; }

        // --- Step 2: Compute Voronoi Edges ---
        Debug.Log("Step 2: Computing Voronoi Edges...");
        yield return StartCoroutine(ComputeVoronoiEdges());
        Debug.Log($"Step 2 Complete. Voronoi road tiles: {roadTiles.Count}");
        if (roadTiles.Count == 0) Debug.LogWarning("No road tiles generated from Voronoi edges.");
        if (asyncGeneration) yield return StartCoroutine(InstantiateQueuedObjects());

        // --- Step 3: Select Noise Points ---
        Debug.Log($"Step 3: Selecting Noise Points (Threshold: {noiseBranchThreshold})...");
        yield return StartCoroutine(SelectNoisePoints());
        Debug.Log($"Step 3 Complete. Found {noisePoints.Count} noise points.");

        // --- Step 4: Connect Noise Points via MST ---
        if (noisePoints.Count > 0 && roadTiles.Count > 0)
        {
            Debug.Log("Step 4: Connecting Noise Points via MST...");
            yield return StartCoroutine(ConnectNoiseAndRoadsWithMST());
            Debug.Log($"Step 4 Complete. Road tiles after MST: {roadTiles.Count}");
            if (asyncGeneration) yield return StartCoroutine(InstantiateQueuedObjects());
        }
        else Debug.Log("Step 4: Skipped MST Connection.");

        // --- Step 5: Ensure Map Edge Connections ---
        if (ensureEdgeConnections)
        {
            if (roadTiles.Count > 0 || voronoiSites.Count > 0)
            {
                Debug.Log("Step 5: Ensuring Map Edge Connections...");
                yield return StartCoroutine(EnsureMapEdgeConnections());
                Debug.Log($"Step 5 Complete. Road tiles after edge connection: {roadTiles.Count}");
                if (asyncGeneration) yield return StartCoroutine(InstantiateQueuedObjects());
            }
            else Debug.LogWarning("Step 5: Skipped Edge Connections (No roads/sites).");
        }
        else Debug.Log("Step 5: Skipped Map Edge Connections.");

        // --- Step 6: Connectivity Pass 1 ---
        Debug.Log("Step 6: Ensuring Road Connectivity (Pass 1)...");
        yield return StartCoroutine(EnsureRoadConnectivity());
        if (asyncGeneration) yield return StartCoroutine(InstantiateQueuedObjects());

        // --- Step 7: Prune Wide Roads ---
        Debug.Log("Step 7: Pruning Wide Roads...");
        yield return StartCoroutine(PruneWideRoads());
        // No instantiation needed here as it only removes objects

        // --- Step 8: Connectivity Pass 2 ---
        Debug.Log("Step 8: Ensuring Road Connectivity (Pass 2 - Post Pruning)...");
        yield return StartCoroutine(EnsureRoadConnectivity());
        if (asyncGeneration) yield return StartCoroutine(InstantiateQueuedObjects());

        // --- Step 9: Buildings & Accessibility ---
        Debug.Log("Step 9a: Placing Initial Buildings...");
        yield return StartCoroutine(PlaceInitialBuildings());
        if (asyncGeneration) yield return StartCoroutine(InstantiateQueuedObjects());

        Debug.Log("Step 9b: Ensuring Zone Accessibility...");
        yield return StartCoroutine(EnsureZoneAccessibility());
        if (asyncGeneration) yield return StartCoroutine(InstantiateQueuedObjects());

        // --- Step 10: Remove Isolated Tiles ---
        Debug.Log("Step 10: Removing Final Isolated Tiles...");
        yield return StartCoroutine(RemoveIsolatedTiles());
        // No instantiation needed here

        // --- Step 11: Fill Remaining ---
        Debug.Log("Step 11: Filling Remaining Empty Tiles...");
        yield return StartCoroutine(FillRemainingEmptyTiles());

        // --- Step 12: Final Instantiation ---
        Debug.Log("Step 12: Instantiating All Remaining Objects...");
        yield return StartCoroutine(InstantiateQueuedObjects(true));

        Debug.Log("--- Generation Pipeline Finished ---");
    }
    #endregion

    #region Voronoi and Noise Point Generation (Yielding)
    IEnumerator GenerateVoronoiSites()
    {
        voronoiSites.Clear();
        int spacing = Mathf.Max(1, voronoiSiteSpacing);
        int jitter = Mathf.Max(0, voronoiSiteJitter);
        int count = 0;

        for (int x = spacing / 2; x < mapWidth; x += spacing)
        {
            for (int y = spacing / 2; y < mapHeight; y += spacing)
            {
                int jX = (jitter > 0) ? UnityEngine.Random.Range(-jitter, jitter + 1) : 0;
                int jY = (jitter > 0) ? UnityEngine.Random.Range(-jitter, jitter + 1) : 0;
                Vector2Int p = new Vector2Int(
                    Mathf.Clamp(x + jX, 0, mapWidth - 1),
                    Mathf.Clamp(y + jY, 0, mapHeight - 1)
                );
                if (!voronoiSites.Any(site => Mathf.Abs(site.x - p.x) <= 1 && Mathf.Abs(site.y - p.y) <= 1))
                {
                    voronoiSites.Add(p);
                }

                // --- Yielding ---
                count++;
                if (asyncGeneration && count % yieldBatchSize == 0) yield return null;
            }
        }
        // Ensure minimum sites if needed (same logic as before)
        if (voronoiSites.Count < 4 && mapWidth > 5 && mapHeight > 5)
        {
            voronoiSites.Add(new Vector2Int(mapWidth / 4, mapHeight / 4));
            voronoiSites.Add(new Vector2Int(3 * mapWidth / 4, mapHeight / 4));
            voronoiSites.Add(new Vector2Int(mapWidth / 4, 3 * mapHeight / 4));
            voronoiSites.Add(new Vector2Int(3 * mapWidth / 4, 3 * mapHeight / 4));
            voronoiSites = voronoiSites.Distinct().ToList();
            Debug.LogWarning($"Added default Voronoi sites. Count: {voronoiSites.Count}");
        }
    }

    IEnumerator ComputeVoronoiEdges()
    {
        // Requires at least 2 sites, checked earlier
        int processed = 0;
        HashSet<Vector2Int> potentialRoads = new HashSet<Vector2Int>();
        for (int x = 0; x < mapWidth; x++)
        {
            for (int y = 0; y < mapHeight; y++)
            {
                Vector2Int currentPos = new Vector2Int(x, y);
                int nearestSiteIndex = FindNearestSiteIndex(currentPos, voronoiSites);
                if (nearestSiteIndex < 0) continue;

                foreach (var dir in directions)
                {
                    Vector2Int neighborPos = currentPos + dir;
                    if (!IsInMap(neighborPos)) continue;
                    int neighborNearestSiteIndex = FindNearestSiteIndex(neighborPos, voronoiSites);
                    if (neighborNearestSiteIndex >= 0 && nearestSiteIndex != neighborNearestSiteIndex)
                    {
                        potentialRoads.Add(currentPos);
                        break;
                    }
                }
                processed++;
                if (asyncGeneration && processed % yieldBatchSize == 0) yield return null;
            }
        }

        // Apply roads (this part is fast, no yielding needed here)
        foreach (var roadPos in potentialRoads)
        {
            if (map[roadPos.x, roadPos.y] == ZoneType.Empty)
            {
                map[roadPos.x, roadPos.y] = ZoneType.Road;
                roadTiles.Add(roadPos);
                AddToQueue(roadPrefab, roadPos.x, roadPos.y);
            }
        }
    }

    int FindNearestSiteIndex(Vector2Int point, List<Vector2Int> sites)
    {
        if (sites == null || sites.Count == 0) return -1;
        int nearestIndex = -1;
        float minDistSq = float.MaxValue;
        for (int i = 0; i < sites.Count; i++)
        {
            float distSq = (sites[i] - point).sqrMagnitude;
            if (distSq < minDistSq)
            {
                minDistSq = distSq;
                nearestIndex = i;
            }
        }
        return nearestIndex;
    }
    Vector2Int? FindNearestSite(Vector2Int point, List<Vector2Int> sites)
    {
        int index = FindNearestSiteIndex(point, sites);
        return index >= 0 ? sites[index] : (Vector2Int?)null;
    }

    IEnumerator SelectNoisePoints()
    {
        noisePoints.Clear();
        float noiseOffsetX = noiseOffset.x; float noiseOffsetY = noiseOffset.y;
        int checkedCount = 0;
        for (int x = 0; x < mapWidth; x++)
        {
            for (int y = 0; y < mapHeight; y++)
            {
                if (map[x, y] == ZoneType.Empty)
                {
                    float nX = (noiseOffsetX + (float)x / mapWidth * branchNoiseScale);
                    float nY = (noiseOffsetY + (float)y / mapHeight * branchNoiseScale);
                    float nV = Mathf.PerlinNoise(nX, nY);
                    if (nV > noiseBranchThreshold) { noisePoints.Add(new Vector2Int(x, y)); }
                }
                // --- Yielding ---
                checkedCount++;
                if (asyncGeneration && checkedCount % yieldBatchSize == 0) yield return null;
            }
        }
    }
    #endregion

    #region MST Connection for Noise Points (Yielding)
    IEnumerator ConnectNoiseAndRoadsWithMST()
    {
        // Requires noisePoints and roadTiles, checked earlier

        // --- Build Graph Nodes --- (Fast)
        List<HashSet<Vector2Int>> roadComponents = FindAllRoadComponents();
        if (roadComponents.Count == 0) { Debug.LogWarning("No road components for MST."); yield break; }
        List<Vector2Int> mstNodes = new List<Vector2Int>(noisePoints);
        Dictionary<Vector2Int, HashSet<Vector2Int>> componentLookup = new Dictionary<Vector2Int, HashSet<Vector2Int>>();
        foreach (var component in roadComponents) { if (component.Count > 0) { Vector2Int rep = component.First(); mstNodes.Add(rep); componentLookup[rep] = component; } }
        if (mstNodes.Count < 2) { Debug.Log("Not enough nodes for MST."); yield break; }

        // --- Calculate Potential Edges (Slow part) ---
        List<GraphEdge> potentialEdges = new List<GraphEdge>();
        int edgeCalcCount = 0;
        for (int i = 0; i < mstNodes.Count; i++)
        {
            for (int j = i + 1; j < mstNodes.Count; j++)
            {
                Vector2Int nodeA = mstNodes[i]; Vector2Int nodeB = mstNodes[j];
                bool nodeAIsRoadRep = componentLookup.ContainsKey(nodeA); bool nodeBIsRoadRep = componentLookup.ContainsKey(nodeB);
                Vector2Int connectionPointA = nodeA; Vector2Int connectionPointB = nodeB; int distance;
                if (nodeAIsRoadRep && !nodeBIsRoadRep) { connectionPointA = FindNearestPointInSet(nodeB, componentLookup[nodeA]) ?? nodeA; distance = ManhattanDistance(connectionPointA, nodeB); }
                else if (!nodeAIsRoadRep && nodeBIsRoadRep) { connectionPointB = FindNearestPointInSet(nodeA, componentLookup[nodeB]) ?? nodeB; distance = ManhattanDistance(nodeA, connectionPointB); }
                else if (nodeAIsRoadRep && nodeBIsRoadRep) { connectionPointA = FindNearestPointInSet(nodeB, componentLookup[nodeA]) ?? nodeA; connectionPointB = FindNearestPointInSet(nodeA, componentLookup[nodeB]) ?? nodeB; distance = ManhattanDistance(connectionPointA, connectionPointB); }
                else { distance = ManhattanDistance(nodeA, nodeB); }
                potentialEdges.Add(new GraphEdge(nodeA, nodeB, distance));

                // --- Yielding during edge calculation ---
                edgeCalcCount++;
                if (asyncGeneration && edgeCalcCount % (yieldBatchSize * 2) == 0)
                { // Yield less often here as inner loop is fast
                  // Debug.Log($"... calculated {edgeCalcCount} potential MST edges");
                    yield return null;
                }
            }
        }
        Debug.Log($"Calculated {potentialEdges.Count} potential MST edges.");

        // --- Compute MST (Fast unless astronomically many edges/nodes) ---
        List<GraphEdge> mstEdges = ComputePrimMST(mstNodes, potentialEdges); // No yield inside Prim's for simplicity
        Debug.Log($"Computed MST with {mstEdges.Count} edges.");

        // --- Draw Roads from MST Edges ---
        int edgesDrawn = 0;
        foreach (GraphEdge edge in mstEdges)
        {
            // ... (logic to find drawStart/drawEnd is the same) ...
            Vector2Int startNode = edge.nodeA; Vector2Int endNode = edge.nodeB; Vector2Int drawStart = startNode; Vector2Int drawEnd = endNode;
            bool startIsRoadRep = componentLookup.ContainsKey(startNode); bool endIsRoadRep = componentLookup.ContainsKey(endNode);
            if (startIsRoadRep && !endIsRoadRep) { drawStart = FindNearestPointInSet(endNode, componentLookup[startNode]) ?? startNode; }
            else if (!startIsRoadRep && endIsRoadRep) { drawEnd = FindNearestPointInSet(startNode, componentLookup[endNode]) ?? endNode; }
            else if (startIsRoadRep && endIsRoadRep) { drawStart = FindNearestPointInSet(endNode, componentLookup[startNode]) ?? startNode; drawEnd = FindNearestPointInSet(startNode, componentLookup[endNode]) ?? endNode; }

            HashSet<Vector2Int> pathTiles = new HashSet<Vector2Int>();
            DrawStraightRoadPath(drawStart, drawEnd, pathTiles); // L-shaped path

            foreach (var roadPos in pathTiles)
            {
                if (!IsInMap(roadPos)) continue;
                if (map[roadPos.x, roadPos.y] != ZoneType.Road)
                {
                    if (map[roadPos.x, roadPos.y] != ZoneType.Empty) { RemoveTrackedObject(roadPos); }
                    map[roadPos.x, roadPos.y] = ZoneType.Road;
                    roadTiles.Add(roadPos);
                    AddToQueue(roadPrefab, roadPos.x, roadPos.y);
                }
            }
            // --- Yielding during drawing ---
            edgesDrawn++;
            // Yield more often during drawing as it affects queue/instantiation
            if (asyncGeneration && edgesDrawn % (yieldBatchSize / 10 + 1) == 0) yield return null;
        }
    }

    // ComputePrimMST - No internal yielding added for simplicity, usually fast enough
    List<GraphEdge> ComputePrimMST(List<Vector2Int> nodes, List<GraphEdge> edges)
    {
        List<GraphEdge> mstResult = new List<GraphEdge>();
        if (nodes.Count == 0) return mstResult;
        HashSet<Vector2Int> inMST = new HashSet<Vector2Int>();
        SimplePriorityQueue<GraphEdge> edgeQueue = new SimplePriorityQueue<GraphEdge>();
        Vector2Int startNode = nodes[0]; inMST.Add(startNode);
        foreach (var edge_iter in edges)
        {
            Vector2Int otherNode = Vector2Int.zero;
            bool connectsToStart = false;
            if (edge_iter.nodeA == startNode && !inMST.Contains(edge_iter.nodeB))
            {
                otherNode = edge_iter.nodeB; connectsToStart = true;
            }
            else if (edge_iter.nodeB == startNode && !inMST.Contains(edge_iter.nodeA))
            {
                otherNode = edge_iter.nodeA; connectsToStart = true;
            }
            if (connectsToStart) edgeQueue.Enqueue(edge_iter, edge_iter.cost);
        }
        while (inMST.Count < nodes.Count && !edgeQueue.IsEmpty)
        {
            GraphEdge cheapestEdge = edgeQueue.Dequeue();
            Vector2Int nodeToAdd = Vector2Int.zero;
            bool edgeCrossesCut = false;
            if (inMST.Contains(cheapestEdge.nodeA) && !inMST.Contains(cheapestEdge.nodeB))
            {
                nodeToAdd = cheapestEdge.nodeB; edgeCrossesCut = true;
            }
            else if (!inMST.Contains(cheapestEdge.nodeA) && inMST.Contains(cheapestEdge.nodeB))
            { nodeToAdd = cheapestEdge.nodeA; edgeCrossesCut = true; }
            if (edgeCrossesCut)
            {
                mstResult.Add(cheapestEdge); inMST.Add(nodeToAdd);
                foreach (var edge_iter in edges)
                {
                    Vector2Int otherNode = Vector2Int.zero;
                    bool connectsToNew = false;
                    if (edge_iter.nodeA == nodeToAdd && !inMST.Contains(edge_iter.nodeB))
                    { otherNode = edge_iter.nodeB; connectsToNew = true; }
                    else if (edge_iter.nodeB == nodeToAdd && !inMST.Contains(edge_iter.nodeA))
                    { otherNode = edge_iter.nodeA; connectsToNew = true; }
                    if (connectsToNew) edgeQueue.Enqueue(edge_iter, edge_iter.cost);
                }
            }
        }
        if (inMST.Count != nodes.Count) { Debug.LogWarning($"MST incomplete. Nodes: {inMST.Count}/{nodes.Count}"); }
        return mstResult;
    }

    // FindNearestPointInSet - Fast, no yield needed
    Vector2Int? FindNearestPointInSet(Vector2Int startPoint, HashSet<Vector2Int> targetPoints)
    {
        if (targetPoints == null || targetPoints.Count == 0) return null; Vector2Int? nearest = null; int minDistance = int.MaxValue; foreach (Vector2Int target in targetPoints) { int distance = ManhattanDistance(startPoint, target); if (distance < minDistance) { minDistance = distance; nearest = target; } if (minDistance <= 1) break; }
        return nearest;
    }
    #endregion

    #region Edge Connection (Yielding per edge)
    IEnumerator EnsureMapEdgeConnections()
    {
        List<Vector2Int> edgeAnchors = new List<Vector2Int>();
        edgeAnchors.Add(new Vector2Int(mapWidth / 2, mapHeight - 1)); edgeAnchors.Add(new Vector2Int(mapWidth / 2, 0));
        edgeAnchors.Add(new Vector2Int(0, mapHeight / 2)); edgeAnchors.Add(new Vector2Int(mapWidth - 1, mapHeight / 2));

        foreach (var edgePoint in edgeAnchors)
        {
            // ConnectToEdge itself is now a coroutine that might yield internally if path is long
            yield return StartCoroutine(ConnectToEdge(edgePoint));
            // Optional: yield here too if connecting many edge points
            // if(asyncGeneration) yield return null;
        }
    }

    // ConnectToEdge now yields *after* applying the path tiles
    IEnumerator ConnectToEdge(Vector2Int edgePoint)
    {
        Vector2Int? startPoint = FindNearestRoadNearPoint(edgePoint, edgeConnectionSearchRadius);
        if (!startPoint.HasValue) { startPoint = FindNearestSite(edgePoint, voronoiSites); if (!startPoint.HasValue) { /*Debug.LogWarning($"No road/site near edge {edgePoint}.");*/ yield break; } }

        HashSet<Vector2Int> pathTiles = new HashSet<Vector2Int>();
        DrawGridPath(startPoint.Value, edgePoint, pathTiles); // Uses cardinal path helper

        int appliedCount = 0;
        foreach (var roadPos in pathTiles)
        {
            if (!IsInMap(roadPos)) continue;
            if (map[roadPos.x, roadPos.y] != ZoneType.Road)
            {
                if (map[roadPos.x, roadPos.y] != ZoneType.Empty) { RemoveTrackedObject(roadPos); }
                map[roadPos.x, roadPos.y] = ZoneType.Road; roadTiles.Add(roadPos); AddToQueue(roadPrefab, roadPos.x, roadPos.y); appliedCount++;
            }
        }
        // --- Yielding after applying path ---
        if (asyncGeneration && appliedCount > 0) yield return null;
    }

    // DrawGridPath - Fast, no yield needed
    void DrawGridPath(Vector2Int start, Vector2Int end, HashSet<Vector2Int> pathTilesSet)
    { /* ... same ... */
        Vector2Int current = start; pathTilesSet.Add(current);
        while (current != end)
        {
            int dx = end.x - current.x; int dy = end.y - current.y; Vector2Int step = Vector2Int.zero;
            if (Mathf.Abs(dx) > Mathf.Abs(dy)) { step = new Vector2Int((int)Mathf.Sign(dx), 0); } else if (Mathf.Abs(dy) > Mathf.Abs(dx)) { step = new Vector2Int(0, (int)Mathf.Sign(dy)); } else if (dx != 0) { step = new Vector2Int((int)Mathf.Sign(dx), 0); } else if (dy != 0) { step = new Vector2Int(0, (int)Mathf.Sign(dy)); } else { break; }
            current += step; if (IsInMap(current)) { pathTilesSet.Add(current); } else { break; }
        }
        pathTilesSet.Add(end);
    }

    // FindNearestRoadNearPoint - Minor yield added if radius is huge
    Vector2Int? FindNearestRoadNearPoint(Vector2Int center, int radius)
    {
        Vector2Int? nearest = null; float minDistanceSq = float.MaxValue;
        int checkedCount = 0;
        for (int x = Mathf.Max(0, center.x - radius); x <= Mathf.Min(mapWidth - 1, center.x + radius); x++)
        {
            for (int y = Mathf.Max(0, center.y - radius); y <= Mathf.Min(mapHeight - 1, center.y + radius); y++)
            {
                Vector2Int currentPos = new Vector2Int(x, y);
                if (roadTiles.Contains(currentPos)) { float distSq = (currentPos - center).sqrMagnitude; if (distSq < minDistanceSq) { minDistanceSq = distSq; nearest = currentPos; } }
                // --- Yielding (only if search area is massive) ---
                checkedCount++;
            }
        }
        return nearest;
    }
    #endregion

    #region Building Placement Step (Yielding)
    IEnumerator PlaceInitialBuildings()
    {
        int placedCount = 0; int checkedCount = 0;
        float placementChance = 0.3f;
        for (int x = 0; x < mapWidth; x++)
        {
            for (int y = 0; y < mapHeight; y++)
            {
                checkedCount++;
                if (map[x, y] == ZoneType.Empty && UnityEngine.Random.value < placementChance)
                {
                    ZoneType buildingType = DetermineBuildingType();
                    map[x, y] = buildingType;
                    AddToQueue(GetZonePrefab(buildingType), x, y);
                    placedCount++;
                }
                // --- Yielding ---
                if (asyncGeneration && checkedCount % yieldBatchSize == 0) yield return null;
            }
        }
        // Debug.Log($"Placed {placedCount} initial buildings.");
    }
    #endregion

    #region Common Post-Processing Steps (Yielding)

    // Step 6 & 8: Global Road Connectivity (Yields per component)
    IEnumerator EnsureRoadConnectivity()
    {
        List<HashSet<Vector2Int>> roadComponents = FindAllRoadComponents(); // O(W*H), no yield inside
        if (roadComponents.Count <= 1) { yield break; }
        // Debug.Log($"Found {roadComponents.Count} road components. Connecting...");
        roadComponents = roadComponents.OrderByDescending(c => c.Count).ToList();
        HashSet<Vector2Int> mainNetwork = new HashSet<Vector2Int>(roadComponents[0]);
        int connectionsAttempted = 0;
        for (int i = 1; i < roadComponents.Count; i++)
        {
            HashSet<Vector2Int> currentComponent = roadComponents[i];
            if (currentComponent.Count == 0) continue;
            if (!currentComponent.All(node => mainNetwork.Contains(node)))
            {
                connectionsAttempted++;
                // --- Yielding after each A* connection attempt ---
                yield return StartCoroutine(ConnectComponentToNetwork(currentComponent, mainNetwork));
                // Optional: Instantiate progress immediately after connecting a component
                // if (asyncGeneration) yield return StartCoroutine(InstantiateQueuedObjects());
            }
            // Optional: Yield even if no connection was needed, to break up the loop itself
            // if (asyncGeneration && i % 5 == 0) yield return null;
        }
        // Debug.Log($"Finished connectivity. Attempted {connectionsAttempted} connections.");
        // No need to yield here, handled by pipeline
    }

    // FindAllRoadComponents - BFS/DFS, generally fast enough, no yield added
    List<HashSet<Vector2Int>> FindAllRoadComponents()
    { /* ... same ... */
        List<HashSet<Vector2Int>> components = new List<HashSet<Vector2Int>>(); HashSet<Vector2Int> visited = new HashSet<Vector2Int>();
        foreach (Vector2Int startPos in roadTiles) { if (!visited.Contains(startPos)) { HashSet<Vector2Int> newComponent = new HashSet<Vector2Int>(); Queue<Vector2Int> queue = new Queue<Vector2Int>(); queue.Enqueue(startPos); visited.Add(startPos); newComponent.Add(startPos); while (queue.Count > 0) { Vector2Int node = queue.Dequeue(); foreach (var dir in directions) { Vector2Int neighbor = node + dir; if (IsInMap(neighbor) && roadTiles.Contains(neighbor) && !visited.Contains(neighbor)) { visited.Add(neighbor); newComponent.Add(neighbor); queue.Enqueue(neighbor); } } } if (newComponent.Count > 0) components.Add(newComponent); } }
        return components;
    }

    // ConnectComponentToNetwork - A* is the bulk, yield is handled by calling function
    IEnumerator ConnectComponentToNetwork(HashSet<Vector2Int> componentToConnect, HashSet<Vector2Int> targetNetwork)
    { /* ... same A* logic ... */
        if (componentToConnect.Count == 0 || targetNetwork.Count == 0) yield break;
        Vector2Int startNode = componentToConnect.First();
        List<Vector2Int> path = FindPath(startNode, targetNetwork); // A* can be slow
        if (path != null && path.Count > 1) { foreach (var point in path) { if (!IsInMap(point)) continue; if (!roadTiles.Contains(point)) { if (map[point.x, point.y] != ZoneType.Empty) { RemoveTrackedObject(point); } map[point.x, point.y] = ZoneType.Road; roadTiles.Add(point); targetNetwork.Add(point); AddToQueue(roadPrefab, point.x, point.y); } } targetNetwork.UnionWith(componentToConnect); }
        // else { Debug.LogWarning($"EnsureConnectivity: A* path failed near {startNode}."); }
        // No yield here, yield happens after this coroutine finishes in EnsureRoadConnectivity
    }

    // Step 7: Prune Wide Roads (Yields per row)
    IEnumerator PruneWideRoads()
    {
        HashSet<Vector2Int> tilesToPrune = new HashSet<Vector2Int>();
        int processed = 0;
        for (int x = 0; x < mapWidth - 1; x++)
        {
            for (int y = 0; y < mapHeight - 1; y++)
            {
                Vector2Int p00 = new Vector2Int(x, y), p10 = new Vector2Int(x + 1, y), p01 = new Vector2Int(x, y + 1), p11 = new Vector2Int(x + 1, y + 1);
                if (roadTiles.Contains(p00) && roadTiles.Contains(p10) && roadTiles.Contains(p01) && roadTiles.Contains(p11)) { tilesToPrune.Add(p11); }
                // --- Yielding ---
                processed++;
                if (asyncGeneration && processed % yieldBatchSize == 0) yield return null;
            }
        }
        if (tilesToPrune.Count > 0) { /* Debug.Log($"Pruning {tilesToPrune.Count} road tiles."); */ foreach (var point in tilesToPrune) { if (roadTiles.Contains(point)) { map[point.x, point.y] = ZoneType.Empty; roadTiles.Remove(point); RemoveTrackedObject(point); } } }
        // No need to yield after removal, it's fast
    }

    // Step 9: Ensure Zone Accessibility (Yields per zone check)
    IEnumerator EnsureZoneAccessibility()
    {
        int processedZones = 0; List<Vector2Int> zonesNeedingAccess = new List<Vector2Int>();
        // Find zones (fast, no yield)
        for (int x = 0; x < mapWidth; x++) { for (int y = 0; y < mapHeight; y++) { ZoneType cz = map[x, y]; if (cz >= ZoneType.Residential && cz <= ZoneType.Industrial && !HasAdjacentRoad(x, y, true)) { zonesNeedingAccess.Add(new Vector2Int(x, y)); } } }
        if (zonesNeedingAccess.Count == 0) { yield break; }
        Debug.Log($"Found {zonesNeedingAccess.Count} buildings needing access. Connecting...");
        HashSet<Vector2Int> currentRoadNetwork = new HashSet<Vector2Int>(roadTiles);

        foreach (var zonePos in zonesNeedingAccess)
        {
            if (IsInMap(zonePos) && map[zonePos.x, zonePos.y] >= ZoneType.Residential && !HasAdjacentRoad(zonePos.x, zonePos.y, true))
            {
                // --- Yielding after each A* connection attempt ---
                yield return StartCoroutine(ConnectWithAStar(zonePos, currentRoadNetwork));
            }
            processedZones++;
            // Optional: Yield periodically even if no connection was needed
            // if (asyncGeneration && processedZones % (yieldBatchSize / 5) == 0) yield return null;
        }
        // Debug.Log("Finished ensuring zone accessibility.");
        // No yield needed here, handled by pipeline
    }

    // ConnectWithAStar - A* is the bulk, yield handled by caller
    IEnumerator ConnectWithAStar(Vector2Int start, HashSet<Vector2Int> targetNetwork)
    { /* ... same A* logic ... */
        if (targetNetwork.Count == 0) yield break;
        List<Vector2Int> path = FindPath(start, targetNetwork); // A* can be slow
        if (path != null && path.Count > 1) { foreach (var p in path) { if (!IsInMap(p)) continue; if (!roadTiles.Contains(p)) { if (map[p.x, p.y] != ZoneType.Empty) { RemoveTrackedObject(p); } map[p.x, p.y] = ZoneType.Road; roadTiles.Add(p); targetNetwork.Add(p); AddToQueue(roadPrefab, p.x, p.y); } } }
        // else { Debug.LogWarning($"EnsureAccessibility: A* failed path from {start}."); }
        // No yield here
    }

    // HasAdjacentRoad - Fast, no yield
    bool HasAdjacentRoad(int x, int y, bool includeDiagonals = false)
    { /* ... same ... */
        var dirsToCheck = includeDiagonals ? allDirections : directions; foreach (var dir in dirsToCheck) { Vector2Int nPos = new Vector2Int(x, y) + dir; if (IsInMap(nPos) && roadTiles.Contains(nPos)) { return true; } }
        return false;
    }

    // Step 10: Remove Final Isolated Tiles (Yields periodically)
    IEnumerator RemoveIsolatedTiles()
    {
        List<Vector2Int> tilesToRemove = new List<Vector2Int>();
        List<Vector2Int> tilesToCheck = new List<Vector2Int>();
        for (int x = 0; x < mapWidth; x++) for (int y = 0; y < mapHeight; y++) { if (map[x, y] != ZoneType.Empty) tilesToCheck.Add(new Vector2Int(x, y)); }

        int processedCount = 0;
        foreach (Vector2Int currentPos in tilesToCheck)
        {
            ZoneType currentType = map[currentPos.x, currentPos.y]; if (currentType == ZoneType.Empty) continue;
            int sameTypeNeighbors = 0;
            foreach (var dir in directions) { Vector2Int nPos = currentPos + dir; if (IsInMap(nPos) && map[nPos.x, nPos.y] == currentType) { sameTypeNeighbors++; } }
            if (sameTypeNeighbors < 1) { tilesToRemove.Add(currentPos); }
            // --- Yielding ---
            processedCount++;
            if (asyncGeneration && processedCount % yieldBatchSize == 0) yield return null;
        }
        if (tilesToRemove.Count > 0) { /* Debug.Log($"Removing {tilesToRemove.Count} isolated tiles."); */ foreach (var point in tilesToRemove) { if (IsInMap(point) && map[point.x, point.y] != ZoneType.Empty) { if (map[point.x, point.y] == ZoneType.Road) roadTiles.Remove(point); map[point.x, point.y] = ZoneType.Empty; RemoveTrackedObject(point); } } }
        // No yield needed after removal
    }

    // Step 11: Fill Remaining Empty Tiles (Yields periodically)
    IEnumerator FillRemainingEmptyTiles()
    {
        int filledCount = 0; int checkedCount = 0;
        for (int x = 0; x < mapWidth; x++)
        {
            for (int y = 0; y < mapHeight; y++)
            {
                checkedCount++;
                if (map[x, y] == ZoneType.Empty) { ZoneType bt = DetermineBuildingType(); map[x, y] = bt; AddToQueue(GetZonePrefab(bt), x, y); filledCount++; }
                // --- Yielding ---
                if (asyncGeneration && checkedCount % yieldBatchSize == 0) yield return null;
            }
        }
        // if(filledCount > 0) Debug.Log($"Filled {filledCount} final empty tiles.");
    }

    // Step 12: Instantiate Final Objects (Already handles async)
    IEnumerator InstantiateQueuedObjects(bool instantiateAll = false)
    {
        int count = 0; int frameCount = 0; int limit = instantiateAll ? int.MaxValue : objectsPerFrame;
        List<GameObject> batchToActivate = new List<GameObject>();
        while (generationQueue.Count > 0 && (instantiateAll || count < limit))
        { // Fixed loop condition for instantiateAll
            GameObject obj = generationQueue.Dequeue(); if (!instantiateAll) count++; if (obj == null) continue;
            bool shouldActivate = true;
            if (TryParsePositionFromName(obj.name, out Vector2Int pos))
            {
                ZoneType expectedType = IsInMap(pos) ? map[pos.x, pos.y] : ZoneType.Empty;
                if (expectedType == ZoneType.Empty) { if (trackedObjects.ContainsKey(pos) && trackedObjects[pos] == obj) trackedObjects.Remove(pos); Destroy(obj); shouldActivate = false; }
                else
                {
                    if (!trackedObjects.ContainsKey(pos) || trackedObjects[pos] != obj) { if (trackedObjects.ContainsKey(pos) && trackedObjects[pos] != null) Destroy(trackedObjects[pos]); trackedObjects[pos] = obj; }
                    GameObject expectedPrefab = GetZonePrefab(expectedType);
                    if (expectedPrefab != null && !obj.name.StartsWith(expectedPrefab.name)) { Debug.LogWarning($"Queue/Map mismatch at {pos}. Obj: {obj.name}, Map: {expectedType}. Replacing."); Destroy(obj); AddToQueue(expectedPrefab, pos.x, pos.y); shouldActivate = false; }
                }
            }
            else { Debug.LogWarning($"Cannot parse position: {obj.name}"); }
            if (shouldActivate)
            {
                batchToActivate.Add(obj); frameCount++;
                if (asyncGeneration && frameCount >= objectsPerFrame && !instantiateAll) { foreach (var o in batchToActivate) if (o != null) o.SetActive(true); batchToActivate.Clear(); yield return null; frameCount = 0; /* count = 0; - No, let count limit overall per call unless instantiateAll */ }
            }
        }
        if (batchToActivate.Count > 0) { foreach (var o in batchToActivate) if (o != null) o.SetActive(true); batchToActivate.Clear(); if (asyncGeneration) yield return null; }
    }

    #endregion

    #region A* Pathfinding Algorithm (No internal yielding)
    List<Vector2Int> FindPath(Vector2Int start, HashSet<Vector2Int> targets)
    {
        if (targets == null || targets.Count == 0) { return null; }
        if (targets.Contains(start)) { return new List<Vector2Int>() { start }; }
        SimplePriorityQueue<Vector2Int> openSet = new SimplePriorityQueue<Vector2Int>(); Dictionary<Vector2Int, Vector2Int> cameFrom = new Dictionary<Vector2Int, Vector2Int>(); Dictionary<Vector2Int, float> gScore = new Dictionary<Vector2Int, float>(); gScore[start] = 0; openSet.Enqueue(start, Heuristic(start, targets)); int iterations = 0; int maxIterations = mapWidth * mapHeight * 3;
        while (!openSet.IsEmpty) { iterations++; if (iterations > maxIterations) { Debug.LogError($"A* limit exceeded from {start}"); return null; } Vector2Int current = openSet.Dequeue(); if (targets.Contains(current)) { return ReconstructPath(cameFrom, current); } foreach (var dir in directions) { Vector2Int neighbor = current + dir; if (!IsInMap(neighbor)) continue; float tentativeGScore = GetGScore(gScore, current) + GetTerrainCost(neighbor); if (float.IsPositiveInfinity(tentativeGScore)) continue; if (tentativeGScore < GetGScore(gScore, neighbor)) { cameFrom[neighbor] = current; gScore[neighbor] = tentativeGScore; openSet.Enqueue(neighbor, tentativeGScore + Heuristic(neighbor, targets)); } } }
        return null;
    }
    float GetGScore(Dictionary<Vector2Int, float> gScoreDict, Vector2Int node) { return gScoreDict.TryGetValue(node, out float score) ? score : float.PositiveInfinity; }
    float GetTerrainCost(Vector2Int pos) { if (!IsInMap(pos)) return float.PositiveInfinity; if (roadTiles.Contains(pos)) return 1; switch (map[pos.x, pos.y]) { case ZoneType.Empty: return 5; case ZoneType.Residential: case ZoneType.Commercial: case ZoneType.Industrial: return 100; default: return float.PositiveInfinity; } }
    float Heuristic(Vector2Int a, HashSet<Vector2Int> targets) { float minDistance = float.MaxValue; if (targets.Count == 0) return 0; foreach (var target in targets) { minDistance = Mathf.Min(minDistance, ManhattanDistance(a, target)); } return minDistance; }
    int ManhattanDistance(Vector2Int a, Vector2Int b) { return Mathf.Abs(a.x - b.x) + Mathf.Abs(a.y - b.y); }
    List<Vector2Int> ReconstructPath(Dictionary<Vector2Int, Vector2Int> cameFrom, Vector2Int current) { List<Vector2Int> totalPath = new List<Vector2Int> { current }; while (cameFrom.ContainsKey(current)) { current = cameFrom[current]; totalPath.Add(current); } totalPath.Reverse(); return totalPath; }
    #endregion

    #region Auxiliary & Helper Methods (No yielding needed)
    ZoneType DetermineBuildingType() { int r = UnityEngine.Random.Range(0, 100); if (r < 50) return ZoneType.Residential; if (r < 80) return ZoneType.Commercial; return ZoneType.Industrial; }
    GameObject GetZonePrefab(ZoneType type) { switch (type) { case ZoneType.Residential: return residentialPrefab; case ZoneType.Commercial: return commercialPrefab; case ZoneType.Industrial: return industrialPrefab; case ZoneType.Road: return roadPrefab; case ZoneType.Empty: return null; default: return null; } }
    void AddToQueue(GameObject prefab, int x, int y) { if (prefab == null) return; Vector2Int pos = new Vector2Int(x, y); if (trackedObjects.ContainsKey(pos)) { RemoveTrackedObject(pos); } GameObject instance = Instantiate(prefab, new Vector3(x, 0, y), Quaternion.identity, mapParent); instance.name = $"{prefab.name}_{x}_{y}"; instance.SetActive(false); generationQueue.Enqueue(instance); trackedObjects.Add(pos, instance); }
    void RemoveTrackedObject(Vector2Int pos) { if (trackedObjects.TryGetValue(pos, out GameObject objToRemove)) { trackedObjects.Remove(pos); if (objToRemove != null) { if (Application.isPlaying) Destroy(objToRemove); else DestroyImmediate(objToRemove); } } roadTiles.Remove(pos); if (IsInMap(pos)) map[pos.x, pos.y] = ZoneType.Empty; /* Ensure map reflects removal */ }
    bool IsInMap(Vector2Int pos) { return pos.x >= 0 && pos.x < mapWidth && pos.y >= 0 && pos.y < mapHeight; }
    void DrawStraightRoadPath(Vector2Int start, Vector2Int end, HashSet<Vector2Int> pathTilesSet) { int xDir = (start.x == end.x) ? 0 : (int)Mathf.Sign(end.x - start.x); for (int x = start.x; x != end.x + xDir; x += xDir) { Vector2Int c = new Vector2Int(x, start.y); if (IsInMap(c)) pathTilesSet.Add(c); if (xDir == 0 || x == end.x) break; } int yDir = (start.y == end.y) ? 0 : (int)Mathf.Sign(end.y - start.y); for (int y = start.y; y != end.y + yDir; y += yDir) { Vector2Int c = new Vector2Int(end.x, y); if (IsInMap(c)) pathTilesSet.Add(c); if (yDir == 0 || y == end.y) break; } if (IsInMap(end)) pathTilesSet.Add(end); if (IsInMap(start)) pathTilesSet.Add(start); }
    bool TryParsePositionFromName(string name, out Vector2Int position) { position = Vector2Int.zero; try { string[] parts = name.Split('_'); if (parts.Length >= 3) { if (int.TryParse(parts[parts.Length - 2], out int x) && int.TryParse(parts[parts.Length - 1], out int y)) { position = new Vector2Int(x, y); return true; } } } catch { } return false; }
    void ClearPreviousGeneration() { Debug.Log("Clearing generation..."); if (mapParent != null) { foreach (Transform child in mapParent.Cast<Transform>().ToList()) { if (child != null) { if (Application.isPlaying) Destroy(child.gameObject); else DestroyImmediate(child.gameObject); } } } trackedObjects.Clear(); generationQueue.Clear(); if (map != null) System.Array.Clear(map, 0, map.Length); voronoiSites.Clear(); noisePoints.Clear(); roadTiles.Clear(); Debug.Log("Clear complete."); }
    #endregion

    #region Helper Classes (GraphEdge, SimplePriorityQueue - Unchanged)
    private class GraphEdge
    {
        public Vector2Int nodeA, nodeB; public int cost; public GraphEdge(Vector2Int a, Vector2Int b, int c) { nodeA = a; nodeB = b; cost = c; }
        public override bool Equals(object obj) { if (obj == null || GetType() != obj.GetType()) { return false; } GraphEdge other = (GraphEdge)obj; bool same = EqualityComparer<Vector2Int>.Default.Equals(nodeA, other.nodeA) && EqualityComparer<Vector2Int>.Default.Equals(nodeB, other.nodeB) && cost == other.cost; bool swapped = EqualityComparer<Vector2Int>.Default.Equals(nodeA, other.nodeB) && EqualityComparer<Vector2Int>.Default.Equals(nodeB, other.nodeA) && cost == other.cost; return same || swapped; }
        public override int GetHashCode() { int hA = nodeA.GetHashCode(); int hB = nodeB.GetHashCode(); int h1 = hA < hB ? hA : hB; int h2 = hA < hB ? hB : hA; unchecked { int hash = 17; hash = hash * 23 + h1; hash = hash * 23 + h2; hash = hash * 23 + cost.GetHashCode(); return hash; } }
    }
    public class SimplePriorityQueue<T>
    {
        private List<(T item, float priority)> elements = new List<(T item, float priority)>(); public int Count => elements.Count; public bool IsEmpty => elements.Count == 0; public void Enqueue(T item, float priority) { elements.Add((item, priority)); elements.Sort((a, b) => a.priority.CompareTo(b.priority)); }
        public T Dequeue() { if (IsEmpty) throw new System.InvalidOperationException("Queue empty."); T item = elements[0].item; elements.RemoveAt(0); return item; }
        public bool Contains(T item) => elements.Any(e => e.item.Equals(item));
    }
    #endregion
}