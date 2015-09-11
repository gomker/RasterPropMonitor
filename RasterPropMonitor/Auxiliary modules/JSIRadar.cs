﻿/*****************************************************************************
 * RasterPropMonitor
 * =================
 * Plugin for Kerbal Space Program
 *
 *  by Mihara (Eugene Medvedev), MOARdV, and other contributors
 * 
 * RasterPropMonitor is free software: you can redistribute it and/or modify
 * it under the terms of the GNU General Public License as published by
 * the Free Software Foundation, either version 3 of the License, revision
 * date 29 June 2007, or (at your option) any later version.
 * 
 * RasterPropMonitor is distributed in the hope that it will be useful, but
 * WITHOUT ANY WARRANTY; without even the implied warranty of MERCHANTABILITY
 * or FITNESS FOR A PARTICULAR PURPOSE.  See the GNU General Public License
 * for more details.
 * 
 * You should have received a copy of the GNU General Public License
 * along with RasterPropMonitor.  If not, see <http://www.gnu.org/licenses/>.
 ****************************************************************************/
using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace JSI
{
    public class JSIRadar : PartModule
    {
        // What is the maximum range for scanning (km)?
        [KSPField]
        public float maxRange = 200.0f;
        private float maxRangeMeters;
        private float maxRangeMetersSquared;

        // How far off-axis can we scan?
        [KSPField]
        public float scanAngle = 30.0f;
        private float scanDotValue;

        // How much resource do we use?
        [KSPField]
        public float resourceAmount = 0.000f;

        // What resource do we use?
        [KSPField]
        public string resourceName = "ElectricCharge";
        private int resourceId;

        // Will we refine our target to the nearest docking port?
        [KSPField]
        public bool targetDockingPorts = false;

        // Do we restrict the tracking angle to the scan angle (must keep in
        // the radar cone to maintain track)?
        [KSPField]
        public bool restrictTrackingAngle = false;

        // Can we maintain a target while the scanner is off?
        [KSPField]
        public bool trackWhileOff = true;

        [UI_Toggle(disabledText = "Standby", enabledText = "Active")]
        [KSPField(guiActive = true, guiActiveEditor = true, guiName = "Radar: ", isPersistant = true)]
        public bool radarEnabled = false;

        [UI_Toggle(disabledText = "Ignore", enabledText = "Target")]
        [KSPField(guiActive = true, guiActiveEditor = true, guiName = "Debris: ", isPersistant = true)]
        public bool targetDebris = false;

        private Transform scanTransform;
        // Because the docking port's basis isn't the same as the part's, we
        // have to look at the forward vector instead of the up vector.
        private bool useFwd;

        public void Start()
        {
            if (!HighLogic.LoadedSceneIsFlight)
            {
                return;
            }

            scanAngle = Mathf.Clamp(scanAngle, 0.0f, 180.0f);
            // Find the equivalent in dot-product format; subtract an epsilon
            // that's about 1.8 degrees at boresight, but a few millidegrees
            // out farther.
            scanDotValue = Mathf.Cos(scanAngle) - (1.0f / 2048.0f);

            maxRangeMeters = maxRange * 1000.0f;
            maxRangeMetersSquared = maxRangeMeters * maxRangeMeters;

            if (!string.IsNullOrEmpty(resourceName))
            {
                try
                {
                    PartResourceDefinition def = PartResourceLibrary.Instance.resourceDefinitions[resourceName];
                    resourceId = def.id;
                }
                catch (Exception)
                {
                    JUtil.LogErrorMessage(this, "Unable to find a resource ID for \"{0}\".  Disabling resource consumption.", resourceName);
                }
            }
            else
            {
                resourceAmount = 0.0f;
            }

            scanTransform = part.transform;
            useFwd = false;
            try
            {
                List<ModuleDockingNode> dockingNode = part.FindModulesImplementing<ModuleDockingNode>();
                scanTransform = dockingNode[0].nodeTransform;
                useFwd = true;
            }
            catch (Exception e)
            {
                // no-op
                JUtil.LogErrorMessage(this, "Setting dockingNode transform: exception {0}", e);
            }

        }

        public void FixedUpdate()
        {
            if (HighLogic.LoadedSceneIsEditor)
            {
                return;
            }

            ITargetable target = FlightGlobals.fetch.VesselTarget;

            if (radarEnabled)
            {
                bool powered = true;
                // Resources check
                if (resourceAmount > 0.0f)
                {
                    float requested = resourceAmount * TimeWarp.fixedDeltaTime;
                    float supplied = part.RequestResource(resourceId, requested);
                    if (supplied < requested * 0.5f)
                    {
                        powered = false;
                    }
                }

                if (target == null)
                {
                    if (!powered)
                    {
                        JUtil.LogMessage(this, "Want to scan, but there's no power");
                        return;
                    }

                    // Scan
                    ScanForTargets();
                }
                else
                {
                    if (!trackWhileOff && !powered)
                    {
                        JUtil.LogMessage(this, "Radar ran out of power and trackWhileOff is false, so clearing target");
                        FlightGlobals.fetch.SetVesselTarget(null);
                        return;
                    }

                    // Target locked; tracking
                    if (restrictTrackingAngle)
                    {
                        Vector3 vectorToTarget = (target.GetTransform().position - scanTransform.position).normalized;
                        float dotAngle = Vector3.Dot(vectorToTarget, (useFwd) ? scanTransform.forward : scanTransform.up);
                        if (dotAngle < scanDotValue)
                        {
                            JUtil.LogMessage(this, "Target is out of scan angle, losing lock");
                            FlightGlobals.fetch.SetVesselTarget(null);
                            return;
                        }
                    }

                    if (powered && targetDockingPorts && (target is Vessel))
                    {
                        // Attempt to refine our target.
                    }
                }
            }
            else if (target != null)
            {
                if (!trackWhileOff)
                {
                    JUtil.LogMessage(this, "Radar is off and trackWhileOff is false, so clearing target");
                    FlightGlobals.fetch.SetVesselTarget(null);
                    return;
                }

                if (restrictTrackingAngle)
                {
                    Vector3 vectorToTarget = (target.GetTransform().position - scanTransform.position).normalized;
                    float dotAngle = Vector3.Dot(vectorToTarget, (useFwd) ? scanTransform.forward : scanTransform.up);
                    if (dotAngle < scanDotValue)
                    {
                        JUtil.LogMessage(this, "Target is out of scan angle, losing lock");
                        FlightGlobals.fetch.SetVesselTarget(null);
                        return;
                    }
                }
            }
        }

        private void ScanForTargets()
        {
            float selectedDistance = maxRangeMetersSquared;
            Vessel selectedTarget = null;

            for (int i = 0; i < FlightGlobals.fetch.vessels.Count; ++i)
            {
                // Filter some craft types
                VesselType vesselType = FlightGlobals.fetch.vessels[i].vesselType;
                if (FlightGlobals.fetch.vessels[i].id != vessel.id && !(vesselType == VesselType.EVA || vesselType == VesselType.Flag || vesselType == VesselType.Unknown) && (vesselType != VesselType.Debris || targetDebris))
                {
                    Vector3 distance = (FlightGlobals.fetch.vessels[i].GetTransform().position - scanTransform.position);
                    float manhattanDistance = Mathf.Max(Mathf.Abs(distance.x), Mathf.Max(Mathf.Abs(distance.y), Mathf.Abs(distance.z)));
                    if (manhattanDistance < maxRangeMeters)
                    {
                        // Within Manhattan distance.  Check for actual distance.
                        float distSq = distance.sqrMagnitude;
                        if (distSq < selectedDistance)
                        {
                            float dotValue = Vector3.Dot(distance.normalized, (useFwd) ? scanTransform.forward : scanTransform.up);
                            if (dotValue > scanDotValue)
                            {
                                selectedDistance = distSq;
                                selectedTarget = FlightGlobals.fetch.vessels[i];
                            }
                        }
                    }
                }

            }

            if (selectedTarget != null)
            {
                JUtil.LogMessage(this, "Detected target {1} at {0:0.000} km.  Locking on.", Mathf.Sqrt(selectedDistance) * 0.001f, selectedTarget.vesselName);
                FlightGlobals.fetch.SetVesselTarget(selectedTarget);
            }
        }

        public override string GetInfo()
        {
            string infoString = string.Format("Max Range: {0:0.0}km\nUp to {1:0.0}° off-axis", maxRange, scanAngle);
            if (resourceAmount > 0.0f)
            {
                infoString += string.Format("\nConsumes {0:0.000} {1}/sec", resourceAmount, resourceName);
            }
            if (!trackWhileOff)
            {
                infoString += "\nMust keep radar on to track.";
            }
            if (restrictTrackingAngle)
            {
                infoString += "\nMust keep target in scanning cone to track.";
            }
            if (targetDockingPorts)
            {
                infoString += "\nWill select nearest docking port on target vessel.";
            }
            return infoString;
        }

        [KSPAction("Turn Radar Off")]
        public void RadarOffAction(KSPActionParam param)
        {
            JUtil.LogMessage(this, "Radar off");
            radarEnabled = false;
        }

        [KSPAction("Turn Radar On")]
        public void RadarOnAction(KSPActionParam param)
        {
            JUtil.LogMessage(this, "Radar on");
            radarEnabled = true;
        }

        [KSPAction("Toggle Radar")]
        public void ToggleRadarAction(KSPActionParam param)
        {
            JUtil.LogMessage(this, "Toggle radar");
            radarEnabled = !radarEnabled;

        }
    }
}
