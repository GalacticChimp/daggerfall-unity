﻿// Project:         Daggerfall Tools For Unity
// Copyright:       Copyright (C) 2009-2015 Daggerfall Workshop
// Web Site:        http://www.dfworkshop.net
// License:         MIT License (http://www.opensource.org/licenses/mit-license.php)
// Source Code:     https://github.com/Interkarma/daggerfall-unity
// Original Author: Gavin Clayton (interkarma@dfworkshop.net)
// Contributors:    LypyL
// 
// Notes:
//

//#define SHOW_LAYOUT_TIMES

using UnityEngine;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using DaggerfallConnect;
using DaggerfallConnect.Arena2;
using DaggerfallConnect.Utility;
using DaggerfallWorkshop.Game;
using DaggerfallWorkshop.Utility;

namespace DaggerfallWorkshop
{
    /// <summary>
    /// Manages terrains for streaming world at runtime.
    /// At runtime only coordinates from LocalPlayerGPS will be used. Be sure to apply desired preview to PlayerGPS.
    /// Terrain tiles are spread outwards from a centre tile up to TerrainDistance around player.
    /// Terrains will be marked inactive once they pass beyond TerrainDistance.
    /// Inactive terrains greater than TerrainDistance+1 from player will be recycled.
    /// Locations greater than TerrainDistance+1 from player will be destroyed.
    /// </summary>
    public class StreamingWorld : MonoBehaviour
    {
        #region Fields

        const int maxTerrainArray = 256;        // Maximum terrains in memory at any time

        // Local player GPS for tracking player virtual position
        public PlayerGPS LocalPlayerGPS;

        // Number of terrains tiles to load around central terrain tile
        // Each terrain is equivalent to one full city area of 8x8 RMB blocks
        // 1 : ( 2 * 1 + 1 ) * ( 2 * 1 + 1 ) = 9 tiles
        // 2 : ( 2 * 2 + 1 ) * ( 2 * 2 + 1 ) = 25 tiles
        // 3 : ( 2 * 3 + 1 ) * ( 2 * 3 + 1 ) = 49 tiles
        // 4 : ( 2 * 4 + 1 ) * ( 2 * 4 + 1 ) = 81 tiles
        [Range(1, 4)]
        public int TerrainDistance = 3;

        // This controls central map pixel for streaming world
        // Synced to PlayerGPS at runtime
        [Range(TerrainHelper.minMapPixelX, TerrainHelper.maxMapPixelX)]
        public int MapPixelX = TerrainHelper.defaultMapPixelX;
        [Range(TerrainHelper.minMapPixelY, TerrainHelper.maxMapPixelY)]
        public int MapPixelY = TerrainHelper.defaultMapPixelY;

        // Increasing scale will amplify height of small details
        // This scale is replicated to every managed terrain
        // Not recommended to use greater than default value
        [Range(TerrainHelper.minTerrainScale, TerrainHelper.maxTerrainScale)]
        public float TerrainScale = TerrainHelper.defaultTerrainScale;

        [HideInInspector]
        public string EditorFindLocationString = "Daggerfall/Privateer's Hold";

        //public bool AddLocationBeacon = false;
        public bool suppressWorld = false;
        public bool ShowDebugString = false;

        // List of terrain objects
        // Terrains all have the same format and will be endlessly recycled
        TerrainDesc[] terrainArray = new TerrainDesc[maxTerrainArray];
        Dictionary<int, int> terrainIndexDict = new Dictionary<int, int>();

        // List of location objects
        // Locations are unique and will be created/destroyed as needed
        List<LocationDesc> locationList = new List<LocationDesc>();

        // Compensation for floating origin
        Vector3 worldCompensation = Vector3.zero;

        Vector3 playerStartPos;
        Vector3 lastPlayerPos;

        DaggerfallUnity dfUnity;
        DFPosition mapOrigin;
        float sceneMapRatio;
        double worldX, worldZ;
        TerrainTexturing terrainTexturing = new TerrainTexturing();
        bool isReady = false;
        bool repositionPlayer;

        bool init;
        bool terrainUpdateRunning;
        bool updateLocatations;

        #endregion

        #region Properties

        public bool IsReady { get { return ReadyCheck(); } }
        public bool IsInit { get { return init; } }
        public Transform PlayerTerrainTransform { get { return GetPlayerTerrainTransform(); } }
        public Vector3 WorldCompensation { get { return worldCompensation; } }

        #endregion

        #region Structs/Enums

        struct TerrainDesc
        {
            public bool active;
            public bool updateData;
            public bool updateNature;
            public bool updateLocation;
            public bool hasLocation;
            public GameObject terrainObject;
            public GameObject billboardBatchObject;
            public int mapPixelX;
            public int mapPixelY;
        }

        struct LocationDesc
        {
            public GameObject locationObject;
            public int mapPixelX;
            public int mapPixelY;
        }

        #endregion

        #region Unity

