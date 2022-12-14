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
using System.IO;
using System.Net;
using System.Reflection;
using System.Text;
using log4net;
using Mono.Addins;
using Nini.Config;

using OpenMetaverse;
using OpenMetaverse.StructuredData;

using OpenSim.Framework;
using OpenSim.Framework.Servers.HttpServer;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;
using OpenSimAssetType = OpenSim.Framework.SLUtil.OpenSimAssetType;

using Ionic.Zlib;

namespace OpenSim.Region.OptionalModules.Materials
{
    [Extension(Path = "/OpenSim/RegionModules", NodeName = "RegionModule", Id = "MaterialsModule")]
    public class MaterialsModule : INonSharedRegionModule, IMaterialsModule
    {
        private static readonly ILog m_log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        public string Name { get { return "MaterialsModule"; } }

        public Type ReplaceableInterface { get { return null; } }

        IAssetCache m_cache;
        private Scene m_scene = null;
        private bool m_enabled = false;
        private int m_maxMaterialsPerTransaction = 50;
        private object materialslock = new object();

        public Dictionary<UUID, FaceMaterial> m_Materials = new Dictionary<UUID, FaceMaterial>();
        public Dictionary<UUID, int> m_MaterialsRefCount = new Dictionary<UUID, int>();

        private Dictionary<FaceMaterial, double> m_changed = new Dictionary<FaceMaterial, double>();
        private Queue<UUID> delayedDelete = new Queue<UUID>();
        private bool m_storeBusy;

        private static byte[] GetPutEmptyResponseBytes = osUTF8.GetASCIIBytes("<llsd><map><key>Zipped</key><binary>eNqLZgCCWAAChQC5</binary></map></llsd>");

        public void Initialise(IConfigSource source)
        {
            m_enabled = true; // default is enabled

            IConfig config = source.Configs["Materials"];
            if (config != null)
            {
                m_enabled = config.GetBoolean("enable_materials", m_enabled);
                m_maxMaterialsPerTransaction = config.GetInt("MaxMaterialsPerTransaction", m_maxMaterialsPerTransaction);
            }

            if (m_enabled)
                m_log.DebugFormat("[Materials]: Initialized");
        }

        public void Close()
        {
            if (!m_enabled)
                return;
        }

        public void AddRegion(Scene scene)
        {
            if (!m_enabled)
                return;

            m_scene = scene;
            m_scene.RegisterModuleInterface<IMaterialsModule>(this);
            m_scene.EventManager.OnRegisterCaps += OnRegisterCaps;
            m_scene.EventManager.OnObjectAddedToScene += EventManager_OnObjectAddedToScene;
            m_scene.EventManager.OnObjectBeingRemovedFromScene += EventManager_OnObjectDeleteFromScene;
            m_scene.EventManager.OnBackup += EventManager_OnBackup;
        }

        public void RemoveRegion(Scene scene)
        {
            if (!m_enabled)
                return;

            m_scene.EventManager.OnRegisterCaps -= OnRegisterCaps;
            m_scene.EventManager.OnObjectAddedToScene -= EventManager_OnObjectAddedToScene;
            m_scene.EventManager.OnObjectBeingRemovedFromScene -= EventManager_OnObjectDeleteFromScene;
            m_scene.EventManager.OnBackup -= EventManager_OnBackup;
            m_scene.UnregisterModuleInterface<IMaterialsModule>(this);
        }

        public void RegionLoaded(Scene scene)
        {
            if (!m_enabled) return;

            m_cache = scene.RequestModuleInterface<IAssetCache>();
            ISimulatorFeaturesModule featuresModule = scene.RequestModuleInterface<ISimulatorFeaturesModule>();
            if (featuresModule != null)
            {
                featuresModule.AddOpenSimExtraFeature("MaxMaterialsPerTransaction", m_maxMaterialsPerTransaction);
                featuresModule.AddOpenSimExtraFeature("RenderMaterialsCapability", 3.0f);
            }
        }

