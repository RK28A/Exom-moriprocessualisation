// NCMK SDK - in-headset UI with Unity's uGUI (UnityEngine.UI + UnityEngine.UIModule).
//
// A world-space Canvas is the only kind visible in VR. Typical use (main thread):
//
//   auto canvas = NCMK::UI::Canvas::CreateWorldSpace("MyPanel", {600, 300});   // 600x300 px, 1 px = 1 mm
//   auto bg     = NCMK::UI::CreatePanel(canvas.rect, "Background", {0, 0, 0, 0.7f});
//   auto label  = NCMK::UI::CreateText(canvas.rect, "Label", "Hello", 32, {1, 1, 1});
//   canvas.rect.SetPosition({0, 1.5f, 1.f});
//
// Everything is resolved by name; a missing class logs once and the helper returns an empty handle.
#pragma once

#include <cmath>
#include <cstdint>
#include <vector>

#include "../Unity/ncmk_unity.hpp"

namespace NCMK {
namespace UI {

using Unity::Color;
using Unity::Component;
using Unity::GameObject;
using Unity::Vec2;
using Unity::Vec3;

namespace detail {
using IL2CPP::ClassRef;
inline ClassRef& CanvasClass() { static ClassRef k{"UnityEngine.UIModule", "UnityEngine", "Canvas"}; return k; }
inline ClassRef& RenderModeEnum() { static ClassRef k{"UnityEngine.UIModule", "UnityEngine", "RenderMode"}; return k; }
inline ClassRef& CanvasGroupClass() { static ClassRef k{"UnityEngine.UIModule", "UnityEngine", "CanvasGroup"}; return k; }
inline ClassRef& CanvasScalerClass() { static ClassRef k{"UnityEngine.UI", "UnityEngine.UI", "CanvasScaler"}; return k; }
inline ClassRef& ImageClass() { static ClassRef k{"UnityEngine.UI", "UnityEngine.UI", "Image"}; return k; }
inline ClassRef& TextClass() { static ClassRef k{"UnityEngine.UI", "UnityEngine.UI", "Text"}; return k; }
inline ClassRef& RectMask2DClass() { static ClassRef k{"UnityEngine.UI", "UnityEngine.UI", "RectMask2D"}; return k; }
inline ClassRef& GraphicClass() { static ClassRef k{"UnityEngine.UI", "UnityEngine.UI", "Graphic"}; return k; }
inline ClassRef& FontClass() { static ClassRef k{"UnityEngine.TextRenderingModule", "UnityEngine", "Font"}; return k; }
inline ClassRef& TextAnchorEnum() { static ClassRef k{"UnityEngine.TextRenderingModule", "UnityEngine", "TextAnchor"}; return k; }
inline ClassRef& Texture2DClass() { static ClassRef k{"UnityEngine.CoreModule", "UnityEngine", "Texture2D"}; return k; }
inline ClassRef& TextureClass() { static ClassRef k{"UnityEngine.CoreModule", "UnityEngine", "Texture"}; return k; }
inline ClassRef& TextureFormatEnum() { static ClassRef k{"UnityEngine.CoreModule", "UnityEngine", "TextureFormat"}; return k; }
inline ClassRef& FilterModeEnum() { static ClassRef k{"UnityEngine.CoreModule", "UnityEngine", "FilterMode"}; return k; }
inline ClassRef& SpriteClass() { static ClassRef k{"UnityEngine.CoreModule", "UnityEngine", "Sprite"}; return k; }
inline ClassRef& ResourcesClass() { static ClassRef k{"UnityEngine.CoreModule", "UnityEngine", "Resources"}; return k; }

inline void SetFloat(const char* what, NCMKMethod* m, NCMKObject* self, float v) {
    void* args[] = {&v};
    IL2CPP::Call(what, m, self, args);
}
inline void SetInt(const char* what, NCMKMethod* m, NCMKObject* self, int32_t v) {
    void* args[] = {&v};
    IL2CPP::Call(what, m, self, args);
}
inline void SetBool(const char* what, NCMKMethod* m, NCMKObject* self, bool v) {
    void* args[] = {&v};
    IL2CPP::Call(what, m, self, args);
}
inline void SetVec2(const char* what, NCMKMethod* m, NCMKObject* self, Vec2 v) {
    void* args[] = {Unity::detail::Arg(v)};
    if (args[0]) IL2CPP::Call(what, m, self, args);
}
inline void SetColor(NCMKObject* graphic, Color c) {
    void* args[] = {Unity::detail::Arg(c)};
    if (args[0])
        IL2CPP::Call("Graphic.set_color", NCMK_METHOD_SIG(GraphicClass().Get(), "set_color", "UnityEngine.Color"), graphic, args);
}
}  // namespace detail

// ============================================================================ RectTransform

class RectTransform : public Unity::Transform {
public:
    using Unity::Transform::Transform;
    static NCMKClass* Class() { return Unity::detail::K("RectTransform").Get(); }