        void Update()
        {
            // Cannot proceed until ready and player is set
            if (!ReadyCheck())
                return;

            // Handle moving to new map pixel or first-time init
            DFPosition curMapPixel = LocalPlayerGPS.CurrentMapPixel;
            if (curMapPixel.X != MapPixelX ||
                curMapPixel.Y != MapPixelY ||
                init)
            {
                //Debug.Log(string.Format("Entering new map pixel X={0}, Y={1}", curMapPixel.X, curMapPixel.Y));
                MapPixelX = curMapPixel.X;
                MapPixelY = curMapPixel.Y;
                UpdateWorld();
                InitPlayerTerrain();
                StartCoroutine(UpdateTerrains());
                updateLocatations = true;
                init = false;
            }

            // Exit if terrain data still updating
            // Raise location update flag any time terrain updates
            if (terrainUpdateRunning)
            {
                updateLocatations = true;
                return;
            }

            // Update locations when terrain data finished
            // This flag is raised during terrain update
            if (updateLocatations)
            {
                UpdateLocations();
                updateLocatations = false;
            }

            // Get distance player has moved in world map units and apply to world position
            Vector3 playerPos = LocalPlayerGPS.transform.position;
            worldX += (playerPos.x - lastPlayerPos.x) * sceneMapRatio;
            worldZ += (playerPos.z - lastPlayerPos.z) * sceneMapRatio;

            // Sync PlayerGPS to new world position
            LocalPlayerGPS.WorldX = (int)worldX;
            LocalPlayerGPS.WorldZ = (int)worldZ;

            // Update last player position
            lastPlayerPos = playerPos;
        }

        void OnGUI()
        {
            if (Event.current.type.Equals(EventType.Repaint) && ShowDebugString)
            {
                GUIStyle style = new GUIStyle();
                style.normal.textColor = Color.black;
                string text = GetDebugString();
                GUI.Label(new Rect(10, 30, 800, 24), text, style);
                GUI.Label(new Rect(8, 28, 800, 24), text);
            }
        }

        #endregion

        #region Public Methods

        // Teleport to new coordinates and re-init world
        public void TeleportToCoordinates(int mapPixelX, int mapPixelY)
        {
            DFPosition worldPos = MapsFile.MapPixelToWorldCoord(mapPixelX, mapPixelY);
            LocalPlayerGPS.WorldX = worldPos.X;
            LocalPlayerGPS.WorldZ = worldPos.Y;
            InitWorld(true);
            RaiseOnTeleportToCoordinatesEvent(worldPos);
        }

        // Offset world compensation for floating origin world
        public void OffsetWorldCompensation(Vector3 change, bool offsetLastPlayerPos = true)
        {
            worldCompensation += change;
            if (offsetLastPlayerPos)
                lastPlayerPos += change;
        }

        /// <summary>
        /// Returns terrain GameObject from mapPixel, or null if
        /// no terrain objects are mapped to that pixel
        /// </summary>
        public GameObject GetTerrainFromPixel(DFPosition mapPixel)
        {
            return GetTerrainFromPixel(mapPixel.X, mapPixel.Y);
        }

        /// <summary>
        /// Returns terrain GameObject from mapPixel, or null if
        /// no terrain objects are mapped to that pixel
        /// </summary>
        public GameObject GetTerrainFromPixel(int mapPixelX, int mapPixelY)//##Lypyl
        {
            //Get Key for terrain lookup
            int key = TerrainHelper.MakeTerrainKey(mapPixelX, mapPixelY);

            if (terrainIndexDict.ContainsKey(key))
                return terrainArray[terrainIndexDict[key]].terrainObject;
            else
                return null;
        }

        #endregion

        #region World Setup Methods

        // Init world at startup or when player teleports
        private void InitWorld(bool repositionPlayer = false)
        {
            // Cannot init world without a player, as world positions around player
            // Also do nothing if world is on hold at start
            if (LocalPlayerGPS == null || suppressWorld)
                return;

            // Player must be at origin on init for proper world sync
            // Starting position will be assigned when terrain ready
            LocalPlayerGPS.transform.position = Vector3.zero;

            // Init streaming world
            ClearStreamingWorld();
            worldCompensation = Vector3.zero;
            mapOrigin = LocalPlayerGPS.CurrentMapPixel;
            MapPixelX = mapOrigin.X;
            MapPixelY = mapOrigin.Y;
            playerStartPos = new Vector3(LocalPlayerGPS.transform.position.x, 0, LocalPlayerGPS.transform.position.z);
            lastPlayerPos = playerStartPos;

            // This value is the amount of scene player movement equivalent to 1 native world map unit
            sceneMapRatio = 1f / MeshReader.GlobalScale;

            // Set player world position to match PlayerGPS
            // StreamingWorld will then sync player to world
            worldX = LocalPlayerGPS.WorldX;
            worldZ = LocalPlayerGPS.WorldZ;

            init = true;
            this.repositionPlayer = repositionPlayer;
            RaiseOnInitWorldEvent();
        }

        // Place terrain tiles when player changes map pixels
        private void UpdateWorld()
        {
            int dim = TerrainDistance * 2 + 1;
            int xs = MapPixelX - TerrainDistance;
            int ys = MapPixelY - TerrainDistance;
            for (int y = ys; y < ys + dim; y++)
            {
                for (int x = xs; x < xs + dim; x++)
                {
                    PlaceTerrain(x, y);
                }
            }
        }

        // Fully init central terrain so player can be dropped into world as soon as possible
        private void InitPlayerTerrain()
        {
            if (!init)
                return;

#if SHOW_LAYOUT_TIMES
            System.Diagnostics.Stopwatch stopwatch = System.Diagnostics.Stopwatch.StartNew();
            long startTime = stopwatch.ElapsedMilliseconds;
#endif

            CollectLocations(true);
            int playerTerrainIndex = terrainIndexDict[TerrainHelper.MakeTerrainKey(MapPixelX, MapPixelY)];

            UpdateTerrainData(terrainArray[playerTerrainIndex]);
            terrainArray[playerTerrainIndex].updateData = false;

            UpdateTerrainNature(terrainArray[playerTerrainIndex]);
            terrainArray[playerTerrainIndex].updateNature = false;

            StartCoroutine(UpdateLocation(playerTerrainIndex, false));
            terrainArray[playerTerrainIndex].updateLocation = false;

#if SHOW_LAYOUT_TIMES
            long totalTime = stopwatch.ElapsedMilliseconds - startTime;
            DaggerfallUnity.LogMessage(string.Format("Time to init player terrain: {0}ms", totalTime), true);
#endif
        }

