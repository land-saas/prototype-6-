using UnityEngine;

namespace PortalShift
{
    public sealed class PortalHUD : MonoBehaviour
    {
        public PortalStage Stage;

        void OnGUI()
        {
            if (Stage == null || Stage.CurrentRoom == null) return;
            Matrix4x4 previousMatrix = GUI.matrix;
            Color previousColor = GUI.color;
            GUI.matrix = Matrix4x4.identity;
            GUI.color = Color.white;

            Rect map = new Rect(Screen.width - 68f, 8f, 28f, 28f);
            Rect restart = new Rect(Screen.width - 36f, 8f, 28f, 28f);
            Color mapTint = IconTint(map);
            Color restartTint = IconTint(restart);
            if (Stage.MapOpen) DrawZoomIcon(map, mapTint);
            else DrawMapIcon(map, mapTint);
            DrawRestartIcon(restart, restartTint);
            if (GUI.Button(map, GUIContent.none, GUIStyle.none)) Stage.ToggleMap();
            if (GUI.Button(restart, GUIContent.none, GUIStyle.none)) Stage.ResetAttempt();

            GUI.matrix = previousMatrix;
            GUI.color = previousColor;
        }

        static Color IconTint(Rect rect)
        {
            return new Color(.65f, .71f, .78f, rect.Contains(Event.current.mousePosition) ? .95f : .65f);
        }

        void DrawMapIcon(Rect rect, Color tint)
        {
            int width = Mathf.Max(1, Stage.Width);
            int height = Mathf.Max(1, Stage.Height);
            float cell = 18f / Mathf.Max(width, height);
            Vector2 origin = rect.center - new Vector2(width * cell, height * cell) * .5f;
            for (int y = 0; y < height; y++)
                for (int x = 0; x < width; x++)
                {
                    Color cellTint = tint;
                    if (new Vector2Int(x, y) == Stage.CurrentRoom.Coordinate)
                        cellTint = new Color(.80f, .84f, .88f, tint.a);
                    Fill(new Rect(origin.x + x * cell + 1f, origin.y + (height - 1 - y) * cell + 1f, Mathf.Max(1f, cell - 2f), Mathf.Max(1f, cell - 2f)), cellTint);
                }
        }

        static void DrawZoomIcon(Rect rect, Color tint)
        {
            Vector2 center = rect.center;
            for (int x = -1; x <= 1; x += 2)
                for (int y = -1; y <= 1; y += 2)
                {
                    Vector2 corner = center + new Vector2(x, y) * 7f;
                    Line(corner, corner - new Vector2(x * 5f, 0f), 1.5f, tint);
                    Line(corner, corner - new Vector2(0f, y * 5f), 1.5f, tint);
                }
        }

        static void DrawRestartIcon(Rect rect, Color tint)
        {
            const int segments = 10;
            Vector2 center = rect.center;
            Vector2 previous = center + new Vector2(Mathf.Cos(-45f * Mathf.Deg2Rad), Mathf.Sin(-45f * Mathf.Deg2Rad)) * 7f;
            for (int i = 1; i <= segments; i++)
            {
                float angle = Mathf.Lerp(-45f, 235f, i / (float)segments) * Mathf.Deg2Rad;
                Vector2 point = center + new Vector2(Mathf.Cos(angle), Mathf.Sin(angle)) * 7f;
                Line(previous, point, 1.5f, tint);
                previous = point;
            }
            Line(previous, previous + new Vector2(4f, -.5f), 1.5f, tint);
            Line(previous, previous + new Vector2(-.5f, -4f), 1.5f, tint);
        }

        static void Line(Vector2 start, Vector2 end, float thickness, Color color)
        {
            Matrix4x4 matrix = GUI.matrix;
            Vector2 delta = end - start;
            GUIUtility.RotateAroundPivot(Mathf.Atan2(delta.y, delta.x) * Mathf.Rad2Deg, start);
            Fill(new Rect(start.x, start.y - thickness * .5f, delta.magnitude, thickness), color);
            GUI.matrix = matrix;
        }

        static void Fill(Rect rect, Color color)
        {
            Color previous = GUI.color;
            GUI.color = color;
            GUI.DrawTexture(rect, Texture2D.whiteTexture);
            GUI.color = previous;
        }
    }
}