        private void EventManager_OnBackup(ISimulationDataService datastore, bool forcedBackup)
        {
            List<FaceMaterial> toStore = null;

            lock (materialslock)
            {
                if(m_storeBusy && !forcedBackup)
                    return;

                if(m_changed.Count == 0)
                {
                    if(forcedBackup)
                        return;

                    UUID id;
                    int throttle = 0;
                    while(delayedDelete.Count > 0 && throttle < 5)
                    {
                        id = delayedDelete.Dequeue();
                        if (m_Materials.ContainsKey(id))
                        {
                            if (m_MaterialsRefCount[id] <= 0)
                            {
                                m_Materials.Remove(id);
                                m_MaterialsRefCount.Remove(id);
                                m_cache.Expire(id.ToString());
                                ++throttle;
                            }
                        }
                    }
                    return;
                }

                if (forcedBackup)
                {
                    toStore = new List<FaceMaterial>(m_changed.Keys);
                    m_changed.Clear();
                }
                else
                {
                    toStore = new List<FaceMaterial>();
                    double storetime = Util.GetTimeStamp() - 30.0;
                    foreach(KeyValuePair<FaceMaterial, double> kvp in m_changed)
                    {
                        if(kvp.Value < storetime)
                        {
                            toStore.Add(kvp.Key);
                        }
                    }
                    foreach(FaceMaterial fm  in toStore)
                    {
                        m_changed.Remove(fm);
                    }
                }
            }

            if(toStore.Count > 0)
            {
                m_storeBusy = true;
                if (forcedBackup)
                {
                    foreach (FaceMaterial fm in toStore)
                    {
                        AssetBase a = MakeAsset(fm, false);
                        m_scene.AssetService.Store(a);
                    }
                    m_storeBusy = false;
                }
                else
                {
                    Util.FireAndForget(delegate
                    {
                        foreach (FaceMaterial fm in toStore)
                        {
                            AssetBase a = MakeAsset(fm, false);
                            m_scene.AssetService.Store(a);
                        }
                        m_storeBusy = false;
                    });
                }
            }
        }

        private void EventManager_OnObjectAddedToScene(SceneObjectGroup obj)
        {
            foreach (var part in obj.Parts)
                if (part != null)
                    GetStoredMaterialsInPart(part);
        }

        private void EventManager_OnObjectDeleteFromScene(SceneObjectGroup obj)
        {
            foreach (var part in obj.Parts)
            {
                if (part != null)
                    RemoveMaterialsInPart(part);
            }
        }

        private void OnRegisterCaps(UUID agentID, OpenSim.Framework.Capabilities.Caps caps)
        {
            caps.RegisterSimpleHandler("RenderMaterials", 
                new SimpleStreamHandler("/" + UUID.Random(),
                    (httpRequest, httpResponse)
                        => preprocess(httpRequest, httpResponse,agentID)
                ));
        }

        private void preprocess(IOSHttpRequest request, IOSHttpResponse response, UUID agentID)
        {
            switch (request.HttpMethod)
            {
                case "GET":
                    RenderMaterialsGetCap(request, response);
                    break;
                case "PUT":
                    RenderMaterialsPutCap(request, response, agentID);
                    break;
                case "POST":
                    RenderMaterialsPostCap(request, response, agentID);
                    break;
                default:
                    response.StatusCode = (int)HttpStatusCode.NotFound;
                    return;
            }
            response.StatusCode = (int)HttpStatusCode.OK;
        }

