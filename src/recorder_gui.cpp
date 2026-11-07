// RSP Audio Recorder - Windows GUI front end.
//
// Borderless, custom-drawn window (GDI+, per-monitor DPI aware) that drives
// pedro-audio-recorder-core.exe through named events + a shared level buffer.
//
//   Play  (left)   : play back the last saved recording
//   Main  (centre) : record -> pause -> resume
//   Stop  (right)  : stop and save
//   Gear           : settings (file name, open after saving)
//
// Keyboard: Space = record / pause / resume, S = stop and save.

#ifndef UNICODE
#define UNICODE
#endif
#ifndef _UNICODE
#define _UNICODE
#endif
#ifndef NOMINMAX
#define NOMINMAX
#endif
#include <windows.h>
#include <windowsx.h>
#include <shellapi.h>
#include <dwmapi.h>
#include <algorithm>
#include <cmath>
#include <cstring>
#include <string>
#include <vector>
namespace Gdiplus { using std::max; using std::min; }
#include <objidl.h>
#include <gdiplus.h>

#include <commctrl.h>

#pragma comment(lib, "gdiplus.lib")
#pragma comment(lib, "dwmapi.lib")
#pragma comment(lib, "comctl32.lib")
#pragma comment(lib, "advapi32.lib")

using namespace Gdiplus;

// ---------------------------------------------------------------------------
// Design tokens
// ---------------------------------------------------------------------------
namespace ui {
const Color kCard(255, 247, 249, 252);
const Color kText(255, 23, 32, 51);
const Color kTextMuted(255, 96, 107, 125);
const Color kGlyph(255, 17, 26, 43);
const Color kGlyphOff(255, 184, 191, 202);
const Color kAccent(255, 10, 124, 255);
const Color kAccentHover(255, 40, 142, 255);
const Color kAccentDown(255, 0, 104, 222);
const Color kSoft(255, 236, 238, 242);
const Color kSoftHover(255, 226, 230, 236);
const Color kSoftDown(255, 214, 219, 227);
const Color kSoftOff(255, 242, 244, 247);
const Color kCaptionHover(255, 232, 235, 240);
const Color kClose(255, 232, 17, 35);
const Color kRecording(255, 229, 57, 53);
const Color kPaused(255, 245, 158, 11);
const Color kSaved(255, 22, 163, 74);
}  // namespace ui

enum class State { Idle, Recording, Paused };
enum Hit { HitNone, HitMin, HitMax, HitClose, HitPlay, HitMain, HitStop, HitGear };

// ---------------------------------------------------------------------------
// Application state
// ---------------------------------------------------------------------------
static HWND g_main{};
static HWND g_settings_win{};
static UINT g_dpi = 96;
static State g_state = State::Idle;
static Hit g_hover = HitNone;
static Hit g_pressed = HitNone;
static bool g_tracking_mouse = false;

static std::wstring g_filename;           // empty = timestamped name
static bool g_open_after_save = true;
static std::wstring g_output;             // file being recorded
static std::wstring g_last_saved;         // most recent finished file
static std::wstring g_message = L"Ready to record";
static Color g_message_dot = Color(0, 0, 0, 0);

static ULONGLONG g_segment_start = 0;     // tick when current recording segment began
static ULONGLONG g_accumulated_ms = 0;    // recorded time before current segment
static float g_glow = 0.f;                // smoothed loopback level for the halo

static HANDLE g_stop_event{}, g_pause_event{}, g_mapping{};
static float* g_levels{};
static PROCESS_INFORMATION g_child{};

static float S(float v) { return v * g_dpi / 96.f; }

// ---------------------------------------------------------------------------
// Background mode: hidden from taskbar/Alt+Tab, summoned by a global hotkey
// ---------------------------------------------------------------------------
static const wchar_t kWindowClass[] = L"RSPAudioRecorderWindow";
static const wchar_t kSettingsKey[] = L"Software\\RSP\\AudioRecorder";
static const wchar_t kRunKey[] = L"Software\\Microsoft\\Windows\\CurrentVersion\\Run";
static const wchar_t kRunValue[] = L"RSP Audio Recorder";
static const int kHotkeyId = 1;
// Hotkey stored in hotkey-control format: LOBYTE = virtual key, HIBYTE = HOTKEYF_* flags.
static const WORD kDefaultHotkey = MAKEWORD(VK_SPACE, HOTKEYF_CONTROL | HOTKEYF_SHIFT);
static WORD g_hotkey = kDefaultHotkey;
static UINT g_msg_show = 0;   // sent by a second launch: show the window
static UINT g_msg_quit = 0;   // sent by "--quit": exit the background instance

static DWORD reg_read_dword(const wchar_t* name, DWORD fallback) {
    DWORD v = 0, size = sizeof(v);
    if (RegGetValueW(HKEY_CURRENT_USER, kSettingsKey, name, RRF_RT_REG_DWORD, nullptr, &v, &size) == ERROR_SUCCESS) return v;
    return fallback;
}

static void reg_write_dword(const wchar_t* name, DWORD v) {
    RegSetKeyValueW(HKEY_CURRENT_USER, kSettingsKey, name, REG_DWORD, &v, sizeof(v));
}

static std::wstring exe_path() {
    wchar_t p[MAX_PATH]{};
    GetModuleFileNameW(nullptr, p, MAX_PATH);
    return p;
}

static bool startup_enabled() {
    return RegGetValueW(HKEY_CURRENT_USER, kRunKey, kRunValue, RRF_RT_REG_SZ, nullptr, nullptr, nullptr) == ERROR_SUCCESS;
}

static void set_startup(bool on) {
    if (on) {
        std::wstring cmd = L"\"" + exe_path() + L"\" --background";
        RegSetKeyValueW(HKEY_CURRENT_USER, kRunKey, kRunValue, REG_SZ, cmd.c_str(),
                        static_cast<DWORD>((cmd.size() + 1) * sizeof(wchar_t)));
    } else {
        HKEY k;
        if (RegOpenKeyExW(HKEY_CURRENT_USER, kRunKey, 0, KEY_SET_VALUE, &k) == ERROR_SUCCESS) {
            RegDeleteValueW(k, kRunValue);
            RegCloseKey(k);
        }
    }
}