        private IEnumerator UpdateTerrains()
        {
            RaiseOnUpdateTerrainsStartEvent();
            terrainUpdateRunning = true;

#if SHOW_LAYOUT_TIMES
            System.Diagnostics.Stopwatch stopwatch = System.Diagnostics.Stopwatch.StartNew();
            long startTime = stopwatch.ElapsedMilliseconds;
#endif

            // Update terrain heights and nature billboards
            CollectTerrains();
            for (int i = 0; i < terrainArray.Length; i++)
            {
                if (terrainArray[i].active)
                {
                    if (terrainArray[i].updateData)
                    {
                        UpdateTerrainData(terrainArray[i]);
                        terrainArray[i].updateData = false;
                        if (!init)
                            yield return new WaitForEndOfFrame();
                    }
                    if (terrainArray[i].updateNature)
                    {
                        UpdateTerrainNature(terrainArray[i]);
                        terrainArray[i].updateNature = false;
                        if (!init)
                            yield return new WaitForEndOfFrame();
                    }
                }
            }

            // If this is an init we can use the load time to unload unused assets
            // Keeps memory usage much lower over time
            if (init)
                Resources.UnloadUnusedAssets();

            // Set terrain neighbours
            UpdateNeighbours();

#if SHOW_LAYOUT_TIMES
            long totalTime = stopwatch.ElapsedMilliseconds - startTime;
            DaggerfallUnity.LogMessage(string.Format("Time to update terrains: {0}ms", totalTime), true);
#endif

            terrainUpdateRunning = false;
            RaiseOnUpdateTerrainsEndEvent();
        }

        private void UpdateLocations()
        {
            CollectLocations();
            for (int i = 0; i < terrainArray.Length; i++)
            {
                if (terrainArray[i].active && terrainArray[i].hasLocation)
                    StartCoroutine(UpdateLocation(i, true));
            }
        }

        private IEnumerator UpdateLocation(int index, bool allowYield)
        {
            int key = TerrainHelper.MakeTerrainKey(terrainArray[index].mapPixelX, terrainArray[index].mapPixelY);
            int playerKey = TerrainHelper.MakeTerrainKey(MapPixelX, MapPixelY);
            bool isPlayerTerrain = (key == playerKey);

            if (terrainArray[index].active && terrainArray[index].hasLocation && terrainArray[index].updateLocation)
            {
                // Disable update flag
                terrainArray[index].updateLocation = false;

                // Create location object
                DFLocation location;
                GameObject locationObject = CreateLocationGameObject(index, out location);
                if (locationObject)
                {
                    // Add location object to dictionary
                    LocationDesc locationDesc = new LocationDesc();
                    locationDesc.locationObject = locationObject;
                    locationDesc.mapPixelX = terrainArray[index].mapPixelX;
                    locationDesc.mapPixelY = terrainArray[index].mapPixelY;
                    locationList.Add(locationDesc);

                    // Create billboard batch game objects for this location
                    // Streaming world always batches for performance, regardless of options
                    int natureArchive = ClimateSwaps.GetNatureArchive(LocalPlayerGPS.ClimateSettings.NatureSet, dfUnity.WorldTime.Now.SeasonValue);
                    TextureAtlasBuilder miscBillboardAtlas = dfUnity.MaterialReader.MiscBillboardAtlas;
                    DaggerfallBillboardBatch natureBillboardBatch = GameObjectHelper.CreateBillboardBatchGameObject(natureArchive, locationObject.transform);
                    DaggerfallBillboardBatch lightsBillboardBatch = GameObjectHelper.CreateBillboardBatchGameObject(TextureReader.LightsTextureArchive, locationObject.transform);
                    DaggerfallBillboardBatch animalsBillboardBatch = GameObjectHelper.CreateBillboardBatchGameObject(TextureReader.AnimalsTextureArchive, locationObject.transform);
                    DaggerfallBillboardBatch miscBillboardBatch = GameObjectHelper.CreateBillboardBatchGameObject(miscBillboardAtlas.AtlasMaterial, locationObject.transform);

                    // Set hide flags
                    natureBillboardBatch.hideFlags = HideFlags.HideAndDontSave;
                    lightsBillboardBatch.hideFlags = HideFlags.HideAndDontSave;
                    animalsBillboardBatch.hideFlags = HideFlags.HideAndDontSave;
                    miscBillboardBatch.hideFlags = HideFlags.HideAndDontSave;

                    // RMB blocks are laid out in centre of terrain to align with ground
                    //int width = location.Exterior.ExteriorData.Width;
                    //int height = location.Exterior.ExteriorData.Height;
                    //float offsetX = ((8 * RMBLayout.RMBSide) - (width * RMBLayout.RMBSide)) / 2;
                    //float offsetZ = ((8 * RMBLayout.RMBSide) - (height * RMBLayout.RMBSide)) / 2;
                    //Vector3 origin = new Vector3(offsetX, 2.0f * MeshReader.GlobalScale, offsetZ);

                    // Position RMB blocks inside terrain area
                    int width = location.Exterior.ExteriorData.Width;
                    int height = location.Exterior.ExteriorData.Height;
                    DFPosition tilePos = TerrainHelper.GetLocationTerrainTileOrigin(width, height);
                    Vector3 origin = new Vector3(tilePos.X * RMBLayout.RMBTileSide, 2.0f * MeshReader.GlobalScale, tilePos.Y * RMBLayout.RMBTileSide);

                    // Get location data
                    DaggerfallLocation dfLocation = locationObject.GetComponent<DaggerfallLocation>();

                    // Perform layout and yield after each block is placed
                    for (int y = 0; y < height; y++)
                    {
                        for (int x = 0; x < width; x++)
                        {
                            // Set block origin for billboard batches
                            // This causes next additions to be offset by this position
                            Vector3 blockOrigin = origin + new Vector3((x * RMBLayout.RMBSide), 0, (y * RMBLayout.RMBSide));
                            natureBillboardBatch.BlockOrigin = blockOrigin;
                            lightsBillboardBatch.BlockOrigin = blockOrigin;
                            animalsBillboardBatch.BlockOrigin = blockOrigin;
                            miscBillboardBatch.BlockOrigin = blockOrigin;

                            // Add block and yield
                            string blockName = dfUnity.ContentReader.BlockFileReader.CheckName(dfUnity.ContentReader.MapFileReader.GetRmbBlockName(ref location, x, y));
                            GameObject go = GameObjectHelper.CreateRMBBlockGameObject(
                                blockName,
                                false,
                                dfUnity.Option_CityBlockPrefab,
                                natureBillboardBatch,
                                lightsBillboardBatch,
                                animalsBillboardBatch,
                                miscBillboardAtlas,
                                miscBillboardBatch);
                            go.hideFlags = HideFlags.HideAndDontSave;
                            go.transform.parent = locationObject.transform;
                            go.transform.localPosition = blockOrigin;
                            dfLocation.ApplyClimateSettings();
                            if (allowYield) yield return new WaitForEndOfFrame();
                        }
                    }

                    // If this is the player terrain we may need to reposition player
                    if (isPlayerTerrain && repositionPlayer)
                    {
                        // Position to location and use start marker for large cities
                        bool useStartMarker = (dfLocation.Summary.LocationType == DFRegion.LocationTypes.TownCity);
                        PositionPlayerToLocation(MapPixelX, MapPixelY, dfLocation, origin, width, height, useStartMarker);
                        repositionPlayer = false;
                    }

                    // Apply billboard batches
                    natureBillboardBatch.Apply();
                    lightsBillboardBatch.Apply();
                    animalsBillboardBatch.Apply();
                    miscBillboardBatch.Apply();
                }
            }
            else if (terrainArray[index].active)
            {
                if (playerKey == key && repositionPlayer)
                {
                    PositionPlayerToTerrain(MapPixelX, MapPixelY, Vector3.zero);
                    repositionPlayer = false;
                }
            }
        }

