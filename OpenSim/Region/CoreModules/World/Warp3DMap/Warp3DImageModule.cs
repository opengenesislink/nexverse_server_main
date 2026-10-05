/*
 * Copyright (c) Contributors, http://opensimulator.org/
 * See CONTRIBUTORS.TXT for a full list of copyright holders.
 *
 * Redistribution and use in source and binary forms, with or without
 * modification, are permitted provided that the following conditions are met:
 *     * Redistributions of source code must retain the above copyright
 *       notice, this list of conditions and the following disclaimer.
 *     * Redistributions in binary form must reproduce the above copyright
 *       notice, this list of conditions and the following disclaimer in the
 *       documentation and/or other materials provided with the distribution.
 *     * Neither the name of the OpenSimulator Project nor the
 *       names of its contributors may be used to endorse or promote products
 *       derived from this software without specific prior written permission.
 *
 * THIS SOFTWARE IS PROVIDED BY THE DEVELOPERS ``AS IS'' AND ANY
 * EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED
 * WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
 * DISCLAIMED. IN NO EVENT SHALL THE CONTRIBUTORS BE LIABLE FOR ANY
 * DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES
 * (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES;
 * LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND
 * ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
 * (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS
 * SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
 */

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime;
using System.Text.Json;

using CSJ2K;
using Nini.Config;
using log4net;
using Warp3D;
using Mono.Addins;

using OpenSim.Framework;
using OpenSim.Framework.Console;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;

using OpenMetaverse;
using OpenMetaverse.Assets;
using OpenMetaverse.Imaging;
using OpenMetaverse.Rendering;
using OpenMetaverse.StructuredData;

using WarpRenderer = Warp3D.Warp3D;

namespace OpenSim.Region.CoreModules.World.Warp3DMap
{
    [Extension(Path = "/OpenSim/RegionModules", NodeName = "RegionModule", Id = "Warp3DImageModule")]
    public class Warp3DImageModule : IMapImageGenerator, ILunaTextureDiagnostics, INonSharedRegionModule
    {
        private static readonly Color4 WATER_COLOR = new Color4(29, 72, 96, 216);
//        private static readonly Color4 WATER_COLOR = new Color4(29, 72, 96, 128);

        private static readonly ILog m_log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

#pragma warning disable 414
        private static string LogHeader = "[WARP 3D IMAGE MODULE]";
#pragma warning restore 414

        internal Scene m_scene;
        private IRendering m_primMesher;
        internal IJ2KDecoder m_imgDecoder;

        // caches per rendering 
        private Dictionary<UUID, warp_Texture> m_warpTextures;
        private Dictionary<UUID, int> m_colors;

        // Keep map generation useful when an old/corrupt texture cannot be
        // decoded.  Report a specific texture UUID only once per region module
        // lifetime instead of flooding the simulator console on every tile
        // render.
        private readonly HashSet<UUID> m_textureDecodeWarnings = new HashSet<UUID>();
        private readonly object m_textureDecodeWarningsLock = new object();
        private readonly Dictionary<UUID, LunaTextureDiagnostic> m_lunaTextureDiagnostics = new Dictionary<UUID, LunaTextureDiagnostic>();
        private readonly Queue<UUID> m_lunaTextureDiagnosticOrder = new Queue<UUID>();
        private int m_lunaTextureDiagnosticLimit = 256;
        private bool m_lunaTexturePlaceholder = true;
        private string m_lunaTextureDiagnosticStore = "data/lunatexture";
        private string m_lunaTextureDiagnosticStorePath = string.Empty;
        private DateTime m_lunaTextureLastPersistUtc = DateTime.MinValue;
        private bool m_lunaTextureStoreDirty;
        private static readonly TimeSpan s_lunaTexturePersistInterval = TimeSpan.FromSeconds(5);
        private static readonly JsonSerializerOptions s_lunaTextureJson = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNameCaseInsensitive = true
        };

        private bool m_drawPrimVolume = true;   // true if should render the prims on the tile
        private bool m_textureTerrain = true;   // true if to create terrain splatting texture
        private bool m_textureAverageTerrain = false; // replace terrain textures by their average color
        private bool m_texturePrims = true;     // true if should texture the rendered prims
        private float m_texturePrimSize = 48f;  // size of prim before we consider texturing it
        private bool m_renderMeshes = false;    // true if to render meshes rather than just bounding boxes
        private bool m_lunaTextureEnabled = true;
        private bool m_lunaTextureRasterFallback = true;

        private const float m_cameraHeight = 4096f;
        private float m_renderMinHeight = -100f;
        private float m_renderMaxHeight = 4096f;

        private bool m_Enabled = false;

        #region Region Module interface

        public void Initialise(IConfigSource source)
        {
            string[] configSections = new string[] { "Map", "Startup" };

            if (Util.GetConfigVarFromSections<string>(
                source, "MapImageModule", configSections, "MapImageModule") != "Warp3DImageModule")
                return;

            m_Enabled = true;

            m_drawPrimVolume =
                Util.GetConfigVarFromSections<bool>(source, "DrawPrimOnMapTile", configSections, m_drawPrimVolume);
            m_textureTerrain =
                Util.GetConfigVarFromSections<bool>(source, "TextureOnMapTile", configSections, m_textureTerrain);
            m_textureAverageTerrain =
                Util.GetConfigVarFromSections<bool>(source, "AverageTextureColorOnMapTile", configSections, m_textureAverageTerrain);
            if (m_textureAverageTerrain)
                m_textureTerrain = true;
            m_texturePrims =
                Util.GetConfigVarFromSections<bool>(source, "TexturePrims", configSections, m_texturePrims);
            m_texturePrimSize =
                Util.GetConfigVarFromSections<float>(source, "TexturePrimSize", configSections, m_texturePrimSize);
            m_renderMeshes =
                Util.GetConfigVarFromSections<bool>(source, "RenderMeshes", configSections, m_renderMeshes);
            m_lunaTextureEnabled =
                Util.GetConfigVarFromSections<bool>(source, "LunaTextureEnabled", configSections, m_lunaTextureEnabled);
            m_lunaTextureRasterFallback =
                Util.GetConfigVarFromSections<bool>(source, "LunaTextureRasterFallback", configSections, m_lunaTextureRasterFallback);
            m_lunaTexturePlaceholder =
                Util.GetConfigVarFromSections<bool>(source, "LunaTexturePlaceholder", configSections, m_lunaTexturePlaceholder);
            m_lunaTextureDiagnosticLimit = Math.Max(16,
                Util.GetConfigVarFromSections<int>(source, "LunaTextureDiagnosticLimit", configSections, m_lunaTextureDiagnosticLimit));
            m_lunaTextureDiagnosticStore =
                Util.GetConfigVarFromSections<string>(
                    source,
                    "LunaTextureDiagnosticStore",
                    configSections,
                    m_lunaTextureDiagnosticStore)?.Trim() ?? string.Empty;

            m_renderMaxHeight = Util.GetConfigVarFromSections<float>(source, "RenderMaxHeight", configSections, m_renderMaxHeight);
            m_renderMinHeight = Util.GetConfigVarFromSections<float>(source, "RenderMinHeight", configSections, m_renderMinHeight);
            /*
            m_cameraHeight = Util.GetConfigVarFromSections<float>(m_config, "RenderCameraHeight", configSections, m_cameraHeight);

            if (m_cameraHeight < 250f)
                m_cameraHeight = 250f;
            else if (m_cameraHeight > 4096f)
                m_cameraHeight = 4096f;
            */
            if (m_renderMaxHeight < 100f)
                m_renderMaxHeight = 100f;
            else if (m_renderMaxHeight > m_cameraHeight - 10f)
                m_renderMaxHeight = m_cameraHeight - 10f;

            if (m_renderMinHeight < -100f)
                m_renderMinHeight = -100f;
            else if (m_renderMinHeight > m_renderMaxHeight - 10f)
                m_renderMinHeight = m_renderMaxHeight - 10f;
        }