static bool register_hotkey(HWND w, WORD hk) {
    UnregisterHotKey(w, kHotkeyId);
    if (!LOBYTE(hk)) return true;
    BYTE f = HIBYTE(hk);
    UINT mods = MOD_NOREPEAT;
    if (f & HOTKEYF_CONTROL) mods |= MOD_CONTROL;
    if (f & HOTKEYF_SHIFT) mods |= MOD_SHIFT;
    if (f & HOTKEYF_ALT) mods |= MOD_ALT;
    return RegisterHotKey(w, kHotkeyId, mods, LOBYTE(hk)) != FALSE;
}

static void show_main() {
    if (!g_main) return;
    if (g_settings_win) { SetForegroundWindow(g_settings_win); return; }
    ShowWindow(g_main, IsIconic(g_main) ? SW_RESTORE : SW_SHOW);
    SetForegroundWindow(g_main);
    SetFocus(g_main);
}

static void hide_main() {
    if (g_main) ShowWindow(g_main, SW_HIDE);
}

static void toggle_main() {
    if (g_settings_win) { SetForegroundWindow(g_settings_win); return; }
    if (IsWindowVisible(g_main) && GetForegroundWindow() == g_main) hide_main();
    else show_main();
}

// ---------------------------------------------------------------------------
// Recorder process control
// ---------------------------------------------------------------------------
static std::wstring core_path() {
    wchar_t p[MAX_PATH]{};
    GetModuleFileNameW(nullptr, p, MAX_PATH);
    std::wstring s(p);
    return s.substr(0, s.find_last_of(L"\\/") + 1) + L"pedro-audio-recorder-core.exe";
}

static std::wstring default_filename() {
    SYSTEMTIME now{};
    GetLocalTime(&now);
    wchar_t name[64]{};
    wsprintfW(name, L"audio-%04u%02u%02u-%02u%02u%02u.wav", now.wYear, now.wMonth, now.wDay,
              now.wHour, now.wMinute, now.wSecond);
    return name;
}

static std::wstring output_folder() {
    wchar_t profile[MAX_PATH]{};
    GetEnvironmentVariableW(L"USERPROFILE", profile, MAX_PATH);
    return std::wstring(profile) + L"\\Music\\Audio Recorder";
}

static std::wstring file_part(const std::wstring& path) {
    size_t p = path.find_last_of(L"\\/");
    return p == std::wstring::npos ? path : path.substr(p + 1);
}

static void set_message(const std::wstring& text, Color dot = Color(0, 0, 0, 0)) {
    g_message = text;
    g_message_dot = dot;
    if (g_main) InvalidateRect(g_main, nullptr, FALSE);
}

static void cleanup() {
    if (g_child.hProcess) {
        WaitForSingleObject(g_child.hProcess, 3000);
        if (WaitForSingleObject(g_child.hProcess, 0) != WAIT_OBJECT_0) TerminateProcess(g_child.hProcess, 1);
        CloseHandle(g_child.hThread);
        CloseHandle(g_child.hProcess);
        g_child = {};
    }
    if (g_stop_event) { CloseHandle(g_stop_event); g_stop_event = nullptr; }
    if (g_pause_event) { CloseHandle(g_pause_event); g_pause_event = nullptr; }
    if (g_levels) { UnmapViewOfFile(g_levels); g_levels = nullptr; }
    if (g_mapping) { CloseHandle(g_mapping); g_mapping = nullptr; }
}

static ULONGLONG elapsed_ms() {
    ULONGLONG t = g_accumulated_ms;
    if (g_state == State::Recording) t += GetTickCount64() - g_segment_start;
    return t;
}

static std::wstring format_elapsed(ULONGLONG ms) {
    unsigned s = static_cast<unsigned>(ms / 1000);
    wchar_t b[32]{};
    if (s >= 3600) wsprintfW(b, L"%u:%02u:%02u", s / 3600, (s / 60) % 60, s % 60);
    else wsprintfW(b, L"%02u:%02u", s / 60, s % 60);
    return b;
}

static void start_capture() {
    if (g_child.hProcess) return;
    std::wstring out = g_filename;
    if (out.empty() || out == L"(timestamp)") out = default_filename();
    if (out.find_first_of(L"\\/") == std::wstring::npos) out = output_folder() + L"\\" + out;
    if (file_part(out).find(L'.') == std::wstring::npos) out += L".wav";
    g_output = out;
    CreateDirectoryW(output_folder().c_str(), nullptr);

    std::wstring id = std::to_wstring(GetCurrentProcessId());
    std::wstring sn = L"Local\\RSPAudioStop_" + id, pn = L"Local\\RSPAudioPause_" + id,
                 mn = L"Local\\RSPAudioLevels_" + id;
    g_stop_event = CreateEventW(nullptr, TRUE, FALSE, sn.c_str());
    g_pause_event = CreateEventW(nullptr, TRUE, FALSE, pn.c_str());
    g_mapping = CreateFileMappingW(INVALID_HANDLE_VALUE, nullptr, PAGE_READWRITE, 0, sizeof(float) * 2, mn.c_str());
    g_levels = g_mapping ? static_cast<float*>(MapViewOfFile(g_mapping, FILE_MAP_ALL_ACCESS, 0, 0, sizeof(float) * 2)) : nullptr;
    if (!g_stop_event || !g_pause_event || !g_levels) {
        set_message(L"Control setup failed", ui::kRecording);
        cleanup();
        return;
    }
    g_levels[0] = g_levels[1] = 0;

    std::wstring cmd = L"\"" + core_path() + L"\" \"" + out + L"\" --stop-event \"" + sn +
                       L"\" --pause-event \"" + pn + L"\" --control-id \"" + id + L"\"";
    std::vector<wchar_t> v(cmd.begin(), cmd.end());
    v.push_back(0);
    STARTUPINFOW si{};
    si.cb = sizeof(si);
    if (!CreateProcessW(nullptr, v.data(), nullptr, nullptr, FALSE, CREATE_NO_WINDOW, nullptr, nullptr, &si, &g_child)) {
        set_message(L"Recorder start failed", ui::kRecording);
        cleanup();
        return;
    }
    g_state = State::Recording;
    g_accumulated_ms = 0;
    g_segment_start = GetTickCount64();
    set_message(L"Recording", ui::kRecording);
}

static void toggle_pause() {
    if (!g_child.hProcess) return;
    if (g_state == State::Recording) {
        g_accumulated_ms += GetTickCount64() - g_segment_start;
        g_state = State::Paused;
        SetEvent(g_pause_event);
        set_message(L"Paused", ui::kPaused);
    } else if (g_state == State::Paused) {
        g_segment_start = GetTickCount64();
        g_state = State::Recording;
        ResetEvent(g_pause_event);
        set_message(L"Recording", ui::kRecording);
    }
}