        // Place a single terrain and mark it for update
        private void PlaceTerrain(int mapPixelX, int mapPixelY)
        {
            // Do nothing if out of range
            if (mapPixelX < MapsFile.MinMapPixelX || mapPixelX >= MapsFile.MaxMapPixelX ||
                mapPixelY < MapsFile.MinMapPixelY || mapPixelY >= MapsFile.MaxMapPixelY)
            {
                return;
            }

            // Get terrain key
            int key = TerrainHelper.MakeTerrainKey(mapPixelX, mapPixelY);

            // If terrain is available
            if (terrainIndexDict.ContainsKey(key))
            {
                // Terrain exists, check if active
                int index = terrainIndexDict[key];
                if (terrainArray[index].active)
                {
                    // Terrain already active in scene, nothing to do
                    return;
                }
                else
                {
                    // Terrain inactive but available, re-activate terrain
                    terrainArray[index].active = true;
                    terrainArray[index].terrainObject.SetActive(true);
                    terrainArray[index].billboardBatchObject.SetActive(true);
                }
                return;
            }

            // Need to place a new terrain, find next available terrain
            // This will either find a fresh terrain or recycle an old one
            int nextTerrain = FindNextAvailableTerrain();
            if (nextTerrain == -1)
                return;

            // Setup new terrain
            terrainArray[nextTerrain].active = true;
            terrainArray[nextTerrain].updateData = true;
            terrainArray[nextTerrain].updateNature = true;
            terrainArray[nextTerrain].mapPixelX = mapPixelX;
            terrainArray[nextTerrain].mapPixelY = mapPixelY;
            if (!terrainArray[nextTerrain].terrainObject)
            {
                // Create game objects for new terrain
                // This is only done once then recycled
                CreateTerrainGameObjects(
                    mapPixelX,
                    mapPixelY,
                    out terrainArray[nextTerrain].terrainObject,
                    out terrainArray[nextTerrain].billboardBatchObject);
            }

            // Apply local transform
            float scale = MapsFile.WorldMapTerrainDim * MeshReader.GlobalScale;
            int xdif = mapPixelX - mapOrigin.X;
            int ydif = mapPixelY - mapOrigin.Y;
            Vector3 localPosition = new Vector3(xdif * scale, 0, -ydif * scale) + WorldCompensation;
            terrainArray[nextTerrain].terrainObject.transform.localPosition = localPosition;

            // Add new terrain index to dictionary
            terrainIndexDict.Add(key, nextTerrain);

            // Check if terrain has a location, if so it will be added on next update
            ContentReader.MapSummary mapSummary;
            terrainArray[nextTerrain].hasLocation = dfUnity.ContentReader.HasLocation(mapPixelX, mapPixelY, out mapSummary);
            terrainArray[nextTerrain].updateLocation = terrainArray[nextTerrain].hasLocation;
        }

