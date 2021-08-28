using UnityEditor;
using UnityEngine;
using NKLI.Nigiri;

namespace NKLI
{
#if UNITY_EDITOR
    [CustomEditor(typeof(FSR_StandardPipeline))]
    public class FSR_Editor : Editor
    {
        /// Property references

        // Voxelization
        SerializedProperty m_RenderScale;
        SerializedProperty m_UpsampleMode;
        SerializedProperty m_Sharpening;
        SerializedProperty m_Sharpness;


        public static GUIStyle titleBoxStyle;


        void DrawFSRSettings()
        {
            EditorGUILayout.PropertyField(m_RenderScale, new GUIContent(" Render scale"));
            EditorGUILayout.PropertyField(m_UpsampleMode, new GUIContent(" Up-sample mode"));

            if (m_UpsampleMode.intValue == 0)
            {
                EditorGUILayout.PropertyField(m_Sharpening, new GUIContent(" Sharpen image"));

                if (m_Sharpening.boolValue == true)
                {
                    EditorGUILayout.PropertyField(m_Sharpness, new GUIContent(" Sharpness amount"));
                }
            }
        }


        #region GetSettings
        /// <summary>
        /// Load all property references from main script
        /// </summary>
        void OnEnable()
        {
            m_RenderScale = serializedObject.FindProperty("render_scale");
            m_UpsampleMode = serializedObject.FindProperty("upsample_mode");
            m_Sharpening = serializedObject.FindProperty("sharpening");
            m_Sharpness = serializedObject.FindProperty("sharpness");
        }
        #endregion


        public override void OnInspectorGUI()
        {
            titleBoxStyle = (GUIStyle)"LODBlackBox";


            //base.OnInspectorGUI();
            // Header logo area -------------------------

            EditorGUILayout.BeginVertical(titleBoxStyle);


            Header("AMD Fidelity Super Resolution", TextTitleStyle, 20, Color.gray);
            EditorGUILayout.Separator();

            DrawFSRSettings();
            EditorGUILayout.Separator();

            //DrawUILine(Color.gray);
            // ------------------------------------------


            // Apply changes to the serializedProperty - always do this at the end of OnInspectorGUI.
            serializedObject.ApplyModifiedProperties();
            EditorGUILayout.EndVertical();

        }


        public static void Header(string text, GUIStyle texStyle, int newHeight, Color color)
        {

            //GUIStyleState ss = new GUIStyleState() { textColor = color };
            GUIStyle h = new GUIStyle("Label")
            {
                fontSize = 20,

                alignment = TextAnchor.MiddleLeft,
                //border = new RectOffset(15, 7, 4, 4),
                fixedHeight = newHeight,
                //contentOffset = new Vector2(20f, -2f)
            };
            h.normal.textColor = color;


            EditorGUILayout.BeginHorizontal(h, GUILayout.Height(25));
            {

                Label(text, h, false);
            }
            EditorGUILayout.EndHorizontal();
        }

        #region Label

        public static void Label(string text, GUIStyle textStyle, bool center)
        {

            if (center)
            {

                EditorGUILayout.BeginHorizontal();
                GUILayout.FlexibleSpace();
                GUILayout.Label(text, textStyle);
                GUILayout.FlexibleSpace();
                EditorGUILayout.EndHorizontal();
            }
            else
            {
                GUILayout.Label(text, textStyle);
            }
        }

        public static void Label(string text, GUIStyle textStyle, bool center, int width)
        {

            if (center)
            {

                EditorGUILayout.BeginHorizontal();
                GUILayout.FlexibleSpace();
                GUILayout.Label(text, textStyle, GUILayout.Width(width));
                GUILayout.FlexibleSpace();
                EditorGUILayout.EndHorizontal();
            }
            else
            {
                GUILayout.Label(text, textStyle, GUILayout.Width(width));
            }
        }

        #endregion

        #region TextStyles
        protected virtual GUIStyle TextTitleStyle
        {

            get
            {

                GUIStyle style = new GUIStyle(EditorStyles.label);
                style.fontStyle = FontStyle.Bold;
                style.fontSize = 20;

                return style;
            }
        }


        protected virtual GUIStyle TextSectionStyle
        {

            get
            {

                GUIStyle style = new GUIStyle(EditorStyles.label);
                style.fontStyle = FontStyle.Bold;
                style.fontSize = 10;

                return style;
            }
        }
        #endregion
    }
#endif
}