static void stop_capture() {
    if (!g_child.hProcess) return;
    SetEvent(g_stop_event);
    cleanup();
    g_state = State::Idle;
    g_accumulated_ms = 0;
    g_last_saved = g_output;
    set_message(L"Saved  \u00B7  " + file_part(g_output), ui::kSaved);
    if (g_open_after_save && !g_output.empty())
        ShellExecuteW(g_main, L"open", g_output.c_str(), nullptr, nullptr, SW_SHOWNORMAL);
}

static void play_last() {
    if (g_state == State::Idle && !g_last_saved.empty())
        ShellExecuteW(g_main, L"open", g_last_saved.c_str(), nullptr, nullptr, SW_SHOWNORMAL);
}

static void main_action() {
    if (g_state == State::Idle) start_capture();
    else toggle_pause();
}

// ---------------------------------------------------------------------------
// Layout
// ---------------------------------------------------------------------------
struct Layout {
    float w, h, title_h;
    RectF cap_min, cap_max, cap_close;
    PointF icon;  // top-left of app icon
    float cx, cy, side_offset, main_r, side_r;
    PointF gear;
    float gear_r;
    float status_y;
};

static Layout layout_for(HWND w) {
    RECT rc;
    GetClientRect(w, &rc);
    Layout L{};
    L.w = static_cast<float>(rc.right);
    L.h = static_cast<float>(rc.bottom);
    L.title_h = S(58);
    const float cw = S(42), ch = S(34), top = S(12), gap = S(4);
    L.cap_close = RectF(L.w - S(14) - cw, top, cw, ch);
    L.cap_max = RectF(L.cap_close.X - gap - cw, top, cw, ch);
    L.cap_min = RectF(L.cap_max.X - gap - cw, top, cw, ch);
    L.icon = PointF(S(20), S(14));
    L.main_r = S(50);
    L.side_r = S(38);
    L.side_offset = S(132);
    L.cx = L.w / 2;
    L.cy = L.title_h + (L.h - L.title_h - S(44)) / 2;
    L.gear = PointF(L.w - S(54), L.cy);
    L.gear_r = S(24);
    L.status_y = L.cy + L.main_r + S(30);
    return L;
}

static bool in_circle(float x, float y, float cx, float cy, float r) {
    return (x - cx) * (x - cx) + (y - cy) * (y - cy) <= r * r;
}

static Hit hit_test(HWND w, int px, int py) {
    Layout L = layout_for(w);
    float x = static_cast<float>(px), y = static_cast<float>(py);
    if (L.cap_close.Contains(x, y)) return HitClose;
    if (L.cap_max.Contains(x, y)) return HitMax;
    if (L.cap_min.Contains(x, y)) return HitMin;
    if (in_circle(x, y, L.cx, L.cy, L.main_r)) return HitMain;
    if (in_circle(x, y, L.cx - L.side_offset, L.cy, L.side_r)) return HitPlay;
    if (in_circle(x, y, L.cx + L.side_offset, L.cy, L.side_r)) return HitStop;
    if (in_circle(x, y, L.gear.X, L.gear.Y, L.gear_r)) return HitGear;
    return HitNone;
}

static bool hit_enabled(Hit h) {
    switch (h) {
    case HitPlay: return g_state == State::Idle && !g_last_saved.empty();
    case HitStop: return g_state != State::Idle;
    case HitNone: return false;
    default: return true;
    }
}

// ---------------------------------------------------------------------------
// Drawing
// ---------------------------------------------------------------------------
static void add_round_rect(GraphicsPath& p, REAL x, REAL y, REAL w, REAL h, REAL r) {
    REAL d = r * 2;
    p.AddArc(x, y, d, d, 180, 90);
    p.AddArc(x + w - d, y, d, d, 270, 90);
    p.AddArc(x + w - d, y + h - d, d, d, 0, 90);
    p.AddArc(x, y + h - d, d, d, 90, 90);
    p.CloseFigure();
}

static void fill_round_rect(Graphics& g, const Color& c, REAL x, REAL y, REAL w, REAL h, REAL r) {
    GraphicsPath p;
    add_round_rect(p, x, y, w, h, r);
    SolidBrush b(c);
    g.FillPath(&b, &p);
}

static void fill_circle(Graphics& g, const Color& c, REAL cx, REAL cy, REAL r) {
    SolidBrush b(c);
    g.FillEllipse(&b, cx - r, cy - r, r * 2, r * 2);
}

static void draw_play_glyph(Graphics& g, const Color& c, REAL cx, REAL cy, REAL size) {
    // Rounded triangle, nudged right for optical centring.
    REAL hh = size / 2, ww = size * 0.88f, ox = cx - ww / 2 + size * 0.08f;
    PointF pts[] = {PointF(ox, cy - hh), PointF(ox + ww, cy), PointF(ox, cy + hh)};
    SolidBrush b(c);
    Pen p(c, size * 0.14f);
    p.SetLineJoin(LineJoinRound);
    g.FillPolygon(&b, pts, 3);
    g.DrawPolygon(&p, pts, 3);
}

static void draw_pause_glyph(Graphics& g, const Color& c, REAL cx, REAL cy, REAL size) {
    REAL bw = size * 0.3f, gap = size * 0.3f;
    fill_round_rect(g, c, cx - gap / 2 - bw, cy - size / 2, bw, size, bw / 2);
    fill_round_rect(g, c, cx + gap / 2, cy - size / 2, bw, size, bw / 2);
}

static void draw_gear(Graphics& g, const Color& c, REAL cx, REAL cy, REAL R) {
    const REAL r_in = R * 0.74f, pi = 3.14159265f;
    std::vector<PointF> pts;
    for (int i = 0; i < 8; ++i) {
        REAL a = i * pi / 4;
        auto at = [&](REAL rad, REAL deg) {
            REAL t = a + deg * pi / 180;
            return PointF(cx + rad * std::cos(t), cy + rad * std::sin(t));
        };
        pts.push_back(at(r_in, -14));
        pts.push_back(at(R, -9));
        pts.push_back(at(R, 9));
        pts.push_back(at(r_in, 14));
        pts.push_back(at(r_in, 22.5f));
    }
    Pen p(c, S(2.3f));
    p.SetLineJoin(LineJoinRound);
    g.DrawPolygon(&p, pts.data(), static_cast<INT>(pts.size()));
    REAL hub = R * 0.32f;
    g.DrawEllipse(&p, cx - hub, cy - hub, hub * 2, hub * 2);
}