        // Finds next available terrain in array
        private int FindNextAvailableTerrain()
        {
            // Evaluate terrain array
            int found = -1;
            for (int i = 0; i < terrainArray.Length; i++)
            {
                // A null terrain has never been instantiated and is free
                if (terrainArray[i].terrainObject == null)
                {
                    found = i;
                    break;
                }

                // Inactive terrain object can be evaluated for recycling based
                // on distance from current map pixel
                if (!terrainArray[i].active)
                {
                    // If terrain out of range then recycle
                    if (!IsInRange(terrainArray[i].mapPixelX, terrainArray[i].mapPixelY))
                    {
                        found = i;
                        break;
                    }
                }
            }

            // Was a terrain found?
            if (found != -1)
            {
                // If we are recycling an inactive terrain, remove it from dictionary first
                int key = TerrainHelper.MakeTerrainKey(terrainArray[found].mapPixelX, terrainArray[found].mapPixelY);
                if (terrainIndexDict.ContainsKey(key))
                {
                    terrainIndexDict.Remove(key);
                }
                return found;
            }
            else
            {
                // Unable to find an available terrain
                // This should never happen unless TerrainDistance too high or maxTerrainArray too low
                DaggerfallUnity.LogMessage("StreamingWorld: Unable to find free terrain. Check maxTerrainArray is sized appropriately and you are collecting terrains. This can also happen when player movement speed too high.", true);
                if (Application.isEditor)
                    Debug.Break();
                else
                    Application.Quit();

                return -1;
            }
        }

        #endregion

        #region World Cleanup Methods

        // Remove managed child objects
        private void ClearStreamingWorld()
        {
            // Collect everything
            CollectTerrains(true);
            CollectLocations(true);

            // Clear lists
            terrainIndexDict.Clear();

            // Raise event
            RaiseOnClearStreamingWorldEvent();
        }

        // Mark inactive any terrains outside of range
        private void CollectTerrains(bool collectAll = false)
        {
            for (int i = 0; i < terrainArray.Length; i++)
            {
                // Ignore uninitialised and inactive terrains
                if (terrainArray[i].terrainObject == null ||
                    !terrainArray[i].active)
                {
                    continue;
                }

                // If terrain out of range then mark inactive for recycling
                if (!IsInRange(terrainArray[i].mapPixelX, terrainArray[i].mapPixelY) || collectAll)
                {
                    // Mark terrain inactive
                    terrainArray[i].active = false;
                    terrainArray[i].terrainObject.SetActive(false);
                    terrainArray[i].billboardBatchObject.SetActive(false);

                    // If collecting all then ensure terrain is out of range
                    // This fixes a bug where continuously loading same location overflows terrain buffer
                    if (collectAll)
                    {
                        terrainArray[i].mapPixelX = int.MinValue;
                        terrainArray[i].mapPixelY = int.MinValue;
                        terrainArray[i].updateLocation = false;
                    }
                }
            }
        }

        // Destroy any locations outside of range
        private void CollectLocations(bool collectAll = false)
        {
            for (int i = 0; i < locationList.Count; i++)
            {
                if (!IsInRange(locationList[i].mapPixelX, locationList[i].mapPixelY) || collectAll)
                {
                    locationList[i].locationObject.SetActive(false);
                    StartCoroutine(DestroyLocationIterative(locationList[i].locationObject));
                    locationList.RemoveAt(i);
                }
            }
        }

        // Iteratively destroys location as unity seems bad at doing this when just destroying parent
        private IEnumerator DestroyLocationIterative(GameObject gameObject)
        {
            // Destroy all children iteratively
            foreach (Transform t in gameObject.transform)
            {
                DestroyLocationIterative(t.gameObject);
                yield return new WaitForEndOfFrame();
            }

            // Now destroy this object
            GameObject.Destroy(gameObject.transform.gameObject);
        }

        #endregion

        #region World Utility Methods

        // Sets terrain neighbours
        // Should only be done after terrain is placed and collected
        private void UpdateNeighbours()
        {
            for (int i = 0; i < terrainArray.Length; i++)
            {
                // Check object exists
                if (!terrainArray[i].terrainObject)
                    continue;

                // Get DaggerfallTerrain
                DaggerfallTerrain dfTerrain = terrainArray[i].terrainObject.GetComponent<DaggerfallTerrain>();
                if (!dfTerrain)
                    continue;

                // Set or clear neighbours
                if (terrainArray[i].active)
                {
                    dfTerrain.LeftNeighbour = GetTerrain(dfTerrain.MapPixelX - 1, dfTerrain.MapPixelY);
                    dfTerrain.RightNeighbour = GetTerrain(dfTerrain.MapPixelX + 1, dfTerrain.MapPixelY);
                    dfTerrain.TopNeighbour = GetTerrain(dfTerrain.MapPixelX, dfTerrain.MapPixelY - 1);
                    dfTerrain.BottomNeighbour = GetTerrain(dfTerrain.MapPixelX, dfTerrain.MapPixelY + 1);
                    dfTerrain.UpdateNeighbours();
                }
                else
                {
                    dfTerrain.LeftNeighbour = null;
                    dfTerrain.RightNeighbour = null;
                    dfTerrain.TopNeighbour = null;
                    dfTerrain.BottomNeighbour = null;
                }

                // Update Unity Terrain
                dfTerrain.UpdateNeighbours();                
            }
        }

        // Anything greater than TerrainDistance+1 is out of range
        private bool IsInRange(int mapPixelX, int mapPixelY)
        {
            if (Math.Abs(mapPixelX - this.MapPixelX) > TerrainDistance + 1 ||
                Math.Abs(mapPixelY - this.MapPixelY) > TerrainDistance + 1)
            {
                return false;
            }

            return true;
        }

