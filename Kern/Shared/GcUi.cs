using System;
using Jotunn.Managers;
using TMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

namespace GC.Shared;

/// <summary>
/// UI-Bausteine im Vanilla-Look. Inventar-Buttons werden von den Original-Buttons des Spiels geklont,
/// Fenster entstehen mit Jötunns GUIManager (Holzpanel, Norse-Überschrift, Valheim-Buttons).
/// </summary>
public static class GcUi
{
	/// <summary>Klont einen Vanilla-Button (z. B. „Alles nehmen") mit neuem Text und eigener Aktion.</summary>
	public static Button CloneButton(Button template, Transform parent, string name, string label, Vector2 anchor, Vector2 pivot, Vector2 position, Vector2 size, UnityAction onClick)
	{
		GameObject go = UnityEngine.Object.Instantiate(template.gameObject, parent, false);
		go.name = name;
		foreach (UIGamePad pad in go.GetComponentsInChildren<UIGamePad>(true))
		{
			if (pad.m_hint != null)
			{
				pad.m_hint.SetActive(false);
			}
			UnityEngine.Object.Destroy(pad);
		}
		Button button = go.GetComponent<Button>();
		button.onClick = new Button.ButtonClickedEvent();
		button.onClick.AddListener(onClick);
		button.interactable = true;
		RectTransform rect = (RectTransform)go.transform;
		rect.anchorMin = anchor;
		rect.anchorMax = anchor;
		rect.pivot = pivot;
		rect.anchoredPosition = position;
		rect.sizeDelta = size;
		foreach (TMP_Text text in go.GetComponentsInChildren<TMP_Text>(true))
		{
			text.text = label;
			text.enableAutoSizing = true;
			text.fontSizeMin = 10f;
			text.fontSizeMax = Mathf.Max(text.fontSizeMax, 16f);
		}
		foreach (Text text in go.GetComponentsInChildren<Text>(true))
		{
			text.text = label;
		}
		go.SetActive(true);
		return button;
	}

	public static void SetLabel(Button? button, string label)
	{
		if (button == null)
		{
			return;
		}
		foreach (TMP_Text text in button.GetComponentsInChildren<TMP_Text>(true))
		{
			text.text = label;
		}
		foreach (Text text in button.GetComponentsInChildren<Text>(true))
		{
			text.text = label;
		}
	}

	/// <summary>Fenster im Kompendium-Stil: Holzpanel, Überschrift in Valheim-Orange, dunkle Innenfläche.</summary>
	public static GameObject CreateWindow(string name, float width, float height, out Text header, out RectTransform inner)
	{
		GUIManager gui = GUIManager.Instance;
		GameObject panel = gui.CreateWoodpanel(GUIManager.CustomGUIFront.transform, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), Vector2.zero, width, height, false);
		panel.name = name;

