// Copyright 2022 Robotec.ai.
//
// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//
//     http://www.apache.org/licenses/LICENSE2.0
//
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.

using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Serialization;

namespace RGLUnityPlugin
{
    /// <summary>
    /// Encapsulates all non-ROS components of a RGL-based Lidar.
    /// </summary>
    public class LidarSensor : MonoBehaviour
    {
        [Tooltip("Sensor processing and callbacks are automatically called in this hz")]
        [FormerlySerializedAs("OutputHz")]
        [Range(0, 50)]
        public int AutomaticCaptureHz = 10;

        /// <summary>
        /// Delegate used in callbacks.
        /// </summary>
        public delegate void OnNewDataDelegate();

        /// <summary>
        /// Called when new data is generated via automatic capture.
        /// </summary>
        public OnNewDataDelegate onNewData;

        /// <summary>
        /// Called when lidar model configuration has changed.
        /// </summary>
        public OnNewDataDelegate onLidarModelChange;

        [Tooltip("Allows to select one of built-in LiDAR models")]
        public LidarModel modelPreset = LidarModel.RangeMeter;

        [Tooltip("Allows to select between LiDAR return modes")]
        public RGLReturnMode returnMode = RGLReturnMode.SingleReturnFirst;

        [Tooltip("Allows to quickly enable/disable distance gaussian noise")]
        public bool applyDistanceGaussianNoise = true;

        [Tooltip("Allows to quickly enable/disable angular gaussian noise")]
        public bool applyAngularGaussianNoise = true;

        [Tooltip("Allows to quickly enable/disable velocity distortion")]
        public bool applyVelocityDistortion = false;

        [Tooltip("If disable, both beam divergence values are set to 0. Otherwise, they are set based on LiDAR configuration.")]
        public bool simulateBeamDivergence = false;

        [Tooltip(
            "If enabled, validates whether the configuration is the same as the manual for the selected model (only on startup)")]
        public bool doValidateConfigurationOnStartup = true;

        /// <summary>
        /// Encapsulates description of a point cloud generated by a LiDAR and allows for fine-tuning.
        /// </summary>
        [SerializeReference]
        public BaseLidarConfiguration configuration = LidarConfigurationLibrary.ByModel[LidarModel.RangeMeter]();

        /// <summary>
        /// Encapsulates description of a output restriction to allow fault injection.
        /// </summary>
        public LidarOutputRestriction outputRestriction = new LidarOutputRestriction();

        private RGLNodeSequence rglGraphLidar;
        private RGLNodeSequence rglSubgraphCompact;
        private RGLNodeSequence rglSubgraphToLidarFrame;
        private SceneManager sceneManager;

        private const string lidarRaysNodeId = "LIDAR_RAYS";
        private const string lidarRangeNodeId = "LIDAR_RANGE";
        private const string lidarRingsNodeId = "LIDAR_RINGS";
        private const string lidarTimeOffsetsNodeId = "LIDAR_OFFSETS";
        private const string lidarPoseNodeId = "LIDAR_POSE";
        private const string noiseLidarRayNodeId = "NOISE_LIDAR_RAY";
        private const string lidarRaytraceNodeId = "LIDAR_RAYTRACE";
        private const string noiseHitpointNodeId = "NOISE_HITPOINT";
        private const string noiseDistanceNodeId = "NOISE_DISTANCE";
        private const string pointsCompactNodeId = "POINTS_COMPACT";
        private const string toLidarFrameNodeId = "TO_LIDAR_FRAME";
        private const string snowNodeId = "SNOW";
        private const string rainNodeId = "RAIN";
        private const string fogNodeId = "FOG";

        private LidarModel? validatedPreset;
        private float timer;

        private Matrix4x4 lastTransform;
        private Matrix4x4 currentTransform;

        private int fixedUpdatesInCurrentFrame = 0;
        private int lastUpdateFrame = -1;

        private static List<LidarSensor> activeSensors = new List<LidarSensor>();

