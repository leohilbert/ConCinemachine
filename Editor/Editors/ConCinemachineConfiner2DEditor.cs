#if !UNITY_2019_3_OR_NEWER
#define CINEMACHINE_PHYSICS_2D
#endif

using System.Collections.Generic;
using System.Runtime.CompilerServices;
using UnityEngine;
using UnityEditor;

namespace Cinemachine.Editor
{
#if CINEMACHINE_PHYSICS_2D
    [CustomEditor(typeof(ConCinemachineConfiner2D))]
    [CanEditMultipleObjects]
    internal sealed class ConCinemachineConfiner2DEditor : BaseEditor<ConCinemachineConfiner2D>
    {
        SerializedProperty m_MaxWindowSizeProperty;
        GUIContent m_ComputeSkeletonLabel = new GUIContent(
            "Oversize Window", "If enabled, the confiner will compute a skeleton polygon to "
                               + "support cases where camera window size is bigger than some regions of the "
                               + "confining polygon.  Enable only if needed, because it's costly");
        GUIContent m_MaxWindowSizeLabel;
        GUIContent m_InvalidateCacheLabel = new GUIContent(
            "Invalidate Cache", "Force a recomputation of the polygon cache.  "
                                + "This needs to be done if points inside the bounding polygon change");

        protected override void GetExcludedPropertiesInInspector(List<string> excluded)
        {
            base.GetExcludedPropertiesInInspector(excluded);
            excluded.Add(FieldPath(x => x.m_MaxWindowSize));
            excluded.Add(FieldPath(x => x.m_Padding));
        }

        private void OnEnable()
        {
            m_MaxWindowSizeProperty = FindProperty(x => x.m_MaxWindowSize);
            m_MaxWindowSizeLabel = new GUIContent(m_MaxWindowSizeProperty.displayName, m_MaxWindowSizeProperty.tooltip);
        }

        public override void OnInspectorGUI()
        {
            BeginInspector();

            if (Target.m_BoundingShape2D == null)
                EditorGUILayout.HelpBox("A Bounding Shape is required.", MessageType.Warning);
            else if (
                Target.m_BoundingShape2D.GetType() != typeof(PolygonCollider2D)
                && Target.m_BoundingShape2D.GetType() != typeof(CompositeCollider2D)
                && Target.m_BoundingShape2D.GetType() != typeof(BoxCollider2D)
            )
            {
                EditorGUILayout.HelpBox(
                    "Must be a PolygonCollider2D or CompositeCollider2D.",
                    MessageType.Warning);
            }
            else if (Target.m_BoundingShape2D.GetType() == typeof(CompositeCollider2D))
            {
                CompositeCollider2D poly = Target.m_BoundingShape2D as CompositeCollider2D;
                if (poly.geometryType != CompositeCollider2D.GeometryType.Polygons)
                {
                    EditorGUILayout.HelpBox(
                        "CompositeCollider2D geometry type must be Polygons",
                        MessageType.Warning);
                }
            }

        #if false
            // Debugging info
            if (Target.GetGizmoPaths(out var originalPath, ref s_currentPathCache, out var pathLocalToWorld))
            {
                int pointCount0 = 0;
                foreach (var path in originalPath )
                    pointCount0 += path.Count;

                int pointCount1 = 1;
                foreach (var path in s_currentPathCache)
                    pointCount1 += path.Count;

                EditorGUILayout.HelpBox(
                    $"Original Path: {pointCount0} points in {originalPath.Count} paths\n"
                    + $"Confiner Path: {pointCount1} points in {s_currentPathCache.Count} paths",
                    MessageType.Info);
            }
        #endif

            DrawRemainingPropertiesInInspector();

            float vSpace = EditorGUIUtility.standardVerticalSpacing;
            float lineHeight = EditorGUIUtility.singleLineHeight;
            float maxSize = m_MaxWindowSizeProperty.floatValue;
            bool computeSkeleton = maxSize >= 0;
            var rect = EditorGUILayout.GetControlRect(true, (lineHeight + vSpace) * (computeSkeleton ? 2 : 1));
            
            EditorGUI.BeginChangeCheck();
            EditorGUI.BeginProperty(rect, m_ComputeSkeletonLabel, m_MaxWindowSizeProperty);
            {
                var r = rect;
                r.height = lineHeight;
                computeSkeleton = EditorGUI.Toggle(r, m_ComputeSkeletonLabel, maxSize >= 0);
                if (!computeSkeleton)
                    maxSize = -1;
                else
                {
                    r.y += lineHeight + vSpace;
                    maxSize = Mathf.Max(0, EditorGUI.FloatField(r, m_MaxWindowSizeLabel, Mathf.Max(0, maxSize)));
                }

                m_MaxWindowSizeProperty.floatValue = maxSize;
                EditorGUI.EndProperty();
            }
            if (computeSkeleton)
                EditorGUILayout.PropertyField(FindProperty(x => x.m_Padding));
            if (EditorGUI.EndChangeCheck())
                serializedObject.ApplyModifiedProperties();
            
            rect = EditorGUILayout.GetControlRect(true);
            if (GUI.Button(rect, m_InvalidateCacheLabel))
            {
                Target.InvalidateCache();
                EditorUtility.SetDirty(Target);
            }

            bool timedOut = Target.ConfinerOvenTimedOut();
            if (computeSkeleton)
            {
                var progress = Target.BakeProgress();
                EditorGUI.ProgressBar(EditorGUILayout.GetControlRect(), progress,
                    timedOut ? "Timed out" : progress == 0 ? "" : progress < 1f ? "Baking" : "Baked");
                if (progress > 0 && progress < 1 && Event.current.type == EventType.Repaint)
                    EditorUtility.SetDirty(target);
            }

            if (timedOut)
            {
                EditorGUILayout.HelpBox(
                    "Polygon skeleton computation timed out.  Confiner result might be incomplete."
                    + "\n\nTo fix this, reduce the number of points in the confining shape, "
                    + "or set the MaxWindowSize parameter to limit skeleton computation.",
                    MessageType.Warning);
            }
        }

        private static List<List<Vector2>> s_currentPathCache = new List<List<Vector2>>();

        [DrawGizmo(GizmoType.Active | GizmoType.Selected, typeof(ConCinemachineConfiner2D))]
        private static void DrawConfinerGizmos(ConCinemachineConfiner2D confiner2D, GizmoType type)
        {
            if (!confiner2D.GetGizmoPaths(out var originalPath, ref s_currentPathCache, out var pathLocalToWorld))
                return;

            Color color = CinemachineSettings.CinemachineCoreSettings.BoundaryObjectGizmoColour;
            Color colorDimmed = new Color(color.r, color.g, color.b, color.a / 2f);

            var oldMatrix = Gizmos.matrix;
            Gizmos.matrix = pathLocalToWorld;

            // Draw input confiner
            Gizmos.color = color;
            foreach (var path in originalPath)
            {
                for (var index = 0; index < path.Count; index++)
                    Gizmos.DrawLine(path[index], path[(index + 1) % path.Count]);
            }

            // Draw confiner for current camera size
            Gizmos.color = colorDimmed;
            foreach (var path in s_currentPathCache)
            {
                for (var index = 0; index < path.Count; index++)
                    Gizmos.DrawLine(path[index], path[(index + 1) % path.Count]);
            }

            Gizmos.matrix = oldMatrix;
        }
    }
#endif
}