        /// <summary>
        /// Finds any legacy materials stored in DynAttrs that may exist for this part and add them to 'm_regionMaterials'.
        /// </summary>
        /// <param name="part"></param>
        private bool GetLegacyStoredMaterialsInPart(SceneObjectPart part)
        {
            if (part.DynAttrs == null)
                return false;

            OSD OSMaterials = null;
            OSDArray matsArr = null;

            bool partchanged = false;

            lock (part.DynAttrs)
            {
                if (part.DynAttrs.ContainsStore("OpenSim", "Materials"))
                {
                    OSDMap materialsStore = part.DynAttrs.GetStore("OpenSim", "Materials");

                    if (materialsStore == null)
                        return false;

                    materialsStore.TryGetValue("Materials", out OSMaterials);
                    part.DynAttrs.RemoveStore("OpenSim", "Materials");
                    partchanged = true;
                }

                if (OSMaterials != null && OSMaterials is OSDArray)
                    matsArr = OSMaterials as OSDArray;
                else
                    return partchanged;
            }

            if (matsArr == null)
                return partchanged;
            
            foreach (OSD elemOsd in matsArr)
            {
                if (elemOsd != null && elemOsd is OSDMap)
                {
                    OSDMap matMap = elemOsd as OSDMap;
                    OSD OSDID;
                    OSD OSDMaterial;
                    if (matMap.TryGetValue("ID", out OSDID) && matMap.TryGetValue("Material", out OSDMaterial) && OSDMaterial is OSDMap)
                    {
                        try
                        {
                            lock (materialslock)
                            {
                                UUID id = OSDID.AsUUID();
                                if(m_Materials.ContainsKey(id))
                                    continue;

                                OSDMap theMatMap = (OSDMap)OSDMaterial;
                                FaceMaterial fmat = new FaceMaterial(theMatMap);

                                if(fmat == null ||
                                        ( fmat.DiffuseAlphaMode == 1
                                        && fmat.NormalMapID.IsZero()
                                        && fmat.SpecularMapID.IsZero()))
                                    continue;

                                fmat.ID = id; 
                                m_Materials[id] = fmat;
                                m_MaterialsRefCount[id] = 0;
                            }
                        }
                        catch (Exception e)
                        {
                            m_log.Warn("[Materials]: exception decoding persisted legacy material: " + e.ToString());
                        }
                    }
                }
            }
            return partchanged;
        }

        /// <summary>
        /// Find the materials used in the SOP, and add them to 'm_regionMaterials'.
        /// </summary>
        private void GetStoredMaterialsInPart(SceneObjectPart part)
        {
            if (part.Shape == null)
                return;

            bool partchanged = false;
            bool facechanged = false;
            var te = new Primitive.TextureEntry(part.Shape.TextureEntry, 0, part.Shape.TextureEntry.Length);
            if (te == null)
                return;

            partchanged = GetLegacyStoredMaterialsInPart(part);

            if (te.DefaultTexture != null)
                facechanged = GetStoredMaterialInFace(part, te.DefaultTexture);
            else
                m_log.WarnFormat(
                    "[Materials]: Default texture for part {0} (part of object {1}) in {2} unexpectedly null.  Ignoring.",
                    part.Name, part.ParentGroup.Name, m_scene.Name);

            foreach (Primitive.TextureEntryFace face in te.FaceTextures)
            {
                if (face != null)
                    facechanged |= GetStoredMaterialInFace(part, face);
            }

            if(facechanged)
                part.Shape.TextureEntry = te.GetBytes(9);

            if(facechanged || partchanged)
            {
                if (part.ParentGroup != null && !part.ParentGroup.IsDeleted)
                    part.ParentGroup.HasGroupChanged = true;
            }
        }

