using UnityEngine;
using UnityEngine.UI;

namespace Kagefumi
{
    /// <summary>Screen-space HUD built entirely from code: scores, sun dial, big messages, toasts.</summary>
    public class KagefumiHud : MonoBehaviour
    {
        class PlayerPanel
        {
            public RectTransform root;
            public Text device;
            public Image[] pips;
            public Color color;
            public float pop;
        }

        const string Hint =
            "STICK  move      A / B / RB  stomp-leap (aim with the stick, neutral = stomp in place)      X / Y / LB  hold to crouch: a short body casts a short shadow";

        Font font;
        Sprite circle;
        Text bigText, subText, toastText, phaseText, sunHolderText, noticeText;
        Image backdrop;
        RectTransform bigRect, toastRect, dialIcon;
        Image dialIconImage;
        readonly PlayerPanel[] panels = new PlayerPanel[2];
        float bigPop, toastTime, toastDuration;
        Color toastColor;

        public void Build(Color[] colors, int pipCount)
        {
            font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            circle = MakeCircleSprite(64);

            var canvasGo = new GameObject("HUD", typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler));
            canvasGo.transform.SetParent(transform, false);
            var canvas = canvasGo.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 10;
            var scaler = canvasGo.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            scaler.matchWidthOrHeight = 0.5f;
            var root = canvasGo.transform;

            for (int i = 0; i < 2; i++) panels[i] = BuildPanel(root, i, colors[i], pipCount);

            // Sun dial: the icon travels along an arc through the day and sits low at night.
            var dial = Rect("Sun Dial", root, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0f, -24f), new Vector2(460f, 110f));
            var bar = Rect("Horizon", dial, new Vector2(0.5f, 0f), new Vector2(0.5f, 0.5f), new Vector2(0f, 42f), new Vector2(440f, 4f));
            bar.gameObject.AddComponent<Image>().color = new Color(1f, 1f, 1f, 0.35f);
            dialIcon = Rect("Sun", dial, new Vector2(0.5f, 0f), new Vector2(0.5f, 0.5f), new Vector2(0f, 42f), new Vector2(34f, 34f));
            dialIconImage = dialIcon.gameObject.AddComponent<Image>();
            dialIconImage.sprite = circle;
            phaseText = Label("Phase", dial, new Vector2(0.5f, 0f), new Vector2(0.5f, 1f), new Vector2(0f, 26f), 24, TextAnchor.UpperCenter);
            sunHolderText = Label("Sun Holder", dial, new Vector2(0.5f, 0f), new Vector2(0.5f, 1f), new Vector2(0f, -4f), 28, TextAnchor.UpperCenter);
            sunHolderText.enabled = false;