        // Create new terrain game objects
        private void CreateTerrainGameObjects(int mapPixelX, int mapPixelY, out GameObject terrainObject, out GameObject billboardBatchObject)
        {
            // Create new terrain object parented to streaming world
            terrainObject = GameObjectHelper.CreateDaggerfallTerrainGameObject(this.transform);
            terrainObject.name = string.Format("DaggerfallTerrain [{0},{1}]", mapPixelX, mapPixelY);
            terrainObject.hideFlags = HideFlags.HideAndDontSave;

            // Create new billboard batch object parented to terrain
            billboardBatchObject = new GameObject();
            billboardBatchObject.name = string.Format("DaggerfallBillboardBatch [{0},{1}]", mapPixelX, mapPixelY);
            billboardBatchObject.hideFlags = HideFlags.HideAndDontSave;
            billboardBatchObject.transform.parent = terrainObject.transform;
            billboardBatchObject.transform.localPosition = Vector3.zero;
            billboardBatchObject.AddComponent<DaggerfallBillboardBatch>();
        }

        // Create new location game object
        private GameObject CreateLocationGameObject(int terrain, out DFLocation locationOut)
        {
            locationOut = new DFLocation();

            // Terrain must have a location
            DaggerfallTerrain dfTerrain = terrainArray[terrain].terrainObject.GetComponent<DaggerfallTerrain>();
            if (!dfTerrain.MapData.hasLocation)
                return null;

            // Get location data
            locationOut = dfUnity.ContentReader.MapFileReader.GetLocation(dfTerrain.MapData.mapRegionIndex, dfTerrain.MapData.mapLocationIndex);
            if (!locationOut.Loaded)
                return null;

            // Sample height of terrain at origin tile position, this is more accurate than scaled average - thanks Nystul!
            // TODO: Daggerfall does not always position locations at exact centre as assumed.
            // Working around this for now and will update this code later
            Terrain terrainInstance = dfTerrain.gameObject.GetComponent<Terrain>();
            float scale = terrainInstance.terrainData.heightmapScale.x;
            float xSamplePos = dfUnity.TerrainSampler.HeightmapDimension * 0.55f;
            float ySamplePos = dfUnity.TerrainSampler.HeightmapDimension * 0.55f;
            Vector3 pos = new Vector3(xSamplePos * scale, 0, ySamplePos * scale);
            float height = terrainInstance.SampleHeight(pos + terrainArray[terrain].terrainObject.transform.position);

            // Spawn parent game object for new location
            //float height = dfTerrain.MapData.averageHeight * TerrainScale;
            GameObject locationObject = new GameObject(string.Format("DaggerfallLocation [Region={0}, Name={1}]", locationOut.RegionName, locationOut.Name));
            locationObject.transform.parent = this.transform;
            //locationObject.hideFlags = HideFlags.HideAndDontSave;
            locationObject.transform.position = terrainArray[terrain].terrainObject.transform.position + new Vector3(0, height, 0);
            DaggerfallLocation dfLocation = locationObject.AddComponent<DaggerfallLocation>() as DaggerfallLocation;
            dfLocation.SetLocation(locationOut, false);

            // Raise event
            RaiseOnCreateLocationGameObjectEvent(dfLocation);

            return locationObject;
        }

        // Update terrain data
        private void UpdateTerrainData(TerrainDesc terrainDesc)
        {
            // Instantiate Daggerfall terrain
            DaggerfallTerrain dfTerrain = terrainDesc.terrainObject.GetComponent<DaggerfallTerrain>();
            if (dfTerrain)
            {
                dfTerrain.TerrainScale = TerrainScale;
                dfTerrain.MapPixelX = terrainDesc.mapPixelX;
                dfTerrain.MapPixelY = terrainDesc.mapPixelY;
                dfTerrain.InstantiateTerrain();
            }

            // Update data for terrain
            dfTerrain.UpdateMapPixelData(terrainTexturing);

            dfTerrain.UpdateTileMapData();

            // Promote data to live terrain
            dfTerrain.UpdateClimateMaterial();
            dfTerrain.PromoteTerrainData();

            // Only set active again once complete
            terrainDesc.terrainObject.SetActive(true);
        }

        // Update terrain nature
        private void UpdateTerrainNature(TerrainDesc terrainDesc)
        {
            // Setup billboards
            DaggerfallTerrain dfTerrain = terrainDesc.terrainObject.GetComponent<DaggerfallTerrain>();
            DaggerfallBillboardBatch dfBillboardBatch = terrainDesc.billboardBatchObject.GetComponent<DaggerfallBillboardBatch>();
            if (dfTerrain && dfBillboardBatch)
            {
                // Get current climate and nature archive
                int natureArchive = ClimateSwaps.GetNatureArchive(LocalPlayerGPS.ClimateSettings.NatureSet, dfUnity.WorldTime.Now.SeasonValue);
                dfBillboardBatch.SetMaterial(natureArchive);
                TerrainHelper.LayoutNatureBillboards(dfTerrain, dfBillboardBatch, TerrainScale);
            }

            // Only set active again once complete
            terrainDesc.billboardBatchObject.SetActive(true);
        }

        // Gets terrain at map pixel coordinates, or null if not found
        private Terrain GetTerrain(int mapPixelX, int mapPixelY)
        {
            int key = TerrainHelper.MakeTerrainKey(mapPixelX, mapPixelY);
            if (terrainIndexDict.ContainsKey(key))
            {
                return terrainArray[terrainIndexDict[key]].terrainObject.GetComponent<Terrain>();
            }

            return null;
        }

        #endregion

        #region Player Utility Methods

        // Gets transform of the terrain player is standing on
        private Transform GetPlayerTerrainTransform()
        {
            int key = TerrainHelper.MakeTerrainKey(MapPixelX, MapPixelY);
            if (!terrainIndexDict.ContainsKey(key))
                return null;

            return terrainArray[terrainIndexDict[key]].terrainObject.transform;
        }