        /// <summary>
        /// Find the materials used in one Face, and add them to 'm_regionMaterials'.
        /// </summary>
        private bool GetStoredMaterialInFace(SceneObjectPart part, Primitive.TextureEntryFace face)
        {
            UUID id = face.MaterialID;
            if (id.IsZero())
                return false;

            OSDMap mat;
            lock (materialslock)
            {
                if(m_Materials.ContainsKey(id))
                {
                    m_MaterialsRefCount[id]++;
                    return false;
                }

                AssetBase matAsset = m_scene.AssetService.Get(id.ToString());
                if (matAsset == null || matAsset.Data == null || matAsset.Data.Length == 0 )
                {
                    // grid may just be down...
                    return false;
                }

                byte[] data = matAsset.Data;

                // string txt = System.Text.Encoding.ASCII.GetString(data);
                try
                {
                    mat = (OSDMap)OSDParser.DeserializeLLSDXml(data);
                }
                catch (Exception e)
                {
                    m_log.WarnFormat("[Materials]: cannot decode material asset {0}: {1}", id, e.Message);
                    return false;
                }

                FaceMaterial fmat = new FaceMaterial(mat);

                if(fmat == null ||
                        (fmat.DiffuseAlphaMode == 1
                        && fmat.NormalMapID.IsZero()
                        && fmat.SpecularMapID.IsZero()))
                {
                        face.MaterialID = UUID.Zero;
                        return true;
                }

                fmat.ID = id;

                if (m_Materials.ContainsKey(id))
                {
                    m_MaterialsRefCount[id]++;
                }
                else
                {
                    m_Materials[id] = fmat;
                    m_MaterialsRefCount[id] = 1;
                }
                return false;
            }
        }

        private void RemoveMaterialsInPart(SceneObjectPart part)
        {
            if (part.Shape == null)
                return;

            var te = new Primitive.TextureEntry(part.Shape.TextureEntry, 0, part.Shape.TextureEntry.Length);
            if (te == null)
                return;

            if (te.DefaultTexture != null)
                RemoveMaterialInFace(te.DefaultTexture);

            foreach (Primitive.TextureEntryFace face in te.FaceTextures)
            {
                if(face != null)
                    RemoveMaterialInFace(face);
            }
        }

       private void RemoveMaterialInFace(Primitive.TextureEntryFace face)
        {
            UUID id = face.MaterialID;
            if (id.IsZero())
                return;

            lock (materialslock)
            {
                if(m_Materials.ContainsKey(id))
                {
                    m_MaterialsRefCount[id]--;
                    if (m_MaterialsRefCount[id] == 0)
                        delayedDelete.Enqueue(id);
                }
            }
        }

        public void RenderMaterialsPostCap(IOSHttpRequest request, IOSHttpResponse response, UUID agentID)
        {
            OSDMap req;
            try
            {
                req = (OSDMap)OSDParser.DeserializeLLSDXml(request.InputStream);
            }
            catch
            {
                response.StatusCode = (int)HttpStatusCode.BadRequest;
                return;
            }

            OSDArray respArr = new OSDArray();
            OSD tmpOSD;

            if (req.TryGetValue("Zipped", out tmpOSD))
            {
                OSD osd = null;

                byte[] inBytes = tmpOSD.AsBinary();

                try
                {
                    osd = ZDecompressBytesToOsd(inBytes);

                    if (osd != null && osd is OSDArray)
                    {
                        foreach (OSD elem in (OSDArray)osd)
                        {
                            try
                            {
                                UUID id = new UUID(elem.AsBinary(), 0);

                                lock (materialslock)
                                {
                                    if (m_Materials.ContainsKey(id))
                                    {
                                        OSDMap matMap = new OSDMap();
                                        matMap["ID"] = OSD.FromBinary(id.GetBytes());
                                        matMap["Material"] = m_Materials[id].toOSD();
                                        respArr.Add(matMap);
                                    }
                                    else
                                    {
                                        m_log.Warn("[Materials]: request for unknown material ID: " + id.ToString());

                                        // Theoretically we could try to load the material from the assets service,
                                        // but that shouldn't be necessary because the viewer should only request
                                        // materials that exist in a prim on the region, and all of these materials
                                        // are already stored in m_regionMaterials.
                                    }
                                }
                            }
                            catch (Exception e)
                            {
                                m_log.Error("Error getting materials in response to viewer request", e);
                                continue;
                            }
                        }
                    }
                }
                catch (Exception e)
                {
                    m_log.Warn("[Materials]: exception decoding zipped CAP payload ", e);
                    response.StatusCode = (int)HttpStatusCode.BadRequest;
                    return;
                }
            }

            OSDMap resp = new OSDMap();
            resp["Zipped"] = ZCompressOSD(respArr, false);
            response.RawBuffer = Encoding.UTF8.GetBytes(OSDParser.SerializeLLSDXmlString(resp));

            //m_log.Debug("[Materials]: cap request: " + request);
            //m_log.Debug("[Materials]: cap request (zipped portion): " + ZippedOsdBytesToString(req["Zipped"].AsBinary()));
            //m_log.Debug("[Materials]: cap response: " + response);
        }