		GameObject headerGo = gui.CreateText("", panel.transform, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0f, -38f),
			gui.NorseBold, 32, gui.ValheimOrange, true, Color.black, width - 80f, 50f, false);
		header = headerGo.GetComponent<Text>();
		header.alignment = TextAnchor.MiddleCenter;
		header.horizontalOverflow = HorizontalWrapMode.Wrap;

		GameObject innerGo = new("Inner", typeof(RectTransform), typeof(Image));
		innerGo.transform.SetParent(panel.transform, false);
		inner = innerGo.GetComponent<RectTransform>();
		inner.anchorMin = Vector2.zero;
		inner.anchorMax = Vector2.one;
		inner.offsetMin = new Vector2(36f, 80f);
		inner.offsetMax = new Vector2(-36f, -76f);
		Image image = innerGo.GetComponent<Image>();
		Sprite? sprite = gui.GetSprite("panel_interior_bkg_128") ?? gui.GetSprite("item_background");
		if (sprite != null)
		{
			image.sprite = sprite;
			image.type = Image.Type.Sliced;
			image.color = new Color(1f, 1f, 1f, 0.9f);
		}
		else
		{
			image.color = new Color(0f, 0f, 0f, 0.45f);
		}
		panel.SetActive(false);
		return panel;
	}

	public static Text CreateLabel(Transform parent, string text, Vector2 anchorMin, Vector2 anchorMax, Vector2 offsetMin, Vector2 offsetMax, int size = 20, TextAnchor align = TextAnchor.UpperLeft, Color? color = null, bool norse = false)
	{
		GUIManager gui = GUIManager.Instance;
		GameObject go = gui.CreateText(text, parent, anchorMin, anchorMax, Vector2.zero, norse ? gui.NorseBold : gui.AveriaSerifBold, size, color ?? Color.white, true, Color.black, 0f, 0f, false);
		RectTransform rect = (RectTransform)go.transform;
		rect.anchorMin = anchorMin;
		rect.anchorMax = anchorMax;
		rect.offsetMin = offsetMin;
		rect.offsetMax = offsetMax;
		Text label = go.GetComponent<Text>();
		label.alignment = align;
		label.horizontalOverflow = HorizontalWrapMode.Wrap;
		label.verticalOverflow = VerticalWrapMode.Truncate;
		label.supportRichText = true;
		return label;
	}

	public static Button CreateButton(Transform parent, string text, Vector2 anchor, Vector2 position, float width, float height, Action onClick)
	{
		GameObject go = GUIManager.Instance.CreateButton(text, parent, anchor, anchor, position, width, height);
		Button button = go.GetComponent<Button>();
		button.onClick.AddListener(() => onClick());
		Text? label = go.GetComponentInChildren<Text>(true);
		if (label != null)
		{
			label.resizeTextForBestFit = true;
			label.resizeTextMinSize = 10;
			label.resizeTextMaxSize = 20;
		}
		return button;
	}

	/// <summary>Scrollbare Fläche, deren Inhalt von oben nach unten gestapelt wird (VerticalLayoutGroup).</summary>
	public static RectTransform CreateScroll(Transform parent, float width, float height)
	{
		GUIManager gui = GUIManager.Instance;
		ColorBlock colors = ColorBlock.defaultColorBlock;
		colors.normalColor = new Color(0.6f, 0.45f, 0.25f, 1f);
		colors.highlightedColor = gui.ValheimOrange;
		GameObject scroll = gui.CreateScrollView(parent, false, true, 8f, 4f, colors, new Color(0f, 0f, 0f, 0.3f), width, height);
		RectTransform root = (RectTransform)scroll.transform;
		root.anchorMin = Vector2.zero;
		root.anchorMax = Vector2.one;
		root.offsetMin = new Vector2(8f, 8f);
		root.offsetMax = new Vector2(-8f, -8f);
		ScrollRect rect = scroll.GetComponentInChildren<ScrollRect>(true);
		rect.scrollSensitivity = 40f; // Jötunns Standard scrollt lange Listen (GC Option) mit dem Mausrad viel zu langsam
		RectTransform content = rect.content;
		VerticalLayoutGroup? layout = content.GetComponent<VerticalLayoutGroup>() ?? content.gameObject.AddComponent<VerticalLayoutGroup>();
		layout.childControlHeight = true;
		layout.childControlWidth = true;
		layout.childForceExpandHeight = false;
		layout.childForceExpandWidth = true;
		layout.spacing = 4f;
		layout.padding = new RectOffset(8, 16, 6, 6);
		ContentSizeFitter? fitter = content.GetComponent<ContentSizeFitter>() ?? content.gameObject.AddComponent<ContentSizeFitter>();
		fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
		return content;
	}

	/// <summary>Zeile für eine Scroll-Liste: Text links, optional Buttons rechts.</summary>
	public static RectTransform CreateRow(Transform content, float height)
	{
		GameObject row = new("Row", typeof(RectTransform), typeof(LayoutElement));
		row.transform.SetParent(content, false);
		LayoutElement element = row.GetComponent<LayoutElement>();
		element.minHeight = height;
		element.preferredHeight = height;
		return (RectTransform)row.transform;
	}

	public static void ClearChildren(Transform parent)
	{
		for (int i = parent.childCount - 1; i >= 0; i--)
		{
			UnityEngine.Object.Destroy(parent.GetChild(i).gameObject);
		}
	}

	/// <summary>Kleines Schloss-Symbol als Sprite (ohne externe Grafik).</summary>
	public static Sprite LockSprite()
	{
		const int size = 16;
		Texture2D tex = new(size, size, TextureFormat.RGBA32, false) { filterMode = FilterMode.Point };
		Color clear = new(0f, 0f, 0f, 0f);
		Color body = new(1f, 0.78f, 0.25f, 1f);
		Color dark = new(0.25f, 0.15f, 0.05f, 1f);
		for (int y = 0; y < size; y++)
		{
			for (int x = 0; x < size; x++)
			{
				Color c = clear;
				bool inBody = x >= 2 && x <= 13 && y >= 1 && y <= 8;
				bool inShackle = y >= 8 && y <= 14 && ((x >= 4 && x <= 5) || (x >= 10 && x <= 11) || (y >= 13 && x >= 4 && x <= 11));
				if (inBody)
				{
					c = (x == 2 || x == 13 || y == 1 || y == 8) ? dark : body;
					if (x >= 7 && x <= 8 && y >= 3 && y <= 6)
					{
						c = dark;
					}
				}
				else if (inShackle)
				{
					c = body;
				}
				tex.SetPixel(x, y, c);
			}
		}
		tex.Apply();
		return Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), 100f);
	}
}
