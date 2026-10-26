using UnityEngine;

namespace ReDefinition.Diagnostics
{
    // The diagnostics window's text and button styles, all copies of KSP's own skin
    // (HighLogic.Skin) so that a UI theme reaching that skin's textures -- ZTheme through
    // HUDReplacer -- reaches the window too. Only sizes, margins, alignment and the grey
    // of what is switched off are the window's.
    internal sealed class DiagnosticsStyles
    {
        // The height of a segment, and of the search field beside them.
        public const float RowHeight = 22f;

        // The styles, each for what its name says it draws: small text, a row's label, a greyed one, a reason, a
        // persistence marker, a segment, a page in the list, the pin star, a heading, the live strip, the frame rate, a
        // value, a table cell, the tooltip box and the search field; and whether the font has the star glyphs.
        public readonly GUIStyle Small;
        public readonly GUIStyle Row;
        public readonly GUIStyle Dim;
        public readonly GUIStyle Reason;
        public readonly GUIStyle Marker;
        public readonly GUIStyle Segment;
        public readonly GUIStyle Category;
        public readonly GUIStyle Star;
        public readonly GUIStyle Heading;
        public readonly GUIStyle Strip;
        public readonly GUIStyle Fps;
        public readonly GUIStyle Value;
        public readonly GUIStyle Cell;
        public readonly GUIStyle Tip;
        public readonly GUIStyle Field;
        public readonly bool StarGlyph;

        // The warning colour (a reason, a long frame), and the grey of what is switched off.
        public static readonly Color Warning = new Color(1f, 0.75f, 0.3f);
        public static readonly Color Grey = new Color(0.6f, 0.6f, 0.6f);

        // Copies the skin's label, button, box and text field into the window's styles.
        public DiagnosticsStyles(GUISkin skin)
        {
            int size = skin.label.fontSize > 0 ? skin.label.fontSize : 13;

            Small = new GUIStyle(skin.label) { fontSize = size - 1, wordWrap = false, clipping = TextClipping.Clip };
            Row = new GUIStyle(skin.label) { wordWrap = false, clipping = TextClipping.Clip };
            Dim = new GUIStyle(skin.label) { wordWrap = false, clipping = TextClipping.Clip };
            Dim.normal.textColor = Grey;
            Reason = new GUIStyle(skin.label) { fontSize = size - 1, wordWrap = true };
            Reason.normal.textColor = Warning;
            Reason.padding = new RectOffset(Reason.padding.left + 12, Reason.padding.right, 0, 0);
            Marker = new GUIStyle(skin.label) { fontSize = size - 2, alignment = TextAnchor.MiddleCenter, wordWrap = false };
            Marker.normal.textColor = Grey;

            Segment = new GUIStyle(skin.button)
            {
                margin = new RectOffset(0, 0, 2, 2),
                padding = new RectOffset(7, 7, 2, 2),
                fontSize = size - 1,
                alignment = TextAnchor.MiddleCenter,
                fixedHeight = RowHeight,
                stretchWidth = false,
                wordWrap = false,
            };
            Category = new GUIStyle(skin.button)
            {
                alignment = TextAnchor.MiddleLeft,
                padding = new RectOffset(8, 6, 3, 3),
                margin = new RectOffset(0, 0, 1, 1),
                fontSize = size,
                fixedHeight = 26f,
                wordWrap = false,
            };
            Star = new GUIStyle(Segment)
            {
                fontSize = size + 2,
                padding = new RectOffset(2, 2, 0, 2),
                margin = new RectOffset(4, 0, 2, 2),
                fixedWidth = 24f,
            };
            Heading = new GUIStyle(skin.label) { fontStyle = FontStyle.Bold, wordWrap = false };
            Strip = new GUIStyle(skin.label) { fontStyle = FontStyle.Bold, wordWrap = false, clipping = TextClipping.Clip };
            Fps = new GUIStyle(skin.label)
            {
                fontStyle = FontStyle.Bold,
                fontSize = size + 3,
                alignment = TextAnchor.MiddleRight,
                wordWrap = false,
            };
            Value = new GUIStyle(skin.label) { alignment = TextAnchor.MiddleCenter, wordWrap = false };
            Cell = new GUIStyle(skin.label) { wordWrap = true, fontSize = size - 1 };
            Tip = new GUIStyle(skin.box)
            {
                fontSize = size - 1,
                alignment = TextAnchor.MiddleLeft,
                padding = new RectOffset(6, 6, 3, 3),
                wordWrap = false,
            };
            Field = new GUIStyle(skin.textField) { fontSize = size - 1, fixedHeight = RowHeight, margin = new RectOffset(0, 0, 2, 2) };

            // Whether the skin's font has the star; the plain letters stand in where not.
            Font font = skin.label.font != null ? skin.label.font : skin.font;
            StarGlyph = font != null && font.HasCharacter('★') && font.HasCharacter('☆');
        }
    }
}