static void draw_app_icon(Graphics& g, REAL x, REAL y, REAL size) {
    fill_round_rect(g, ui::kAccent, x, y, size, size, size * 0.24f);
    Pen p(Color(255, 255, 255, 255), size * 0.075f);
    p.SetLineCap(LineCapRound, LineCapRound, DashCapRound);
    const REAL heights[] = {0.22f, 0.42f, 0.6f, 0.42f, 0.22f};
    REAL step = size * 0.14f, cx = x + size / 2, cy = y + size / 2;
    for (int i = 0; i < 5; ++i) {
        REAL bx = cx + (i - 2) * step, hh = size * heights[i] / 2;
        g.DrawLine(&p, bx, cy - hh, bx, cy + hh);
    }
}

static void draw_caption_button(Graphics& g, Hit which, const RectF& r) {
    bool hover = g_hover == which, down = g_pressed == which && hover;
    Color glyph = ui::kGlyph;
    if (hover) {
        Color bg = which == HitClose ? ui::kClose : (down ? ui::kSoftDown : ui::kCaptionHover);
        fill_round_rect(g, bg, r.X, r.Y, r.Width, r.Height, S(8));
        if (which == HitClose) glyph = Color(255, 255, 255, 255);
    }
    Pen p(glyph, S(1.6f));
    p.SetLineCap(LineCapRound, LineCapRound, DashCapRound);
    REAL cx = r.X + r.Width / 2, cy = r.Y + r.Height / 2, k = S(6.5f);
    if (which == HitMin) {
        g.DrawLine(&p, cx - k, cy, cx + k, cy);
    } else if (which == HitMax) {
        GraphicsPath path;
        if (IsZoomed(g_main)) {
            REAL s = k * 1.6f, o = S(2.5f);
            add_round_rect(path, cx - k, cy - k + o, s, s, S(2.5f));
            g.DrawPath(&p, &path);
            g.DrawLine(&p, cx - k + o, cy - k, cx + k - S(1.5f), cy - k);
            g.DrawLine(&p, cx + k, cy - k + S(1.5f), cx + k, cy + k - o);
        } else {
            add_round_rect(path, cx - k, cy - k, k * 2, k * 2, S(3));
            g.DrawPath(&p, &path);
        }
    } else {
        g.DrawLine(&p, cx - k, cy - k, cx + k, cy + k);
        g.DrawLine(&p, cx + k, cy - k, cx - k, cy + k);
    }
}

static void draw_side_button(Graphics& g, Hit which, REAL cx, REAL cy, REAL r) {
    bool enabled = hit_enabled(which);
    bool hover = enabled && g_hover == which, down = hover && g_pressed == which;
    Color bg = !enabled ? ui::kSoftOff : down ? ui::kSoftDown : hover ? ui::kSoftHover : ui::kSoft;
    fill_circle(g, bg, cx, cy, r);
    Color glyph = enabled ? ui::kGlyph : ui::kGlyphOff;
    if (which == HitPlay) {
        draw_play_glyph(g, glyph, cx, cy, r * 0.74f);
    } else {
        REAL s = r * 0.74f;
        fill_round_rect(g, glyph, cx - s / 2, cy - s / 2, s, s, S(5));
    }
}

static void draw_main_button(Graphics& g, const Layout& L) {
    bool hover = g_hover == HitMain, down = hover && g_pressed == HitMain;
    REAL cx = L.cx, cy = L.cy, r = L.main_r;

    // Soft blue glow below the button; breathes with the input level while recording.
    float level = g_state == State::Recording ? std::min(1.f, g_glow * 2.5f) : 0.f;
    int alpha = static_cast<int>(95 + 90 * level);
    REAL gr = r + S(26) + S(6) * level, oy = S(8);
    GraphicsPath glow;
    glow.AddEllipse(cx - gr, cy - gr + oy, gr * 2, gr * 2);
    PathGradientBrush pb(&glow);
    pb.SetCenterPoint(PointF(cx, cy + oy));
    pb.SetCenterColor(Color(static_cast<BYTE>(alpha), 10, 124, 255));
    Color edge(0, 10, 124, 255);
    INT n = 1;
    pb.SetSurroundColors(&edge, &n);
    pb.SetFocusScales(0.62f, 0.62f);
    g.FillPath(&pb, &glow);

    // Halo ring.
    REAL hr = r + S(10);
    fill_circle(g, Color(255, 252, 253, 255), cx, cy, hr);
    Pen ring(Color(22, 15, 40, 90), S(1));
    g.DrawEllipse(&ring, cx - hr, cy - hr, hr * 2, hr * 2);

    fill_circle(g, down ? ui::kAccentDown : hover ? ui::kAccentHover : ui::kAccent, cx, cy, r);

    Color white(255, 255, 255, 255);
    if (g_state == State::Idle) fill_circle(g, white, cx, cy, r * 0.3f);
    else if (g_state == State::Recording) draw_pause_glyph(g, white, cx, cy, r * 0.62f);
    else draw_play_glyph(g, white, cx, cy, r * 0.56f);
}

static void draw_status(Graphics& g, const Layout& L) {
    std::wstring text = g_message;
    if (g_state != State::Idle) text += L"  \u00B7  " + format_elapsed(elapsed_ms());

    FontFamily fam(L"Segoe UI");
    Font font(&fam, S(13), FontStyleRegular, UnitPixel);
    StringFormat fmt(StringFormat::GenericTypographic());
    fmt.SetFormatFlags(fmt.GetFormatFlags() | StringFormatFlagsMeasureTrailingSpaces);
    RectF bounds;
    g.MeasureString(text.c_str(), static_cast<INT>(text.size()), &font, PointF(0, 0), &fmt, &bounds);

    bool dot = g_message_dot.GetA() != 0;
    REAL dot_r = S(3.5f), gap = S(8);
    REAL total = bounds.Width + (dot ? dot_r * 2 + gap : 0);
    REAL x = std::max(S(16), L.cx - total / 2);
    if (dot) {
        // Blink the dot while recording.
        bool show = g_state != State::Recording || (GetTickCount64() / 600) % 2 == 0;
        if (show) fill_circle(g, g_message_dot, x + dot_r, L.status_y, dot_r);
        x += dot_r * 2 + gap;
    }
    SolidBrush tb(ui::kTextMuted);
    g.DrawString(text.c_str(), static_cast<INT>(text.size()), &font,
                 PointF(x, L.status_y - bounds.Height / 2), &fmt, &tb);
}