    void SetSize(Vec2 sizeDelta) const {
        NCMK_REQUIRE_MAIN_THREAD();
        detail::SetVec2("RectTransform.set_sizeDelta",
                        NCMK_METHOD_SIG(Class(), "set_sizeDelta", "UnityEngine.Vector2"), ptr_, sizeDelta);
    }
    void SetAnchors(Vec2 min, Vec2 max) const {
        NCMK_REQUIRE_MAIN_THREAD();
        detail::SetVec2("RectTransform.set_anchorMin", NCMK_METHOD_SIG(Class(), "set_anchorMin", "UnityEngine.Vector2"), ptr_, min);
        detail::SetVec2("RectTransform.set_anchorMax", NCMK_METHOD_SIG(Class(), "set_anchorMax", "UnityEngine.Vector2"), ptr_, max);
    }
    void SetPivot(Vec2 pivot) const {
        NCMK_REQUIRE_MAIN_THREAD();
        detail::SetVec2("RectTransform.set_pivot", NCMK_METHOD_SIG(Class(), "set_pivot", "UnityEngine.Vector2"), ptr_, pivot);
    }
    void SetAnchoredPosition(Vec2 pos) const {
        NCMK_REQUIRE_MAIN_THREAD();
        detail::SetVec2("RectTransform.set_anchoredPosition",
                        NCMK_METHOD_SIG(Class(), "set_anchoredPosition", "UnityEngine.Vector2"), ptr_, pos);
    }
    /// Fill the parent, minus `inset` pixels on every side.
    void Stretch(float inset = 0.f) const {
        SetAnchors({0, 0}, {1, 1});
        SetPivot({0.5f, 0.5f});
        SetAnchoredPosition({0, 0});
        SetSize({-2 * inset, -2 * inset});
    }
    /// Top-left anchored box: position from the parent's top-left corner, y going down.
    void PlaceTopLeft(Vec2 position, Vec2 size) const {
        SetAnchors({0, 1}, {0, 1});
        SetPivot({0, 1});
        SetAnchoredPosition({position.x, -position.y});
        SetSize(size);
    }
};

/// A child object with a RectTransform.
struct Element {
    GameObject gameObject;
    RectTransform rect;
    explicit operator bool() const { return static_cast<bool>(rect); }
};

/// new GameObject(name) + RectTransform, parented to `parent` (worldPositionStays = false).
inline Element CreateElement(const RectTransform& parent, const char* name) {
    NCMK_REQUIRE_MAIN_THREAD(Element{});
    GameObject go = GameObject::Create(name);
    if (!go) return {};
    // Adding a RectTransform replaces the plain Transform of a new GameObject.
    RectTransform rect(go.GetOrAddComponent(RectTransform::Class()).Ptr());
    if (!rect) return {};
    if (parent) rect.SetParent(parent, false);
    rect.SetLocalScale({1, 1, 1});
    return {go, rect};
}

// ============================================================================ Canvas

struct Canvas {
    GameObject gameObject;
    RectTransform rect;
    Component canvas;
    explicit operator bool() const { return static_cast<bool>(canvas); }

