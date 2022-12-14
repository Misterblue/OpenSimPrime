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
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.ScriptEngine.Shared;
using OpenSim.Region.ScriptEngine.Interfaces;
using log4net;

namespace OpenSim.Region.ScriptEngine.XEngine
{
    /// <summary>
    /// Prepares events so they can be directly executed upon a script by EventQueueManager, then queues it.
    /// </summary>
    public class EventManager
    {
        private static readonly ILog m_log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        private XEngine myScriptEngine;

        public EventManager(XEngine _ScriptEngine)
        {
            myScriptEngine = _ScriptEngine;

//            m_log.Info("[XEngine] Hooking up to server events");
            myScriptEngine.World.EventManager.OnAttach += attach;
            myScriptEngine.World.EventManager.OnObjectGrab += touch_start;
            myScriptEngine.World.EventManager.OnObjectGrabbing += touch;
            myScriptEngine.World.EventManager.OnObjectDeGrab += touch_end;
            myScriptEngine.World.EventManager.OnScriptChangedEvent += changed;
            myScriptEngine.World.EventManager.OnScriptAtTargetEvent += at_target;
            myScriptEngine.World.EventManager.OnScriptNotAtTargetEvent += not_at_target;
            myScriptEngine.World.EventManager.OnScriptAtRotTargetEvent += at_rot_target;
            myScriptEngine.World.EventManager.OnScriptNotAtRotTargetEvent += not_at_rot_target;
            myScriptEngine.World.EventManager.OnScriptMovingStartEvent += moving_start;
            myScriptEngine.World.EventManager.OnScriptMovingEndEvent += moving_end;
            myScriptEngine.World.EventManager.OnScriptControlEvent += control;
            myScriptEngine.World.EventManager.OnScriptColliderStart += collision_start;
            myScriptEngine.World.EventManager.OnScriptColliding += collision;
            myScriptEngine.World.EventManager.OnScriptCollidingEnd += collision_end;
            myScriptEngine.World.EventManager.OnScriptLandColliderStart += land_collision_start;
            myScriptEngine.World.EventManager.OnScriptLandColliding += land_collision;
            myScriptEngine.World.EventManager.OnScriptLandColliderEnd += land_collision_end;
            myScriptEngine.World.EventManager.OnScriptListenEvent += script_listen;
            IMoneyModule money = myScriptEngine.World.RequestModuleInterface<IMoneyModule>();
            if (money != null)
            {
                money.OnObjectPaid+=HandleObjectPaid;
            }
        }

        /// <summary>
        /// When an object gets paid by an avatar and generates the paid event,
        /// this will pipe it to the script engine
        /// </summary>
        /// <param name="objectID">Object ID that got paid</param>
        /// <param name="agentID">Agent Id that did the paying</param>
        /// <param name="amount">Amount paid</param>
        private void HandleObjectPaid(UUID objectID, UUID agentID,
                int amount)
        {
            // Since this is an event from a shared module, all scenes will
            // get it. But only one has the object in question. The others
            // just ignore it.
            //
            SceneObjectPart part =
                    myScriptEngine.World.GetSceneObjectPart(objectID);

            if (part == null)
                return;

            if ((part.ScriptEvents & scriptEvents.money) == 0)
                part = part.ParentGroup.RootPart;

            m_log.Debug("Paid: " + objectID + " from " + agentID + ", amount " + amount);

//            part = part.ParentGroup.RootPart;
            money(part.LocalId, agentID, amount);
        }

        /// <summary>
        /// Handles piping the proper stuff to The script engine for touching
        /// Including DetectedParams
        /// </summary>
        /// <param name="localID"></param>
        /// <param name="originalID"></param>
        /// <param name="offsetPos"></param>
        /// <param name="remoteClient"></param>
        /// <param name="surfaceArgs"></param>
        public void touch_start(uint localID, uint originalID, Vector3 offsetPos,
                IClientAPI remoteClient, SurfaceTouchEventArgs surfaceArgs)
        {
            // Add to queue for all scripts in ObjectID object
            DetectParams[] det = new DetectParams[1];
            det[0] = new DetectParams();
            det[0].Key = remoteClient.AgentId;
            det[0].Populate(myScriptEngine.World);

            if (originalID == 0)
            {
                SceneObjectPart part = myScriptEngine.World.GetSceneObjectPart(localID);
                if (part == null)
                    return;

                det[0].LinkNum = part.LinkNum;
            }
            else
            {
                SceneObjectPart originalPart = myScriptEngine.World.GetSceneObjectPart(originalID);
                det[0].LinkNum = originalPart.LinkNum;
            }

            if (surfaceArgs != null)
            {
                det[0].SurfaceTouchArgs = surfaceArgs;
            }

            myScriptEngine.PostObjectEvent(localID, new EventParams(
                    "touch_start", new Object[] { new LSL_Types.LSLInteger(1) },
                    det));
        }