        // Sets player to gound level at position in specified terrain
        // Terrain data must already be loaded
        // LocalGPS must be attached to your player game object
        private void PositionPlayerToTerrain(int mapPixelX, int mapPixelY, Vector3 position)
        {
            // Get terrain key
            int key = TerrainHelper.MakeTerrainKey(mapPixelX, mapPixelY);
            if (!terrainIndexDict.ContainsKey(key))
                return;

            // Get terrain
            Terrain terrain = terrainArray[terrainIndexDict[key]].terrainObject.GetComponent<Terrain>();

            // Sample height at this position
            CharacterController controller = LocalPlayerGPS.gameObject.GetComponent<CharacterController>();
            if (controller)
            {
                Vector3 pos = new Vector3(position.x, 0, position.z);
                float height = terrain.SampleHeight(pos + terrain.transform.position);
                pos.y = height + controller.height * 1.5f;

                // Move player to this position and align to ground using raycast
                LocalPlayerGPS.transform.position = pos;
                FixStanding(LocalPlayerGPS.transform, controller.height);
                InitFloatingOrigin(LocalPlayerGPS.transform);
            }
            else
            {
                throw new Exception("StreamingWorld: Could not find CapsuleCollider peered with LocalPlayerGPS.");
            }
        }

        // Sets player to ground level near a location
        // Will spawn at a random edge facing location
        // Can use start markers if present
        private void PositionPlayerToLocation(
            int mapPixelX,
            int mapPixelY,
            DaggerfallLocation dfLocation,
            Vector3 origin,
            int mapWidth,
            int mapHeight,
            bool useNearestStartMarker = false)
        {
            // Randomly pick one side of location to spawn
            // A better implementation would base on previous coordinates
            // e.g. if new location is east of old location then player starts at west edge of new location
            UnityEngine.Random.seed = System.DateTime.Now.Millisecond;
            int side = UnityEngine.Random.Range(0, 4);

            // Get half width and height
            float halfWidth = (float)mapWidth * 0.5f * RMBLayout.RMBSide;
            float halfHeight = (float)mapHeight * 0.5f * RMBLayout.RMBSide;
            Vector3 centre = origin + new Vector3(halfWidth, 0, halfHeight);

            // Sometimes buildings are right up to edge of block
            // Extra distance places player a little bit outside location area
            float extraDistance = RMBLayout.RMBSide * 0.1f;

            // Start player in position
            // Will also SendMessage to receiver called SetFacing on player gameobject with forward vector
            // You should implement this if using your own mouselook component
            Vector3 newPlayerPosition = centre;
            switch (side)
            {
                case 0:         // North
                    newPlayerPosition += new Vector3(0, 0, (halfHeight + extraDistance));
                    LocalPlayerGPS.SendMessage("SetFacing", Vector3.back, SendMessageOptions.DontRequireReceiver);
                    //Debug.Log("Spawned player north.");
                    break;
                case 1:         // South
                    newPlayerPosition += new Vector3(0, 0, -(halfHeight + extraDistance));
                    LocalPlayerGPS.SendMessage("SetFacing", Vector3.forward, SendMessageOptions.DontRequireReceiver);
                    //Debug.Log("Spawned player south.");
                    break;
                case 2:         // East
                    newPlayerPosition += new Vector3((halfWidth + extraDistance), 0, 0);
                    LocalPlayerGPS.SendMessage("SetFacing", Vector3.left, SendMessageOptions.DontRequireReceiver);
                    //Debug.Log("Spawned player east.");
                    break;
                case 3:         // West
                    newPlayerPosition += new Vector3(-(halfWidth + extraDistance), 0, 0);
                    LocalPlayerGPS.SendMessage("SetFacing", Vector3.right, SendMessageOptions.DontRequireReceiver);
                    //Debug.Log("Spawned player west.");
                    break;
            }

            // Adjust to nearest start marker if requested
            if (useNearestStartMarker)
            {
                float smallestDistance = float.MaxValue;
                int closestMarker = -1;
                GameObject[] startMarkers = dfLocation.StartMarkers;
                for (int i = 0; i < startMarkers.Length; i++)
                {
                    float distance = Vector3.Distance(newPlayerPosition, startMarkers[i].transform.position);
                    if (distance < smallestDistance)
                    {
                        smallestDistance = distance;
                        closestMarker = i;
                    }
                }
                if (closestMarker != -1)
                {
                    PositionPlayerToTerrain(mapPixelX, mapPixelY, startMarkers[closestMarker].transform.position);
                    return;
                }
            }

            // Just position to outside location
            PositionPlayerToTerrain(mapPixelX, mapPixelY, newPlayerPosition);
        }

        // Align player to ground
        private bool FixStanding(Transform playerTransform, float playerHeight, float extraHeight = 0, float extraDistance = 0)
        {
            RaycastHit hit;
            Ray ray = new Ray(playerTransform.position + (Vector3.up * extraHeight), Vector3.down);
            if (Physics.Raycast(ray, out hit, (playerHeight * 2) + extraHeight + extraDistance))
            {
                // Position player at hit position plus just over half controller height up
                playerTransform.position = hit.point + Vector3.up * (playerHeight * 0.6f);
                return true;
            }

            return false;
        }

        // If using FloatingOrigin on player, we must sometimes reinitialize so it does not get out of sync
        private void InitFloatingOrigin(Transform playerTransform)
        {
            FloatingOrigin fo = playerTransform.GetComponent<FloatingOrigin>();
            if (fo)
            {
                fo.Initialize();
            }
        }