static void paint(HWND w, HDC hdc) {
    RECT rc;
    GetClientRect(w, &rc);
    int cw = rc.right, ch = rc.bottom;
    if (cw <= 0 || ch <= 0) return;

    // Render into a 32-bpp DIB with opaque alpha so DWM's extended frame
    // never shows through, then blit in one go (no flicker).
    BITMAPINFO bi{};
    bi.bmiHeader.biSize = sizeof(BITMAPINFOHEADER);
    bi.bmiHeader.biWidth = cw;
    bi.bmiHeader.biHeight = -ch;
    bi.bmiHeader.biPlanes = 1;
    bi.bmiHeader.biBitCount = 32;
    bi.bmiHeader.biCompression = BI_RGB;
    void* bits = nullptr;
    HBITMAP dib = CreateDIBSection(hdc, &bi, DIB_RGB_COLORS, &bits, nullptr, 0);
    if (!dib) return;
    HDC mem = CreateCompatibleDC(hdc);
    HGDIOBJ old = SelectObject(mem, dib);
    {
        Bitmap canvas(cw, ch, cw * 4, PixelFormat32bppPARGB, static_cast<BYTE*>(bits));
        Graphics g(&canvas);
        g.SetSmoothingMode(SmoothingModeAntiAlias);
        g.SetPixelOffsetMode(PixelOffsetModeHalf);
        g.SetTextRenderingHint(TextRenderingHintAntiAliasGridFit);
        g.Clear(ui::kCard);

        Layout L = layout_for(w);

        // Title bar.
        REAL icon = S(30);
        draw_app_icon(g, L.icon.X, L.icon.Y, icon);
        FontFamily fam(L"Segoe UI Semibold");
        Font title(&fam, S(17), FontStyleRegular, UnitPixel);
        StringFormat fmt;
        fmt.SetLineAlignment(StringAlignmentCenter);
        SolidBrush tb(ui::kText);
        g.DrawString(L"RSP Audio Recorder", -1, &title,
                     RectF(L.icon.X + icon + S(13), L.icon.Y, S(300), icon), &fmt, &tb);
        draw_caption_button(g, HitMin, L.cap_min);
        draw_caption_button(g, HitMax, L.cap_max);
        draw_caption_button(g, HitClose, L.cap_close);

        // Transport.
        draw_side_button(g, HitPlay, L.cx - L.side_offset, L.cy, L.side_r);
        draw_side_button(g, HitStop, L.cx + L.side_offset, L.cy, L.side_r);
        draw_main_button(g, L);

        // Settings.
        if (g_hover == HitGear)
            fill_circle(g, g_pressed == HitGear ? ui::kSoftDown : ui::kCaptionHover, L.gear.X, L.gear.Y, L.gear_r);
        draw_gear(g, ui::kGlyph, L.gear.X, L.gear.Y, S(15));

        draw_status(g, L);
    }
    BitBlt(hdc, 0, 0, cw, ch, mem, 0, 0, SRCCOPY);
    SelectObject(mem, old);
    DeleteObject(dib);
    DeleteDC(mem);
}

// ---------------------------------------------------------------------------
// Settings window
// ---------------------------------------------------------------------------
enum { IdName = 101, IdOpenAfter = 102, IdFolder = 103, IdHint = 104, IdHotkey = 105, IdStartup = 106,
       IdQuit = 107, IdHotkeyHint = 108 };
static HFONT g_settings_font{};
static HBRUSH g_white_brush{};

static void close_settings() {
    if (!g_settings_win) return;
    EnableWindow(g_main, TRUE);
    register_hotkey(g_main, g_hotkey);  // suspended while the shortcut picker is open
    SetForegroundWindow(g_main);
    DestroyWindow(g_settings_win);
    g_settings_win = nullptr;
    if (g_settings_font) { DeleteObject(g_settings_font); g_settings_font = nullptr; }
}

static LRESULT CALLBACK settings_proc(HWND w, UINT m, WPARAM wp, LPARAM lp) {
    switch (m) {
    case WM_COMMAND:
        switch (LOWORD(wp)) {
        case IDOK: {
            wchar_t b[512]{};
            GetWindowTextW(GetDlgItem(w, IdName), b, 512);
            g_filename = b;
            WORD hk = static_cast<WORD>(SendDlgItemMessageW(w, IdHotkey, HKM_GETHOTKEY, 0, 0));
            if (!LOBYTE(hk)) hk = g_hotkey;  // empty picker keeps the current shortcut
            if (!register_hotkey(g_main, hk)) {
                MessageBoxW(w, L"That shortcut is already used by another app. Choose a different one.",
                            L"Keyboard shortcut", MB_OK | MB_ICONWARNING);
                SetFocus(GetDlgItem(w, IdHotkey));
                return 0;
            }
            UnregisterHotKey(g_main, kHotkeyId);  // close_settings() registers it again
            g_hotkey = hk;
            reg_write_dword(L"Hotkey", hk);
            g_open_after_save = SendDlgItemMessageW(w, IdOpenAfter, BM_GETCHECK, 0, 0) == BST_CHECKED;
            set_startup(SendDlgItemMessageW(w, IdStartup, BM_GETCHECK, 0, 0) == BST_CHECKED);
            close_settings();
            return 0;
        }
        case IdQuit: {
            if (g_state != State::Idle &&
                MessageBoxW(w, L"A recording is in progress. Stop and save it, then quit?", L"Quit RSP Audio Recorder",
                            MB_OKCANCEL | MB_ICONQUESTION) != IDOK)
                return 0;
            close_settings();
            DestroyWindow(g_main);
            return 0;
        }
        case IDCANCEL:
            close_settings();
            return 0;
        case IdFolder: {
            std::wstring f = output_folder();
            CreateDirectoryW(f.c_str(), nullptr);
            ShellExecuteW(w, L"open", f.c_str(), nullptr, nullptr, SW_SHOWNORMAL);
            return 0;
        }
        }
        break;
    case WM_CTLCOLORSTATIC: {
        HDC dc = reinterpret_cast<HDC>(wp);
        SetBkColor(dc, RGB(255, 255, 255));
        int cid = GetDlgCtrlID(reinterpret_cast<HWND>(lp));
        bool hint = cid == IdHint || cid == IdHotkeyHint;
        SetTextColor(dc, hint ? RGB(96, 107, 125) : RGB(23, 32, 51));
        return reinterpret_cast<LRESULT>(g_white_brush);
    }
    case WM_CLOSE:
        close_settings();
        return 0;
    }
    return DefWindowProcW(w, m, wp, lp);
}