        public void AddRegion(Scene scene)
        {
            if (!m_Enabled)
                return;

            m_scene = scene;

            if (m_lunaTextureEnabled)
            {
                m_lunaTextureDiagnosticStorePath =
                    ResolveLunaTextureDiagnosticStorePath(scene);
                LoadLunaTextureDiagnostics();
            }

            List<string> renderers = RenderingLoader.ListRenderers(Util.ExecutingDirectory());
            if (renderers.Count > 0)
                m_log.Info("[MAPTILE]: Loaded prim mesher " + renderers[0]);
            else
                m_log.Info("[MAPTILE]: No prim mesher loaded, prim rendering will be disabled");

            m_scene.RegisterModuleInterface<IMapImageGenerator>(this);
            if (m_lunaTextureEnabled)
                m_scene.RegisterModuleInterface<ILunaTextureDiagnostics>(this);

            if (m_lunaTextureEnabled && MainConsole.Instance is not null)
            {
                MainConsole.Instance.Commands.AddCommand(
                    "NexVerse", false, "nex texture status", "nex texture status",
                    "Show bounded LunaTexture diagnostics for the selected region.", HandleLunaTextureCommand);
                MainConsole.Instance.Commands.AddCommand(
                    "NexVerse", false, "nex texture inspect", "nex texture inspect <texture-uuid>",
                    "Inspect one LunaTexture diagnostic record.", HandleLunaTextureCommand);
                MainConsole.Instance.Commands.AddCommand(
                    "NexVerse", false, "nex texture retry", "nex texture retry <texture-uuid>",
                    "Evict one Warp3D texture cache entry and retry its asset on the next render.", HandleLunaTextureCommand);
                MainConsole.Instance.Commands.AddCommand(
                    "NexVerse", false, "nex texture clear", "nex texture clear",
                    "Clear LunaTexture diagnostics for the selected region.", HandleLunaTextureCommand);
            }
        }

        public void RegionLoaded(Scene scene)
        {
            if (!m_Enabled)
                return;

            m_imgDecoder = m_scene.RequestModuleInterface<IJ2KDecoder>();
        }

        public void RemoveRegion(Scene scene)
        {
            if (m_lunaTextureEnabled)
                PersistLunaTextureDiagnostics(true);
        }

        public void Close()
        {
            if (m_lunaTextureEnabled)
                PersistLunaTextureDiagnostics(true);
        }

        public string Name
        {
            get { return "Warp3DImageModule"; }
        }

        public Type ReplaceableInterface
        {
            get { return null; }
        }

        #endregion

        #region IMapImageGenerator Members

        private Vector3 cameraPos;
        private Vector3 cameraDir;
        private int viewWidth = 256;
        private int viewHeight = 256;
        private float fov;
        private bool orto;

        public Bitmap CreateMapTile()
        {
            List<string> renderers = RenderingLoader.ListRenderers(Util.ExecutingDirectory());
            if (renderers.Count > 0)
            {
                m_primMesher = RenderingLoader.LoadRenderer(renderers[0]);
            }

            viewWidth = (int)m_scene.RegionInfo.RegionSizeX;
            viewHeight = (int)m_scene.RegionInfo.RegionSizeY;

            cameraPos = new Vector3(
                            viewWidth * 0.5f,
                            viewHeight * 0.5f,
                            m_cameraHeight);

            cameraDir = -Vector3.UnitZ;
            orto = true;

            Bitmap tile = GenImage();
            // image may be reloaded elsewhere, so no compression format
            string filename = "MAP-" + m_scene.RegionInfo.RegionID.ToString() + ".png";
            tile.Save(filename,ImageFormat.Png);
            m_primMesher = null;
            return tile;
        }

        public Bitmap CreateViewImage(Vector3 camPos, Vector3 camDir, float pfov, int width, int height, bool useTextures)
        {
            List<string> renderers = RenderingLoader.ListRenderers(Util.ExecutingDirectory());
            if (renderers.Count > 0)
            {
                m_primMesher = RenderingLoader.LoadRenderer(renderers[0]);
            }

            cameraPos = camPos;
            cameraDir = camDir;
            viewWidth = width;
            viewHeight = height;
            fov = pfov;
            orto = false;

            Bitmap tile = GenImage();
            m_primMesher = null;
            return tile;
        }

        private Bitmap GenImage()
        {
            m_colors= new Dictionary<UUID, int>();
            m_warpTextures= new Dictionary<UUID, warp_Texture>();

            WarpRenderer renderer = new WarpRenderer();

            if (!renderer.CreateScene(viewWidth, viewHeight))
                return new Bitmap(viewWidth, viewHeight);

            #region Camera

            warp_Vector pos = ConvertVector(ref cameraPos);
            warp_Vector lookat = ConvertVector(cameraPos + cameraDir);

            if (orto)
                renderer.Scene.defaultCamera.setOrthographic(true, viewWidth, viewHeight);
            else
                renderer.Scene.defaultCamera.setFov(fov);

            renderer.Scene.defaultCamera.setPos(pos);
            renderer.Scene.defaultCamera.lookAt(lookat);
            #endregion Camera

            renderer.Scene.setAmbient(warp_Color.getColor(192, 191, 173));
            renderer.Scene.addLight("Light1", new warp_Light(new warp_Vector(0f, 1f, 8f), warp_Color.White, 0, 200, 20));

            CreateWater(renderer);
            CreateTerrain(renderer);
            if (m_drawPrimVolume)
                CreateAllPrims(renderer);

            renderer.Render();
            Bitmap bitmap = renderer.Scene.getImage();

            renderer.Scene.destroy();
            renderer.Reset();
            renderer = null;

            m_colors = null;
            m_warpTextures = null;

            GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.Default;
            return bitmap;
        }

