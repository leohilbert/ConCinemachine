#if !UNITY_2019_3_OR_NEWER
#define CINEMACHINE_PHYSICS_2D
#endif

    using System;
    using System.Collections.Generic;
    using Cinemachine.Utility;
    using UnityEditor;
    using UnityEngine;

    namespace Cinemachine
    {
#if CINEMACHINE_PHYSICS_2D
    /// <summary>
    /// <para>
    /// An add-on module for Cinemachine Virtual Camera that post-processes the final position 
    /// of the virtual camera.  It will confine the camera's position such that the screen edges stay 
    /// within a shape defined by a 2D polygon.  This will work for orthographic or perspective cameras, 
    /// provided that the camera's forward vector remains parallel to the bounding shape's normal, 
    /// i.e. that the camera is looking straight at the polygon, and not obliquely at it.
    /// </para>
    /// 
    /// <para>
    /// When confining the camera, the camera's view size at the polygon plane is considered, and 
    /// also its aspect ratio. Based on this information and the input polygon, a second (smaller) 
    /// polygon is computed to which the camera's transform is constrained. Computation of this secondary 
    /// polygon is nontrivial and expensive, so it should be done only when absolutely necessary.
    /// </para>
    ///
    /// <para>
    /// The cached secondary polygon needs to be recomputed in the following circumstances:
    /// <list type="bullet">
    /// <item>when the input polygon's points change</item>
    /// <item>when the input polygon is non-uniformly scaled</item>
    /// <item>when the input polygon is rotated</item>
    /// </list>
    /// For efficiency reasons, Cinemachine will not automatically regenerate the inner polygon 
    /// in these cases, and it is the responsibility of the client to call the InvalidateCache() 
    /// method to trigger the recalculation. An inspector button is also provided for this purpose.
    /// </para>
    ///
    /// <para>
    /// If the input polygon scales uniformly or translates, the cache remains valid. If the 
    /// polygon rotates, then the cache degrades in quality (more or less depending on the aspect 
    /// ratio - it's better if the ratio is close to 1:1) but can still be used. 
    /// Regenerating it will eliminate the imperfections.
    /// </para>
    ///
    /// <para>
    /// The cached secondary polygon is not a single polygon, but rather a family of polygons from 
    /// which a member is chosen depending on the current size of the camera view. The number of 
    /// polygons in this family will depend on the complexity of the input polygon, and the maximum 
    /// expected camera view size. The MaxOrthoSize property is provided to give a hint to the 
    /// algorithm to stop generating polygons for camera view sizes larger than the one specified. 
    /// This can represent a substantial cost saving when regenerating the cache, so it is a good 
    /// idea to set it carefully. Leaving it at 0 will cause the maximum number of polygons to be generated.
    /// </para>
    /// </summary>
    [AddComponentMenu("")] // Hide in menu
    [SaveDuringPlay]
    [ExecuteAlways]
    [DisallowMultipleComponent]
    [DefaultExecutionOrder(1)] // run after FramingTransposer so Z Pos is correct during baking
    [HelpURL(Documentation.BaseURL + "manual/CinemachineConfiner2D.html")]
    public class ConCinemachineConfiner2D : CinemachineExtension
    {
        /// <summary>The 2D shape within which the camera is to be contained.</summary>
        [Tooltip("The 2D shape within which the camera is to be contained.  " +
                 "Can be a 2D polygon or 2D composite collider.")]
        public Collider2D m_BoundingShape2D;

        /// <summary>Damping applied automatically around corners to avoid jumps.</summary>
        [Tooltip("Damping applied around corners to avoid jumps.  Higher numbers are more gradual.")]
        [Range(0, 5)]
        public float m_Damping;

        /// <summary>
        /// To optimize computation and memory costs, set this to the largest view size that the camera 
        /// is expected to have.  The confiner will not compute a polygon cache for frustum sizes larger 
        /// than this.  This refers to the size in world units of the frustum at the confiner plane 
        /// (for orthographic cameras, this is just the orthographic size).  If set to 0, then this 
        /// parameter is ignored and a polygon cache will be calculated for all potential window sizes.
        /// </summary>
        [Tooltip("To optimize computation and memory costs, set this to the largest view size that the "
                 + "camera is expected to have.  The confiner will not compute a polygon cache for frustum "
                 + "sizes larger than this.  This refers to the size in world units of the frustum at the "
                 + "confiner plane (for orthographic cameras, this is just the orthographic size).  If set "
                 + "to 0, then this parameter is ignored and a polygon cache will be calculated for all "
                 + "potential window sizes.")]
        public float m_MaxWindowSize;
        
        /// <summary>
        /// For large window sizes, the confiner will potentially generate polygons with zero area.  
        /// The padding may be used to add a small amount of area to these polygons, to prevent them from being 
        /// a series of disconnected dots.
        /// </summary>
        [Tooltip("For large window sizes, the confiner will potentially generate polygons with zero area.  "
                 + "The padding may be used to add a small amount of area to these polygons, to prevent them from "
                 + "being a series of disconnected dots.")]
        [Range(0, 100)]
        public float m_Padding;

        /// <summary>Size of the slow-down zone at the edge of the bounding shape.</summary>
        [Tooltip("Size of the slow-down zone at the edge of the bounding shape.")]
        public float SlowingDistance = 2;

        float m_MaxComputationTimePerFrameInSeconds = 1f / 120f;

        public Vector3 PanOffset { get; set; }

        /// <summary>
        /// Report maximum damping time needed for this component.
        /// </summary>
        /// <returns>Highest damping setting in this component</returns>
        public override float GetMaxDampTime()
            => Mathf.Max(m_Damping, SlowingDistance * 0.2f); // just an approximation - we don't know the time

        /// <summary>Invalidates cache and consequently trigger a rebake at next iteration.</summary>
        public void InvalidateCache()
        {
            m_shapeCache.Invalidate();
        }

        /// <summary>Validates cache</summary>
        /// <param name="cameraAspectRatio">Aspect ratio of camera.</param>
        /// <returns>Returns true if the cache could be validated. False, otherwise.</returns>
        public bool ValidateCache(float cameraAspectRatio)
        {
            return m_shapeCache.ValidateCache(m_BoundingShape2D, m_MaxWindowSize, cameraAspectRatio, m_Padding, out _);
        }

        const float k_cornerAngleTreshold = 10f;

        /// <summary>
        /// Callback to do the camera confining
        /// </summary>
        /// <param name="vcam">The virtual camera being processed</param>
        /// <param name="stage">The current pipeline stage</param>
        /// <param name="state">The current virtual camera state</param>
        /// <param name="deltaTime">The current applicable deltaTime</param>
        protected override void PostPipelineStageCallback(
            CinemachineVirtualCameraBase vcam,
            CinemachineCore.Stage stage, ref CameraState state, float deltaTime
        )
        {
            if (stage != CinemachineCore.Stage.Body) return;

            float aspectRatio = state.Lens.Aspect;
            if (!m_shapeCache.ValidateCache(
                m_BoundingShape2D, m_MaxWindowSize, aspectRatio, m_Padding, out bool confinerStateChanged))
            {
                return;
            }

            var camPos = state.CorrectedPosition;
            var cameraPosLocal = m_shapeCache.m_DeltaWorldToBaked.MultiplyPoint3x4(camPos);
            var currentFrustumHeight = CalculateHalfFrustumHeight(state, cameraPosLocal.z);
            // convert frustum height from world to baked space. deltaWorldToBaked.lossyScale is always uniform.
            var bakedSpaceFrustumHeight =
                currentFrustumHeight * m_shapeCache.m_DeltaWorldToBaked.lossyScale.x;

            // Make sure we have a solution for our current frustum size
            var extra = GetExtraState<VcamExtraState>(vcam);
            extra.m_vcam = vcam;
            if (confinerStateChanged || extra.m_BakedSolution == null
                                     || !extra.m_BakedSolution.IsValid())
            {
                extra.m_BakedSolution = m_shapeCache.m_confinerOven.GetBakedSolution(bakedSpaceFrustumHeight);
            }

            Vector3 newPos = CalculateNewPos(camPos, extra, ref state, deltaTime, vcam);

            // Remember the desired displacement for next frame
            var prev = extra.m_PreviousDisplacement;

            var displacement = newPos - camPos;

            extra.m_PreviousDisplacement = displacement;

            if (!vcam.PreviousStateIsValid || deltaTime < 0 || m_Damping <= 0)
                extra.m_DampedDisplacement = Vector3.zero;
            else
            {
                // If a big change from previous frame's desired displacement is detected, 
                // assume we are going around a corner and extract that difference for damping
                if (prev.sqrMagnitude > 0.01f && Vector2.Angle(prev, displacement) > k_cornerAngleTreshold)
                    extra.m_DampedDisplacement += displacement - prev;

                extra.m_DampedDisplacement -= Damper.Damp(extra.m_DampedDisplacement, m_Damping, deltaTime);
                displacement -= extra.m_DampedDisplacement;
            }

            state.PositionCorrection += displacement;

            extra.PreviousCameraPosition = state.CorrectedPosition;
        }

        private Vector3 CalculateNewPos(
            Vector3 camPos, VcamExtraState extra,
            ref CameraState state, float deltaTime,
            CinemachineVirtualCameraBase vcam
        )
        {
            // Don't move the camera along its z-axis
            var fwd = state.CorrectedOrientation * Vector3.forward;
            Vector3 newPos = ConfinePoint(camPos);

            if (PanOffset.sqrMagnitude > 0.001F)
            {
                // we need to apply the panning after confining, but then need to confine again.
                // otherwise we might be able to look out of bounds!
                newPos = ConfinePoint(newPos + PanOffset);
            }

            // we want to slowly approach the confiner edge and not bonk at it with full force
            if (SlowingDistance < Epsilon || deltaTime < 0 || !vcam.PreviousStateIsValid) return newPos;

            Vector3 prevPos = extra.PreviousCameraPosition;

            // check if the camera moved at all. saves some time when standing still I guess
            if (Vector3.Distance(prevPos, newPos) < Epsilon) return newPos;

            // confine the point FIRST. we have to make sure p is not already outside the confiner.
            prevPos = ConfinePoint(prevPos);
            Vector3 distance = newPos - prevPos;
            if (distance.magnitude < Epsilon) return newPos;

            if (Mathf.Abs(distance.x) > Epsilon)
                newPos.x = AdjustAxis(prevPos.x, newPos.x, distance.x > 0 ? Vector3.right : Vector3.left);
            if (Mathf.Abs(distance.y) > Epsilon)
                newPos.y = AdjustAxis(prevPos.y, newPos.y, distance.y > 0 ? Vector3.up : Vector3.down);

            return newPos;

            // Returns distance from edge in direction of motion, or max if distance is greater than max.
            // dirUnit must be unit length.
            float GetDistanceFromEdge(Vector3 p, Vector3 dirUnit, float max)
            {
                p += dirUnit * max;
                return (max - (ConfinePoint(p) - p).magnitude) / max;
            }

            Vector3 ConfinePoint(Vector3 pos)
            {
                var posLocal = m_shapeCache.m_DeltaWorldToBaked.MultiplyPoint3x4(pos);
                var confined = m_shapeCache.m_DeltaBakedToWorld.MultiplyPoint3x4(
                    extra.m_BakedSolution.ConfinePoint(posLocal)
                );

                // Don't move the point along the fwd axis
                return confined - fwd * Vector3.Dot(fwd, confined - pos);
            }

            float AdjustAxis(float prevPosAxis, float newPosAxis, Vector3 dirUnit)
            {
                float t = GetDistanceFromEdge(prevPos, dirUnit, SlowingDistance);
                // Smooth slowing curve formula
                return Mathf.Lerp(prevPosAxis, newPosAxis, t * t * t + 0.05f);
            }
        }

        /// <summary>
        /// Calculates half frustum height for orthographic or perspective camera.
        /// For more info on frustum height, see <see cref="docs.unity3d.com/Manual/FrustumSizeAtDistance.html"/> 
        /// </summary>
        /// <param name="state">CameraState for checking if Orthographic or Perspective</param>
        /// <param name="vcam">vcam, to check its position</param>
        /// <returns>Frustum height of the camera</returns>
        float CalculateHalfFrustumHeight(in CameraState state, in float cameraPosLocalZ)
        {
            float frustumHeight;
            if (state.Lens.Orthographic)
            {
                frustumHeight = state.Lens.OrthographicSize;
            }
            else
            {
                // distance between the collider's plane and the camera
                float distance = cameraPosLocalZ;
                frustumHeight = distance * Mathf.Tan(state.Lens.FieldOfView * 0.5f * Mathf.Deg2Rad);
            }

            return Mathf.Abs(frustumHeight);
        }

        class VcamExtraState
        {
            public Vector3 m_PreviousDisplacement;
            public Vector3 PreviousCameraPosition;
            public Vector3 m_DampedDisplacement;
            public ConfinerOven.BakedSolution m_BakedSolution;
            public CinemachineVirtualCameraBase m_vcam;
        };

        ShapeCache m_shapeCache;

        /// <summary>
        /// ShapeCache: contains all states that dependent only on the settings in the confiner.
        /// </summary>
        struct ShapeCache
        {
            public ConfinerOven m_confinerOven;
            public List<List<Vector2>> m_OriginalPath; // in baked space, not including offset

            // These account for offset and transform change since baking
            public Matrix4x4 m_DeltaWorldToBaked;
            public Matrix4x4 m_DeltaBakedToWorld;

            float m_aspectRatio;
            float m_maxWindowSize;
            float m_skeletonPadding;
            internal float m_maxComputationTimePerFrameInSeconds;

            Matrix4x4 m_bakedToWorld; // defines baked space
            Collider2D m_boundingShape2D;

            /// <summary>
            /// Invalidates shapeCache
            /// </summary>
            public void Invalidate()
            {
                m_aspectRatio = 0;
                m_maxWindowSize = -1;
                m_DeltaBakedToWorld = m_DeltaWorldToBaked = Matrix4x4.identity;

                m_boundingShape2D = null;
                m_OriginalPath = null;

                m_confinerOven = null;
            }

            /// <summary>
            /// Checks if we have a valid confiner state cache. Calculates cache if it is invalid (outdated or empty).
            /// </summary>
            /// <param name="boundingShape2D">Bounding shape</param>
            /// <param name="maxWindowSize">Max Window size</param>
            /// <param name="aspectRatio">Aspect ratio/param>
            /// <param name="confinerStateChanged">True, if the baked confiner state has changed.
            /// False, otherwise.</param>
            /// <returns>True, if input is valid. False, otherwise.</returns>
            public bool ValidateCache(
                Collider2D boundingShape2D, float maxWindowSize,
                float aspectRatio, float skeletonPadding, out bool confinerStateChanged
            )
            {
                confinerStateChanged = false;
                if (IsValid(boundingShape2D, aspectRatio, maxWindowSize, skeletonPadding))
                {
                    // Advance confiner baking
                    if (m_confinerOven.State == ConfinerOven.BakingState.BAKING)
                    {
                        m_confinerOven.BakeConfiner(m_maxComputationTimePerFrameInSeconds);

                        // If no longer baking, then confinerStateChanged
                        confinerStateChanged = m_confinerOven.State != ConfinerOven.BakingState.BAKING;
                    }

                    // Update in case the polygon's transform changed
                    CalculateDeltaTransformationMatrix();

                    // If delta world to baked scale is uniform, cache is valid.
                    Vector2 lossyScaleXY = m_DeltaWorldToBaked.lossyScale;
                    if (lossyScaleXY.IsUniform())
                    {
                        return true;
                    }
                }

                Invalidate();
                confinerStateChanged = true;

                Type colliderType = boundingShape2D == null ? null : boundingShape2D.GetType();
                if (colliderType == typeof(PolygonCollider2D))
                {
                    var poly = boundingShape2D as PolygonCollider2D;
                    m_OriginalPath = new List<List<Vector2>>();

                    // Cache the current worldspace shape
                    m_bakedToWorld = boundingShape2D.transform.localToWorldMatrix;
                    for (int i = 0; i < poly.pathCount; ++i)
                    {
                        Vector2[] path = poly.GetPath(i);
                        List<Vector2> dst = new List<Vector2>();
                        for (int j = 0; j < path.Length; ++j)
                            dst.Add(m_bakedToWorld.MultiplyPoint3x4(path[j]));
                        m_OriginalPath.Add(dst);
                    }
                }
                else if (colliderType == typeof(CompositeCollider2D))
                {
                    var poly = boundingShape2D as CompositeCollider2D;
                    m_OriginalPath = new List<List<Vector2>>();

                    // Cache the current worldspace shape
                    m_bakedToWorld = boundingShape2D.transform.localToWorldMatrix;
                    var path = new Vector2[poly.pointCount];
                    for (int i = 0; i < poly.pathCount; ++i)
                    {
                        int numPoints = poly.GetPath(i, path);
                        List<Vector2> dst = new List<Vector2>();
                        for (int j = 0; j < numPoints; ++j)
                            dst.Add(m_bakedToWorld.MultiplyPoint3x4(path[j]));
                        m_OriginalPath.Add(dst);
                    }
                }
                else
                {
                    return false; // input collider is invalid
                }

                m_confinerOven = new ConfinerOven(m_OriginalPath, aspectRatio, maxWindowSize, skeletonPadding);
                m_aspectRatio = aspectRatio;
                m_boundingShape2D = boundingShape2D;
                m_maxWindowSize = maxWindowSize;
                m_skeletonPadding = skeletonPadding;

                CalculateDeltaTransformationMatrix();

                return true;
            }

            bool IsValid(in Collider2D boundingShape2D, in float aspectRatio, in float maxOrthoSize, in float padding)
            {
                return boundingShape2D != null && m_boundingShape2D != null &&
                       m_boundingShape2D == boundingShape2D && // same boundingShape?
                       m_OriginalPath != null && // first time?
                       m_confinerOven != null && // cache not empty? 
                       Mathf.Abs(m_aspectRatio - aspectRatio) < UnityVectorExtensions.Epsilon && // aspect changed?
                       Mathf.Abs(m_maxWindowSize - maxOrthoSize) < UnityVectorExtensions.Epsilon && // max ortho changed?
                       Mathf.Abs(m_skeletonPadding - padding) < UnityVectorExtensions.Epsilon;
            }

            void CalculateDeltaTransformationMatrix()
            {
                // Account for current collider offset (in local space) and 
                // incorporate the worldspace delta that the confiner has moved since baking
                var m = Matrix4x4.Translate(-m_boundingShape2D.offset) *
                        m_boundingShape2D.transform.worldToLocalMatrix;
                m_DeltaWorldToBaked = m_bakedToWorld * m;
                m_DeltaBakedToWorld = m_DeltaWorldToBaked.inverse;
            }
        }

    #if UNITY_EDITOR
        // Used by editor gizmo drawer
        internal bool GetGizmoPaths(
            out List<List<Vector2>> originalPath,
            ref List<List<Vector2>> currentPath,
            out Matrix4x4 pathLocalToWorld
        )
        {
            originalPath = m_shapeCache.m_OriginalPath;
            pathLocalToWorld = m_shapeCache.m_DeltaBakedToWorld;
            currentPath.Clear();
            var allExtraStates = GetAllExtraStates<VcamExtraState>();
            for (var i = 0; i < allExtraStates.Count; ++i)
            {
                var e = allExtraStates[i];
                if (e.m_BakedSolution != null)
                {
                    currentPath.AddRange(e.m_BakedSolution.GetBakedPath());
                }
            }

            return originalPath != null;
        }

        internal float BakeProgress() => m_shapeCache.m_confinerOven != null ? m_shapeCache.m_confinerOven.bakeProgress : 0f;
        internal bool ConfinerOvenTimedOut() => m_shapeCache.m_confinerOven != null &&
                                                m_shapeCache.m_confinerOven.State == ConfinerOven.BakingState.TIMEOUT;
    #endif

        void OnValidate()
        {
            m_Damping = Mathf.Max(0, m_Damping);
            m_shapeCache.m_maxComputationTimePerFrameInSeconds = m_MaxComputationTimePerFrameInSeconds;
        }

        void Reset()
        {
            m_Damping = 0.5f;
            m_MaxWindowSize = -1;
        }
    }
#endif
    }