    /// World-space canvas of `sizePx` pixels; metersPerPixel 0.001 means 1 px = 1 mm.
    /// Kept across scene loads unless dontDestroyOnLoad is false.
    static Canvas CreateWorldSpace(const char* name, Vec2 sizePx, float metersPerPixel = 0.001f, bool dontDestroyOnLoad = true) {
        NCMK_REQUIRE_MAIN_THREAD(Canvas{});
        Canvas c;
        NCMKClass* canvasClass = detail::CanvasClass().Get();
        if (!canvasClass) return c;
        c.gameObject = GameObject::Create(name);
        if (!c.gameObject) return c;
        c.canvas = c.gameObject.AddComponent(canvasClass);
        if (!c.canvas) return c;
        int32_t worldSpace = static_cast<int32_t>(IL2CPP::EnumValue(detail::RenderModeEnum().Get(), "WorldSpace", 2));
        detail::SetInt("Canvas.set_renderMode", NCMK_METHOD_SIG(canvasClass, "set_renderMode", "UnityEngine.RenderMode"),
                       c.canvas.Ptr(), worldSpace);
        if (NCMKClass* scaler = detail::CanvasScalerClass().Get()) {
            Component s = c.gameObject.AddComponent(scaler);
            // Sharper text in world space.
            if (s) detail::SetFloat("CanvasScaler.set_dynamicPixelsPerUnit",
                                    NCMK_METHOD_SIG(scaler, "set_dynamicPixelsPerUnit", "System.Single"), s.Ptr(), 4.f);
        }
        c.rect = RectTransform(c.gameObject.GetComponent(RectTransform::Class()).Ptr());
        if (c.rect) {
            c.rect.SetSize(sizePx);
            c.rect.SetLocalScale({metersPerPixel, metersPerPixel, metersPerPixel});
        }
        if (Unity::Camera cam = Unity::Camera::Main()) {
            void* args[] = {cam.Ptr()};
            IL2CPP::Call("Canvas.set_worldCamera", NCMK_METHOD_SIG(canvasClass, "set_worldCamera", "UnityEngine.Camera"),
                         c.canvas.Ptr(), args);
        }
        if (dontDestroyOnLoad) Unity::DontDestroyOnLoad(c.gameObject);
        return c;
    }