        public byte[] WriteJpeg2000Image()
        {
            try
            {
                using (Bitmap mapbmp = CreateMapTile())
                    return OpenJPEG.EncodeFromImage(mapbmp, false);
            }
            catch (Exception e)
            {
                // JPEG2000 encoder failed
                m_log.Error("[WARP 3D IMAGE MODULE]: Failed generating terrain map: ", e);
            }

            return null;
        }

        #endregion

        #region Rendering Methods

        // Add a water plane to the renderer.
        private void CreateWater(WarpRenderer renderer)
        {
            float waterHeight = (float)m_scene.RegionInfo.RegionSettings.WaterHeight;

            renderer.AddPlane("Water", m_scene.RegionInfo.RegionSizeX * 0.5f, false);
            renderer.Scene.sceneobject("Water").setPos(m_scene.RegionInfo.RegionSizeX * 0.5f,
                                                       waterHeight,
                                                       m_scene.RegionInfo.RegionSizeY * 0.5f);

            warp_Material waterMaterial = new warp_Material(ConvertColor(WATER_COLOR));
            renderer.Scene.addMaterial("WaterMat", waterMaterial);
            renderer.SetObjectMaterial("Water", "WaterMat");
        }

        // Add a terrain to the renderer.
        // Note that we create a 'low resolution' 257x257 vertex terrain rather than trying for
        //    full resolution. This saves a lot of memory especially for very large regions.
        private void CreateTerrain(WarpRenderer renderer)
        {
            ITerrainChannel terrain = m_scene.Heightmap;

            float regionsx = m_scene.RegionInfo.RegionSizeX;
            float regionsy = m_scene.RegionInfo.RegionSizeY;

            // 'diff' is the difference in scale between the real region size and the size of terrain we're buiding

            int bitWidth = Util.intLog2((uint)terrain.Width);
            int bitHeight = Util.intLog2((uint)terrain.Height);
            if(bitHeight > bitWidth)
                bitWidth = bitHeight;

            if (bitWidth > 8) // more than 256 is very heavy :(
                bitWidth = 8;

            int twidth = 1 << bitWidth;

            float diff = regionsx / twidth;

            int npointsx = (int)(regionsx / diff);
            int npointsy = (int)(regionsy / diff);

            float invsx = 1.0f / (npointsx * diff);
            float invsy = 1.0f / (npointsy * diff);

            npointsx++;
            npointsy++;

            // Create all the vertices for the terrain
            warp_Object obj = new warp_Object();
            float x, y;
            float tv;
            for (y = 0; y < regionsy; y += diff)
            {
                tv = y * invsy;
                for (x = 0; x < regionsx; x += diff)
                    obj.addVertex(x, terrain[(int)x, (int)y], y, x * invsx, tv);
                obj.addVertex(x, terrain[(int)(x - diff), (int)y], y, 1.0f, tv);
            }

            int lastY = (int)(y - diff);
            for (x = 0; x < regionsx; x += diff)
                obj.addVertex(x, terrain[(int)x, lastY], y, x * invsx, 1.0f);
            obj.addVertex(x, terrain[(int)(x - diff), lastY],y, 1.0f, 1.0f);

            // create triangles.
            int limx = npointsx - 1;
            int limy = npointsy - 1;
            for (int j = 0; j < limy; j++)
            {
                for (int i = 0; i < limx; i++)
                {
                    int v = j * npointsx + i;

                    // Make two triangles for each of the squares in the grid of vertices
                    obj.addTriangle(v, v + 1, v + npointsx);
                    obj.addTriangle( v + npointsx + 1, v + npointsx, v + 1);
                }
            }

            renderer.Scene.addObject("Terrain", obj);

            OpenSim.Framework.RegionSettings regionInfo = m_scene.RegionInfo.RegionSettings;
            UUID[] textureIDs = new UUID[4]
            {
                regionInfo.TerrainTexture1,
                regionInfo.TerrainTexture2,
                regionInfo.TerrainTexture3,
                regionInfo.TerrainTexture4,
            };

            float[] startHeights = new float[4]
            {
                (float)regionInfo.Elevation1SW,
                (float)regionInfo.Elevation1NW,
                (float)regionInfo.Elevation1SE,
                (float)regionInfo.Elevation1NE
            };

            float[] heightRanges = new float[4]
            {
                (float)regionInfo.Elevation2SW,
                (float)regionInfo.Elevation2NW,
                (float)regionInfo.Elevation2SE,
                (float)regionInfo.Elevation2NE
            };

            warp_Texture texture;
            using (Bitmap image = TerrainSplat.Splat(terrain, textureIDs, startHeights, heightRanges,
                        m_scene.RegionInfo.WorldLocX, m_scene.RegionInfo.WorldLocY,
                        m_scene.AssetService, m_imgDecoder, m_textureTerrain, m_textureAverageTerrain,
                        twidth, twidth))
                    texture = new warp_Texture(image);

            warp_Material material = new warp_Material(texture);
            obj.setMaterial(material);
            renderer.Scene.addMaterial("TerrainMat", material);
        }

        private void CreateAllPrims(WarpRenderer renderer)
        {
            if (m_primMesher == null)
                return;

            m_scene.ForEachSOG(
                delegate (SceneObjectGroup group)
                {
                    foreach (SceneObjectPart child in group.Parts)
                        CreatePrim(renderer, child);
                }
            );
        }

        private void UVPlanarMap(ref Vertex v, ref Vector3 scale, out float tu, out float tv)
        {
            Vector3 scaledPos = v.Position * scale;
            float d = v.Normal.X;
            if (d >= 0.5f)
            {
                tu = 2f * scaledPos.Y;
                tv = scaledPos.X * v.Normal.Z - scaledPos.Z * v.Normal.X;
            }
            else if( d <= -0.5f)
            {
                tu = -2f * scaledPos.Y;
                tv = -scaledPos.X * v.Normal.Z + scaledPos.Z * v.Normal.X;
            }
            else if (v.Normal.Y > 0f)
            {
                tu = -2f * scaledPos.X;
                tv = scaledPos.Y * v.Normal.Z - scaledPos.Z * v.Normal.Y;
            }
            else 
            {
                tu = 2f * scaledPos.X;
                tv = -scaledPos.Y * v.Normal.Z + scaledPos.Z * v.Normal.Y;
            }

            tv *= 2f;
        }