        public void Awake()
        {
            rglGraphLidar = new RGLNodeSequence()
                .AddNodeRaysFromMat3x4f(lidarRaysNodeId, new Matrix4x4[1] { Matrix4x4.identity })
                .AddNodeRaysSetRange(lidarRangeNodeId, new Vector2[1] { new Vector2(0.0f, Mathf.Infinity) })
                .AddNodeRaysSetRingIds(lidarRingsNodeId, new int[1] { 0 })
                .AddNodeRaysSetTimeOffsets(lidarTimeOffsetsNodeId, new float[1] { 0 })
                .AddNodeRaysTransform(lidarPoseNodeId, Matrix4x4.identity)
                .AddNodeGaussianNoiseAngularRay(noiseLidarRayNodeId, 0, 0)
                .AddNodeRaytrace(lidarRaytraceNodeId)
                .AddNodeGaussianNoiseAngularHitpoint(noiseHitpointNodeId, 0, 0)
                .AddNodeGaussianNoiseDistance(noiseDistanceNodeId, 0, 0, 0);

            rglSubgraphCompact = new RGLNodeSequence()
                .AddNodePointsCompactByField(pointsCompactNodeId, RGLField.IS_HIT_I32);

            rglSubgraphToLidarFrame = new RGLNodeSequence()
                .AddNodePointsTransform(toLidarFrameNodeId, Matrix4x4.identity);

            RGLNodeSequence.Connect(rglGraphLidar, rglSubgraphCompact);
            RGLNodeSequence.Connect(rglSubgraphCompact, rglSubgraphToLidarFrame);
        }

        public void Start()
        {
            sceneManager = SceneManager.Instance;
            if (sceneManager == null)
            {
                // TODO(prybicki): this is too tedious, implement automatic instantiation of RGL Scene Manager
                Debug.LogError($"RGL Scene Manager is not present on the scene. Destroying {name}.");
                Destroy(this);
                return;
            }

            // Apply initial transform of the sensor.
            lastTransform = gameObject.transform.localToWorldMatrix;

            if (LidarSnowManager.Instance != null)
            {
                // Add deactivated node with some initial values. To be activated and updated when validating.
                rglGraphLidar.AddNodePointsSimulateSnow(snowNodeId, 0.0f, 1.0f, 0.0001f, 0.0001f, 0.2f, 0.01f, 1, 0.01f, 0.0f);
                rglGraphLidar.SetActive(snowNodeId, false);
                LidarSnowManager.Instance.OnNewConfig += OnValidate;
            }

            if (LidarRainManager.Instance != null)
            {
                // Add deactivated node with some initial values. To be activated and updated when validating.
                rglGraphLidar.AddNodePointsSimulateRain(rainNodeId, 0.0f, 1.0f, 0.0001f, 1, 0.01f, false, 1);
                rglGraphLidar.SetActive(rainNodeId, false);
                LidarRainManager.Instance.OnNewConfig += OnValidate;
            }

            if(LidarFogManager.Instance != null)
            {
                // Add deactivated node with some initial values. To be activated and updated when validating.
                rglGraphLidar.AddNodePointsSimulateFog(fogNodeId, 0.03f, 0.1f, 1.0f);
                rglGraphLidar.SetActive(fogNodeId, false);
                LidarFogManager.Instance.OnNewConfig += OnValidate;
            }

            OnValidate();

            if (doValidateConfigurationOnStartup)
            {
                if (!configuration.ValidateWithModel(modelPreset))
                {
                    Debug.LogWarning(
                        $"{name}: the configuration of the selected model preset ({modelPreset.ToString()}) is modified. " +
                        "Ignore this warning if you have consciously changed them.");
                }
            }
        }

        public void OnValidate()
        {
            // This tricky code ensures that configuring from a preset dropdown
            // in Unity Inspector works well in prefab edit mode and regular edit mode.
            bool presetChanged = validatedPreset != modelPreset;
            bool firstValidation = validatedPreset == null;
            if (!firstValidation && presetChanged)
            {
                configuration = LidarConfigurationLibrary.ByModel[modelPreset]();
            }

            outputRestriction.Update(configuration);

            ApplyConfiguration(configuration);
            validatedPreset = modelPreset;
            onLidarModelChange?.Invoke();
        }