        public void touch(uint localID, uint originalID, Vector3 offsetPos,
                IClientAPI remoteClient, SurfaceTouchEventArgs surfaceArgs)
        {
            // Add to queue for all scripts in ObjectID object
            DetectParams[] det = new DetectParams[1];
            det[0] = new DetectParams();
            det[0].Key = remoteClient.AgentId;
            det[0].Populate(myScriptEngine.World);
            det[0].OffsetPos = offsetPos;

            if (originalID == 0)
            {
                SceneObjectPart part = myScriptEngine.World.GetSceneObjectPart(localID);
                if (part == null)
                    return;

                det[0].LinkNum = part.LinkNum;
            }
            else
            {
                SceneObjectPart originalPart = myScriptEngine.World.GetSceneObjectPart(originalID);
                det[0].LinkNum = originalPart.LinkNum;
            }
            if (surfaceArgs != null)
            {
                det[0].SurfaceTouchArgs = surfaceArgs;
            }

            myScriptEngine.PostObjectEvent(localID, new EventParams(
                    "touch", new Object[] { new LSL_Types.LSLInteger(1) },
                    det));
        }

        public void touch_end(uint localID, uint originalID, IClientAPI remoteClient,
                              SurfaceTouchEventArgs surfaceArgs)
        {
            // Add to queue for all scripts in ObjectID object
            DetectParams[] det = new DetectParams[1];
            det[0] = new DetectParams();
            det[0].Key = remoteClient.AgentId;
            det[0].Populate(myScriptEngine.World);

            if (originalID == 0)
            {
                SceneObjectPart part = myScriptEngine.World.GetSceneObjectPart(localID);
                if (part == null)
                    return;

                det[0].LinkNum = part.LinkNum;
            }
            else
            {
                SceneObjectPart originalPart = myScriptEngine.World.GetSceneObjectPart(originalID);
                det[0].LinkNum = originalPart.LinkNum;
            }

            if (surfaceArgs != null)
            {
                det[0].SurfaceTouchArgs = surfaceArgs;
            }

            myScriptEngine.PostObjectEvent(localID, new EventParams(
                    "touch_end", new Object[] { new LSL_Types.LSLInteger(1) },
                    det));
        }

        public void changed(uint localID, uint change, object parameter)
        {
            // Add to queue for all scripts in localID, Object pass change.
            if(parameter == null)
            {
                myScriptEngine.PostObjectEvent(localID, new EventParams(
                    "changed", new object[] { new LSL_Types.LSLInteger(change) },
                    new DetectParams[0]));
                return;
            }
            if (parameter is UUID)
            {
                DetectParams det = new DetectParams();
                det.Key = (UUID)parameter;
                myScriptEngine.PostObjectEvent(localID, new EventParams(
                    "changed", new object[] { new LSL_Types.LSLInteger(change) },
                    new DetectParams[] { det }));
                return;
            }
        }

        public void script_listen(UUID scriptID, int channel, string name, UUID id, string message)
        {
            object[] resobj = new object[]
            {
                new LSL_Types.LSLInteger(channel),
                new LSL_Types.LSLString(name),
                new LSL_Types.LSLString(id.ToString()),
                new LSL_Types.LSLString(message)
            };
            myScriptEngine.PostScriptEvent(scriptID, new EventParams("listen", resobj, new DetectParams[0]));
        }

        // state_entry: not processed here
        // state_exit: not processed here

        public void money(uint localID, UUID agentID, int amount)
        {
            myScriptEngine.PostObjectEvent(localID, new EventParams(
                    "money", new object[] {
                    new LSL_Types.LSLString(agentID.ToString()),
                    new LSL_Types.LSLInteger(amount) },
                    new DetectParams[0]));
        }

        public void collision_start(uint localID, ColliderArgs col)
        {
            // Add to queue for all scripts in ObjectID object
            List<DetectParams> det = new List<DetectParams>();

            foreach (DetectedObject detobj in col.Colliders)
            {
                DetectParams d = new DetectParams();
                d.Key =detobj.keyUUID;
                d.Populate(myScriptEngine.World, detobj);
                det.Add(d);
            }

            if (det.Count > 0)
                myScriptEngine.PostObjectEvent(localID, new EventParams(
                        "collision_start",
                        new Object[] { new LSL_Types.LSLInteger(det.Count) },
                        det.ToArray()));
        }

        public void collision(uint localID, ColliderArgs col)
        {
            // Add to queue for all scripts in ObjectID object
            List<DetectParams> det = new List<DetectParams>();

            foreach (DetectedObject detobj in col.Colliders)
            {
                DetectParams d = new DetectParams();
                d.Populate(myScriptEngine.World, detobj);
                det.Add(d);
            }

            if (det.Count > 0)
                myScriptEngine.PostObjectEvent(localID, new EventParams(
                        "collision", new Object[] { new LSL_Types.LSLInteger(det.Count) },
                        det.ToArray()));
        }

        public void collision_end(uint localID, ColliderArgs col)
        {
            // Add to queue for all scripts in ObjectID object
            List<DetectParams> det = new List<DetectParams>();

            foreach (DetectedObject detobj in col.Colliders)
            {
                DetectParams d = new DetectParams();
                d.Populate(myScriptEngine.World, detobj);
                det.Add(d);
            }

            if (det.Count > 0)
                myScriptEngine.PostObjectEvent(localID, new EventParams(
                        "collision_end",
                        new Object[] { new LSL_Types.LSLInteger(det.Count) },
                        det.ToArray()));
        }