        private void CreatePrim(WarpRenderer renderer, SceneObjectPart prim)
        {
            if ((PCode)prim.Shape.PCode != PCode.Prim)
                return;

            Vector3 ppos = prim.GetWorldPosition();
            if (ppos.Z < m_renderMinHeight || ppos.Z > m_renderMaxHeight)
                return;

            warp_Vector primPos = ConvertVector(ref ppos);
            warp_Matrix m = warp_Matrix.quaternionMatrix(ConvertQuaternion(prim.GetWorldRotation()));

            Vector3 primScale = prim.Scale;
            float screenFactor = renderer.Scene.EstimateBoxProjectedArea(primPos, ConvertVector(primScale), m);
            if (screenFactor < 0)
                return;

            int p2 = (int)(MathF.Log2(screenFactor) * 0.25 - 1);

            if (p2 < 0)
                p2 = 0;
            else if (p2 > 3)
                p2 = 3;

            DetailLevel lod = (DetailLevel)(3 - p2);

            FacetedMesh renderMesh = null;
            Primitive omvPrim = prim.Shape.ToOmvPrimitive(prim.OffsetPosition, prim.RotationOffset);

            if (m_renderMeshes)
            {
                if (omvPrim.Sculpt is not null && !omvPrim.Sculpt.SculptTexture.IsZero())
                {
                    // Try fetchinng the asset
                    AssetBase sculptAsset = m_scene.AssetService.Get(omvPrim.Sculpt.SculptTexture.ToString());
                    if (sculptAsset is not null)
                    {
                        // Is it a mesh?
                        if (omvPrim.Sculpt.Type == SculptType.Mesh)
                        {
                            AssetMesh meshAsset = new AssetMesh(omvPrim.Sculpt.SculptTexture, sculptAsset.Data);
                            FacetedMesh.TryDecodeFromAsset(omvPrim, meshAsset, lod, out renderMesh);
                        }
                        else // It's sculptie
                        {
                            if (m_imgDecoder is not null)
                            {
                                Image sculpt = m_imgDecoder.DecodeToImage(sculptAsset.Data);
                                if (sculpt is not null)
                                {
                                    renderMesh = m_primMesher.GenerateFacetedSculptMesh(omvPrim, (Bitmap)sculpt, lod);
                                    sculpt.Dispose();
                                }
                            }
                        }
                    }
                    else
                    {
                        m_log.WarnFormat("[Warp3D] failed to get mesh or sculpt asset {0} of prim {1} at {2}",
                            omvPrim.Sculpt.SculptTexture.ToString(), prim.Name, prim.GetWorldPosition().ToString());
                    }
                }
            }

            // If not a mesh or sculptie, try the regular mesher
            renderMesh ??= m_primMesher.GenerateFacetedMesh(omvPrim, lod);

            if (renderMesh is null)
                return;

            Primitive.TextureEntry te = prim.Shape.Textures;
            if (te is null)
                return;

            string primID = prim.UUID.ToString();

            float rc = 0;
            float rs = 0;

            for (int i = 0; i < renderMesh.Faces.Count; i++)
            {
                Primitive.TextureEntryFace teFace = te.GetFace((uint)i);
                Color4 faceColor = teFace.RGBA;
                if (faceColor.A == 0)
                    continue;

                warp_Material faceMaterial;
                if (m_texturePrims)
                {
                    faceMaterial = GetOrCreateMaterial(renderer, faceColor, teFace.TextureID, false, prim);
                    if (faceMaterial is null)
                        continue;
                    if ((faceMaterial.getColor() & warp_Color.MASKALPHA) == 0)
                        continue;
                }
                else
                    faceMaterial = GetOrCreateMaterial(renderer, faceColor);

                warp_Object faceObj = new warp_Object();
                faceObj.setMaterial(faceMaterial);

                Face face = renderMesh.Faces[i];
                if (faceMaterial.getTexture() is null)
                {
                    // uv map details dont not matter for color;
                    for (int j = 0; j < face.Vertices.Count; j++)
                    {
                        warp_Vector pos = ConvertVector(face.Vertices[j].Position);
                        warp_Vertex vert = new warp_Vertex(pos, face.Vertices[j].TexCoord.X, face.Vertices[j].TexCoord.Y);
                        faceObj.addVertex(vert);
                    }
                }
                else
                {
                    float tu;
                    float tv;
                    float offsetu = teFace.OffsetU + 0.5f;
                    float offsetv = teFace.OffsetV + 0.5f;
                    float scaleu = teFace.RepeatU;
                    float scalev = teFace.RepeatV;
                    float rotation = teFace.Rotation;
                    if (rotation != 0)
                    {
                        rc = MathF.Cos(rotation);
                        rs = MathF.Sin(rotation);
                    }

                    for (int j = 0; j < face.Vertices.Count; j++)
                    {
                        if(teFace.TexMapType == MappingType.Planar)
                        {
                            Vertex v = face.Vertices[j];
                            UVPlanarMap(ref v, ref primScale, out tu, out tv);
                        }
                        else
                        {
                            tu = face.Vertices[j].TexCoord.X - 0.5f;
                            tv = 0.5f - face.Vertices[j].TexCoord.Y;
                        }

                        warp_Vector pos = ConvertVector(face.Vertices[j].Position);
                        if (rotation != 0)
                        {
                            float tur = tu * rc - tv * rs;
                            float tvr = tu * rs + tv * rc;
                            faceObj.addVertex(new warp_Vertex(pos, tur * scaleu + offsetu, tvr * scalev + offsetv));
                        }
                        else
                        {
                            faceObj.addVertex(new warp_Vertex(pos, tu * scaleu + offsetu, tv * scalev + offsetv));
                        }
                    }
                }

                for (int j = 0; j < face.Indices.Count; j += 3)
                {
                    faceObj.addTriangle(
                        face.Indices[j + 0],
                        face.Indices[j + 1],
                        face.Indices[j + 2]);
                }

                faceObj.scaleSelf(primScale.X, primScale.Z, primScale.Y);
                faceObj.transform(m);
                faceObj.setPos(primPos);

                renderer.Scene.addObject(primID + i.ToString(), faceObj);
            }
        }

        private int GetFaceColor(Primitive.TextureEntryFace face)
        {
            int color;
            Color4 ctmp = Color4.White;

            if (face.TextureID.IsZero())
                return warp_Color.White;

            if (!m_colors.TryGetValue(face.TextureID, out color))
            {
                bool fetched = false;

                // Attempt to fetch the texture metadata
                string cacheName = "MAPCLR" + face.TextureID.ToString();
                AssetBase metadata = m_scene.AssetService.GetCached(cacheName);
                if (metadata != null)
                {
                    OSDMap map = null;
                    try { map = OSDParser.Deserialize(metadata.Data) as OSDMap; } catch { }

                    if (map != null)
                    {
                        ctmp = map["X-RGBA"].AsColor4();
                        fetched = true;
                    }
                }

                if (!fetched)
                {
                    // Fetch the texture, decode and get the average color,
                    // then save it to a temporary metadata asset
                    AssetBase textureAsset = m_scene.AssetService.Get(face.TextureID.ToString());
                    if (textureAsset != null)
                    {
                        int width, height;
                        ctmp = GetAverageColor(textureAsset.FullID, textureAsset.Data, out width, out height);

                        OSDMap data = new OSDMap { { "X-RGBA", OSD.FromColor4(ctmp) } };
                        metadata = new AssetBase
                        {
                            Data = System.Text.Encoding.UTF8.GetBytes(OSDParser.SerializeJsonString(data)),
                            Description = "Metadata for texture color" + face.TextureID.ToString(),
                            Flags = AssetFlags.Collectable,
                            FullID = UUID.Zero,
                            ID = cacheName,
                            Local = true,
                            Temporary = true,
                            Name = String.Empty,
                            Type = (sbyte)AssetType.Unknown
                        };
                        m_scene.AssetService.Store(metadata);
                    }
                    else
                    {
                        ctmp = new Color4(0.5f, 0.5f, 0.5f, 1.0f);
                    }
                }
                color = ConvertColor(ctmp);
                m_colors[face.TextureID] = color;
            }

            return color;
        }