        public void RenderMaterialsPutCap(IOSHttpRequest request, IOSHttpResponse response, UUID agentID)
        {
            OSDMap req;
            try
            {
                 req = (OSDMap)OSDParser.DeserializeLLSDXml(request.InputStream);
            }
            catch
            {
                response.StatusCode = (int)HttpStatusCode.BadRequest;
                return;
            }

            OSD tmpOSD;
            if (req.TryGetValue("Zipped", out tmpOSD))
            {
                try
                {
                    byte[] inBytes = tmpOSD.AsBinary();
                    OSD osd = ZDecompressBytesToOsd(inBytes);

                    if (osd != null && osd is OSDMap)
                    {
                        OSDMap materialsFromViewer = osd as OSDMap;

                        if (materialsFromViewer.TryGetValue("FullMaterialsPerFace", out tmpOSD) && (tmpOSD is OSDArray))
                        {
                            Dictionary<uint, SceneObjectPart> parts = new Dictionary<uint, SceneObjectPart>();
                            HashSet<uint> errorReported = new HashSet<uint>();
                            OSDArray matsArr = tmpOSD as OSDArray;
                            try
                            {
                                foreach (OSDMap matsMap in matsArr)
                                {
                                    uint primLocalID = 0;
                                    try
                                    {
                                        primLocalID = matsMap["ID"].AsUInteger();
                                    }
                                    catch (Exception e)
                                    {
                                        m_log.Warn("[Materials]: cannot decode \"ID\" from matsMap: " + e.Message);
                                        continue;
                                    }

                                    SceneObjectPart sop = m_scene.GetSceneObjectPart(primLocalID);
                                    if (sop == null)
                                    {
                                        m_log.WarnFormat("[Materials]: SOP not found for localId: {0}", primLocalID.ToString());
                                        continue;
                                    }

                                    if (!m_scene.Permissions.CanEditObject(sop.UUID, agentID))
                                    {
                                        if(!errorReported.Contains(primLocalID))
                                        {
                                            m_log.WarnFormat("[Materials]: User {0} can't edit object {1} {2}", agentID, sop.Name, sop.UUID);
                                            errorReported.Add(primLocalID);
                                        }
                                        continue;
                                    }

                                    OSDMap mat = null;
                                    try
                                    {
                                        mat = matsMap["Material"] as OSDMap;
                                    }
                                    catch (Exception e)
                                    {
                                        m_log.Warn("[Materials]: cannot decode \"Material\" from matsMap: " + e.Message);
                                        continue;
                                    }

                                    Primitive.TextureEntry te = new Primitive.TextureEntry(sop.Shape.TextureEntry, 0, sop.Shape.TextureEntry.Length);
                                    if (te == null)
                                    {
                                        m_log.WarnFormat("[Materials]: Error in TextureEntry for SOP {0} {1}", sop.Name, sop.UUID);
                                        continue;
                                    }

                                    int face = -1;
                                    UUID oldid = UUID.Zero;
                                    Primitive.TextureEntryFace faceEntry = null;
                                    if (matsMap.TryGetValue("Face", out tmpOSD))
                                    {
                                        face = tmpOSD.AsInteger();
                                        faceEntry = te.CreateFace((uint)face);
                                    }
                                    else
                                        faceEntry = te.DefaultTexture;

                                    if (faceEntry == null)
                                        continue;

                                    UUID id;
                                    FaceMaterial newFaceMat = null;
                                    if (mat == null)
                                    {
                                        // This happens then the user removes a material from a prim
                                        id = UUID.Zero;
                                    }
                                    else
                                    {
                                        newFaceMat = new FaceMaterial(mat);
                                        if(newFaceMat.DiffuseAlphaMode == 1 
                                                && newFaceMat.NormalMapID.IsZero()
                                                && newFaceMat.SpecularMapID.IsZero())
                                            id = UUID.Zero;
                                        else
                                        {
                                            newFaceMat.genID();
                                            id = newFaceMat.ID;
                                        }
                                    }

                                    oldid = faceEntry.MaterialID;

                                    if(oldid == id)
                                        continue;

                                    if (faceEntry != null)
                                    {
                                        faceEntry.MaterialID = id;
                                        //m_log.DebugFormat("[Materials]: in \"{0}\" {1}, setting material ID for face {2} to {3}", sop.Name, sop.UUID, face, id);
                                        // We can't use sop.UpdateTextureEntry(te) because it filters, so do it manually
                                        sop.Shape.TextureEntry = te.GetBytes(9);
                                    }

                                    if(!oldid.IsZero())
                                        RemoveMaterial(oldid);

                                    lock(materialslock)
                                    {
                                        if(!id.IsZero())
                                        {
                                            if (m_Materials.ContainsKey(id))
                                                m_MaterialsRefCount[id]++;
                                            else
                                            {
                                                m_Materials[id] = newFaceMat;
                                                m_MaterialsRefCount[id] = 1;
                                                m_changed[newFaceMat] = Util.GetTimeStamp();
                                            }
                                        }
                                    }

                                    if(!parts.ContainsKey(primLocalID))
                                        parts[primLocalID] = sop;
                                }

                                foreach(SceneObjectPart sop in parts.Values)
                                {
                                    if (sop.ParentGroup != null && !sop.ParentGroup.IsDeleted)
                                    {
                                        sop.TriggerScriptChangedEvent(Changed.TEXTURE);
                                        sop.ScheduleFullUpdate();
                                        sop.ParentGroup.HasGroupChanged = true;
                                    }
                                }
                            }
                            catch (Exception e)
                            {
                                m_log.Warn("[Materials]: exception processing received material ", e);
                                response.StatusCode = (int)HttpStatusCode.BadRequest;
                                return;
                            }
                        }
                    }
                }
                catch (Exception e)
                {
                    m_log.Warn("[Materials]: exception decoding zipped CAP payload ", e);
                    response.StatusCode = (int)HttpStatusCode.BadRequest;
                    return;
                }
            }

            //OSDMap resp = new OSDMap();
            //OSDArray respArr = new OSDArray();
            //resp["Zipped"] = ZCompressOSD(respArr, false);
            //string tmp = OSDParser.SerializeLLSDXmlString(resp);
            //response.RawBuffer = OSDParser.SerializeLLSDXmlToBytes(resp);

            response.RawBuffer = GetPutEmptyResponseBytes;
        }