static void open_settings() {
    if (g_settings_win) { SetForegroundWindow(g_settings_win); return; }
    // Let the shortcut picker receive the current hotkey instead of it toggling the window.
    UnregisterHotKey(g_main, kHotkeyId);
    static bool registered = false;
    HINSTANCE inst = GetModuleHandleW(nullptr);
    if (!registered) {
        g_white_brush = CreateSolidBrush(RGB(255, 255, 255));
        WNDCLASSW c{};
        c.hInstance = inst;
        c.lpfnWndProc = settings_proc;
        c.lpszClassName = L"RSPRecorderSettings";
        c.hCursor = LoadCursorW(nullptr, IDC_ARROW);
        c.hbrBackground = g_white_brush;
        c.hIcon = LoadIconW(inst, L"IDI_APPICON");
        RegisterClassW(&c);
        registered = true;
    }
    auto px = [](int v) { return MulDiv(v, static_cast<int>(g_dpi), 96); };

    NONCLIENTMETRICSW ncm{};
    ncm.cbSize = sizeof(ncm);
    SystemParametersInfoForDpi(SPI_GETNONCLIENTMETRICS, sizeof(ncm), &ncm, 0, g_dpi);
    g_settings_font = CreateFontIndirectW(&ncm.lfMessageFont);

    const DWORD style = WS_POPUP | WS_CAPTION | WS_SYSMENU, ex = WS_EX_DLGMODALFRAME;
    RECT r{0, 0, px(420), px(338)};
    AdjustWindowRectExForDpi(&r, style, FALSE, ex, g_dpi);
    int ww = r.right - r.left, wh = r.bottom - r.top;
    RECT mr;
    GetWindowRect(g_main, &mr);
    int x = (mr.left + mr.right - ww) / 2, y = (mr.top + mr.bottom - wh) / 2;

    g_settings_win = CreateWindowExW(ex, L"RSPRecorderSettings", L"Settings", style, x, y, ww, wh,
                                     g_main, nullptr, inst, nullptr);
    BOOL dark = FALSE;
    DwmSetWindowAttribute(g_settings_win, 20 /*DWMWA_USE_IMMERSIVE_DARK_MODE*/, &dark, sizeof(dark));

    auto make = [&](DWORD ex_style, const wchar_t* cls, const wchar_t* text, DWORD st, int x0, int y0, int w0, int h0, int id) {
        HWND c = CreateWindowExW(ex_style, cls, text, WS_CHILD | WS_VISIBLE | st, px(x0), px(y0), px(w0), px(h0),
                                 g_settings_win, reinterpret_cast<HMENU>(static_cast<INT_PTR>(id)), inst, nullptr);
        SendMessageW(c, WM_SETFONT, reinterpret_cast<WPARAM>(g_settings_font), TRUE);
        return c;
    };
    make(0, L"STATIC", L"File name", 0, 24, 20, 372, 20, 0);
    HWND edit = make(WS_EX_CLIENTEDGE, L"EDIT", g_filename.c_str(), WS_TABSTOP | ES_AUTOHSCROLL, 24, 42, 372, 28, IdName);
    SendMessageW(edit, EM_SETCUEBANNER, TRUE, reinterpret_cast<LPARAM>(L"Automatic (date and time)"));
    make(0, L"STATIC", L"Recordings are saved in Music\\Audio Recorder.", 0,
         24, 76, 372, 20, IdHint);
    HWND check = make(0, L"BUTTON", L"Open audio file after saving", WS_TABSTOP | BS_AUTOCHECKBOX,
                      24, 104, 372, 24, IdOpenAfter);
    SendMessageW(check, BM_SETCHECK, g_open_after_save ? BST_CHECKED : BST_UNCHECKED, 0);

    make(0, L"STATIC", L"Show / hide shortcut", 0, 24, 144, 372, 20, 0);
    HWND hot = make(WS_EX_CLIENTEDGE, HOTKEY_CLASSW, L"", WS_TABSTOP, 24, 166, 220, 28, IdHotkey);
    SendMessageW(hot, HKM_SETRULES, HKCOMB_NONE | HKCOMB_S, MAKELPARAM(HOTKEYF_CONTROL | HOTKEYF_SHIFT, 0));
    SendMessageW(hot, HKM_SETHOTKEY, g_hotkey, 0);
    make(0, L"STATIC", L"Click the box and press a new key combination.", 0, 24, 200, 372, 20, IdHotkeyHint);
    HWND startup = make(0, L"BUTTON", L"Start with Windows (runs in the background)", WS_TABSTOP | BS_AUTOCHECKBOX,
                        24, 230, 372, 24, IdStartup);
    SendMessageW(startup, BM_SETCHECK, startup_enabled() ? BST_CHECKED : BST_UNCHECKED, 0);

    make(0, L"BUTTON", L"Open folder", WS_TABSTOP, 24, 284, 96, 30, IdFolder);
    make(0, L"BUTTON", L"Quit", WS_TABSTOP, 128, 284, 72, 30, IdQuit);
    make(0, L"BUTTON", L"Save", WS_TABSTOP | BS_DEFPUSHBUTTON, 220, 284, 84, 30, IDOK);
    make(0, L"BUTTON", L"Cancel", WS_TABSTOP, 312, 284, 84, 30, IDCANCEL);

    EnableWindow(g_main, FALSE);
    ShowWindow(g_settings_win, SW_SHOW);
    SetFocus(edit);
    SendMessageW(edit, EM_SETSEL, 0, -1);
}

// ---------------------------------------------------------------------------
// Main window
// ---------------------------------------------------------------------------
static void activate(Hit h) {
    if (!hit_enabled(h)) return;
    switch (h) {
    case HitMin: hide_main(); break;  // no taskbar button to minimize to
    case HitMax: ShowWindow(g_main, IsZoomed(g_main) ? SW_RESTORE : SW_MAXIMIZE); break;
    case HitClose: PostMessageW(g_main, WM_CLOSE, 0, 0); break;
    case HitPlay: play_last(); break;
    case HitMain: main_action(); break;
    case HitStop: stop_capture(); break;
    case HitGear: open_settings(); break;
    default: break;
    }
    InvalidateRect(g_main, nullptr, FALSE);
}