        private warp_Material GetOrCreateMaterial(WarpRenderer renderer, Color4 color)
        {
            string name = color.ToString();

            if(renderer.Scene.TryGetMaterial(name, out warp_Material material))
                return material;

            material = new warp_Material(ConvertColor(color));
            renderer.Scene.addMaterial(name, material);
            return material;
        }

        public warp_Material GetOrCreateMaterial(WarpRenderer renderer, Color4 faceColor, UUID textureID, bool useAverageTextureColor, SceneObjectPart sop)
        {
            int color = ConvertColor(faceColor);
            string idstr = textureID.ToString() + color.ToString();
            string materialName = "MAPMAT" + idstr;

            if (renderer.Scene.TryGetMaterial(materialName, out warp_Material mat))
                return mat;

            mat = new warp_Material();
            warp_Texture texture = GetTexture(textureID, sop);
            if (texture is not null)
            {
                if (useAverageTextureColor)
                    color = warp_Color.multiply(color, texture.averageColor);
                else
                    mat.setTexture(texture);
            }
            else
                color = warp_Color.multiply(color, warp_Color.Grey);

            mat.setColor(color);
            renderer.Scene.addMaterial(materialName, mat);

            return mat;
        }

        private warp_Texture GetTexture(UUID id, SceneObjectPart sop)
        {
            if (id.IsZero())
                return null;
            if (m_warpTextures.TryGetValue(id, out warp_Texture ret))
                return ret;

            AssetBase asset = m_scene.AssetService.Get(id.ToString());
            if (asset is not null)
            {
                byte[] data = asset.Data;
                string payloadKind = ClassifyTexturePayload(data);

                if (m_lunaTextureEnabled && (data is null || data.Length == 0))
                {
                    LogTextureDecodeFailureOnce(id, sop, "LunaTexture classified asset payload as empty");
                }
                else
                {
                    try
                    {
                        Image decoded = m_imgDecoder?.DecodeToImage(data);
                        if (decoded is null && m_lunaTextureEnabled && m_lunaTextureRasterFallback)
                            decoded = TryDecodeRasterTexture(data, payloadKind);

                        if (decoded != null)
                        {
                            using (decoded)
                            {
                                if (decoded is Bitmap img)
                                    ret = new warp_Texture(img, 8); // reduce textures size to 256 * 256
                                else
                                    using (Bitmap bitmap = new Bitmap(decoded))
                                        ret = new warp_Texture(bitmap, 8);
                            }

                            if (m_lunaTextureEnabled && payloadKind != "jpeg2000")
                            {
                                m_log.InfoFormat(
                                    "[LunaTexture]: recovered texture {0} for prim {1} from {2} payload without modifying the asset",
                                    id,
                                    sop?.Name ?? "<unknown>",
                                    payloadKind);
                            }
                        }
                        else
                        {
                            LogTextureDecodeFailureOnce(
                                id,
                                sop,
                                $"LunaTexture classification={payloadKind}; decoder returned no image");
                        }
                    }
                    catch (Exception e)
                    {
                        Image recovered = null;
                        if (m_lunaTextureEnabled && m_lunaTextureRasterFallback)
                            recovered = TryDecodeRasterTexture(data, payloadKind);

                        if (recovered != null)
                        {
                            using (recovered)
                            {
                                if (recovered is Bitmap img)
                                    ret = new warp_Texture(img, 8);
                                else
                                    using (Bitmap bitmap = new Bitmap(recovered))
                                        ret = new warp_Texture(bitmap, 8);
                            }

                            m_log.InfoFormat(
                                "[LunaTexture]: recovered texture {0} for prim {1} from {2} payload after JPEG2000 decoder failure",
                                id,
                                sop?.Name ?? "<unknown>",
                                payloadKind);
                        }
                        else
                            LogTextureDecodeFailureOnce(id, sop, $"LunaTexture classification={payloadKind}; {e.Message}");
                    }
                }
            }
            else
                LogMissingTextureOnce(id, sop);

            if (ret is null && m_lunaTextureEnabled && m_lunaTexturePlaceholder)
                ret = CreateLunaTexturePlaceholder();

            m_warpTextures[id] = ret;
            return ret;
        }

        private sealed class LunaTextureDiagnostic
        {
            public UUID TextureID;
            public string Classification;
            public string PrimName;
            public string Position;
            public string Reason;
            public DateTime LastSeenUtc;
            public int Count;
        }

        private void RecordLunaTextureDiagnostic(UUID id, SceneObjectPart sop, string classification, string reason)
        {
            lock (m_textureDecodeWarningsLock)
            {
                if (m_lunaTextureDiagnostics.TryGetValue(id, out LunaTextureDiagnostic existing))
                {
                    existing.Count++;
                    existing.LastSeenUtc = DateTime.UtcNow;
                    existing.Classification = classification ?? existing.Classification;
                    existing.PrimName = sop?.Name ?? existing.PrimName;
                    existing.Position = sop?.GetWorldPosition().ToString() ?? existing.Position;
                    existing.Reason = reason;
                    m_lunaTextureStoreDirty = true;
                }
                else
                {
                    while (m_lunaTextureDiagnostics.Count >= m_lunaTextureDiagnosticLimit &&
                           m_lunaTextureDiagnosticOrder.Count > 0)
                    {
                        UUID oldest = m_lunaTextureDiagnosticOrder.Dequeue();
                        m_lunaTextureDiagnostics.Remove(oldest);
                    }

                    m_lunaTextureDiagnostics[id] = new LunaTextureDiagnostic
                    {
                        TextureID = id,
                        Classification = classification,
                        PrimName = sop?.Name ?? "<unknown>",
                        Position = sop?.GetWorldPosition().ToString() ?? "<unknown>",
                        Reason = reason,
                        LastSeenUtc = DateTime.UtcNow,
                        Count = 1
                    };
                    m_lunaTextureDiagnosticOrder.Enqueue(id);
                    m_lunaTextureStoreDirty = true;
                }
            }

            PersistLunaTextureDiagnostics(false);
        }