        private AssetBase MakeAsset(FaceMaterial fm, bool local)
        {
            // this are not true assets, should had never been...
            AssetBase asset = null;
            byte[] data = fm.toLLSDxml();

            asset = new AssetBase(fm.ID, "llmaterial", (sbyte)OpenSimAssetType.Material, "00000000-0000-0000-0000-000000000000");
            asset.Data = data;
            asset.Local = local;
            return asset;
        }

        private byte[] CacheGet = null;
        private object CacheGetLock = new object();
        private double CacheGetTime = 0;

        public void RenderMaterialsGetCap(IOSHttpRequest request, IOSHttpResponse response)
        {
            lock(CacheGetLock)
            {
                OSDArray allOsd = new OSDArray();
                double now = Util.GetTimeStamp();
                if(CacheGet == null || now - CacheGetTime > 30)
                {
                    CacheGetTime = now;

                    lock (m_Materials)
                    {
                        foreach (KeyValuePair<UUID, FaceMaterial> kvp in m_Materials)
                        {
                            OSDMap matMap = new OSDMap
                            {
                                ["ID"] = OSD.FromBinary(kvp.Key.GetBytes()),
                                ["Material"] = kvp.Value.toOSD()
                            };
                            allOsd.Add(matMap);
                        }
                    }

                    OSDMap resp = new OSDMap
                    {
                        ["Zipped"] = ZCompressOSD(allOsd, false)
                    };

                    CacheGet = OSDParser.SerializeLLSDXmlToBytes(resp);
                }
                response.RawBuffer = CacheGet ?? GetPutEmptyResponseBytes;
            }
        }