            backdrop = Rect("Backdrop", root, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0f, 20f), new Vector2(4000f, 400f))
                .gameObject.AddComponent<Image>();
            backdrop.color = new Color(0f, 0f, 0f, 0.4f);
            backdrop.raycastTarget = false;

            bigText = Label("Big", root, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0f, 90f), 150, TextAnchor.MiddleCenter);
            bigRect = bigText.rectTransform;
            subText = Label("Sub", root, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 1f), new Vector2(0f, -10f), 34, TextAnchor.UpperCenter, FontStyle.Normal);
            toastText = Label("Toast", root, new Vector2(0.5f, 1f), new Vector2(0.5f, 0.5f), new Vector2(0f, -190f), 54, TextAnchor.MiddleCenter);
            toastRect = toastText.rectTransform;
            toastText.color = Color.clear;

            var hint = Label("Hint", root, new Vector2(0.5f, 0f), new Vector2(0.5f, 0f), new Vector2(0f, 22f), 22, TextAnchor.LowerCenter, FontStyle.Normal);
            hint.text = Hint;
            hint.color = new Color(1f, 1f, 1f, 0.85f);

            noticeText = Label("Notice", root, new Vector2(0.5f, 0f), new Vector2(0.5f, 0f), new Vector2(0f, 62f), 28, TextAnchor.LowerCenter);
            noticeText.color = new Color(1f, 0.84f, 0.4f);
            noticeText.enabled = false;

            HideBig();
        }

        PlayerPanel BuildPanel(Transform root, int i, Color color, int pipCount)
        {
            bool left = i == 0;
            var anchor = left ? new Vector2(0f, 1f) : new Vector2(1f, 1f);
            var panel = new PlayerPanel { color = color, pips = new Image[pipCount] };
            panel.root = Rect($"P{i + 1} Panel", root, anchor, anchor, new Vector2(left ? 40f : -40f, -28f), new Vector2(520f, 150f));

            var align = left ? TextAnchor.UpperLeft : TextAnchor.UpperRight;
            var title = Label("Name", panel.root, anchor, anchor, Vector2.zero, 64, align);
            title.text = $"P{i + 1}";
            title.color = color;

            for (int k = 0; k < pipCount; k++)
            {
                float x = (left ? 1f : -1f) * (110f + k * 46f);
                var pip = Rect($"Pip {k}", panel.root, anchor, new Vector2(0.5f, 0.5f), new Vector2(x, -38f), new Vector2(34f, 34f));
                var img = pip.gameObject.AddComponent<Image>();
                img.sprite = circle;
                panel.pips[k] = img;
            }

            panel.device = Label("Device", panel.root, anchor, anchor, new Vector2(0f, -84f), 24, align, FontStyle.Normal);
            SetPipColors(panel, 0);
            return panel;
        }

        public void SetPlayer(int i, bool joined, string deviceName)
        {
            var p = panels[i];
            p.device.text = joined ? deviceName : (i == 0 ? "press A  ·  or SPACE" : "press A  ·  or ENTER");
            p.device.color = joined ? new Color(1f, 1f, 1f, 0.9f) : new Color(1f, 1f, 1f, 0.5f + 0.2f * Mathf.Sin(Time.unscaledTime * 4f));
        }

        public void SetScore(int i, int score, bool pop)
        {
            SetPipColors(panels[i], score);
            if (pop) panels[i].pop = 1f;
        }

        static void SetPipColors(PlayerPanel p, int score)
        {
            for (int k = 0; k < p.pips.Length; k++)
                p.pips[k].color = k < score ? p.color : new Color(1f, 1f, 1f, 0.22f);
        }

        public void ShowBig(string text, Color color, string sub)
        {
            bigText.text = text;
            bigText.color = color;
            subText.text = sub ?? string.Empty;
            bigText.enabled = subText.enabled = backdrop.enabled = true;
            subText.rectTransform.anchoredPosition = new Vector2(0f, string.IsNullOrEmpty(text) ? 60f : -10f);
            bigPop = 1f;
        }

        public void HideBig()
        {
            bigText.enabled = subText.enabled = backdrop.enabled = false;
        }

        public void Toast(string text, Color color, float duration)
        {
            toastText.text = text;
            toastColor = color;
            toastDuration = duration;
            toastTime = 0f;
        }

        /// <summary>Line above the controls hint (e.g. a Joy-Con 2 tip); null hides it.</summary>
        public void SetNotice(string text)
        {
            noticeText.enabled = text != null;
            if (text != null) noticeText.text = text;
        }

        /// <summary>Line under the sun dial saying who steers the sun; null hides it.</summary>
        public void SetSunHolder(string text, Color color)
        {
            sunHolderText.enabled = text != null;
            if (text == null) return;
            sunHolderText.text = text;
            sunHolderText.color = color;
            dialIconImage.color = Color.Lerp(dialIconImage.color, color, 0.5f + 0.5f * Mathf.Sin(Time.unscaledTime * 10f));
        }

        public void SetPhase(string label, float cycle01, DayCycle.Phase phase)
        {
            phaseText.text = label;
            bool night = phase == DayCycle.Phase.Night;
            float x, y;
            if (night)
            {
                x = 0f;
                y = 42f + 34f;
                dialIconImage.color = new Color(0.75f, 0.82f, 1f);
            }
            else
            {
                // Map dawn..dusk onto the arc; the remainder of the cycle is night.
                float day01 = Mathf.Clamp01(cycle01 / 0.7f);
                x = Mathf.Lerp(-210f, 210f, day01);
                y = 42f + Mathf.Sin(day01 * Mathf.PI) * 52f;
                dialIconImage.color = Color.Lerp(new Color(1f, 0.55f, 0.3f), new Color(1f, 0.92f, 0.5f), Mathf.Sin(day01 * Mathf.PI));
            }
            dialIcon.anchoredPosition = new Vector2(x, y);
        }

        void Update()
        {
            float dt = Time.unscaledDeltaTime;

            bigPop = Mathf.MoveTowards(bigPop, 0f, dt * 4f);
            bigRect.localScale = Vector3.one * (1f + 0.35f * bigPop * bigPop);

            toastTime += dt;
            float a = toastTime < toastDuration ? Mathf.Clamp01((toastDuration - toastTime) * 3f) : 0f;
            toastText.color = new Color(toastColor.r, toastColor.g, toastColor.b, a);
            toastRect.localScale = Vector3.one * (1f + 0.25f * Mathf.Clamp01(1f - toastTime * 6f));

            foreach (var p in panels)
            {
                if (p == null) continue;
                p.pop = Mathf.MoveTowards(p.pop, 0f, dt * 3f);
                p.root.localScale = Vector3.one * (1f + 0.25f * p.pop);
            }
        }

        // ───────────────────────────── Builders ─────────────────────────────

        static RectTransform Rect(string name, Transform parent, Vector2 anchor, Vector2 pivot, Vector2 position, Vector2 size)
        {
            var go = new GameObject(name, typeof(RectTransform));
            var rt = (RectTransform)go.transform;
            rt.SetParent(parent, false);
            rt.anchorMin = rt.anchorMax = anchor;
            rt.pivot = pivot;
            rt.anchoredPosition = position;
            rt.sizeDelta = size;
            return rt;
        }

        Text Label(string name, Transform parent, Vector2 anchor, Vector2 pivot, Vector2 position, int size,
                   TextAnchor align, FontStyle style = FontStyle.Bold)
        {
            var rt = Rect(name, parent, anchor, pivot, position, new Vector2(1600f, size * 1.3f));
            var text = rt.gameObject.AddComponent<Text>();
            text.font = font;
            text.fontSize = size;
            text.fontStyle = style;
            text.alignment = align;
            text.color = Color.white;
            text.horizontalOverflow = HorizontalWrapMode.Overflow;
            text.verticalOverflow = VerticalWrapMode.Overflow;
            text.raycastTarget = false;
            var shadow = rt.gameObject.AddComponent<Shadow>();
            shadow.effectColor = new Color(0f, 0f, 0f, 0.65f);
            shadow.effectDistance = new Vector2(3f, -3f);
            return text;
        }

        static Sprite MakeCircleSprite(int size)
        {
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp };
            float r = size * 0.5f;
            for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                float d = Vector2.Distance(new Vector2(x + 0.5f, y + 0.5f), new Vector2(r, r));
                tex.SetPixel(x, y, new Color(1f, 1f, 1f, Mathf.Clamp01(r - 1f - d + 0.5f)));
            }
            tex.Apply();
            return Sprite.Create(tex, new Rect(0f, 0f, size, size), new Vector2(0.5f, 0.5f), 100f);
        }
    }
}