        private string ResolveLunaTextureDiagnosticStorePath(Scene scene)
        {
            if (scene == null || string.IsNullOrWhiteSpace(m_lunaTextureDiagnosticStore))
                return string.Empty;

            string root = Path.IsPathRooted(m_lunaTextureDiagnosticStore)
                ? m_lunaTextureDiagnosticStore
                : Path.Combine(AppContext.BaseDirectory, m_lunaTextureDiagnosticStore);

            root = Path.GetFullPath(root);
            Directory.CreateDirectory(root);

            return Path.Combine(
                root,
                scene.RegionInfo.RegionID.ToString() + ".json");
        }

        private void LoadLunaTextureDiagnostics()
        {
            if (string.IsNullOrWhiteSpace(m_lunaTextureDiagnosticStorePath) ||
                !File.Exists(m_lunaTextureDiagnosticStorePath))
            {
                return;
            }

            try
            {
                string json =
                    File.ReadAllText(m_lunaTextureDiagnosticStorePath);
                LunaTextureDiagnosticStoreDocument document =
                    JsonSerializer.Deserialize<LunaTextureDiagnosticStoreDocument>(
                        json,
                        s_lunaTextureJson);

                if (document?.Records == null)
                    return;

                List<LunaTextureDiagnosticStoreRecord> records =
                    document.Records
                        .Where(record =>
                            record != null &&
                            UUID.TryParse(record.TextureID, out _) &&
                            record.Count > 0)
                        .OrderBy(record => record.LastSeenUtc)
                        .TakeLast(m_lunaTextureDiagnosticLimit)
                        .ToList();

                lock (m_textureDecodeWarningsLock)
                {
                    m_lunaTextureDiagnostics.Clear();
                    m_lunaTextureDiagnosticOrder.Clear();

                    foreach (LunaTextureDiagnosticStoreRecord record in records)
                    {
                        if (!UUID.TryParse(record.TextureID, out UUID textureID))
                            continue;

                        m_lunaTextureDiagnostics[textureID] =
                            new LunaTextureDiagnostic
                            {
                                TextureID = textureID,
                                Classification = record.Classification ?? "unknown",
                                PrimName = record.PrimName ?? "<unknown>",
                                Position = record.Position ?? "<unknown>",
                                Reason = record.Reason ?? string.Empty,
                                LastSeenUtc =
                                    record.LastSeenUtc == default
                                        ? DateTime.UtcNow
                                        : record.LastSeenUtc.ToUniversalTime(),
                                Count = Math.Max(1, record.Count)
                            };

                        m_lunaTextureDiagnosticOrder.Enqueue(textureID);
                        m_textureDecodeWarnings.Add(textureID);
                    }

                    m_lunaTextureStoreDirty = false;
                    m_lunaTextureLastPersistUtc = DateTime.UtcNow;
                }

                m_log.InfoFormat(
                    "[LunaTexture]: loaded {0} persistent diagnostic record(s) for region {1}",
                    records.Count,
                    m_scene?.RegionInfo.RegionName ?? "<unknown>");
            }
            catch (Exception e)
            {
                m_log.Warn(
                    "[LunaTexture]: persistent diagnostic store could not be loaded; runtime diagnostics continue in memory.",
                    e);
            }
        }

        private void PersistLunaTextureDiagnostics(bool force)
        {
            if (string.IsNullOrWhiteSpace(m_lunaTextureDiagnosticStorePath))
                return;

            List<LunaTextureDiagnosticStoreRecord> records;

            lock (m_textureDecodeWarningsLock)
            {
                if (!m_lunaTextureStoreDirty && !force)
                    return;

                DateTime now = DateTime.UtcNow;
                if (!force &&
                    now - m_lunaTextureLastPersistUtc <
                    s_lunaTexturePersistInterval)
                {
                    return;
                }

                records =
                    m_lunaTextureDiagnostics.Values
                        .OrderBy(item => item.LastSeenUtc)
                        .Select(item =>
                            new LunaTextureDiagnosticStoreRecord
                            {
                                TextureID = item.TextureID.ToString(),
                                Classification = item.Classification,
                                PrimName = item.PrimName,
                                Position = item.Position,
                                Reason = item.Reason,
                                LastSeenUtc = item.LastSeenUtc,
                                Count = item.Count
                            })
                        .ToList();
            }

            string tempPath =
                m_lunaTextureDiagnosticStorePath +
                ".tmp-" +
                Guid.NewGuid().ToString("N");

            try
            {
                string directory =
                    Path.GetDirectoryName(m_lunaTextureDiagnosticStorePath);
                if (!string.IsNullOrWhiteSpace(directory))
                    Directory.CreateDirectory(directory);

                LunaTextureDiagnosticStoreDocument document =
                    new LunaTextureDiagnosticStoreDocument
                    {
                        Version = 1,
                        RegionID =
                            m_scene?.RegionInfo.RegionID.ToString() ??
                            string.Empty,
                        UpdatedUtc = DateTime.UtcNow,
                        Records = records
                    };

                File.WriteAllText(
                    tempPath,
                    JsonSerializer.Serialize(
                        document,
                        s_lunaTextureJson));
                File.Move(
                    tempPath,
                    m_lunaTextureDiagnosticStorePath,
                    true);

                lock (m_textureDecodeWarningsLock)
                {
                    m_lunaTextureStoreDirty = false;
                    m_lunaTextureLastPersistUtc = DateTime.UtcNow;
                }
            }
            catch (Exception e)
            {
                lock (m_textureDecodeWarningsLock)
                    m_lunaTextureStoreDirty = true;

                m_log.Warn(
                    "[LunaTexture]: persistent diagnostic store could not be updated.",
                    e);
            }
            finally
            {
                try
                {
                    if (File.Exists(tempPath))
                        File.Delete(tempPath);
                }
                catch
                {
                }
            }
        }

        private void DeleteLunaTextureDiagnosticStore()
        {
            if (string.IsNullOrWhiteSpace(m_lunaTextureDiagnosticStorePath))
                return;

            try
            {
                if (File.Exists(m_lunaTextureDiagnosticStorePath))
                    File.Delete(m_lunaTextureDiagnosticStorePath);
            }
            catch (Exception e)
            {
                m_log.Warn(
                    "[LunaTexture]: persistent diagnostic store could not be cleared.",
                    e);
            }
        }

        private sealed class LunaTextureDiagnosticStoreDocument
        {
            public LunaTextureDiagnosticStoreDocument()
            {
            }

            public int Version { get; set; }
            public string RegionID { get; set; }
            public DateTime UpdatedUtc { get; set; }
            public List<LunaTextureDiagnosticStoreRecord> Records { get; set; }
        }

        private sealed class LunaTextureDiagnosticStoreRecord
        {
            public LunaTextureDiagnosticStoreRecord()
            {
            }

            public string TextureID { get; set; }
            public string Classification { get; set; }
            public string PrimName { get; set; }
            public string Position { get; set; }
            public string Reason { get; set; }
            public DateTime LastSeenUtc { get; set; }
            public int Count { get; set; }
        }