        public void land_collision_start(uint localID, ColliderArgs col)
         {
            List<DetectParams> det = new List<DetectParams>();

            foreach (DetectedObject detobj in col.Colliders)
            {
                DetectParams d = new DetectParams();
                d.Populate(myScriptEngine.World, detobj);
                det.Add(d);
                myScriptEngine.PostObjectEvent(localID, new EventParams(
                        "land_collision_start",
                        new Object[] { new LSL_Types.Vector3(d.Position) },
                        det.ToArray()));
            }

        }

        public void land_collision(uint localID, ColliderArgs col)
        {
            List<DetectParams> det = new List<DetectParams>();

            foreach (DetectedObject detobj in col.Colliders)
            {
                DetectParams d = new DetectParams();
                d.Populate(myScriptEngine.World,detobj);
                det.Add(d);
                myScriptEngine.PostObjectEvent(localID, new EventParams(
                        "land_collision",
                        new Object[] { new LSL_Types.Vector3(d.Position) },
                        det.ToArray()));
            }
        }

        public void land_collision_end(uint localID, ColliderArgs col)
        {
            List<DetectParams> det = new List<DetectParams>();

            foreach (DetectedObject detobj in col.Colliders)
            {
                DetectParams d = new DetectParams();
                d.Populate(myScriptEngine.World,detobj);
                det.Add(d);
                myScriptEngine.PostObjectEvent(localID, new EventParams(
                        "land_collision_end",
                        new Object[] { new LSL_Types.Vector3(d.Position) },
                        det.ToArray()));
            }
         }

        // timer: not handled here
        // listen: not handled here

        public void control(UUID itemID, UUID agentID, uint held, uint change)
        {
            myScriptEngine.PostScriptEvent(itemID, new EventParams(
                    "control",new object[] {
                    new LSL_Types.LSLString(agentID.ToString()),
                    new LSL_Types.LSLInteger(held),
                    new LSL_Types.LSLInteger(change)},
                    new DetectParams[0]));
        }

        public void email(uint localID, UUID itemID, string timeSent,
                string address, string subject, string message, int numLeft)
        {
            myScriptEngine.PostObjectEvent(localID, new EventParams(
                    "email",new object[] {
                    new LSL_Types.LSLString(timeSent),
                    new LSL_Types.LSLString(address),
                    new LSL_Types.LSLString(subject),
                    new LSL_Types.LSLString(message),
                    new LSL_Types.LSLInteger(numLeft)},
                    new DetectParams[0]));
        }

        public void at_target(UUID itemID, uint handle, Vector3 targetpos,
                Vector3 atpos)
        {
            myScriptEngine.PostScriptEvent(itemID, new EventParams(
                    "at_target", new object[] {
                    new LSL_Types.LSLInteger(handle),
                    new LSL_Types.Vector3(targetpos),
                    new LSL_Types.Vector3(atpos) },
                    new DetectParams[0]));
        }

        public void not_at_target(UUID itemID)
        {
            myScriptEngine.PostScriptEvent(itemID, new EventParams(
                    "not_at_target",new object[0],
                    new DetectParams[0]));
        }

        public void at_rot_target(UUID itemID, uint handle, Quaternion targetrot,
                Quaternion atrot)
        {
            myScriptEngine.PostScriptEvent(itemID, new EventParams(
                    "at_rot_target", new object[] {
                    new LSL_Types.LSLInteger(handle),
                    new LSL_Types.Quaternion(targetrot),
                    new LSL_Types.Quaternion(atrot) },
                    new DetectParams[0]));
        }

        public void not_at_rot_target(UUID itemID)
        {
            myScriptEngine.PostScriptEvent(itemID, new EventParams(
                    "not_at_rot_target",new object[0],
                    new DetectParams[0]));
        }

        // run_time_permissions: not handled here

        public void attach(uint localID, UUID itemID, UUID avatar)
        {
            SceneObjectGroup grp = myScriptEngine.World.GetSceneObjectGroup(localID);
            if(grp == null)
                return;

            foreach(SceneObjectPart part in grp.Parts)
            {
                myScriptEngine.PostObjectEvent(part.LocalId, new EventParams(
                    "attach",new object[] {
                    new LSL_Types.LSLString(avatar.ToString()) },
                    new DetectParams[0]));
            }
        }

        // dataserver: not handled here
        // link_message: not handled here

        public void moving_start(uint localID)
        {
            myScriptEngine.PostObjectEvent(localID, new EventParams(
                    "moving_start",new object[0],
                    new DetectParams[0]));
        }

        public void moving_end(uint localID)
        {
            myScriptEngine.PostObjectEvent(localID, new EventParams(
                    "moving_end",new object[0],
                    new DetectParams[0]));
        }

        // object_rez: not handled here
        // remote_data: not handled here
        // http_response: not handled here
    }
}