        #endregion

        #region Startup/Shutdown Methods

        private bool ReadyCheck()
        {
            if (isReady)
                return true;

            if (dfUnity == null)
            {
                dfUnity = DaggerfallUnity.Instance;
            }

            if (LocalPlayerGPS == null || suppressWorld)
                return false;
            else
                InitWorld(true);

            // Do nothing if DaggerfallUnity not ready
            if (!dfUnity.IsReady)
            {
                DaggerfallUnity.LogMessage("StreamingWorld: DaggerfallUnity component is not ready. Have you set your Arena2 path?");
                return false;
            }

            // Perform initial runtime setup
            if (Application.isPlaying)
            {
                // Fix coastal climate data
                TerrainHelper.DilateCoastalClimate(dfUnity.ContentReader, 2);

                // Smooth steep location on steep gradients
                TerrainHelper.SmoothLocationNeighbourhood(dfUnity.ContentReader);
            }

            // Raise ready flag
            isReady = true;
            RaiseOnReadyEvent();

            return true;
        }

        private string GetDebugString()
        {
            string final = string.Format("[{0},{1}] [{2},{3}] You are in the {4} region with a {5} climate.",
                MapPixelX,
                MapPixelY,
                LocalPlayerGPS.WorldX,
                LocalPlayerGPS.WorldZ,
                LocalPlayerGPS.CurrentRegionName,
                LocalPlayerGPS.ClimateSettings.ClimateType.ToString());
            if (LocalPlayerGPS.CurrentLocation.Loaded)
            {
                final += string.Format(" {0} is nearby.", LocalPlayerGPS.CurrentLocation.Name);
            }

            return final;
        }

        #endregion

        #region Editor Support

#if UNITY_EDITOR

        public void __EditorFindLocation()
        {
            DFLocation location;
            if (!GameObjectHelper.FindMultiNameLocation(EditorFindLocationString, out location))
            {
                DaggerfallUnity.LogMessage(string.Format("Could not find location [Region={0}, Name={1}]", location.RegionName, location.Name), true);
                return;
            }

            int longitude = (int)location.MapTableData.Longitude;
            int latitude = (int)location.MapTableData.Latitude;
            DFPosition pos = MapsFile.LongitudeLatitudeToMapPixel(longitude, latitude);

            MapPixelX = pos.X;
            MapPixelY = pos.Y;
        }

        public void __EditorGetFromPlayerGPS()
        {
            if (LocalPlayerGPS != null)
            {
                DFPosition pos = MapsFile.WorldCoordToMapPixel(LocalPlayerGPS.WorldX, LocalPlayerGPS.WorldZ);
                MapPixelX = pos.X;
                MapPixelY = pos.Y;
            }
        }

        public void __EditorApplyToPlayerGPS()
        {
            if (LocalPlayerGPS != null)
            {
                DFPosition pos = MapsFile.MapPixelToWorldCoord(MapPixelX, MapPixelY);
                LocalPlayerGPS.WorldX = pos.X;
                LocalPlayerGPS.WorldZ = pos.Y;
            }
        }

#endif

        #endregion

        #region Event Handlers

        // OnReady
        public delegate void OnReadyEventHandler();
        public static event OnReadyEventHandler OnReady;
        protected virtual void RaiseOnReadyEvent()
        {
            if (OnReady != null)
                OnReady();
        }

        // OnTeleportToCoordinates
        public delegate void OnTeleportToCoordinatesEventHandler(DFPosition worldPos);
        public static event OnTeleportToCoordinatesEventHandler OnTeleportToCoordinates;
        protected virtual void RaiseOnTeleportToCoordinatesEvent(DFPosition worldPos)
        {
            if (OnTeleportToCoordinates != null)
                OnTeleportToCoordinates(worldPos);
        }

        // OnInitWorld
        public delegate void OnInitWorldEventHandler();
        public static event OnInitWorldEventHandler OnInitWorld;
        protected virtual void RaiseOnInitWorldEvent()
        {
            if (OnInitWorld != null)
                OnInitWorld();
        }

        // OnUpdateTerrainsStart
        public delegate void OnUpdateTerrainsStartEventHandler();
        public static event OnUpdateTerrainsStartEventHandler OnUpdateTerrainsStart;
        protected virtual void RaiseOnUpdateTerrainsStartEvent()
        {
            if (OnUpdateTerrainsStart != null)
                OnUpdateTerrainsStart();
        }

        // OnUpdateTerrainsEnd
        public delegate void OnUpdateTerrainsEndEventHandler();
        public static event OnUpdateTerrainsEndEventHandler OnUpdateTerrainsEnd;
        protected virtual void RaiseOnUpdateTerrainsEndEvent()
        {
            if (OnUpdateTerrainsEnd != null)
                OnUpdateTerrainsEnd();
        }

        // OnClearStreamingWorld
        public delegate void OnClearStreamingWorldEventHandler();
        public static event OnClearStreamingWorldEventHandler OnClearStreamingWorld;
        protected virtual void RaiseOnClearStreamingWorldEvent()
        {
            if (OnClearStreamingWorld != null)
                OnClearStreamingWorld();
        }

        // OnCreateLocationGameObject
        public delegate void OnCreateLocationGameObjectEventHandler(DaggerfallLocation dfLocation);
        public static event OnCreateLocationGameObjectEventHandler OnCreateLocationGameObject;
        protected virtual void RaiseOnCreateLocationGameObjectEvent(DaggerfallLocation dfLocation)
        {
            if (OnCreateLocationGameObject != null)
                OnCreateLocationGameObject(dfLocation);
        }

        #endregion
    }
}