        private static warp_Texture CreateLunaTexturePlaceholder()
        {
            using Bitmap placeholder = new Bitmap(8, 8);
            using Graphics graphics = Graphics.FromImage(placeholder);
            graphics.Clear(Color.Gray);
            using Brush dark = new SolidBrush(Color.FromArgb(72, 72, 72));
            graphics.FillRectangle(dark, 0, 0, 4, 4);
            graphics.FillRectangle(dark, 4, 4, 4, 4);
            return new warp_Texture(placeholder);
        }

        public IReadOnlyList<LunaTextureDiagnosticInfo> GetDiagnostics()
        {
            List<LunaTextureDiagnosticInfo> result = new List<LunaTextureDiagnosticInfo>();
            lock (m_textureDecodeWarningsLock)
            {
                foreach (LunaTextureDiagnostic item in m_lunaTextureDiagnostics.Values)
                    result.Add(ToDiagnosticInfo(item));
            }
            result.Sort((a, b) => b.LastSeenUtc.CompareTo(a.LastSeenUtc));
            return result;
        }

        public bool TryGetDiagnostic(UUID textureID, out LunaTextureDiagnosticInfo diagnostic)
        {
            lock (m_textureDecodeWarningsLock)
            {
                if (m_lunaTextureDiagnostics.TryGetValue(textureID, out LunaTextureDiagnostic item))
                {
                    diagnostic = ToDiagnosticInfo(item);
                    return true;
                }
            }
            diagnostic = null;
            return false;
        }

        public bool RetryTexture(UUID textureID, out string result)
        {
            AssetBase asset = m_scene?.AssetService?.Get(textureID.ToString());
            if (asset is null)
            {
                result = "asset_missing";
                RecordLunaTextureDiagnostic(textureID, null, "missing", "retry: asset service returned no texture data");
                return false;
            }

            string classification = ClassifyTexturePayload(asset.Data);
            lock (m_textureDecodeWarningsLock)
                m_textureDecodeWarnings.Remove(textureID);
            m_warpTextures?.Remove(textureID);

            result = $"cache_evicted; classification={classification}; retry_on_next_render";
            return true;
        }

        public void ClearDiagnostics()
        {
            lock (m_textureDecodeWarningsLock)
            {
                m_lunaTextureDiagnostics.Clear();
                m_lunaTextureDiagnosticOrder.Clear();
                m_textureDecodeWarnings.Clear();
                m_lunaTextureStoreDirty = false;
                m_lunaTextureLastPersistUtc = DateTime.UtcNow;
            }

            DeleteLunaTextureDiagnosticStore();
        }

        private static LunaTextureDiagnosticInfo ToDiagnosticInfo(LunaTextureDiagnostic item)
        {
            return new LunaTextureDiagnosticInfo
            {
                TextureID = item.TextureID,
                Classification = item.Classification,
                PrimName = item.PrimName,
                Position = item.Position,
                Reason = item.Reason,
                LastSeenUtc = item.LastSeenUtc,
                Count = item.Count
            };
        }

        private void HandleLunaTextureCommand(string module, string[] args)
        {
            if (MainConsole.Instance.ConsoleScene is Scene selected && selected != m_scene)
                return;

            if (args.Length >= 3 && args[2].Equals("clear", StringComparison.OrdinalIgnoreCase))
            {
                ClearDiagnostics();
                MainConsole.Instance.Output("[LunaTexture] Diagnostics cleared for region {0}", m_scene.RegionInfo.RegionName);
                return;
            }

            if (args.Length >= 4 &&
                (args[2].Equals("inspect", StringComparison.OrdinalIgnoreCase) ||
                 args[2].Equals("retry", StringComparison.OrdinalIgnoreCase)))
            {
                if (!UUID.TryParse(args[3], out UUID textureID))
                {
                    MainConsole.Instance.Output("[LunaTexture] Invalid texture UUID: {0}", args[3]);
                    return;
                }

                if (args[2].Equals("retry", StringComparison.OrdinalIgnoreCase))
                {
                    bool ok = RetryTexture(textureID, out string retryResult);
                    MainConsole.Instance.Output("[LunaTexture] Retry {0}: {1}", ok ? "scheduled" : "failed", retryResult);
                    return;
                }

                if (!TryGetDiagnostic(textureID, out LunaTextureDiagnosticInfo item))
                {
                    MainConsole.Instance.Output("[LunaTexture] No diagnostic record for {0}", textureID);
                    return;
                }

                MainConsole.Instance.Output(
                    "[LunaTexture] {0} class={1} count={2} last={3:o} prim={4} pos={5} reason={6}",
                    item.TextureID, item.Classification, item.Count, item.LastSeenUtc,
                    item.PrimName, item.Position, item.Reason);
                return;
            }

            List<LunaTextureDiagnostic> snapshot;
            lock (m_textureDecodeWarningsLock)
                snapshot = new List<LunaTextureDiagnostic>(m_lunaTextureDiagnostics.Values);

            MainConsole.Instance.Output(
                "[LunaTexture] Region {0}: {1} diagnostic texture(s), limit {2}",
                m_scene.RegionInfo.RegionName, snapshot.Count, m_lunaTextureDiagnosticLimit);

            snapshot.Sort((a, b) => b.LastSeenUtc.CompareTo(a.LastSeenUtc));
            foreach (LunaTextureDiagnostic item in snapshot)
            {
                MainConsole.Instance.Output(
                    "[LunaTexture] {0} class={1} count={2} prim={3} pos={4} reason={5}",
                    item.TextureID, item.Classification, item.Count, item.PrimName, item.Position, item.Reason);
            }
        }

        private static string ClassifyTexturePayload(byte[] data)
        {
            if (data is null || data.Length == 0)
                return "empty";

            if (data.Length >= 12 &&
                data[0] == 0x00 && data[1] == 0x00 && data[2] == 0x00 && data[3] == 0x0C &&
                data[4] == 0x6A && data[5] == 0x50 && data[6] == 0x20 && data[7] == 0x20 &&
                data[8] == 0x0D && data[9] == 0x0A && data[10] == 0x87 && data[11] == 0x0A)
                return "jpeg2000";

            if (data.Length >= 2 && data[0] == 0xFF && data[1] == 0x4F)
                return "jpeg2000";

            if (data.Length >= 8 &&
                data[0] == 0x89 && data[1] == 0x50 && data[2] == 0x4E && data[3] == 0x47 &&
                data[4] == 0x0D && data[5] == 0x0A && data[6] == 0x1A && data[7] == 0x0A)
                return "png";

            if (data.Length >= 3 && data[0] == 0xFF && data[1] == 0xD8 && data[2] == 0xFF)
                return "jpeg";

            if (data.Length >= 6 && data[0] == 0x47 && data[1] == 0x49 && data[2] == 0x46 &&
                data[3] == 0x38 && (data[4] == 0x37 || data[4] == 0x39) && data[5] == 0x61)
                return "gif";

            if (data.Length >= 2 && data[0] == 0x42 && data[1] == 0x4D)
                return "bmp";

            return "unknown";
        }