static void set_hover(Hit h) {
    if (h == g_hover) return;
    g_hover = h;
    SetCursor(LoadCursorW(nullptr, hit_enabled(h) && h >= HitPlay ? IDC_HAND : IDC_ARROW));
    InvalidateRect(g_main, nullptr, FALSE);
}

static LRESULT CALLBACK proc(HWND w, UINT m, WPARAM wp, LPARAM lp) {
    switch (m) {
    case WM_NCCALCSIZE:
        if (wp) {
            // Remove the standard frame; keep the DWM shadow and rounded corners.
            if (IsZoomed(w)) {
                auto* p = reinterpret_cast<NCCALCSIZE_PARAMS*>(lp);
                UINT dpi = GetDpiForWindow(w);
                int fx = GetSystemMetricsForDpi(SM_CXFRAME, dpi) + GetSystemMetricsForDpi(SM_CXPADDEDBORDER, dpi);
                int fy = GetSystemMetricsForDpi(SM_CYFRAME, dpi) + GetSystemMetricsForDpi(SM_CXPADDEDBORDER, dpi);
                p->rgrc[0].left += fx;
                p->rgrc[0].right -= fx;
                p->rgrc[0].top += fy;
                p->rgrc[0].bottom -= fy;
            }
            return 0;
        }
        break;
    case WM_NCACTIVATE:
        return DefWindowProcW(w, m, wp, -1);
    case WM_NCHITTEST: {
        POINT pt{GET_X_LPARAM(lp), GET_Y_LPARAM(lp)};
        ScreenToClient(w, &pt);
        Layout L = layout_for(w);
        if (pt.y < L.title_h && hit_test(w, pt.x, pt.y) == HitNone) return HTCAPTION;
        return HTCLIENT;
    }
    case WM_SETCURSOR:
        if (LOWORD(lp) == HTCLIENT) {
            SetCursor(LoadCursorW(nullptr, hit_enabled(g_hover) && g_hover >= HitPlay ? IDC_HAND : IDC_ARROW));
            return TRUE;
        }
        break;
    case WM_MOUSEMOVE:
        if (!g_tracking_mouse) {
            TRACKMOUSEEVENT t{sizeof(t), TME_LEAVE, w, 0};
            TrackMouseEvent(&t);
            g_tracking_mouse = true;
        }
        set_hover(hit_test(w, GET_X_LPARAM(lp), GET_Y_LPARAM(lp)));
        return 0;
    case WM_MOUSELEAVE:
        g_tracking_mouse = false;
        set_hover(HitNone);
        return 0;
    case WM_LBUTTONDOWN:
        g_pressed = hit_test(w, GET_X_LPARAM(lp), GET_Y_LPARAM(lp));
        if (g_pressed != HitNone) SetCapture(w);
        InvalidateRect(w, nullptr, FALSE);
        return 0;
    case WM_LBUTTONUP: {
        Hit up = hit_test(w, GET_X_LPARAM(lp), GET_Y_LPARAM(lp));
        Hit down = g_pressed;
        g_pressed = HitNone;
        ReleaseCapture();
        if (up == down) activate(up);
        InvalidateRect(w, nullptr, FALSE);
        return 0;
    }
    case WM_KEYDOWN:
        if (wp == VK_SPACE) activate(HitMain);
        else if (wp == 'S') activate(HitStop);
        else if (wp == VK_ESCAPE) hide_main();
        return 0;
    case WM_HOTKEY:
        if (wp == kHotkeyId) toggle_main();
        return 0;
    case WM_CLOSE:
        // Close (button, Alt+F4) only hides the interface; the app keeps running
        // in the background, listening for the shortcut. Quit lives in Settings.
        hide_main();
        return 0;
    case WM_QUERYENDSESSION:
        return TRUE;
    case WM_ENDSESSION:
        if (wp && g_child.hProcess) { SetEvent(g_stop_event); cleanup(); }  // save on sign-out/shutdown
        return 0;
    case WM_TIMER: {
        if (g_child.hProcess && WaitForSingleObject(g_child.hProcess, 0) == WAIT_OBJECT_0) {
            DWORD code = 0;
            GetExitCodeProcess(g_child.hProcess, &code);
            std::wstring out = g_output;
            cleanup();
            g_state = State::Idle;
            g_accumulated_ms = 0;
            if (code == 0) {
                g_last_saved = out;
                set_message(L"Saved  \u00B7  " + file_part(out), ui::kSaved);
            } else {
                set_message(L"Recording stopped unexpectedly", ui::kRecording);
            }
        }
        float level = g_levels ? std::max(g_levels[0], g_levels[1]) : 0.f;
        g_glow = std::max(level, g_glow * 0.86f);
        if (g_state != State::Idle) InvalidateRect(w, nullptr, FALSE);
        return 0;
    }
    case WM_SIZE:
        InvalidateRect(w, nullptr, FALSE);
        return 0;
    case WM_DPICHANGED: {
        g_dpi = HIWORD(wp);
        auto* r = reinterpret_cast<RECT*>(lp);
        SetWindowPos(w, nullptr, r->left, r->top, r->right - r->left, r->bottom - r->top,
                     SWP_NOZORDER | SWP_NOACTIVATE);
        return 0;
    }
    case WM_ERASEBKGND:
        return 1;
    case WM_PAINT: {
        PAINTSTRUCT ps;
        HDC h = BeginPaint(w, &ps);
        paint(w, h);
        EndPaint(w, &ps);
        return 0;
    }
    case WM_DESTROY:
        if (g_child.hProcess) SetEvent(g_stop_event);
        cleanup();
        UnregisterHotKey(w, kHotkeyId);
        PostQuitMessage(0);
        return 0;
    }
    if (m && m == g_msg_show) { show_main(); return 0; }
    if (m && m == g_msg_quit) { if (g_settings_win) close_settings(); DestroyWindow(w); return 0; }
    return DefWindowProcW(w, m, wp, lp);
}