    void SetSortingOrder(int order) const {
        NCMK_REQUIRE_MAIN_THREAD();
        detail::SetInt("Canvas.set_sortingOrder", NCMK_METHOD_SIG(detail::CanvasClass().Get(), "set_sortingOrder", "System.Int32"),
                       canvas.Ptr(), order);
    }
};

// ============================================================================ components

/// UnityEngine.UI.Graphic: shared by Image and Text.
class Graphic : public Component {
public:
    using Component::Component;
    void SetColor(Color c) const {
        NCMK_REQUIRE_MAIN_THREAD();
        detail::SetColor(ptr_, c);
    }
    /// false = clicks / pointer rays go through it (decorative elements).
    void SetRaycastTarget(bool v) const {
        NCMK_REQUIRE_MAIN_THREAD();
        detail::SetBool("Graphic.set_raycastTarget",
                        NCMK_METHOD_SIG(detail::GraphicClass().Get(), "set_raycastTarget", "System.Boolean"), ptr_, v);
    }
};

class Image : public Graphic {
public:
    using Graphic::Graphic;
    void SetSprite(NCMKObject* sprite) const {
        NCMK_REQUIRE_MAIN_THREAD();
        void* args[] = {sprite};
        IL2CPP::Call("Image.set_sprite", NCMK_METHOD_SIG(detail::ImageClass().Get(), "set_sprite", "UnityEngine.Sprite"), ptr_, args);
    }
    /// 9-slice: the sprite's border (CreateSprite with a border, CreateRoundedSprite...) keeps its
    /// corners unscaled while the element is resized.
    void SetSliced(NCMKObject* sprite, bool fillCenter = true) const {
        NCMK_REQUIRE_MAIN_THREAD();
        SetSprite(sprite);
        NCMKClass* image = detail::ImageClass().Get();
        int32_t sliced = static_cast<int32_t>(IL2CPP::EnumValue(IL2CPP::FindNestedClass(image, "Type"), "Sliced", 1));
        detail::SetInt("Image.set_type", NCMK_METHOD_SIG(image, "set_type", "UnityEngine.UI.Image.Type"), ptr_, sliced);
        detail::SetBool("Image.set_fillCenter", NCMK_METHOD_SIG(image, "set_fillCenter", "System.Boolean"), ptr_, fillCenter);
    }
};

enum class Anchor { UpperLeft, UpperCenter, UpperRight, MiddleLeft, MiddleCenter, MiddleRight, LowerLeft, LowerCenter, LowerRight };

class Text : public Graphic {
public:
    using Graphic::Graphic;
    void SetText(const char* utf8) const {
        NCMK_REQUIRE_MAIN_THREAD();
        void* args[] = {IL2CPP::NewString(utf8)};
        IL2CPP::Call("Text.set_text", NCMK_METHOD_SIG(detail::TextClass().Get(), "set_text", "System.String"), ptr_, args);
    }
    void SetFontSize(int size) const {
        NCMK_REQUIRE_MAIN_THREAD();
        detail::SetInt("Text.set_fontSize", NCMK_METHOD_SIG(detail::TextClass().Get(), "set_fontSize", "System.Int32"), ptr_, size);
    }
    void SetFont(NCMKObject* font) const {
        NCMK_REQUIRE_MAIN_THREAD();
        void* args[] = {font};
        IL2CPP::Call("Text.set_font", NCMK_METHOD_SIG(detail::TextClass().Get(), "set_font", "UnityEngine.Font"), ptr_, args);
    }
    void SetAlignment(Anchor a) const {
        NCMK_REQUIRE_MAIN_THREAD();
        static const char* names[] = {"UpperLeft", "UpperCenter", "UpperRight", "MiddleLeft", "MiddleCenter",
                                      "MiddleRight", "LowerLeft", "LowerCenter", "LowerRight"};
        int32_t v = static_cast<int32_t>(IL2CPP::EnumValue(detail::TextAnchorEnum().Get(), names[static_cast<int>(a)], 0));
        detail::SetInt("Text.set_alignment", NCMK_METHOD_SIG(detail::TextClass().Get(), "set_alignment", "UnityEngine.TextAnchor"),
                       ptr_, v);
    }
    /// FontStyle.Bold / FontStyle.Normal.
    void SetBold(bool bold) const {
        NCMK_REQUIRE_MAIN_THREAD();
        static IL2CPP::ClassRef style{"UnityEngine.TextRenderingModule", "UnityEngine", "FontStyle"};
        int32_t v = static_cast<int32_t>(IL2CPP::EnumValue(style.Get(), bold ? "Bold" : "Normal", bold ? 1 : 0));
        detail::SetInt("Text.set_fontStyle", NCMK_METHOD_SIG(detail::TextClass().Get(), "set_fontStyle", "UnityEngine.FontStyle"),
                       ptr_, v);
    }
    /// wrap: wrap long lines (else they overflow horizontally); truncate: cut text that does not fit
    /// vertically (else it overflows below the rect).
    void SetOverflow(bool wrap, bool truncate) const {
        NCMK_REQUIRE_MAIN_THREAD();
        static IL2CPP::ClassRef hMode{"UnityEngine.TextRenderingModule", "UnityEngine", "HorizontalWrapMode"};
        static IL2CPP::ClassRef vMode{"UnityEngine.TextRenderingModule", "UnityEngine", "VerticalWrapMode"};
        int32_t h = static_cast<int32_t>(IL2CPP::EnumValue(hMode.Get(), wrap ? "Wrap" : "Overflow", wrap ? 0 : 1));
        int32_t v = static_cast<int32_t>(IL2CPP::EnumValue(vMode.Get(), truncate ? "Truncate" : "Overflow", truncate ? 0 : 1));
        detail::SetInt("Text.set_horizontalOverflow",
                       NCMK_METHOD_SIG(detail::TextClass().Get(), "set_horizontalOverflow", "UnityEngine.HorizontalWrapMode"), ptr_, h);
        detail::SetInt("Text.set_verticalOverflow",
                       NCMK_METHOD_SIG(detail::TextClass().Get(), "set_verticalOverflow", "UnityEngine.VerticalWrapMode"), ptr_, v);
    }
};

class CanvasGroup : public Component {
public:
    using Component::Component;
    void SetAlpha(float a) const {
        NCMK_REQUIRE_MAIN_THREAD();
        detail::SetFloat("CanvasGroup.set_alpha", NCMK_METHOD_SIG(detail::CanvasGroupClass().Get(), "set_alpha", "System.Single"),
                         ptr_, a);
    }
    void SetInteractable(bool v) const {
        NCMK_REQUIRE_MAIN_THREAD();
        detail::SetBool("CanvasGroup.set_interactable",
                        NCMK_METHOD_SIG(detail::CanvasGroupClass().Get(), "set_interactable", "System.Boolean"), ptr_, v);
    }
    void SetBlocksRaycasts(bool v) const {
        NCMK_REQUIRE_MAIN_THREAD();
        detail::SetBool("CanvasGroup.set_blocksRaycasts",
                        NCMK_METHOD_SIG(detail::CanvasGroupClass().Get(), "set_blocksRaycasts", "System.Boolean"), ptr_, v);
    }
};

inline Image AddImage(const GameObject& go, Color color) {
    NCMK_REQUIRE_MAIN_THREAD(Image());
    Image img(go.AddComponent(detail::ImageClass().Get()).Ptr());
    if (img) img.SetColor(color);
    return img;
}

/// Unity's built-in font: LegacyRuntime.ttf (Unity 2022.2+), Arial.ttf (older), else an OS font.
inline NCMKObject* BuiltinFont() {
    NCMK_REQUIRE_MAIN_THREAD(nullptr);
    // Pinned once and kept for the process lifetime (fonts are shared by every Text).
    static NCMKObject* font = nullptr;
    if (font) return font;
    auto keep = [](NCMKObject* f) {
        Host()->pin(f);
        font = f;
        return f;
    };
    NCMKClass* fontClass = detail::FontClass().Get();
    NCMKObject* type = IL2CPP::TypeOf(fontClass);
    NCMKMethod* get = NCMK_METHOD_SIG(detail::ResourcesClass().Get(), "GetBuiltinResource", "System.Type", "System.String");
    for (const char* name : {"LegacyRuntime.ttf", "Arial.ttf"}) {
        if (!type || !get) break;
        void* args[] = {type, IL2CPP::NewString(name)};
        bool threw = false;
        NCMKObject* f = IL2CPP::Invoke(get, nullptr, args, &threw);  // a missing name throws: try the next
        if (f && !threw) return keep(f);
    }
    void* args[2];
    NCMKString* osName = IL2CPP::NewString("Roboto");
    int32_t size = 32;
    args[0] = osName;
    args[1] = &size;
    NCMKObject* f = IL2CPP::Call("Font.CreateDynamicFontFromOSFont",
                                 NCMK_METHOD_SIG(fontClass, "CreateDynamicFontFromOSFont", "System.String", "System.Int32"), nullptr,
                                 args);
    return f ? keep(f) : nullptr;
}

inline Text AddText(const GameObject& go, const char* text, int fontSize, Color color, Anchor anchor = Anchor::UpperLeft) {
    NCMK_REQUIRE_MAIN_THREAD(Text());
    Text t(go.AddComponent(detail::TextClass().Get()).Ptr());
    if (!t) return t;
    if (NCMKObject* font = BuiltinFont()) t.SetFont(font);
    t.SetFontSize(fontSize);
    t.SetColor(color);
    t.SetAlignment(anchor);
    t.SetText(text);
    return t;
}

/// Clips children to this element's rectangle.
inline Component AddRectMask2D(const GameObject& go) {
    NCMK_REQUIRE_MAIN_THREAD(Component());
    return go.AddComponent(detail::RectMask2DClass().Get());
}

inline CanvasGroup AddCanvasGroup(const GameObject& go) {
    NCMK_REQUIRE_MAIN_THREAD(CanvasGroup());
    return CanvasGroup(go.AddComponent(detail::CanvasGroupClass().Get()).Ptr());
}

/// Child element filling its parent with a solid color.
inline Element CreatePanel(const RectTransform& parent, const char* name, Color color, float inset = 0.f) {
    Element e = CreateElement(parent, name);
    if (!e) return e;
    e.rect.Stretch(inset);
    AddImage(e.gameObject, color);
    return e;
}

/// Child element filling its parent with a Text.
inline Text CreateText(const RectTransform& parent, const char* name, const char* text, int fontSize, Color color,
                       float inset = 0.f, Anchor anchor = Anchor::UpperLeft) {
    Element e = CreateElement(parent, name);
    if (!e) return Text();
    e.rect.Stretch(inset);
    return AddText(e.gameObject, text, fontSize, color, anchor);
}

// ============================================================================ textures / sprites

/// Texture2D (RGBA32, no mipmaps) from `width*height*4` bytes, rows bottom-up like Unity.
inline NCMKObject* CreateTexture(int width, int height, const uint8_t* rgba) {
    NCMK_REQUIRE_MAIN_THREAD(nullptr);
    NCMKClass* k = detail::Texture2DClass().Get();
    NCMKMethod* ctor = NCMK_METHOD_SIG(k, ".ctor", "System.Int32", "System.Int32", "UnityEngine.TextureFormat", "System.Boolean");
    NCMKObject* tex = ctor ? IL2CPP::NewObject(k) : nullptr;
    if (!tex) {
        FailOnce("UI::CreateTexture", "CreateTexture: Texture2D(int, int, TextureFormat, bool) not available");
        return nullptr;
    }
    int32_t w = width, h = height;
    int32_t format = static_cast<int32_t>(IL2CPP::EnumValue(detail::TextureFormatEnum().Get(), "RGBA32", 4));
    bool mip = false;
    void* ctorArgs[] = {&w, &h, &format, &mip};
    bool ok = false;
    IL2CPP::Call("Texture2D..ctor", ctor, tex, ctorArgs, &ok);
    if (!ok) return nullptr;

    int32_t size = width * height * 4;
    // LoadRawTextureData(IntPtr, int) copies straight from native memory; byte[] overload as fallback.
    if (NCMKMethod* raw = NCMK_METHOD_SIG(k, "LoadRawTextureData", "System.IntPtr", "System.Int32")) {
        intptr_t p = reinterpret_cast<intptr_t>(rgba);
        void* args[] = {&p, &size};
        IL2CPP::Call("Texture2D.LoadRawTextureData(IntPtr, int)", raw, tex, args, &ok);
    } else {
        NCMKObject* bytes = IL2CPP::NewByteArray(rgba, size);
        void* args[] = {bytes};
        ok = false;
        if (bytes)
            IL2CPP::Call("Texture2D.LoadRawTextureData(byte[])", NCMK_METHOD_SIG(k, "LoadRawTextureData", "System.Byte[]"), tex, args, &ok);
    }
    if (!ok) return nullptr;
    int32_t bilinear = static_cast<int32_t>(IL2CPP::EnumValue(detail::FilterModeEnum().Get(), "Bilinear", 1));
    detail::SetInt("Texture.set_filterMode", NCMK_METHOD_SIG(detail::TextureClass().Get(), "set_filterMode", "UnityEngine.FilterMode"),
                   tex, bilinear);
    IL2CPP::Call("Texture2D.Apply", NCMK_METHOD(k, "Apply", 0), tex);
    return tex;
}

/// Solid-color texture (handy for rounded/plain UI backgrounds and debug quads).
inline NCMKObject* CreateSolidTexture(int width, int height, Color c) {
    std::vector<uint8_t> px(static_cast<size_t>(width) * height * 4);
    auto to8 = [](float v) { return static_cast<uint8_t>(v <= 0 ? 0 : v >= 1 ? 255 : v * 255.f + 0.5f); };
    for (size_t i = 0; i < px.size(); i += 4) {
        px[i] = to8(c.r);
        px[i + 1] = to8(c.g);
        px[i + 2] = to8(c.b);
        px[i + 3] = to8(c.a);
    }
    return CreateTexture(width, height, px.data());
}

/// Sprite.Create(texture, full rect, pivot, pixelsPerUnit).
inline NCMKObject* CreateSprite(NCMKObject* texture2D, int width, int height, Vec2 pivot = {0.5f, 0.5f},
                                float pixelsPerUnit = 100.f) {
    NCMK_REQUIRE_MAIN_THREAD(nullptr);
    if (!texture2D) return nullptr;
    Unity::Rect r{0, 0, static_cast<float>(width), static_cast<float>(height)};
    void* args[] = {texture2D, Unity::detail::Arg(r), Unity::detail::Arg(pivot), &pixelsPerUnit};
    if (!args[1] || !args[2]) return nullptr;
    return IL2CPP::Call("Sprite.Create",
                        NCMK_METHOD_SIG(detail::SpriteClass().Get(), "Create", "UnityEngine.Texture2D", "UnityEngine.Rect",
                                        "UnityEngine.Vector2", "System.Single"),
                        nullptr, args);
}

/// Sprite.Create with a 9-slice border (left, bottom, right, top in pixels) for Image::SetSliced.
inline NCMKObject* CreateSprite(NCMKObject* texture2D, int width, int height, Unity::Vec4 border, Vec2 pivot = {0.5f, 0.5f},
                                float pixelsPerUnit = 100.f) {
    NCMK_REQUIRE_MAIN_THREAD(nullptr);
    if (!texture2D) return nullptr;
    static IL2CPP::ClassRef meshType{"UnityEngine.CoreModule", "UnityEngine", "SpriteMeshType"};
    Unity::Rect r{0, 0, static_cast<float>(width), static_cast<float>(height)};
    uint32_t extrude = 0;
    int32_t fullRect = static_cast<int32_t>(IL2CPP::EnumValue(meshType.Get(), "FullRect", 0));
    void* args[] = {texture2D, Unity::detail::Arg(r), Unity::detail::Arg(pivot), &pixelsPerUnit, &extrude, &fullRect,
                    Unity::detail::Arg(border)};
    if (!args[1] || !args[2] || !args[6]) return nullptr;
    return IL2CPP::Call("Sprite.Create(border)",
                        NCMK_METHOD_SIG(detail::SpriteClass().Get(), "Create", "UnityEngine.Texture2D", "UnityEngine.Rect",
                                        "UnityEngine.Vector2", "System.Single", "System.UInt32", "UnityEngine.SpriteMeshType",
                                        "UnityEngine.Vector4"),
                        nullptr, args);
}

namespace detail {
/// Signed distance from pixel (x, y) to a rounded square of `size` px inset by `inset`, corner `radius`.
inline float RoundedRectDistance(float x, float y, float size, float inset, float radius) {
    float half = size * 0.5f - inset;
    float px = std::fabs(x - size * 0.5f) - (half - radius);
    float py = std::fabs(y - size * 0.5f) - (half - radius);
    float ox = px > 0 ? px : 0, oy = py > 0 ? py : 0;
    float inside = px > py ? px : py;
    return std::sqrt(ox * ox + oy * oy) + (inside < 0 ? inside : 0) - radius;
}

inline NCMKObject* SpriteFromAlpha(int size, float border, float (*alphaAt)(float d, float param), float param, float inset,
                                   float radius) {
    std::vector<uint8_t> px(static_cast<size_t>(size) * size * 4);
    for (int y = 0; y < size; ++y)
        for (int x = 0; x < size; ++x) {
            float d = RoundedRectDistance(x + 0.5f, y + 0.5f, static_cast<float>(size), inset, radius);
            float a = alphaAt(d, param);
            uint8_t* p = &px[(static_cast<size_t>(y) * size + x) * 4];
            p[0] = p[1] = p[2] = 255;  // white: tint with Image::SetColor
            p[3] = static_cast<uint8_t>(a <= 0 ? 0 : a >= 1 ? 255 : a * 255.f + 0.5f);
        }
    NCMKObject* tex = CreateTexture(size, size, px.data());
    return CreateSprite(tex, size, size, Unity::Vec4{border, border, border, border});
}
}  // namespace detail

/// White rounded square, 9-sliced (border = radius): Image::SetSliced(CreateRoundedSprite(64, 16))
/// then SetColor for a panel / button of any size with constant corners. Cache the result.
inline NCMKObject* CreateRoundedSprite(int size, float radius) {
    NCMK_REQUIRE_MAIN_THREAD(nullptr);
    if (size < 4) size = 4;
    if (radius > size * 0.5f - 1) radius = size * 0.5f - 1;
    return detail::SpriteFromAlpha(
        size, radius, [](float d, float) { return 0.5f - d; }, 0.f, 0.f, radius);  // 1 px anti-aliased edge
}

/// Soft glow: a rounded square whose alpha fades out over `softness` px outside its edge. Put it
/// behind a panel (slightly larger) for a halo. 9-sliced with border = radius + softness.
inline NCMKObject* CreateGlowSprite(int size, float radius, float softness) {
    NCMK_REQUIRE_MAIN_THREAD(nullptr);
    if (softness < 1) softness = 1;
    if (size < static_cast<int>(2 * (radius + softness)) + 4) size = static_cast<int>(2 * (radius + softness)) + 4;
    return detail::SpriteFromAlpha(
        size, radius + softness,
        [](float d, float soft) {
            if (d <= 0) return 1.f;
            float t = 1.f - d / soft;
            return t <= 0 ? 0.f : t * t;  // quadratic falloff
        },
        softness, softness, radius);
}

}  // namespace UI
}  // namespace NCMK