        private static Image TryDecodeRasterTexture(byte[] data, string payloadKind)
        {
            if (data is null || data.Length == 0)
                return null;

            if (payloadKind != "png" && payloadKind != "jpeg" &&
                payloadKind != "gif" && payloadKind != "bmp")
                return null;

            try
            {
                using MemoryStream stream = new MemoryStream(data, false);
                using Image source = Image.FromStream(stream, true, true);
                return new Bitmap(source);
            }
            catch
            {
                return null;
            }
        }

        private void LogMissingTextureOnce(UUID id, SceneObjectPart sop)
        {
            RecordLunaTextureDiagnostic(id, sop, "missing", "asset service returned no texture data");

            lock (m_textureDecodeWarningsLock)
            {
                if (!m_textureDecodeWarnings.Add(id))
                    return;
            }

            m_log.WarnFormat(
                "[LunaTexture]: missing texture {0} data for prim {1} at {2}; classification=missing. " +
                "Further warnings for this texture UUID are suppressed.",
                id,
                sop?.Name ?? "<unknown>",
                sop?.GetWorldPosition().ToString() ?? "<unknown>");
        }

        private void LogTextureDecodeFailureOnce(
            UUID id,
            SceneObjectPart sop,
            string reason)
        {
            string classification = "decoder_failed";
            int marker = reason?.IndexOf("classification=", StringComparison.Ordinal) ?? -1;
            if (marker >= 0)
            {
                int start = marker + "classification=".Length;
                int end = reason.IndexOfAny(new[] { ';', ' ' }, start);
                classification = end > start ? reason.Substring(start, end - start) : reason.Substring(start);
            }
            RecordLunaTextureDiagnostic(id, sop, classification, reason);

            lock (m_textureDecodeWarningsLock)
            {
                if (!m_textureDecodeWarnings.Add(id))
                    return;
            }

            m_log.WarnFormat(
                "[LunaTexture]: Failed to decode texture {0} for prim {1} at {2}; " +
                "decoder recovery exhausted: {3}. Further warnings for " +
                "this texture UUID are suppressed.",
                id,
                sop?.Name ?? "<unknown>",
                sop?.GetWorldPosition().ToString() ?? "<unknown>",
                reason);
        }

        #endregion Rendering Methods

        #region Static Helpers
        // Note: axis change.
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        private static warp_Vector ConvertVector(float x, float y, float z)
        {
            return new warp_Vector(x, z, y);
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        private static warp_Vector ConvertVector(Vector3 vector)
        {
            return new warp_Vector(vector.X, vector.Z, vector.Y);
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        private static warp_Vector ConvertVector(ref Vector3 vector)
        {
            return new warp_Vector(vector.X, vector.Z, vector.Y);
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        private static warp_Quaternion ConvertQuaternion(Quaternion quat)
        {
            return new warp_Quaternion(quat.X, quat.Z, quat.Y, -quat.W);
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        private static int ConvertColor(Color4 color)
        {
            int c = warp_Color.getColor((byte)(color.R * 255f), (byte)(color.G * 255f), (byte)(color.B * 255f), (byte)(color.A * 255f));
            return c;
        }

        private static Vector3 SurfaceNormal(Vector3 c1, Vector3 c2, Vector3 c3)
        {
            Vector3 normal = Vector3.Cross(c2 - c1, c3 - c1);
            normal.Normalize();

            return normal;
        }

        public Color4 GetAverageColor(UUID textureID, byte[] j2kData, out int width, out int height)
        {
            ulong r = 0;
            ulong g = 0;
            ulong b = 0;
            ulong a = 0;
            int pixelBytes;

            try
            {
                using (MemoryStream stream = new MemoryStream(j2kData))
                using (Bitmap bitmap = (Bitmap)J2kImage.FromStream(stream))
                {
                    width = bitmap.Width;
                    height = bitmap.Height;

                    BitmapData bitmapData = bitmap.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.ReadOnly, bitmap.PixelFormat);
                    pixelBytes = (bitmapData.PixelFormat == PixelFormat.Format24bppRgb) ? 3 : 4;

                    // Sum up the individual channels
                    unsafe
                    {
                        byte* start = (byte*)bitmapData.Scan0;
                        if (pixelBytes == 4)
                        {
                            for (int y = 0; y < height; y++)
                            {
                                
                                byte* end = start + 4 * width;
                                for(byte* row = start; row < end; row += 4)
                                {
                                    b += row[0];
                                    g += row[1];
                                    r += row[2];
                                    a += row[3];
                                }
                                start += bitmapData.Stride;
                            }
                        }
                        else
                        {
                            for (int y = 0; y < height; y++)
                            {
                                byte* end = start + 3 * width;
                                for (byte* row = start; row < end; row += 3)
                                {
                                    b += row[0];
                                    g += row[1];
                                    r += row[2];
                                }
                                start += bitmapData.Stride;
                            }
                        }
                    }
                    bitmap.UnlockBits(bitmapData);
                }
                // Get the averages for each channel
                double invtotalPixels = 1.0/(255.0 * width * height);
                double rm = r * invtotalPixels;
                double gm = g * invtotalPixels;
                double bm = b * invtotalPixels;
                double am = pixelBytes == 3 ? 1.0 : a * invtotalPixels;
                return new Color4((float)rm, (float)gm, (float)bm, (float)am);
            }
            catch (Exception ex)
            {
                m_log.WarnFormat(
                    "[WARP 3D IMAGE MODULE]: Error decoding JPEG2000 texture {0} ({1} bytes): {2}",
                    textureID, j2kData.Length, ex.Message);

                width = 0;
                height = 0;
                return new Color4(0.5f, 0.5f, 0.5f, 1.0f);
            }
        }

        #endregion Static Helpers
    }

    public static class ImageUtils
    {
        /// <summary>
        /// Performs bilinear interpolation between four values
        /// </summary>
        /// <param name="v00">First, or top left value</param>
        /// <param name="v01">Second, or top right value</param>
        /// <param name="v10">Third, or bottom left value</param>
        /// <param name="v11">Fourth, or bottom right value</param>
        /// <param name="xPercent">Interpolation value on the X axis, between 0.0 and 1.0</param>
        /// <param name="yPercent">Interpolation value on fht Y axis, between 0.0 and 1.0</param>
        /// <returns>The bilinearly interpolated result</returns>
        public static float Bilinear(float v00, float v01, float v10, float v11, float xPercent, float yPercent)
        {
            return Utils.Lerp(Utils.Lerp(v00, v01, xPercent), Utils.Lerp(v10, v11, xPercent), yPercent);
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public static float Bilinear(float[] v, float xPercent, float yPercent)
        {
            return Utils.Lerp(Utils.Lerp(v[0], v[2], xPercent), Utils.Lerp(v[1], v[3], xPercent), yPercent);
        }
    }
}