        private void ApplyConfiguration(BaseLidarConfiguration newConfig)
        {
            if (rglGraphLidar == null)
            {
                return;
            }

            rglGraphLidar.UpdateNodeRaysFromMat3x4f(lidarRaysNodeId, newConfig.GetRayPoses())
                .UpdateNodeRaysSetRange(lidarRangeNodeId, newConfig.GetRayRanges())
                .UpdateNodeRaysSetRingIds(lidarRingsNodeId, newConfig.GetRayRingIds())
                .UpdateNodeRaysTimeOffsets(lidarTimeOffsetsNodeId, newConfig.GetRayTimeOffsets())
                .UpdateNodeGaussianNoiseAngularRay(noiseLidarRayNodeId,
                    newConfig.noiseParams.angularNoiseMean * Mathf.Deg2Rad,
                    newConfig.noiseParams.angularNoiseStDev * Mathf.Deg2Rad)
                .UpdateNodeGaussianNoiseAngularHitpoint(noiseHitpointNodeId,
                    newConfig.noiseParams.angularNoiseMean * Mathf.Deg2Rad,
                    newConfig.noiseParams.angularNoiseStDev * Mathf.Deg2Rad)
                .UpdateNodeGaussianNoiseDistance(noiseDistanceNodeId, newConfig.noiseParams.distanceNoiseMean,
                    newConfig.noiseParams.distanceNoiseStDevBase, newConfig.noiseParams.distanceNoiseStDevRisePerMeter);

            if (simulateBeamDivergence)
            {
                rglGraphLidar.ConfigureNodeRaytraceBeamDivergence(lidarRaytraceNodeId,
                    Mathf.Deg2Rad * newConfig.horizontalBeamDivergence,
                    Mathf.Deg2Rad * newConfig.verticalBeamDivergence);
            }
            else
            {
                rglGraphLidar.ConfigureNodeRaytraceBeamDivergence(lidarRaytraceNodeId, 0.0f, 0.0f);
            }
            
            rglGraphLidar.ConfigureNodeRaytraceReturnMode(lidarRaytraceNodeId, returnMode);

            rglGraphLidar.SetActive(noiseDistanceNodeId, applyDistanceGaussianNoise);
            var angularNoiseType = newConfig.noiseParams.angularNoiseType;
            rglGraphLidar.SetActive(noiseLidarRayNodeId,
                applyAngularGaussianNoise && angularNoiseType == AngularNoiseType.RayBased);
            rglGraphLidar.SetActive(noiseHitpointNodeId,
                applyAngularGaussianNoise && angularNoiseType == AngularNoiseType.HitpointBased);

            // Snow model updates
            if (rglGraphLidar.HasNode(snowNodeId))
            {
                // Update snow parameters only if feature is enabled (expensive operation)
                if (LidarSnowManager.Instance.IsSnowEnabled)
                {
                    rglGraphLidar.UpdateNodePointsSimulateSnow(snowNodeId,
                        newConfig.GetRayRanges()[0].x,
                        newConfig.GetRayRanges()[0].y,
                        LidarSnowManager.Instance.RainRate,
                        LidarSnowManager.Instance.MeanSnowflakeDiameter,
                        LidarSnowManager.Instance.TerminalVelocity,
                        LidarSnowManager.Instance.Density,
                        newConfig.laserArray.GetLaserRingIds().Length,
                        newConfig.horizontalBeamDivergence * Mathf.Deg2Rad,
                        LidarSnowManager.Instance.OccupancyThreshold);
                    rglGraphLidar.UpdateNodePointsSnowDefaults(snowNodeId,
                        LidarSnowManager.Instance.SnowflakesId,
                        LidarSnowManager.Instance.FullBeamIntensity,
                        0.0f); // Default, because it is not supported in AWSIM.
                }

                rglGraphLidar.SetActive(snowNodeId, LidarSnowManager.Instance.IsSnowEnabled);
            }

            // Rain model updates
            if (rglGraphLidar.HasNode(rainNodeId))
            {
                // Update rain parameters only if feature is enabled (expensive operation)
                if (LidarRainManager.Instance.IsRainEnabled)
                {
                    rglGraphLidar.UpdateNodePointsSimulateRain(rainNodeId,
                    newConfig.GetRayRanges()[0].x,
                    newConfig.GetRayRanges()[0].y,
                    LidarRainManager.Instance.RainRate,
                    newConfig.laserArray.GetLaserRingIds().Length,
                    newConfig.horizontalBeamDivergence * Mathf.Deg2Rad,
                    LidarRainManager.Instance.DoSimulateEnergyLoss,
                    LidarRainManager.Instance.RainNumericalThreshold);
                    }

                rglGraphLidar.SetActive(rainNodeId, LidarRainManager.Instance.IsRainEnabled);
            }

            rglGraphLidar.ConfigureNodeRaytraceDistortion(lidarRaytraceNodeId, applyVelocityDistortion);

            if (outputRestriction.enablePeriodicRestriction && outputRestriction.applyRestriction)
            {
                outputRestriction.coroutine = outputRestriction.BlinkingRoutine(rglGraphLidar, lidarRaytraceNodeId);
                StartCoroutine(outputRestriction.coroutine);
            }
            else
            {
                if (outputRestriction.coroutine != null)
                {
                    StopCoroutine(outputRestriction.coroutine);
                }

                outputRestriction.ApplyStaticRestriction(rglGraphLidar, lidarRaytraceNodeId);
            }
        }

        public void OnEnable()
        {
            activeSensors.Add(this);
            // Sync timer with the active sensors to achieve the best performance. It minimizes number of scene updates.
            if (activeSensors.Count > 0)
            {
                timer = activeSensors[0].timer;
            }
        }

        public void OnDisable()
        {
            activeSensors.Remove(this);
        }