int WINAPI wWinMain(HINSTANCE h, HINSTANCE, PWSTR cmdline, int) {
    SetProcessDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);

    // Command line:  --background  start hidden (used by Start with Windows)
    //                --quit        close the running background instance
    std::wstring args = cmdline ? cmdline : L"";
    const bool start_hidden = args.find(L"--background") != std::wstring::npos;
    const bool quit_request = args.find(L"--quit") != std::wstring::npos;
    g_msg_show = RegisterWindowMessageW(L"RSPAudioRecorder.Show");
    g_msg_quit = RegisterWindowMessageW(L"RSPAudioRecorder.Quit");

    // Single instance: a second launch shows (or quits) the one already running.
    HANDLE instance_mutex = CreateMutexW(nullptr, TRUE, L"Local\\RSPAudioRecorder.Instance");
    if (GetLastError() == ERROR_ALREADY_EXISTS) {
        HWND existing = nullptr;
        for (int i = 0; i < 20 && !existing; ++i) {
            existing = FindWindowW(kWindowClass, nullptr);
            if (!existing) Sleep(100);
        }
        if (existing) {
            DWORD pid = 0;
            GetWindowThreadProcessId(existing, &pid);
            if (quit_request) {
                HANDLE proc_handle = OpenProcess(SYNCHRONIZE, FALSE, pid);
                PostMessageW(existing, g_msg_quit, 0, 0);
                if (proc_handle) { WaitForSingleObject(proc_handle, 10000); CloseHandle(proc_handle); }
            } else {
                AllowSetForegroundWindow(pid);
                PostMessageW(existing, g_msg_show, 0, 0);
            }
        }
        if (instance_mutex) CloseHandle(instance_mutex);
        return 0;
    }
    if (quit_request) {
        if (instance_mutex) CloseHandle(instance_mutex);
        return 0;
    }

    INITCOMMONCONTROLSEX icc{sizeof(icc), ICC_HOTKEY_CLASS | ICC_STANDARD_CLASSES};
    InitCommonControlsEx(&icc);

    HANDLE trace = CreateFileW(L"C:\\Users\\Pedro Sanhueza\\pedro-audio-recorder\\gui-trace.txt", FILE_APPEND_DATA,
                               FILE_SHARE_READ, nullptr, OPEN_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr);
    auto mark = [&](const char* s) {
        if (trace != INVALID_HANDLE_VALUE) { DWORD n = 0; WriteFile(trace, s, static_cast<DWORD>(strlen(s)), &n, nullptr); }
    };
    mark("entered\n");

    GdiplusStartupInput gdi_in;
    ULONG_PTR gdi_token = 0;
    GdiplusStartup(&gdi_token, &gdi_in, nullptr);

    const wchar_t* cls = kWindowClass;
    WNDCLASSW wc{};
    wc.hInstance = h;
    wc.lpfnWndProc = proc;
    wc.lpszClassName = cls;
    wc.hCursor = LoadCursorW(nullptr, IDC_ARROW);
    wc.hIcon = LoadIconW(h, L"IDI_APPICON");
    ATOM atom = RegisterClassW(&wc);
    mark(atom ? "class_ok\n" : "class_fail\n");
    if (!atom && GetLastError() != ERROR_CLASS_ALREADY_EXISTS) {
        if (trace != INVALID_HANDLE_VALUE) CloseHandle(trace);
        return 2;
    }

    g_dpi = GetDpiForSystem();
    int ww = MulDiv(580, g_dpi, 96), hh = MulDiv(262, g_dpi, 96);
    RECT work{};
    SystemParametersInfoW(SPI_GETWORKAREA, 0, &work, 0);
    const DWORD style = WS_POPUP | WS_THICKFRAME | WS_CAPTION | WS_SYSMENU | WS_MINIMIZEBOX | WS_MAXIMIZEBOX;
    // WS_EX_TOOLWINDOW keeps the window out of the taskbar, Alt+Tab and Task View.
    HWND win = CreateWindowExW(WS_EX_TOOLWINDOW, cls, L"RSP Audio Recorder", style,
                               work.left + (work.right - work.left - ww) / 2, work.top + (work.bottom - work.top - hh) / 2,
                               ww, hh, nullptr, nullptr, h, nullptr);
    mark(win ? "window_ok\n" : "window_fail\n");
    if (!win) {
        if (trace != INVALID_HANDLE_VALUE) CloseHandle(trace);
        return 3;
    }
    g_main = win;

    // Window landed on a monitor with a different DPI than the primary one.
    UINT actual = GetDpiForWindow(win);
    if (actual != g_dpi) {
        g_dpi = actual;
        SetWindowPos(win, nullptr, 0, 0, MulDiv(580, g_dpi, 96), MulDiv(262, g_dpi, 96),
                     SWP_NOMOVE | SWP_NOZORDER | SWP_NOACTIVATE);
    }

    // Native shadow + Windows 11 rounded corners for the borderless window.
    MARGINS shadow{0, 0, 0, 1};
    DwmExtendFrameIntoClientArea(win, &shadow);
    int corner = 2;  // DWMWCP_ROUND
    DwmSetWindowAttribute(win, 33 /*DWMWA_WINDOW_CORNER_PREFERENCE*/, &corner, sizeof(corner));
    SetWindowPos(win, nullptr, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_FRAMECHANGED);

    // Global show/hide shortcut (default Ctrl+Shift+Space, changeable in Settings).
    g_hotkey = static_cast<WORD>(reg_read_dword(L"Hotkey", kDefaultHotkey));
    if (!register_hotkey(win, g_hotkey)) mark("hotkey_fail\n");

    // First run: register to start with Windows (can be turned off in Settings).
    if (!reg_read_dword(L"Initialized", 0)) {
        set_startup(true);
        reg_write_dword(L"Initialized", 1);
    } else if (startup_enabled()) {
        set_startup(true);  // keep the Run entry pointing at this exe if it was moved
    }

    if (!start_hidden) {
        ShowWindow(win, SW_SHOWNORMAL);
        UpdateWindow(win);
        SetForegroundWindow(win);
    }
    mark(IsWindowVisible(win) ? "visible=1\n" : "visible=0\n");
    if (trace != INVALID_HANDLE_VALUE) CloseHandle(trace);

    SetTimer(win, 1, 50, nullptr);
    MSG msg{};
    while (GetMessageW(&msg, nullptr, 0, 0) > 0) {
        if (g_settings_win && IsDialogMessageW(g_settings_win, &msg)) continue;
        TranslateMessage(&msg);
        DispatchMessageW(&msg);
    }
    GdiplusShutdown(gdi_token);
    if (instance_mutex) CloseHandle(instance_mutex);
    return 0;
}