        private static string ZippedOsdBytesToString(byte[] bytes)
        {
            try
            {
                return OSDParser.SerializeJsonString(ZDecompressBytesToOsd(bytes));
            }
            catch (Exception e)
            {
                return "ZippedOsdBytesToString caught an exception: " + e.ToString();
            }
        }

        public static OSD ZCompressOSD(OSD inOsd, bool useHeader)
        {
            OSD osd = null;

            byte[] data = OSDParser.SerializeLLSDBinary(inOsd, useHeader);

            using (MemoryStream msSinkCompressed = new MemoryStream())
            {
                using (Ionic.Zlib.ZlibStream zOut = new Ionic.Zlib.ZlibStream(msSinkCompressed,
                    Ionic.Zlib.CompressionMode.Compress, CompressionLevel.BestCompression, true))
                {
                    zOut.Write(data, 0, data.Length);
                }

                msSinkCompressed.Seek(0L, SeekOrigin.Begin);
                osd = OSD.FromBinary(msSinkCompressed.ToArray());
            }

            return osd;
        }

        public static OSD ZDecompressBytesToOsd(byte[] input)
        {
            OSD osd = null;

            using (MemoryStream msSinkUnCompressed = new MemoryStream())
            {
                using (Ionic.Zlib.ZlibStream zOut = new Ionic.Zlib.ZlibStream(msSinkUnCompressed, CompressionMode.Decompress, true))
                {
                    zOut.Write(input, 0, input.Length);
                }

                msSinkUnCompressed.Seek(0L, SeekOrigin.Begin);
                osd = OSDParser.DeserializeLLSDBinary(msSinkUnCompressed.ToArray());
            }

            return osd;
        }

        public FaceMaterial GetMaterial(UUID ID)
        {
            FaceMaterial fm = null;
            if(m_Materials.TryGetValue(ID, out fm))
                return fm;
            return null;
        }

        public FaceMaterial GetMaterialCopy(UUID ID)
        {
            FaceMaterial fm = null;
            if(m_Materials.TryGetValue(ID, out fm))
                return new FaceMaterial(fm);
            return null;
        }

        public UUID AddNewMaterial(FaceMaterial fm)
        {
            if(fm.DiffuseAlphaMode == 1 && fm.NormalMapID.IsZero() && fm.SpecularMapID.IsZero())
            {
                fm.ID = UUID.Zero;
                return UUID.Zero;
            }

            fm.genID();
            UUID id = fm.ID;
            lock(materialslock)
            {
                if(m_Materials.ContainsKey(id))
                    m_MaterialsRefCount[id]++;
                else
                {
                    m_Materials[id] = fm;
                    m_MaterialsRefCount[id] = 1;
                    m_changed[fm] = Util.GetTimeStamp();
                }
            }
            return id;
        }

        public void RemoveMaterial(UUID id)
        {
            if(id.IsZero())
                return;

            lock(materialslock)
            {
                if(m_Materials.ContainsKey(id))
                {
                    m_MaterialsRefCount[id]--;
                    if(m_MaterialsRefCount[id] == 0)
                    {
                        FaceMaterial fm = m_Materials[id];
                        m_changed.Remove(fm);
                        delayedDelete.Enqueue(id);
                    }
                }
            }
        }
    }
}