        public void FixedUpdate()
        {
            // One LidarSensor triggers FixedUpdateLogic for all of active LidarSensors on the scene
            // This is an optimization to take full advantage of asynchronous RGL graph execution
            // First, all RGL graphs are run which enqueue the most priority graph branches (e.g., visualization for Unity) properly
            // Then, `onNewData` delegate is called to notify other components about new data available
            // This way, the most important (Unity blocking) computations for all of the sensors are performed first
            // Non-blocking operations (e.g., ROS2 publishing) are performed next
            if (activeSensors[0] != this)
            {
                return;
            }

            var triggeredSensorsIndexes = new List<int>();
            for (var i = 0; i < activeSensors.Count; i++)
            {
                if (activeSensors[i].FixedUpdateLogic())
                {
                    triggeredSensorsIndexes.Add(i);
                }
            }

            foreach (var idx in triggeredSensorsIndexes)
            {
                activeSensors[idx].NotifyNewData();
            }
        }

        /// <summary>
        /// Performs fixed update logic.
        /// Returns true if sensor was triggered (raytracing was performed)
        /// </summary>
        private bool FixedUpdateLogic()
        {
            if (lastUpdateFrame != Time.frameCount)
            {
                fixedUpdatesInCurrentFrame = 0;
                lastUpdateFrame = Time.frameCount;
            }

            fixedUpdatesInCurrentFrame += 1;

            if (AutomaticCaptureHz == 0.0f)
            {
                return false;
            }

            timer += Time.deltaTime;

            // Update last known transform of lidar.
            UpdateTransforms();

            var interval = 1.0f / AutomaticCaptureHz;
            if (timer + 0.00001f < interval)
                return false;

            timer = 0;

            Capture();
            return true;
        }

        private void NotifyNewData()
        {
            onNewData?.Invoke();
        }

        /// <summary>
        /// Connect to point cloud in world coordinate frame.
        /// </summary>
        public void ConnectToWorldFrame(RGLNodeSequence nodeSequence, bool compacted = true)
        {
            if (compacted)
            {
                RGLNodeSequence.Connect(rglSubgraphCompact, nodeSequence);
            }
            else
            {
                RGLNodeSequence.Connect(rglGraphLidar, nodeSequence);
            }
        }

        /// <summary>
        /// Connect to compacted point cloud in lidar coordinate frame.
        /// </summary>
        public void ConnectToLidarFrame(RGLNodeSequence nodeSequence)
        {
            RGLNodeSequence.Connect(rglSubgraphToLidarFrame, nodeSequence);
        }

        public void Capture()
        {
            sceneManager.DoUpdate(fixedUpdatesInCurrentFrame);

            // Set lidar pose
            Matrix4x4 lidarPose = gameObject.transform.localToWorldMatrix * configuration.GetLidarOriginTransfrom();
            rglGraphLidar.UpdateNodeRaysTransform(lidarPoseNodeId, lidarPose);
            rglSubgraphToLidarFrame.UpdateNodePointsTransform(toLidarFrameNodeId, lidarPose.inverse);

            // Set lidar velocity
            if (applyVelocityDistortion)
            {
                SetVelocityToRaytrace();
            }

            rglGraphLidar.Run();
        }

        private void UpdateTransforms()
        {
            lastTransform = currentTransform;
            currentTransform = gameObject.transform.localToWorldMatrix;
        }

        private void SetVelocityToRaytrace()
        {
            // Calculate delta transform of lidar.
            // Velocities must be in sensor-local coordinate frame.
            // Sensor linear velocity in m/s.
            Vector3 globalLinearVelocity =
                (currentTransform.GetColumn(3) - lastTransform.GetColumn(3)) / Time.deltaTime;
            Vector3 localLinearVelocity = gameObject.transform.InverseTransformDirection(globalLinearVelocity);

            Vector3 deltaRotation =
                Quaternion.LookRotation(currentTransform.GetColumn(2), currentTransform.GetColumn(1)).eulerAngles
                - Quaternion.LookRotation(lastTransform.GetColumn(2), lastTransform.GetColumn(1)).eulerAngles;
            // Fix delta rotation when switching between 0 and 360.
            deltaRotation = new Vector3(Mathf.DeltaAngle(0, deltaRotation.x), Mathf.DeltaAngle(0, deltaRotation.y),
                Mathf.DeltaAngle(0, deltaRotation.z));
            // Sensor angular velocity in rad/s.
            Vector3 localAngularVelocity = (deltaRotation * Mathf.Deg2Rad) / Time.deltaTime;

            rglGraphLidar.ConfigureNodeRaytraceVelocity(lidarRaytraceNodeId, localLinearVelocity, localAngularVelocity);
        }
    }
}