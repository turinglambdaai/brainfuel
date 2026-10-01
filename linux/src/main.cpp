// BrainFuel Linux host — GTK4 floating quota card over one embedded Racket
// CS backend. Mirrors the macOS host: the backend owns all business logic
// (refresh scheduling, provider clients, burn rate, history, credentials);
// this host only renders state and forwards interaction over typed RPC.
// Boot the runtime off the UI thread, dispatch every completion and backend
// event back to the main loop before touching widgets.
//
// Honest gaps vs the macOS host: no tray (upstream rivet #118 — so closing
// the window quits instead of hiding) and no global hotkey (compositor-
// dependent; the settings fields are still persisted).

#include <gtk/gtk.h>

#if defined(__APPLE__)
#include <mach-o/dyld.h>
#endif

#include <algorithm>
#include <atomic>
#include <chrono>
#include <cmath>
#include <cstdint>
#include <cstdio>
#include <filesystem>
#include <functional>
#include <memory>
#include <mutex>
#include <optional>
#include <string>
#include <thread>
#include <utility>
#include <vector>

#include "GeneratedBackend.hpp"
#include "GeneratedStrings.hpp"
#include "system_services.hpp"

namespace {

constexpr double kPi = 3.14159265358979323846;
constexpr char const* kApplicationId = "site.jrtx.brainfuel";

// ---------------------------------------------------------------- utilities

std::int64_t now_ms() {
  return std::chrono::duration_cast<std::chrono::milliseconds>(
             std::chrono::system_clock::now().time_since_epoch())
      .count();
}

std::filesystem::path executable_path() {
#if defined(__APPLE__)
  // Dev-iteration convenience: the host targets Linux, but this Mac is the
  // local build/run harness.
  std::uint32_t size = 0;
  _NSGetExecutablePath(nullptr, &size);
  std::string buf(size + 1, '\0');
  _NSGetExecutablePath(buf.data(), &size);
  return std::filesystem::path(buf.c_str()).lexically_normal();
#else
  return std::filesystem::read_symlink("/proc/self/exe");
#endif
}

struct RuntimeLayout {
  std::filesystem::path petite_boot;
  std::filesystem::path scheme_boot;
  std::filesystem::path racket_boot;
  std::filesystem::path core;
};

// Rivet keeps runtime/res beside the executable in development and packages.
std::optional<RuntimeLayout> discover_runtime_layout() {
  std::filesystem::path const dir = executable_path().parent_path();
  RuntimeLayout layout{
      dir / "runtime" / "petite.boot",
      dir / "runtime" / "scheme.boot",
      dir / "runtime" / "racket.boot",
      dir / "res" / "core.zo",
  };
  if (std::filesystem::exists(layout.petite_boot) &&
      std::filesystem::exists(layout.scheme_boot) &&
      std::filesystem::exists(layout.racket_boot) &&
      std::filesystem::exists(layout.core)) {
    return layout;
  }
  return std::nullopt;
}

struct MainJob {
  std::function<void()> run;
};

int run_main_job(gpointer data) {
  auto* job = static_cast<MainJob*>(data);
  job->run();
  delete job;
  return G_SOURCE_REMOVE;
}

// Post a closure onto the GTK main loop (thread-safe from reader threads).
void post_main(std::function<void()> run) {
  g_idle_add(run_main_job, new MainJob{std::move(run)});
}

// Deliver a typed RPC result on the main loop.
template <typename T, typename Apply>
void deliver(rivet_app::Result<T> result, Apply apply) {
  auto* payload = new rivet_app::Result<T>(std::move(result));
  post_main([payload, apply = std::move(apply)] {
    apply(*payload);
    delete payload;
  });
}

GdkRGBA rgba(double r, double g, double b, double a = 1.0) {
  return GdkRGBA{static_cast<float>(r / 255.0),
                 static_cast<float>(g / 255.0),
                 static_cast<float>(b / 255.0),
                 static_cast<float>(a)};
}

// Windowing APIs GTK removed in 4.20 (wayland-first toplevel rework): on
// 4.20+ the compositor owns placement and keep-above, so those become
// no-ops and the card lands wherever the session places it.
void keep_window_above(GtkWindow* window, bool above) {
#if !GTK_CHECK_VERSION(4, 20, 0)
  gtk_window_set_keep_above(window, above ? TRUE : FALSE);
#else
  (void)window;
  (void)above;
#endif
}

gboolean place_top_right_once(gpointer data) {
#if !GTK_CHECK_VERSION(4, 20, 0)
  auto* window = static_cast<GtkWindow*>(data);
  GdkSurface* surface = gtk_native_get_surface(GTK_NATIVE(window));
  if (surface == nullptr) return G_SOURCE_CONTINUE;
  GdkDisplay* display = gdk_display_get_default();
  GdkMonitor* monitor = gdk_display_get_monitor_at_surface(display, surface);
  GdkRectangle area{};
#if !GTK_CHECK_VERSION(4, 18, 0)
  gdk_monitor_get_workarea(monitor, &area);
#else
  gdk_monitor_get_geometry(monitor, &area);
#endif
  gdk_surface_move(surface,
                   area.x + area.width -
                       gtk_widget_get_width(GTK_WIDGET(window)) - 20,
                   area.y + 20);
  return G_SOURCE_REMOVE;
#else
  (void)data;
  return G_SOURCE_REMOVE;
#endif
}

std::string format_percent(std::optional<std::int64_t> const& bp,
                           bool remaining = false) {
  if (!bp.has_value()) return "--";
  double value = static_cast<double>(*bp) / 100.0;
  if (remaining) value = std::max(0.0, 100.0 - value);
  char buf[32];
  if (value == std::round(value)) {
    std::snprintf(buf, sizeof buf, "%d%%", static_cast<int>(value));
  } else {
    std::snprintf(buf, sizeof buf, "%.1f%%", value);
  }
  return buf;
}

std::string relative_time(std::int64_t ms, std::int64_t now) {
  std::int64_t delta_sec = (ms - now) / 1000;
  bool const future = delta_sec > 0;
  std::int64_t const abs_sec = delta_sec < 0 ? -delta_sec : delta_sec;
  if (abs_sec < 60) return l10n::t("JustNow");
  if (abs_sec < 3600)
    return l10n::t(future ? "MinutesLater" : "MinutesAgo",
                   {std::to_string(abs_sec / 60)});
  if (abs_sec < 86400)
    return l10n::t(future ? "HoursLater" : "HoursAgo",
                   {std::to_string(abs_sec / 3600)});
  return l10n::t(future ? "DaysLater" : "DaysAgo",
                 {std::to_string(abs_sec / 86400)});
}

std::string failure_key(rivet_app::FailureKind kind) {
  switch (kind) {
    case rivet_app::FailureKind::authentication: return "FailureAuthentication";
    case rivet_app::FailureKind::network: return "FailureNetwork";
    case rivet_app::FailureKind::tls: return "FailureTls";
    case rivet_app::FailureKind::proxy: return "FailureProxy";
    case rivet_app::FailureKind::timeout: return "FailureTimeout";
    case rivet_app::FailureKind::rate_limited: return "FailureRateLimited";
    case rivet_app::FailureKind::no_coding_plan: return "FailureNoCodingPlan";
    case rivet_app::FailureKind::service_unavailable:
      return "FailureServiceUnavailable";
    case rivet_app::FailureKind::invalid_response:
      return "FailureInvalidResponse";
    case rivet_app::FailureKind::unknown: return "FailureUnknown";
  }
  return "FailureUnknown";
}

// ------------------------------------------------------------------- model

struct Widgets {
  GtkWindow* window = nullptr;
  GtkBox* card = nullptr;  // the .bf-card rounded container
  GtkLabel* subtitle = nullptr;
  GtkDrawingArea* rings = nullptr;
  GtkLabel* chip_dots[2] = {nullptr, nullptr};      // 0 hourly, 1 weekly
  GtkLabel* chip_percents[2] = {nullptr, nullptr};
  GtkLabel* chip_resets[2] = {nullptr, nullptr};
  GtkLabel* footer = nullptr;
  GtkSpinner* spinner = nullptr;
  GtkWidget* refresh_button = nullptr;
  GtkLabel* mini_percent = nullptr;
  GtkLabel* mini_label = nullptr;
  GtkDrawingArea* mini_ring = nullptr;
  GMenu* accounts_menu = nullptr;
  GtkCssProvider* provider = nullptr;

  // details popover
  GtkPopover* details_popover = nullptr;
  GtkLabel* details_plan = nullptr;
  GtkLabel* details_reset[2] = {nullptr, nullptr};
  GtkLabel* details_percent[2] = {nullptr, nullptr};
  GtkLabel* details_burn[2] = {nullptr, nullptr};
  GtkDrawingArea* details_graph[2] = {nullptr, nullptr};
  GtkLabel* details_note = nullptr;
  std::optional<rivet_app::Details> details_data;

  // settings window
  GtkWindow* settings_window = nullptr;
  GtkNotebook* settings_notebook = nullptr;
  GtkDropDown* language_dd = nullptr;
  GtkDropDown* theme_dd = nullptr;
  GtkDropDown* palette_dd = nullptr;
  GtkScale* opacity_scale = nullptr;
  GtkCheckButton* topmost_check = nullptr;
  GtkDropDown* interval_dd = nullptr;
  GtkCheckButton* hourly_remaining_check = nullptr;
  GtkCheckButton* weekly_remaining_check = nullptr;
  GtkCheckButton* autostart_check = nullptr;
  GtkDropDown* accounts_dd = nullptr;
  GtkButton* remove_account_btn = nullptr;
  GtkEntry* account_name = nullptr;
  GtkDropDown* platform_dd = nullptr;
  GtkEntry* api_key = nullptr;
  GtkLabel* account_message = nullptr;
  GtkButton* save_account_btn = nullptr;
  GtkCheckButton* notify_check = nullptr;
  GtkLabel* threshold_value = nullptr;
  GtkScale* threshold_scale = nullptr;
  GtkButton* copy_diagnostics = nullptr;
  GtkLabel* software_status = nullptr;
};

struct Model {
  bool ready = false;
  bool refreshing = false;
  bool applying_settings = false;  // suppress handlers while refreshing UI
  std::string status = "Starting embedded Racket CS…";
  std::vector<rivet_app::Account> accounts;
  std::string active_account_id;
  std::optional<rivet_app::QuotaSnapshot> snapshot;
  rivet_app::SettingsData settings{};
  std::unique_ptr<rivet::linux_runtime::Backend> backend;
  std::unique_ptr<rivet_app::API> api;

  std::mutex startup_mutex;
  std::thread startup_thread;
  std::unique_ptr<rivet::linux_runtime::Backend> startup_backend;
  std::string startup_error;
  std::atomic<bool> shutting_down{false};

  std::unique_ptr<rivet::system::SingleInstanceLease> lease;

  Widgets ui;
  guint single_click_timer = 0;
};

Model g;

rivet_app::SettingsData default_settings() {
  rivet_app::SettingsData s{};
  s.refresh_interval_minutes = 5;
  s.hourly_remaining = true;
  s.weekly_remaining = false;
  s.notify_enabled = true;
  s.notify_threshold = 80;
  s.theme = "dark";
  s.language = "zh";
  s.size_mode = "standard";
  s.always_on_top = false;
  s.autostart = false;
  s.hotkey_enabled = false;
  s.hotkey_combo = "";
  s.ring_palette = "classic";
  s.card_opacity_bp = 10'000;
  s.credential_state = "none";
  return s;
}

// Cross-referenced builders; definitions follow their first use order below.
void update_card();
void update_details_content();
void open_details();
void open_settings_window();
void rebuild_card_for_mode();
void rebuild_accounts_ui();
void refresh_now();
void switch_account(std::string const& id);
void save_settings(rivet_app::SettingsData next);

rivet_app::Account const* active_account() {
  for (auto const& account : g.accounts) {
    if (account.id == g.active_account_id) return &account;
  }
  return nullptr;
}

std::string subtitle_text() {
  if (g.accounts.size() > 1) {
    if (auto const* account = active_account()) {
      return account->name.empty() ? account->base_domain : account->name;
    }
  }
  return l10n::t("CardSubtitle");
}

// ----------------------------------------------------------------- palette

bool system_prefers_dark() {
  GtkSettings* settings = gtk_settings_get_default();
  gboolean dark = FALSE;
  if (settings != nullptr) {
    g_object_get(settings, "gtk-application-prefer-dark-theme", &dark, nullptr);
  }
  return dark == TRUE;
}

// Calm ring colors per palette id: (weekly, hourly). Amber/red severity
// overrides never follow the ring palette (parity with the old widget).
std::pair<GdkRGBA, GdkRGBA> ring_colors(std::string const& palette_id) {
  if (palette_id == "teal")
    return {rgba(0x2A, 0xA1, 0x98), rgba(0x8F, 0xD3, 0xCA)};
  if (palette_id == "forest")
    return {rgba(0x5B, 0x8C, 0x5A), rgba(0xA7, 0xC7, 0xA1)};
  if (palette_id == "violet")
    return {rgba(0x8B, 0x7B, 0xD8), rgba(0xC9, 0xBF, 0xF2)};
  if (palette_id == "mono")
    return {rgba(0x8A, 0x86, 0x80), rgba(0xC9, 0xC4, 0xBA)};
  return {rgba(0xC2, 0x5E, 0x3E), rgba(0xB5, 0x89, 0x5A)};  // classic
}

GdkRGBA severity_color(rivet_app::Severity severity, GdkRGBA calm) {
  switch (severity) {
    case rivet_app::Severity::amber: return rgba(245, 166, 35);
    case rivet_app::Severity::red: return rgba(229, 72, 77);
    case rivet_app::Severity::calm: return calm;
  }
  return calm;
}

struct Palette {
  GdkRGBA card_bg;
  GdkRGBA border;
  GdkRGBA text_primary;
  GdkRGBA text_secondary;
  GdkRGBA text_muted;
  GdkRGBA track;
};

Palette palette_for_theme(std::string const& theme) {
  std::string effective = theme;
  if (effective != "dark" && effective != "light") {
    effective = system_prefers_dark() ? "dark" : "light";
  }
  if (effective == "light") {
    return Palette{rgba(242, 240, 233), rgba(128, 120, 114, 0.13),
                   rgba(31, 31, 30),   rgba(110, 105, 97),
                   rgba(138, 134, 126), rgba(222, 219, 210)};
  }
  return Palette{rgba(31, 31, 30),       rgba(240, 238, 230, 0.15),
                 rgba(240, 238, 230),    rgba(163, 158, 148),
                 rgba(155, 150, 139),    rgba(58, 57, 55)};
}

// --------------------------------------------------------------------- css

std::string css_rgb(GdkRGBA const& c) {
  char buf[64];
  std::snprintf(buf, sizeof buf, "rgb(%.0f,%.0f,%.0f)", c.red * 255.0,
                c.green * 255.0, c.blue * 255.0);
  return buf;
}

std::string css_rgba(GdkRGBA const& c) {
  char buf[80];
  std::snprintf(buf, sizeof buf, "rgba(%.0f,%.0f,%.0f,%.2f)", c.red * 255.0,
                c.green * 255.0, c.blue * 255.0, c.alpha);
  return buf;
}

void apply_css() {
  if (g.ui.provider == nullptr) {
    g.ui.provider = gtk_css_provider_new();
    gtk_style_context_add_provider_for_display(
        gdk_display_get_default(), GTK_STYLE_PROVIDER(g.ui.provider),
        GTK_STYLE_PROVIDER_PRIORITY_APPLICATION);
  }
  Palette const p = palette_for_theme(g.settings.theme);
  GdkRGBA const calm_weekly = ring_colors("classic").first;
  GdkRGBA const calm_hourly = ring_colors("classic").second;
  std::string css =
      "window { background-color: transparent; }\n"
      ".bf-card { background-color: " + css_rgba(p.card_bg) +
      "; border-radius: 16px; border: 1px solid " + css_rgba(p.border) +
      "; padding: 14px; }\n"
      ".bf-title { color: " + css_rgb(p.text_primary) +
      "; font-size: 14px; font-weight: 600; }\n"
      ".bf-subtitle { color: " + css_rgb(p.text_secondary) +
      "; font-size: 11px; }\n"
      ".bf-dim { color: " + css_rgb(p.text_muted) + "; font-size: 10px; }\n"
      ".bf-reset { color: " + css_rgb(p.text_muted) +
      "; font-size: 9px; }\n"
      ".bf-footer { color: " + css_rgb(p.text_muted) +
      "; font-size: 10px; }\n"
      ".bf-percent { font-size: 20px; font-weight: 600; }\n"
      ".bf-percent-weekly { color: " + css_rgb(calm_weekly) + "; }\n"
      ".bf-percent-hourly { color: " + css_rgb(calm_hourly) + "; }\n"
      ".bf-percent-amber { color: rgb(245,166,35); }\n"
      ".bf-percent-red { color: rgb(229,72,77); }\n"
      ".bf-dot { min-width: 7px; min-height: 7px; border-radius: 4px; }\n"
      ".bf-dot-weekly { background-color: " + css_rgb(calm_weekly) + "; }\n"
      ".bf-dot-hourly { background-color: " + css_rgb(calm_hourly) + "; }\n"
      ".bf-dot-amber { background-color: rgb(245,166,35); }\n"
      ".bf-dot-red { background-color: rgb(229,72,77); }\n"
      ".bf-popover { background-color: " + css_rgba(p.card_bg) +
      "; color: " + css_rgb(p.text_primary) + "; border-radius: 12px; }\n"
      ".bf-popover > contents { background-color: " + css_rgba(p.card_bg) +
      "; color: " + css_rgb(p.text_primary) + "; border-radius: 12px; }\n"
      ".bf-menu-button { color: " + css_rgb(p.text_secondary) +
      "; background: transparent; border: none; padding: 0; min-height: 0; "
      "min-width: 0; }\n"
      ".bf-refresh { color: " + css_rgb(p.text_secondary) +
      "; background: transparent; border: none; padding: 2px; min-height: 0; "
      "min-width: 0; }\n";
  gtk_css_provider_load_from_data(g.ui.provider, css.c_str(), -1);
}

// ----------------------------------------------------------------- drawing

void draw_ring(cairo_t* cr, double cx, double cy, double radius, double width,
               GdkRGBA const& color, double fraction) {
  cairo_save(cr);
  cairo_set_line_cap(cr, CAIRO_LINE_CAP_ROUND);
  cairo_set_line_width(cr, width);
  cairo_set_source_rgba(cr, 0.5, 0.5, 0.5, 0.25);
  cairo_arc(cr, cx, cy, radius, 0.0, 2.0 * kPi);
  cairo_stroke(cr);
  if (fraction > 0.001) {
    gdk_cairo_set_source_rgba(cr, &color);
    cairo_arc(cr, cx, cy, radius, -kPi / 2.0, -kPi / 2.0 + fraction * 2.0 * kPi);
    cairo_stroke(cr);
  }
  cairo_restore(cr);
}

double bp_fraction(std::optional<std::int64_t> const& bp) {
  if (!bp.has_value()) return 0.0;
  return std::min(1.0, std::max(0.0, static_cast<double>(*bp) / 10'000.0));
}

void draw_rings(GtkDrawingArea*, cairo_t* cr, int width, int height, gpointer) {
  GdkRGBA weekly = rgba(128, 128, 128);
  GdkRGBA hourly = weekly;
  std::optional<std::int64_t> weekly_used;
  std::optional<std::int64_t> hourly_used;
  if (g.snapshot.has_value()) {
    auto colors = ring_colors(g.settings.ring_palette);
    weekly = severity_color(g.snapshot->severity, colors.first);
    hourly = severity_color(g.snapshot->severity, colors.second);
    weekly_used = g.snapshot->weekly.used_bp;
    hourly_used = g.snapshot->hourly.used_bp;
  }
  double const cx = width / 2.0;
  double const cy = height / 2.0;
  double const outer = std::min(cx, cy) - 5.0;
  draw_ring(cr, cx, cy, outer, 9.0, weekly, bp_fraction(weekly_used));
  draw_ring(cr, cx, cy, outer - 14.0, 7.0, hourly, bp_fraction(hourly_used));
}

void draw_mini_ring(GtkDrawingArea*, cairo_t* cr, int width, int height,
                    gpointer) {
  GdkRGBA color = rgba(128, 128, 128);
  std::optional<std::int64_t> used;
  if (g.snapshot.has_value()) {
    std::int64_t const hourly = g.snapshot->hourly.used_bp.value_or(0);
    std::int64_t const weekly = g.snapshot->weekly.used_bp.value_or(0);
    bool const show_hourly = hourly >= weekly;
    used = show_hourly ? g.snapshot->hourly.used_bp
                       : g.snapshot->weekly.used_bp;
    auto colors = ring_colors(g.settings.ring_palette);
    color = severity_color(g.snapshot->severity,
                           show_hourly ? colors.second : colors.first);
  }
  double const cx = width / 2.0;
  double const cy = height / 2.0;
  draw_ring(cr, cx, cy, std::min(cx, cy) - 5.0, 8.0, color, bp_fraction(used));
}

// The details history graph: solid line for samples inside the current
// period window, dashed for the same window one period earlier.
void draw_history(GtkDrawingArea*, cairo_t* cr, int width, int height,
                  gpointer user_data) {
  if (!g.ui.details_data.has_value()) return;
  bool const hourly = static_cast<bool>(reinterpret_cast<intptr_t>(user_data));
  auto const& samples = g.ui.details_data->history;
  std::int64_t const window_ms =
      hourly ? 5LL * 3'600'000 : 7LL * 86'400'000;
  std::int64_t const now = now_ms();

  auto stroke_line = [&](std::int64_t anchor, double alpha, bool dashed) {
    cairo_save(cr);
    GdkRGBA const line = rgba(0xC2, 0x5E, 0x3E, alpha);
    gdk_cairo_set_source_rgba(cr, &line);
    cairo_set_line_width(cr, dashed ? 1.0 : 1.5);
    cairo_set_line_cap(cr, CAIRO_LINE_CAP_ROUND);
    if (dashed) {
      double dash[2] = {3.0, 3.0};
      cairo_set_dash(cr, dash, 2, 0);
    }
    bool started = false;
    for (auto const& s : samples) {
      auto const& bp = hourly ? s.hourly_bp : s.weekly_bp;
      if (!bp.has_value()) continue;
      double const v =
          std::min(1.0, std::max(0.0, static_cast<double>(*bp) / 10'000.0));
      double const x =
          (1.0 - static_cast<double>(anchor - s.at_ms) /
                     static_cast<double>(window_ms)) * width;
      if (x < -1.0 || x > static_cast<double>(width) + 1.0) continue;
      double const y = (1.0 - v) * height;
      if (!started) {
        cairo_move_to(cr, x, y);
        started = true;
      } else {
        cairo_line_to(cr, x, y);
      }
    }
    if (started) cairo_stroke(cr);
    cairo_restore(cr);
  };
  stroke_line(now, 1.0, false);
  stroke_line(now - window_ms, 0.45, true);
}

// ------------------------------------------------------------- card update

void set_severity_class(GtkWidget* widget, std::string const& base,
                        rivet_app::Severity severity, bool hourly) {
  gtk_widget_remove_css_class(widget, (base + "-hourly").c_str());
  gtk_widget_remove_css_class(widget, (base + "-weekly").c_str());
  gtk_widget_remove_css_class(widget, (base + "-amber").c_str());
  gtk_widget_remove_css_class(widget, (base + "-red").c_str());
  char const* suffix = hourly ? "-hourly" : "-weekly";
  if (severity == rivet_app::Severity::amber) suffix = "-amber";
  if (severity == rivet_app::Severity::red) suffix = "-red";
  gtk_widget_add_css_class(widget, (base + suffix).c_str());
}

std::string chip_percent_text(bool hourly) {
  if (!g.snapshot.has_value()) return "--";
  auto const& usage = hourly ? g.snapshot->hourly : g.snapshot->weekly;
  bool const remaining =
      hourly ? g.settings.hourly_remaining : g.settings.weekly_remaining;
  return format_percent(usage.used_bp, remaining);
}

void update_card() {
  Widgets& ui = g.ui;
  if (ui.window == nullptr || !g.ready) return;

  std::int64_t const now = now_ms();
  rivet_app::Severity const severity =
      g.snapshot.has_value() ? g.snapshot->severity : rivet_app::Severity::calm;

  gtk_label_set_text(ui.subtitle, subtitle_text().c_str());

  if (g.settings.size_mode != "mini") {
    for (int i = 0; i < 2; ++i) {
      bool const hourly = i == 0;
      auto const& usage = hourly ? g.snapshot->hourly : g.snapshot->weekly;
      if (ui.chip_dots[i] != nullptr) {
        set_severity_class(GTK_WIDGET(ui.chip_dots[i]), "bf-dot", severity,
                           hourly);
      }
      if (ui.chip_percents[i] != nullptr) {
        set_severity_class(GTK_WIDGET(ui.chip_percents[i]), "bf-percent",
                           severity, hourly);
        gtk_label_set_text(ui.chip_percents[i],
                           chip_percent_text(hourly).c_str());
      }
      if (ui.chip_resets[i] != nullptr) {
        if (g.snapshot.has_value() && usage.reset_at_ms.has_value()) {
          gtk_label_set_text(
              ui.chip_resets[i],
              l10n::t("DetailResets",
                      {relative_time(*usage.reset_at_ms, now)}).c_str());
        } else {
          gtk_label_set_text(ui.chip_resets[i], "--");
        }
      }
    }
    if (ui.footer != nullptr) {
      if (g.snapshot.has_value() && g.snapshot->failure.has_value()) {
        gtk_label_set_text(ui.footer,
                           g.snapshot->failure->message.c_str());
        gtk_widget_set_tooltip_text(
            GTK_WIDGET(ui.window),
            (l10n::t(failure_key(g.snapshot->failure->kind)) + ": " +
             g.snapshot->failure->message).c_str());
      } else {
        std::int64_t const at =
            g.snapshot.has_value() ? g.snapshot->fetched_at_ms : now;
        gtk_label_set_text(
            ui.footer,
            (l10n::t("QuotaDataStatus") + " · " + relative_time(at, now))
                .c_str());
        gtk_widget_set_tooltip_text(GTK_WIDGET(ui.window),
                                    l10n::t("TipSizeHint").c_str());
      }
    }
    if (ui.spinner != nullptr) {
      gtk_spinner_stop(ui.spinner);
      gtk_widget_set_visible(GTK_WIDGET(ui.spinner), FALSE);
    }
    if (ui.refresh_button != nullptr) {
      gtk_widget_set_visible(ui.refresh_button, g.refreshing == false);
    }
  } else if (ui.mini_percent != nullptr) {
    std::int64_t const hourly =
        g.snapshot.has_value() ? g.snapshot->hourly.used_bp.value_or(0) : 0;
    std::int64_t const weekly =
        g.snapshot.has_value() ? g.snapshot->weekly.used_bp.value_or(0) : 0;
    bool const show_hourly = hourly >= weekly;
    set_severity_class(GTK_WIDGET(ui.mini_percent), "bf-percent", severity,
                       show_hourly);
    gtk_label_set_text(ui.mini_percent,
                       chip_percent_text(show_hourly).c_str());
    gtk_label_set_text(
        ui.mini_label,
        l10n::t(show_hourly ? "LblHourly" : "LblWeekly").c_str());
  }

  if (ui.rings != nullptr) {
    gtk_widget_queue_draw(GTK_WIDGET(ui.rings));
  }
  if (ui.mini_ring != nullptr) {
    gtk_widget_queue_draw(GTK_WIDGET(ui.mini_ring));
  }

  // card chrome (opacity/theme may have changed)
  apply_css();

  // menu state
  if (GtkApplication* app = GTK_APPLICATION(g_application_get_default())) {
    if (GSimpleAction* topmost = G_SIMPLE_ACTION(g_action_map_lookup_action(
            G_ACTION_MAP(app), "topmost"))) {
      g_simple_action_set_state(
          topmost, g_variant_new_boolean(g.settings.always_on_top));
    }
  }
}

// ---------------------------------------------------------- details popover

void build_details_popover() {
  Widgets& ui = g.ui;
  GtkBox* box = GTK_BOX(gtk_box_new(GTK_ORIENTATION_VERTICAL, 10));
  gtk_widget_set_margin_top(GTK_WIDGET(box), 12);
  gtk_widget_set_margin_bottom(GTK_WIDGET(box), 12);
  gtk_widget_set_margin_start(GTK_WIDGET(box), 12);
  gtk_widget_set_margin_end(GTK_WIDGET(box), 12);

  ui.details_plan = GTK_LABEL(gtk_label_new(""));
  gtk_widget_set_halign(GTK_WIDGET(ui.details_plan), GTK_ALIGN_START);
  gtk_widget_add_css_class(GTK_WIDGET(ui.details_plan), "bf-title");
  gtk_box_append(box, GTK_WIDGET(ui.details_plan));

  for (int i = 0; i < 2; ++i) {
    bool const hourly = i == 0;
    GtkBox* col = GTK_BOX(gtk_box_new(GTK_ORIENTATION_VERTICAL, 4));
    GtkBox* head = GTK_BOX(gtk_box_new(GTK_ORIENTATION_HORIZONTAL, 6));
    GtkLabel* title = GTK_LABEL(
        gtk_label_new(l10n::t(hourly ? "LblHourly" : "LblWeekly").c_str()));
    gtk_widget_add_css_class(GTK_WIDGET(title), "bf-dim");
    gtk_widget_set_halign(GTK_WIDGET(title), GTK_ALIGN_START);
    gtk_widget_set_hexpand(GTK_WIDGET(title), TRUE);
    ui.details_reset[i] = GTK_LABEL(gtk_label_new(""));
    gtk_widget_add_css_class(GTK_WIDGET(ui.details_reset[i]), "bf-reset");
    gtk_box_append(head, GTK_WIDGET(title));
    gtk_box_append(head, GTK_WIDGET(ui.details_reset[i]));
    ui.details_percent[i] = GTK_LABEL(gtk_label_new("--"));
    gtk_widget_set_halign(GTK_WIDGET(ui.details_percent[i]), GTK_ALIGN_START);
    gtk_widget_add_css_class(GTK_WIDGET(ui.details_percent[i]), "bf-percent");
    gtk_widget_add_css_class(GTK_WIDGET(ui.details_percent[i]),
                             hourly ? "bf-percent-hourly"
                                    : "bf-percent-weekly");
    ui.details_burn[i] = GTK_LABEL(gtk_label_new(""));
    gtk_widget_add_css_class(GTK_WIDGET(ui.details_burn[i]), "bf-reset");
    gtk_widget_set_halign(GTK_WIDGET(ui.details_burn[i]), GTK_ALIGN_START);
    GtkDrawingArea* graph = GTK_DRAWING_AREA(gtk_drawing_area_new());
    gtk_drawing_area_set_draw_func(
        graph, draw_history,
        reinterpret_cast<gpointer>(static_cast<intptr_t>(hourly)), nullptr);
    gtk_widget_set_size_request(GTK_WIDGET(graph), -1, 44);
    ui.details_graph[i] = graph;
    gtk_box_append(col, GTK_WIDGET(head));
    gtk_box_append(col, GTK_WIDGET(ui.details_percent[i]));
    gtk_box_append(col, GTK_WIDGET(ui.details_burn[i]));
    gtk_box_append(col, GTK_WIDGET(graph));
    gtk_box_append(box, GTK_WIDGET(col));
    if (i == 0) {
      gtk_box_append(box, gtk_separator_new(GTK_ORIENTATION_HORIZONTAL));
    }
  }

  ui.details_note = GTK_LABEL(gtk_label_new(""));
  gtk_widget_add_css_class(GTK_WIDGET(ui.details_note), "bf-reset");
  gtk_label_set_ellipsize(ui.details_note, PANGO_ELLIPSIZE_END);
  gtk_label_set_xalign(ui.details_note, 0.0);
  gtk_box_append(box, GTK_WIDGET(ui.details_note));

  gtk_widget_set_size_request(GTK_WIDGET(box), 302, 280);

  GtkPopover* popover = GTK_POPOVER(gtk_popover_new());
  gtk_widget_add_css_class(GTK_WIDGET(popover), "bf-popover");
  gtk_popover_set_child(popover, GTK_WIDGET(box));
  gtk_widget_set_parent(GTK_WIDGET(popover), GTK_WIDGET(g.ui.card));
  ui.details_popover = popover;
}

void update_details_content() {
  if (g.ui.details_popover == nullptr ||
      !gtk_widget_get_visible(GTK_WIDGET(g.ui.details_popover)) ||
      !g.ui.details_data.has_value()) {
    return;
  }
  rivet_app::Details const& d = *g.ui.details_data;
  std::int64_t const now = now_ms();
  rivet_app::WindowUsage const usages[2] = {d.hourly, d.weekly};
  rivet_app::BurnInfo const burns[2] = {d.hourly_burn, d.weekly_burn};
  for (int i = 0; i < 2; ++i) {
    gtk_label_set_text(g.ui.details_percent[i],
                       format_percent(usages[i].used_bp).c_str());
    if (usages[i].reset_at_ms.has_value()) {
      gtk_label_set_text(
          g.ui.details_reset[i],
          l10n::t("DetailResets",
                  {relative_time(*usages[i].reset_at_ms, now)}).c_str());
    } else {
      gtk_label_set_text(g.ui.details_reset[i], "");
    }
    if (burns[i].rate_bp_per_hour.has_value()) {
      std::string percent = format_percent(burns[i].rate_bp_per_hour);
      percent.erase(percent.find('%'));
      std::string exhaustion = "--";
      if (burns[i].minutes_to_empty.has_value()) {
        exhaustion = relative_time(now + *burns[i].minutes_to_empty * 60'000,
                                   now);
      }
      std::string text = l10n::t("TipBurn", {percent, exhaustion});
      if (burns[i].comparison.has_value() && !burns[i].comparison->empty()) {
        text += " " + *burns[i].comparison;
      }
      gtk_label_set_text(g.ui.details_burn[i], text.c_str());
    } else {
      gtk_label_set_text(g.ui.details_burn[i], "");
    }
    gtk_widget_queue_draw(GTK_WIDGET(g.ui.details_graph[i]));
  }
  gtk_label_set_text(g.ui.details_note, d.history_note.c_str());
}

void load_details() {
  if (g.api == nullptr || g.active_account_id.empty()) return;
  g.api->get_details_async(
      g.active_account_id,
      [](rivet_app::Result<rivet_app::Details> result) {
        deliver(std::move(result),
                [](rivet_app::Result<rivet_app::Details>& r) {
                  if (r.succeeded()) {
                    g.ui.details_data = *r.value;
                    update_details_content();
                  }
                });
      });
}

void open_details() {
  Widgets& ui = g.ui;
  if (!g.ready || g.settings.size_mode == "mini") return;
  if (ui.details_popover == nullptr) {
    build_details_popover();
  }
  gtk_label_set_text(
      ui.details_plan,
      g.snapshot.has_value() && !g.snapshot->plan_level.empty()
          ? g.snapshot->plan_level.c_str()
          : subtitle_text().c_str());
  for (int i = 0; i < 2; ++i) {
    gtk_label_set_text(ui.details_percent[i], "--");
    gtk_label_set_text(ui.details_reset[i], "");
    gtk_label_set_text(ui.details_burn[i], "");
  }
  gtk_label_set_text(ui.details_note, "");
  gtk_popover_popup(ui.details_popover);
  load_details();
}

// --------------------------------------------------------------- 30s tick

gboolean on_tick(gpointer) {
  if (!g.ready) return G_SOURCE_CONTINUE;
  update_card();
  update_details_content();
  return G_SOURCE_CONTINUE;
}

// ------------------------------------------------------------ RPC wrappers

void update_refreshing_ui() {
  Widgets& ui = g.ui;
  if (ui.spinner != nullptr) {
    if (g.refreshing) {
      gtk_widget_set_visible(GTK_WIDGET(ui.spinner), TRUE);
      gtk_spinner_start(ui.spinner);
    } else {
      gtk_spinner_stop(ui.spinner);
      gtk_widget_set_visible(GTK_WIDGET(ui.spinner), FALSE);
    }
  }
  if (ui.refresh_button != nullptr) {
    gtk_widget_set_visible(ui.refresh_button, g.refreshing == false);
  }
}

void refresh_now() {
  if (!g.ready || g.refreshing || g.api == nullptr) return;
  g.refreshing = true;
  update_refreshing_ui();
  g.api->refresh_now_async([](rivet_app::Result<void> result) {
    deliver(std::move(result), [](rivet_app::Result<void>&) {
      g.refreshing = false;
      update_refreshing_ui();
    });
  });
}

void switch_account(std::string const& id) {
  if (!g.ready || g.api == nullptr) return;
  g.active_account_id = id;
  g.ui.details_data.reset();
  rebuild_accounts_ui();
  update_card();
  g.api->switch_account_async(
      id, [](rivet_app::Result<void> result) {
        deliver(std::move(result), [](rivet_app::Result<void>&) {
          if (g.api == nullptr) return;
          g.api->get_snapshot_async(
              [](rivet_app::Result<std::optional<rivet_app::QuotaSnapshot>>
                     snap) {
                deliver(std::move(snap),
                        [](rivet_app::Result<
                            std::optional<rivet_app::QuotaSnapshot>>& r) {
                          if (r.succeeded()) {
                            g.snapshot = *r.value;
                            update_card();
                          }
                        });
              });
        });
      });
}

std::string account_display(rivet_app::Account const& a) {
  std::string label = a.name.empty() ? a.base_domain : a.name;
  if (!a.configured) label += " · " + l10n::t("NotConfigured");
  return label;
}

void rebuild_accounts_ui() {
  Widgets& ui = g.ui;
  // submenu on the card
  if (ui.accounts_menu != nullptr) {
    g_menu_remove_all(ui.accounts_menu);
    for (auto const& account : g.accounts) {
      GMenuItem* item =
          g_menu_item_new(account_display(account).c_str(), nullptr);
      g_menu_item_set_action_and_target(item, "app.switch-account", "s",
                                        account.id.c_str());
      g_menu_append_item(ui.accounts_menu, item);
      g_object_unref(item);
    }
  }
  // settings dropdown
  if (ui.accounts_dd != nullptr) {
    GtkStringList* list =
        GTK_STRING_LIST(gtk_drop_down_get_model(ui.accounts_dd));
    guint const count =
        g_list_model_get_n_items(G_LIST_MODEL(gtk_drop_down_get_model(
            ui.accounts_dd)));
    gtk_string_list_splice(list, 0, count, nullptr);
    guint active_index = 0;
    for (guint i = 0; i < g.accounts.size(); ++i) {
      gtk_string_list_append(list, account_display(g.accounts[i]).c_str());
      if (g.accounts[i].id == g.active_account_id) active_index = i;
    }
    g.applying_settings = true;
    gtk_drop_down_set_selected(ui.accounts_dd, active_index);
    g.applying_settings = false;
    gtk_widget_set_sensitive(GTK_WIDGET(ui.remove_account_btn),
                             !g.accounts.empty() &&
                                 !g.active_account_id.empty());
  }
}

void remove_active_account() {
  if (!g.ready || g.api == nullptr || g.active_account_id.empty()) return;
  g.api->remove_account_async(
      g.active_account_id,
      [](rivet_app::Result<std::vector<rivet_app::Account>> result) {
        deliver(std::move(result), [](rivet_app::Result<
            std::vector<rivet_app::Account>>& r) {
          if (!r.succeeded()) return;
          g.accounts = *r.value;
          bool const active_gone =
              std::none_of(g.accounts.begin(), g.accounts.end(),
                           [](rivet_app::Account const& a) {
                             return a.id == g.active_account_id;
                           });
          if (active_gone) {
            if (!g.accounts.empty()) {
              switch_account(g.accounts.front().id);
            } else {
              g.active_account_id.clear();
              g.snapshot.reset();
              update_card();
            }
          }
          rebuild_accounts_ui();
        });
      });
}

void save_account_form(std::string const& name, std::string const& platform,
                       std::string const& key) {
  rivet_app::AccountDraft draft{};
  draft.id = std::nullopt;
  draft.name = name;
  if (platform == "codex") {
    draft.provider = rivet_app::Provider::codex;
    draft.base_domain = "https://chatgpt.com";
    draft.api_key = std::nullopt;
  } else if (platform == "claude") {
    draft.provider = rivet_app::Provider::claude;
    draft.base_domain = "https://api.anthropic.com";
    draft.api_key = std::nullopt;
  } else if (platform == "intl") {
    draft.provider = rivet_app::Provider::glm;
    draft.base_domain = "https://api.z.ai";
    draft.api_key = key;
  } else {
    draft.provider = rivet_app::Provider::glm;
    draft.base_domain = "https://open.bigmodel.cn";
    draft.api_key = key;
  }
  draft.clear_key = false;
  gtk_widget_set_sensitive(GTK_WIDGET(g.ui.save_account_btn), FALSE);
  g.api->save_account_async(
      std::move(draft),
      [](rivet_app::Result<std::vector<rivet_app::Account>> result) {
        deliver(std::move(result), [](rivet_app::Result<
            std::vector<rivet_app::Account>>& r) {
          gtk_widget_set_sensitive(GTK_WIDGET(g.ui.save_account_btn), TRUE);
          if (!r.succeeded()) return;
          g.accounts = *r.value;
          if (g.active_account_id.empty() && !g.accounts.empty()) {
            switch_account(g.accounts.front().id);
          }
          gtk_editable_set_text(GTK_EDITABLE(g.ui.account_name), "");
          gtk_editable_set_text(GTK_EDITABLE(g.ui.api_key), "");
          rebuild_accounts_ui();
        });
      });
}

void save_settings(rivet_app::SettingsData next) {
  rivet_app::SettingsData const previous = g.settings;
  bool const autostart_changed = next.autostart != previous.autostart;
  bool const size_changed = next.size_mode != previous.size_mode;
  g.settings = next;
  l10n::language() = next.language;
  if (size_changed) {
    rebuild_card_for_mode();
  } else {
    update_card();
  }
  if (g.ui.settings_window != nullptr) {
    keep_window_above(g.ui.settings_window, next.always_on_top);
  }

  auto previous_box = std::make_shared<rivet_app::SettingsData>(previous);
  g.api->save_settings_async(
      next, [previous_box, autostart_changed](
                rivet_app::Result<rivet_app::SettingsData> result) {
        deliver(std::move(result),
                [previous_box, autostart_changed](
                    rivet_app::Result<rivet_app::SettingsData>& r) {
                  if (r.succeeded()) {
                    bool const old_autostart = g.settings.autostart;
                    g.settings = *r.value;
                    l10n::language() = r.value->language;
                    update_card();
                    if (autostart_changed && old_autostart != r.value->autostart) {
                      rivet::system::Autostart::SetEnabled(
                          kApplicationId, executable_path().string(),
                          r.value->autostart);
                    }
                  } else {
                    g.settings = *previous_box;
                    l10n::language() = previous_box->language;
                    g.status = "save-settings failed";
                    update_card();
                  }
                });
      });
}

// ---------------------------------------------------------- card gestures

gboolean open_details_from_timer(gpointer) {
  g.single_click_timer = 0;
  open_details();
  return G_SOURCE_REMOVE;
}

// True when the press landed on an interactive child (refresh button,
// menu button): those handle their own activation, and the card-level
// gestures must not also fire (details popover / mini toggle).
bool press_on_button(double x, double y) {
  GtkWidget* picked =
      gtk_widget_pick(GTK_WIDGET(g.ui.card), x, y, GTK_PICK_DEFAULT);
  for (GtkWidget* w = picked; w != nullptr; w = gtk_widget_get_parent(w)) {
    if (GTK_IS_MENU_BUTTON(w)) return true;
    if (w == g.ui.refresh_button) return true;
  }
  return false;
}

void on_card_click(GtkGestureClick* gesture, gint n_press, gdouble x,
                   gdouble y, gpointer) {
  if (press_on_button(x, y)) return;
  if (n_press == 2) {
    if (g.single_click_timer != 0) {
      g_source_remove(g.single_click_timer);
      g.single_click_timer = 0;
    }
    rivet_app::SettingsData next = g.settings;
    next.size_mode = "mini";
    save_settings(next);
    return;
  }
  if (n_press == 1 && g.single_click_timer == 0) {
    g.single_click_timer =
        g_timeout_add(260, open_details_from_timer, nullptr);
  }
}

void on_mini_click(GtkGestureClick*, gint n_press, gdouble, gdouble,
                   gpointer) {
  if (n_press == 2) {
    rivet_app::SettingsData next = g.settings;
    next.size_mode = "standard";
    save_settings(next);
  }
}

void on_card_drag(GtkGestureDrag* gesture, gdouble, gdouble, gdouble,
                  gpointer) {
  gdouble start_x = 0;
  gdouble start_y = 0;
  if (!gtk_gesture_drag_get_start_point(gesture, &start_x, &start_y)) return;
  if (press_on_button(start_x, start_y)) return;
  guint32 const time = gtk_event_controller_get_current_event_time(
      GTK_EVENT_CONTROLLER(gesture));
  graphene_point_t const local = {static_cast<float>(start_x),
                                  static_cast<float>(start_y)};
  graphene_point_t root{};
  GtkWidget* root_widget = GTK_WIDGET(g.ui.window);
  if (gtk_widget_compute_point(GTK_WIDGET(g.ui.card), root_widget, &local,
                               &root)) {
#if !GTK_CHECK_VERSION(4, 20, 0)
    gtk_window_begin_move_drag(g.ui.window, 1,
                               static_cast<gint>(std::lround(root.x)),
                               static_cast<gint>(std::lround(root.y)), time);
#else
    GdkSurface* surface = gtk_native_get_surface(GTK_NATIVE(g.ui.window));
    if (surface != nullptr && GDK_IS_TOPLEVEL(surface)) {
      gdk_toplevel_begin_move(GDK_TOPLEVEL(surface), nullptr, 1, root.x,
                              root.y, time);
    }
#endif
  }
}

// ------------------------------------------------------------ menu actions

void on_action_detail(GSimpleAction*, GVariant*, gpointer) { open_details(); }

void on_action_refresh(GSimpleAction*, GVariant*, gpointer) { refresh_now(); }

void on_action_settings(GSimpleAction*, GVariant*, gpointer) {
  open_settings_window();
}

void on_action_quit(GSimpleAction*, GVariant*, gpointer user_data) {
  g_application_quit(G_APPLICATION(user_data));
}

void on_action_topmost(GSimpleAction* action, GVariant* value, gpointer) {
  g_simple_action_set_state(action, value);
  rivet_app::SettingsData next = g.settings;
  next.always_on_top = g_variant_get_boolean(value);
  save_settings(next);
  keep_window_above(g.ui.window, next.always_on_top);
}

void on_action_switch(GSimpleAction*, GVariant* value, gpointer) {
  switch_account(g_variant_get_string(value, nullptr));
}

void on_action_mini(GSimpleAction*, GVariant*, gpointer) {
  rivet_app::SettingsData next = g.settings;
  next.size_mode = g.settings.size_mode == "mini" ? "standard" : "mini";
  save_settings(next);
}

void install_actions(GtkApplication* app) {
  auto add = [&](GSimpleAction* action, GCallback handler) {
    g_signal_connect(action, "activate", handler, nullptr);
    g_action_map_add_action(G_ACTION_MAP(app), G_ACTION(action));
    g_object_unref(action);
  };
  add(g_simple_action_new("detail", nullptr), G_CALLBACK(on_action_detail));
  add(g_simple_action_new("refresh", nullptr), G_CALLBACK(on_action_refresh));
  add(g_simple_action_new("settings", nullptr),
      G_CALLBACK(on_action_settings));
  add(g_simple_action_new("mini", nullptr), G_CALLBACK(on_action_mini));
  GSimpleAction* quit_action = g_simple_action_new("quit", nullptr);
  g_signal_connect(quit_action, "activate", G_CALLBACK(on_action_quit), app);
  g_action_map_add_action(G_ACTION_MAP(app), G_ACTION(quit_action));
  g_object_unref(quit_action);
  add(g_simple_action_new_stateful("topmost", nullptr,
                                   g_variant_new_boolean(false)),
      G_CALLBACK(on_action_topmost));
  add(g_simple_action_new("switch-account", G_VARIANT_TYPE_STRING),
      G_CALLBACK(on_action_switch));
}

GtkWidget* build_menu_button() {
  GMenu* menu = g_menu_new();
  GMenu* accounts = g_menu_new();
  g.ui.accounts_menu = accounts;  // kept alive; content rebuilt on changes
  g_menu_append_submenu(menu, l10n::t("MenuAccounts").c_str(),
                        G_MENU_MODEL(accounts));
  g_object_unref(accounts);
  g_menu_append(menu, l10n::t("MenuDetail").c_str(), "app.detail");
  g_menu_append(menu, l10n::t("MenuRefresh").c_str(), "app.refresh");
  g_menu_append(menu, l10n::t("MenuSettings").c_str(), "app.settings");
  GMenuItem* topmost = g_menu_item_new(l10n::t("ChkAlwaysOnTop").c_str(),
                                       "app.topmost");
  g_menu_append_item(menu, topmost);
  g_object_unref(topmost);
  g_menu_append(menu, l10n::t("MenuMiniMode").c_str(), "app.mini");
  g_menu_append(menu, l10n::t("MenuQuit").c_str(), "app.quit");

  GtkWidget* button = gtk_menu_button_new();
  gtk_menu_button_set_icon_name(GTK_MENU_BUTTON(button), "open-menu-symbolic");
  gtk_menu_button_set_menu_model(GTK_MENU_BUTTON(button), G_MENU_MODEL(menu));
  gtk_widget_add_css_class(button, "bf-menu-button");
  gtk_widget_set_tooltip_text(button, l10n::t("MenuMoreTip").c_str());
  g_object_unref(menu);
  rebuild_accounts_ui();
  return button;
}

// ---------------------------------------------------------- card builders

void clear_card_children() {
  GtkWidget* child = gtk_widget_get_first_child(GTK_WIDGET(g.ui.card));
  while (child != nullptr) {
    GtkWidget* next = gtk_widget_get_next_sibling(child);
    gtk_box_remove(g.ui.card, child);
    child = next;
  }
}

GtkWidget* build_chip(GtkLabel** dot, GtkLabel** percent, GtkLabel** reset,
                      bool hourly) {
  GtkWidget* row = gtk_box_new(GTK_ORIENTATION_HORIZONTAL, 5);
  *dot = GTK_LABEL(gtk_label_new(nullptr));
  gtk_widget_add_css_class(GTK_WIDGET(*dot), "bf-dot");
  gtk_widget_add_css_class(GTK_WIDGET(*dot),
                           hourly ? "bf-dot-hourly" : "bf-dot-weekly");
  gtk_widget_set_valign(GTK_WIDGET(*dot), GTK_ALIGN_CENTER);
  gtk_box_append(GTK_BOX(row), GTK_WIDGET(*dot));

  GtkBox* col = GTK_BOX(gtk_box_new(GTK_ORIENTATION_VERTICAL, 2));
  GtkLabel* title =
      GTK_LABEL(gtk_label_new(l10n::t(hourly ? "LblHourly" : "LblWeekly")
                                  .c_str()));
  gtk_widget_add_css_class(GTK_WIDGET(title), "bf-dim");
  gtk_widget_set_halign(GTK_WIDGET(title), GTK_ALIGN_START);
  gtk_box_append(col, GTK_WIDGET(title));

  *percent = GTK_LABEL(gtk_label_new("--"));
  gtk_widget_set_halign(GTK_WIDGET(*percent), GTK_ALIGN_START);
  gtk_widget_add_css_class(GTK_WIDGET(*percent), "bf-percent");
  gtk_widget_add_css_class(GTK_WIDGET(*percent),
                           hourly ? "bf-percent-hourly"
                                  : "bf-percent-weekly");
  gtk_box_append(col, GTK_WIDGET(*percent));

  *reset = GTK_LABEL(gtk_label_new("--"));
  gtk_widget_add_css_class(GTK_WIDGET(*reset), "bf-reset");
  gtk_widget_set_halign(GTK_WIDGET(*reset), GTK_ALIGN_START);
  gtk_box_append(col, GTK_WIDGET(*reset));

  gtk_box_append(GTK_BOX(row), GTK_WIDGET(col));
  gtk_widget_set_hexpand(row, TRUE);
  return row;
}

void build_standard_card() {
  Widgets& ui = g.ui;
  clear_card_children();
  ui.mini_percent = nullptr;
  ui.mini_label = nullptr;
  ui.mini_ring = nullptr;

  // header: titles + menu button
  GtkBox* header = GTK_BOX(gtk_box_new(GTK_ORIENTATION_HORIZONTAL, 0));
  GtkBox* titles = GTK_BOX(gtk_box_new(GTK_ORIENTATION_VERTICAL, 2));
  GtkLabel* title = GTK_LABEL(gtk_label_new("BrainFuel"));
  gtk_widget_set_halign(GTK_WIDGET(title), GTK_ALIGN_START);
  gtk_widget_add_css_class(GTK_WIDGET(title), "bf-title");
  ui.subtitle = GTK_LABEL(gtk_label_new(""));
  gtk_widget_set_halign(GTK_WIDGET(ui.subtitle), GTK_ALIGN_START);
  gtk_widget_add_css_class(GTK_WIDGET(ui.subtitle), "bf-subtitle");
  gtk_label_set_ellipsize(ui.subtitle, PANGO_ELLIPSIZE_END);
  gtk_widget_set_hexpand(GTK_WIDGET(titles), TRUE);
  gtk_box_append(titles, GTK_WIDGET(title));
  gtk_box_append(titles, GTK_WIDGET(ui.subtitle));
  gtk_box_append(header, GTK_WIDGET(titles));
  GtkWidget* menu_button = build_menu_button();
  gtk_widget_set_valign(menu_button, GTK_ALIGN_START);
  gtk_box_append(header, menu_button);
  gtk_box_append(ui.card, GTK_WIDGET(header));

  // body: rings + chips
  GtkBox* body = GTK_BOX(gtk_box_new(GTK_ORIENTATION_HORIZONTAL, 14));
  GtkDrawingArea* rings = GTK_DRAWING_AREA(gtk_drawing_area_new());
  gtk_drawing_area_set_draw_func(rings, draw_rings, nullptr, nullptr);
  gtk_widget_set_size_request(GTK_WIDGET(rings), 96, 96);
  gtk_widget_set_valign(GTK_WIDGET(rings), GTK_ALIGN_CENTER);
  ui.rings = rings;
  gtk_box_append(body, GTK_WIDGET(rings));

  GtkBox* chips = GTK_BOX(gtk_box_new(GTK_ORIENTATION_VERTICAL, 8));
  gtk_widget_set_valign(GTK_WIDGET(chips), GTK_ALIGN_CENTER);
  gtk_widget_set_hexpand(GTK_WIDGET(chips), TRUE);
  gtk_box_append(chips, build_chip(&ui.chip_dots[0], &ui.chip_percents[0],
                                   &ui.chip_resets[0], true));
  gtk_box_append(chips, build_chip(&ui.chip_dots[1], &ui.chip_percents[1],
                                   &ui.chip_resets[1], false));
  gtk_box_append(body, GTK_WIDGET(chips));
  gtk_box_append(ui.card, GTK_WIDGET(body));

  // footer: freshness / failure + refresh
  GtkBox* footer = GTK_BOX(gtk_box_new(GTK_ORIENTATION_HORIZONTAL, 6));
  GtkLabel* footer_label = GTK_LABEL(gtk_label_new(""));
  ui.footer = footer_label;
  gtk_widget_add_css_class(GTK_WIDGET(footer_label), "bf-footer");
  gtk_label_set_ellipsize(footer_label, PANGO_ELLIPSIZE_END);
  gtk_widget_set_hexpand(GTK_WIDGET(footer_label), TRUE);
  gtk_box_append(footer, GTK_WIDGET(footer_label));

  GtkOverlay* overlay = GTK_OVERLAY(gtk_overlay_new());
  GtkSpinner* spinner = GTK_SPINNER(gtk_spinner_new());
  ui.spinner = spinner;
  GtkWidget* refresh =
      gtk_button_new_from_icon_name("media-playlist-repeat-symbolic");
  ui.refresh_button = refresh;
  gtk_widget_add_css_class(refresh, "bf-refresh");
  gtk_widget_set_valign(refresh, GTK_ALIGN_CENTER);
  gtk_widget_set_tooltip_text(refresh, l10n::t("BtnRefreshQuota").c_str());
  gtk_overlay_set_child(overlay, refresh);
  gtk_overlay_add_overlay(overlay, GTK_WIDGET(spinner));
  gtk_widget_set_halign(GTK_WIDGET(spinner), GTK_ALIGN_CENTER);
  gtk_widget_set_valign(GTK_WIDGET(spinner), GTK_ALIGN_CENTER);
  gtk_widget_set_visible(GTK_WIDGET(spinner), FALSE);
  g_signal_connect(
      refresh, "clicked",
      G_CALLBACK(+[](GtkButton*, gpointer) { refresh_now(); }), nullptr);
  gtk_box_append(footer, GTK_WIDGET(overlay));
  gtk_box_append(ui.card, GTK_WIDGET(footer));

  // gestures: single click opens details, double toggles mini, drag moves
  GtkGesture* click = gtk_gesture_click_new();
  g_signal_connect(click, "pressed", G_CALLBACK(on_card_click), nullptr);
  gtk_widget_add_controller(GTK_WIDGET(ui.card), GTK_EVENT_CONTROLLER(click));
  GtkGesture* drag = gtk_gesture_drag_new();
  g_signal_connect(drag, "drag-begin", G_CALLBACK(on_card_drag), nullptr);
  gtk_widget_add_controller(GTK_WIDGET(ui.card), GTK_EVENT_CONTROLLER(drag));

  gtk_widget_set_size_request(GTK_WIDGET(ui.card), 368, 226);
}

void build_mini_card() {
  Widgets& ui = g.ui;
  clear_card_children();
  ui.rings = nullptr;
  ui.subtitle = nullptr;
  ui.footer = nullptr;
  ui.spinner = nullptr;
  ui.refresh_button = nullptr;
  ui.chip_dots[0] = ui.chip_dots[1] = nullptr;
  ui.chip_percents[0] = ui.chip_percents[1] = nullptr;
  ui.chip_resets[0] = ui.chip_resets[1] = nullptr;

  GtkBox* col = GTK_BOX(gtk_box_new(GTK_ORIENTATION_VERTICAL, 2));
  gtk_widget_set_valign(GTK_WIDGET(col), GTK_ALIGN_CENTER);
  gtk_widget_set_halign(GTK_WIDGET(col), GTK_ALIGN_CENTER);
  gtk_widget_set_hexpand(GTK_WIDGET(col), TRUE);
  gtk_widget_set_vexpand(GTK_WIDGET(col), TRUE);
  GtkDrawingArea* ring = GTK_DRAWING_AREA(gtk_drawing_area_new());
  gtk_drawing_area_set_draw_func(ring, draw_mini_ring, nullptr, nullptr);
  gtk_widget_set_size_request(GTK_WIDGET(ring), 64, 64);
  ui.mini_ring = ring;
  gtk_box_append(col, GTK_WIDGET(ring));
  ui.mini_percent = GTK_LABEL(gtk_label_new("--"));
  gtk_widget_add_css_class(GTK_WIDGET(ui.mini_percent), "bf-percent");
  gtk_widget_add_css_class(GTK_WIDGET(ui.mini_percent), "bf-percent-weekly");
  gtk_box_append(col, GTK_WIDGET(ui.mini_percent));
  ui.mini_label = GTK_LABEL(gtk_label_new(""));
  gtk_widget_add_css_class(GTK_WIDGET(ui.mini_label), "bf-reset");
  gtk_box_append(col, GTK_WIDGET(ui.mini_label));
  gtk_box_append(ui.card, GTK_WIDGET(col));

  GtkGesture* click = gtk_gesture_click_new();
  g_signal_connect(click, "pressed", G_CALLBACK(on_mini_click), nullptr);
  gtk_widget_add_controller(GTK_WIDGET(ui.card), GTK_EVENT_CONTROLLER(click));

  gtk_widget_set_size_request(GTK_WIDGET(ui.card), 118, 118);
}

void rebuild_card_for_mode() {
  Widgets& ui = g.ui;
  if (g.settings.size_mode == "mini") {
    build_mini_card();
    gtk_window_set_default_size(ui.window, 142, 142);
  } else {
    build_standard_card();
    gtk_window_set_default_size(ui.window, 392, 250);
  }
  update_card();
}

// ------------------------------------------------------------ window setup

void build_card_window(GtkApplication* app) {
  Widgets& ui = g.ui;
  GtkWindow* window = GTK_WINDOW(gtk_application_window_new(app));
  gtk_window_set_title(window, "BrainFuel");
  gtk_window_set_decorated(window, FALSE);
  gtk_window_set_resizable(window, FALSE);
  ui.window = window;
  gtk_widget_set_name(GTK_WIDGET(window), "brainfuel-card");

  ui.card = GTK_BOX(gtk_box_new(GTK_ORIENTATION_VERTICAL, 10));
  gtk_widget_add_css_class(GTK_WIDGET(ui.card), "bf-card");
  GtkBox* outer = GTK_BOX(gtk_box_new(GTK_ORIENTATION_VERTICAL, 0));
  gtk_widget_set_margin_top(GTK_WIDGET(outer), 12);
  gtk_widget_set_margin_bottom(GTK_WIDGET(outer), 12);
  gtk_widget_set_margin_start(GTK_WIDGET(outer), 12);
  gtk_widget_set_margin_end(GTK_WIDGET(outer), 12);
  gtk_box_append(outer, GTK_WIDGET(ui.card));
  gtk_window_set_child(window, GTK_WIDGET(outer));

  g_signal_connect(
      window, "map",
      G_CALLBACK(+[](GtkWidget*, gpointer) {
        g_timeout_add_full(G_PRIORITY_LOW, 50, place_top_right_once,
                           g.ui.window, nullptr);
      }),
      nullptr);
  g_signal_connect(
      window, "close-request",
      G_CALLBACK(+[](GtkWindow*, gpointer user_data) -> gboolean {
        // No tray on Linux (rivet #118): closing the card quits rather than
        // stranding the app without a restore path.
        g_application_quit(G_APPLICATION(user_data));
        return TRUE;
      }),
      app);
}

// ------------------------------------------------------------- startup rpc

void apply_bootstrap(std::vector<rivet_app::Account> accounts,
                     std::string const& active,
                     std::optional<rivet_app::QuotaSnapshot> snapshot,
                     rivet_app::SettingsData settings) {
  g.accounts = std::move(accounts);
  g.active_account_id = active;
  g.snapshot = std::move(snapshot);
  g.settings = settings;
  l10n::language() = settings.language;
  g.ready = true;
  g.status.clear();
  apply_css();
  rebuild_card_for_mode();
  rebuild_accounts_ui();
  keep_window_above(g.ui.window, g.settings.always_on_top);
}

// Fetch the four bootstrap states in sequence, then publish them together.
void bootstrap_from_api() {
  using Accounts = std::vector<rivet_app::Account>;
  using Snap = std::optional<rivet_app::QuotaSnapshot>;
  auto accounts = std::make_shared<std::optional<Accounts>>();
  auto active = std::make_shared<std::optional<std::string>>();
  auto snapshot = std::make_shared<std::optional<Snap>>();

  g.api->get_accounts_async(
      [accounts, active, snapshot](rivet_app::Result<Accounts> r) {
    if (r.succeeded()) *accounts = *r.value;
    g.api->get_active_account_id_async(
        [accounts, active, snapshot](rivet_app::Result<std::string> r2) {
          if (r2.succeeded()) *active = *r2.value;
          g.api->get_snapshot_async(
              [accounts, active, snapshot](rivet_app::Result<Snap> r3) {
                if (r3.succeeded()) *snapshot = *r3.value;
                g.api->get_settings_async(
                    [accounts, active, snapshot](
                        rivet_app::Result<rivet_app::SettingsData> r4) {
                      deliver(std::move(r4),
                              [accounts, active, snapshot](
                                  rivet_app::Result<
                                      rivet_app::SettingsData>& s) {
                                if (!s.succeeded()) {
                                  g.status = "bootstrap failed";
                                  update_card();
                                  return;
                                }
                                apply_bootstrap(
                                    accounts->value_or(Accounts{}),
                                                active->value_or(""),
                                                snapshot->value_or(
                                                    std::nullopt),
                                                *s.value);
                              });
                    });
              });
        });
  });
}

int on_backend_finished(gpointer) {
  if (g.startup_thread.joinable()) {
    g.startup_thread.join();
  }
  std::unique_ptr<rivet::linux_runtime::Backend> backend;
  std::string error;
  {
    std::lock_guard lock(g.startup_mutex);
    backend = std::move(g.startup_backend);
    error = std::move(g.startup_error);
  }
  if (g.shutting_down.load(std::memory_order_acquire)) {
    if (backend != nullptr) backend->stop();
    return G_SOURCE_REMOVE;
  }
  if (!error.empty() || backend == nullptr) {
    g.status = "Backend error: " +
               (error.empty() ? "startup completed without a backend" : error);
    update_card();
    return G_SOURCE_REMOVE;
  }
  g.backend = std::move(backend);
  g.api = std::make_unique<rivet_app::API>(*g.backend);

  // Backend events arrive on the reader thread; re-post to the main loop.
  g.backend->set_event_handler(
      [](std::string const& name, rivet::Value const& value) {
        auto decoded = std::make_shared<rivet_app::Event>(
            rivet_app::decode_event(name, value));
        post_main([decoded] {
          if (auto* snap =
                  std::get_if<rivet_app::Quota_updatedEvent>(decoded.get())) {
            g.snapshot = snap->value;
            update_card();
            update_details_content();
          } else if (auto* alert = std::get_if<rivet_app::Alert_triggeredEvent>(
                         decoded.get())) {
            // The backend owns thresholds and hysteresis; the host delivers.
            if (rivet::system::Notifications::available()) {
              rivet::system::Notifications::Notify(
                  "BrainFuel",
                  "alert-" + alert->value.account_id + "-" + alert->value.which,
                  "BrainFuel", alert->value.message);
            }
          }
        });
      });

  // initialize starts the backend refresh scheduler (mirrors macOS host).
  g.api->initialize_async([](rivet_app::Result<void> result) {
    deliver(std::move(result), [](rivet_app::Result<void>& r) {
      if (r.succeeded()) {
        bootstrap_from_api();
      } else {
        g.status = "initialize failed";
        update_card();
      }
    });
  });
  return G_SOURCE_REMOVE;
}

void start_backend() {
  auto layout = discover_runtime_layout();
  if (!layout.has_value()) {
    g.status =
        "Missing Rivet runtime layout (runtime/*.boot, res/core.zo) next to "
        "the executable. Build with raco rivet build/dev.";
    update_card();
    return;
  }
  rivet::linux_runtime::RacketRuntimeConfig config;
  config.executable_path = executable_path().string();
  config.petite_boot = layout->petite_boot.string();
  config.scheme_boot = layout->scheme_boot.string();
  config.racket_boot = layout->racket_boot.string();
  config.backend_bundle = layout->core.string();
  config.module_name = rivet_app::kModuleName;
  config.entry_symbol = rivet_app::kEntryName;

  // Booting the embedded runtime blocks on file I/O; only startup runs off
  // the main loop. Everything after completion dispatches back through
  // g_idle_add.
  g.startup_thread = std::thread([config = std::move(config)]() mutable {
    auto backend =
        std::make_unique<rivet::linux_runtime::Backend>(std::move(config));
    try {
      backend->start();
      std::lock_guard lock(g.startup_mutex);
      g.startup_backend = std::move(backend);
    } catch (std::exception const& e) {
      std::lock_guard lock(g.startup_mutex);
      g.startup_error = e.what();
    }
    g_idle_add(on_backend_finished, nullptr);
  });
}

// ------------------------------------------------------------- settings UI

GtkWidget* settings_row(GtkWidget* left, GtkWidget* control) {
  GtkBox* row = GTK_BOX(gtk_box_new(GTK_ORIENTATION_HORIZONTAL, 12));
  gtk_widget_set_hexpand(left, TRUE);
  gtk_widget_set_halign(left, GTK_ALIGN_START);
  gtk_widget_set_valign(control, GTK_ALIGN_CENTER);
  gtk_box_append(GTK_BOX(row), left);
  gtk_box_append(GTK_BOX(row), control);
  return GTK_WIDGET(row);
}

GtkWidget* make_scroll(GtkWidget* content) {
  GtkWidget* scroll = gtk_scrolled_window_new();
  gtk_scrolled_window_set_child(GTK_SCROLLED_WINDOW(scroll), content);
  gtk_widget_set_vexpand(scroll, TRUE);
  return scroll;
}

GtkWidget* make_column() {
  GtkBox* col = GTK_BOX(gtk_box_new(GTK_ORIENTATION_VERTICAL, 10));
  gtk_widget_set_margin_top(GTK_WIDGET(col), 14);
  gtk_widget_set_margin_bottom(GTK_WIDGET(col), 14);
  gtk_widget_set_margin_start(GTK_WIDGET(col), 14);
  gtk_widget_set_margin_end(GTK_WIDGET(col), 14);
  return GTK_WIDGET(col);
}

GtkWidget* section_title(char const* key) {
  char* markup = g_markup_printf_escaped("<b>%s</b>", l10n::t(key).c_str());
  GtkLabel* label = GTK_LABEL(gtk_label_new(nullptr));
  gtk_label_set_markup(label, markup);
  g_free(markup);
  gtk_widget_set_halign(GTK_WIDGET(label), GTK_ALIGN_START);
  return GTK_WIDGET(label);
}

GtkDropDown* make_dropdown(std::vector<std::string> const& labels,
                           guint selected, GCallback changed) {
  GtkStringList* list = gtk_string_list_new(nullptr);
  for (auto const& label : labels) {
    gtk_string_list_append(list, label.c_str());
  }
  GtkDropDown* dd = GTK_DROP_DOWN(gtk_drop_down_new(G_LIST_MODEL(list), nullptr));
  gtk_drop_down_set_selected(dd, selected);
  if (changed != nullptr) {
    g_signal_connect(dd, "notify::selected", changed, nullptr);
  }
  return dd;
}

// ---- change handlers (immediate-apply, backend clamps and persists)

void on_language_changed(GtkDropDown* dd, GParamSpec*, gpointer) {
  if (g.applying_settings) return;
  rivet_app::SettingsData next = g.settings;
  next.language = gtk_drop_down_get_selected(dd) == 1 ? "en" : "zh";
  // Rebuild the card so every label follows the new language; the settings
  // window is rebuilt after the deferred save settles.
  save_settings(next);
  post_main([] {
    if (g.ui.settings_window != nullptr) {
      gtk_window_destroy(g.ui.settings_window);
      g.ui.settings_window = nullptr;
    }
    open_settings_window();
  });
}

void on_theme_changed(GtkDropDown* dd, GParamSpec*, gpointer) {
  if (g.applying_settings) return;
  rivet_app::SettingsData next = g.settings;
  guint const selected = gtk_drop_down_get_selected(dd);
  next.theme = selected == 1 ? "light" : selected == 2 ? "dark" : "system";
  save_settings(next);
}

void on_palette_changed(GtkDropDown* dd, GParamSpec*, gpointer) {
  if (g.applying_settings) return;
  char const* const palettes[] = {"classic", "teal", "forest", "violet",
                                  "mono"};
  rivet_app::SettingsData next = g.settings;
  guint const selected = gtk_drop_down_get_selected(dd);
  next.ring_palette = palettes[std::min<guint>(selected, 4)];
  save_settings(next);
}

void on_opacity_changed(GtkRange* range, gpointer) {
  if (g.applying_settings) return;
  rivet_app::SettingsData next = g.settings;
  next.card_opacity_bp = static_cast<std::int64_t>(
      std::lround(gtk_range_get_value(range))) * 100;
  save_settings(next);
}

void on_topmost_toggled(GtkCheckButton* check, gpointer) {
  if (g.applying_settings) return;
  rivet_app::SettingsData next = g.settings;
  next.always_on_top = gtk_check_button_get_active(check);
  save_settings(next);
}

void on_interval_changed(GtkDropDown* dd, GParamSpec*, gpointer) {
  if (g.applying_settings) return;
  gint const intervals[] = {1, 5, 10, 15, 30, 60};
  rivet_app::SettingsData next = g.settings;
  guint const selected = gtk_drop_down_get_selected(dd);
  next.refresh_interval_minutes =
      intervals[std::min<guint>(selected, 5)];
  save_settings(next);
}

void on_hourly_remaining_toggled(GtkCheckButton* check, gpointer) {
  if (g.applying_settings) return;
  rivet_app::SettingsData next = g.settings;
  next.hourly_remaining = gtk_check_button_get_active(check);
  save_settings(next);
}

void on_weekly_remaining_toggled(GtkCheckButton* check, gpointer) {
  if (g.applying_settings) return;
  rivet_app::SettingsData next = g.settings;
  next.weekly_remaining = gtk_check_button_get_active(check);
  save_settings(next);
}

void on_autostart_toggled(GtkCheckButton* check, gpointer) {
  if (g.applying_settings) return;
  rivet_app::SettingsData next = g.settings;
  next.autostart = gtk_check_button_get_active(check);
  save_settings(next);
}

void on_notify_toggled(GtkCheckButton* check, gpointer) {
  if (g.applying_settings) return;
  rivet_app::SettingsData next = g.settings;
  next.notify_enabled = gtk_check_button_get_active(check);
  save_settings(next);
}

void on_threshold_changed(GtkRange* range, gpointer) {
  if (g.applying_settings) return;
  rivet_app::SettingsData next = g.settings;
  next.notify_threshold =
      static_cast<std::int64_t>(std::lround(gtk_range_get_value(range)));
  if (g.ui.threshold_value != nullptr) {
    gtk_label_set_text(
        g.ui.threshold_value,
        (std::string(l10n::t("LblThreshold")) + ": " +
         std::to_string(next.notify_threshold) + "%").c_str());
  }
  save_settings(next);
}

void on_platform_changed(GtkDropDown* dd, GParamSpec*, gpointer) {
  if (g.applying_settings) return;
  guint const selected = gtk_drop_down_get_selected(dd);
  bool const cli = selected == 2 || selected == 3;  // codex, claude
  gtk_widget_set_visible(GTK_WIDGET(g.ui.api_key), !cli);
  gtk_label_set_text(g.ui.account_message,
                     l10n::t(cli ? "CliLoginHint" : "AccountDesc").c_str());
}

void on_save_account_clicked(GtkButton*, gpointer) {
  char const* const platforms[] = {"cn", "intl", "codex", "claude"};
  guint const selected =
      std::min<guint>(gtk_drop_down_get_selected(g.ui.platform_dd), 3);
  std::string const key =
      gtk_editable_get_text(GTK_EDITABLE(g.ui.api_key));
  // GLM platforms require a key; Codex/Claude reuse the local CLI login.
  if (selected < 2 && key.empty()) return;
  save_account_form(
      gtk_editable_get_text(GTK_EDITABLE(g.ui.account_name)),
                    platforms[selected], key);
}

void on_accounts_dd_changed(GtkDropDown* dd, GParamSpec*, gpointer) {
  if (g.applying_settings) return;
  guint const selected = gtk_drop_down_get_selected(dd);
  if (selected < g.accounts.size()) {
    switch_account(g.accounts[selected].id);
  }
}

void on_copy_diagnostics(GtkButton* button, gpointer) {
  if (g.api == nullptr) return;
  g.api->get_diagnostics_async(
      [button](rivet_app::Result<std::string> result) {
        // Button outlives the async call: the window owns it and the user
        // cannot close the window between click and decode on this path.
        deliver(std::move(result), [button](rivet_app::Result<std::string>& r) {
          std::string const text = r.succeeded() ? *r.value : "";
          GdkClipboard* clipboard =
              gtk_widget_get_clipboard(GTK_WIDGET(button));
          gdk_clipboard_set_text(clipboard, text.c_str());
          gtk_button_set_label(button, l10n::t("DiagnosticsCopied").c_str());
        });
      });
}

void on_settings_window_destroy(GtkWindow*, gpointer) {
  g.ui.settings_window = nullptr;
  g.ui.settings_notebook = nullptr;
}

GtkWidget* build_general_tab() {
  Widgets& ui = g.ui;
  GtkWidget* col = make_column();

  gtk_box_append(GTK_BOX(col), section_title("SectionAppearance"));
  ui.language_dd = make_dropdown({"中文", "English"},
                                 g.settings.language == "en" ? 1 : 0,
                                 G_CALLBACK(on_language_changed));
  gtk_box_append(GTK_BOX(col),
                 settings_row(gtk_label_new(l10n::t("LblLanguage").c_str()),
                              GTK_WIDGET(ui.language_dd)));
  ui.theme_dd = make_dropdown(
      {l10n::t("ThemeSystem"), l10n::t("ThemeLight"), l10n::t("ThemeDark")},
      g.settings.theme == "light" ? 1 : g.settings.theme == "dark" ? 2 : 0,
      G_CALLBACK(on_theme_changed));
  gtk_box_append(GTK_BOX(col),
                 settings_row(gtk_label_new(l10n::t("LblTheme").c_str()),
                              GTK_WIDGET(ui.theme_dd)));
  ui.palette_dd = make_dropdown({"Classic", "Teal", "Forest", "Violet", "Mono"},
                                0, G_CALLBACK(on_palette_changed));
  gtk_box_append(GTK_BOX(col),
                 settings_row(gtk_label_new(l10n::t("LblRingPalette").c_str()),
                              GTK_WIDGET(ui.palette_dd)));
  GtkWidget* opacity_label = gtk_label_new(
      (std::string(l10n::t("LblOpacity")) + ": " +
       std::to_string(g.settings.card_opacity_bp / 100) + "%").c_str());
  ui.opacity_scale = GTK_SCALE(
      gtk_scale_new_with_range(GTK_ORIENTATION_HORIZONTAL, 30, 100, 5));
  gtk_range_set_value(GTK_RANGE(ui.opacity_scale),
                      static_cast<gdouble>(g.settings.card_opacity_bp / 100));
  gtk_widget_set_hexpand(GTK_WIDGET(ui.opacity_scale), TRUE);
  g_signal_connect(ui.opacity_scale, "value-changed",
                   G_CALLBACK(on_opacity_changed), nullptr);
  gtk_box_append(GTK_BOX(col),
                 settings_row(opacity_label, GTK_WIDGET(ui.opacity_scale)));

  gtk_box_append(GTK_BOX(col), section_title("SectionDesktopBehavior"));
  ui.topmost_check = GTK_CHECK_BUTTON(
      gtk_check_button_new_with_label(l10n::t("ChkAlwaysOnTop").c_str()));
  gtk_check_button_set_active(ui.topmost_check, g.settings.always_on_top);
  g_signal_connect(ui.topmost_check, "toggled",
                   G_CALLBACK(on_topmost_toggled), nullptr);
  gtk_box_append(GTK_BOX(col), GTK_WIDGET(ui.topmost_check));
  gint const intervals[] = {1, 5, 10, 15, 30, 60};
  std::vector<std::string> interval_labels;
  guint selected_interval = 2;
  for (int i = 0; i < 6; ++i) {
    interval_labels.push_back(std::to_string(intervals[i]) + " " +
                              l10n::t("UnitMinutes"));
    if (g.settings.refresh_interval_minutes == intervals[i]) {
      selected_interval = static_cast<guint>(i);
    }
  }
  ui.interval_dd = make_dropdown(interval_labels, selected_interval,
                                 G_CALLBACK(on_interval_changed));
  gtk_box_append(GTK_BOX(col),
                 settings_row(gtk_label_new(l10n::t("LblInterval").c_str()),
                              GTK_WIDGET(ui.interval_dd)));

  gtk_box_append(GTK_BOX(col), section_title("SectionQuotaDisplay"));
  ui.hourly_remaining_check = GTK_CHECK_BUTTON(
      gtk_check_button_new_with_label(l10n::t("ChkHourlyRemaining").c_str()));
  gtk_check_button_set_active(ui.hourly_remaining_check,
                              g.settings.hourly_remaining);
  g_signal_connect(ui.hourly_remaining_check, "toggled",
                   G_CALLBACK(on_hourly_remaining_toggled), nullptr);
  gtk_box_append(GTK_BOX(col), GTK_WIDGET(ui.hourly_remaining_check));
  ui.weekly_remaining_check = GTK_CHECK_BUTTON(
      gtk_check_button_new_with_label(l10n::t("ChkWeeklyRemaining").c_str()));
  gtk_check_button_set_active(ui.weekly_remaining_check,
                              g.settings.weekly_remaining);
  g_signal_connect(ui.weekly_remaining_check, "toggled",
                   G_CALLBACK(on_weekly_remaining_toggled), nullptr);
  gtk_box_append(GTK_BOX(col), GTK_WIDGET(ui.weekly_remaining_check));

  gtk_box_append(GTK_BOX(col), section_title("SectionStartup"));
  ui.autostart_check = GTK_CHECK_BUTTON(
      gtk_check_button_new_with_label(l10n::t("ChkAutostart").c_str()));
  gtk_check_button_set_active(ui.autostart_check, g.settings.autostart);
  g_signal_connect(ui.autostart_check, "toggled",
                   G_CALLBACK(on_autostart_toggled), nullptr);
  gtk_box_append(GTK_BOX(col), GTK_WIDGET(ui.autostart_check));

  return make_scroll(col);
}

GtkWidget* build_account_tab() {
  Widgets& ui = g.ui;
  GtkWidget* col = make_column();

  // active account picker + remove
  GtkStringList* account_list = gtk_string_list_new(nullptr);
  ui.accounts_dd = GTK_DROP_DOWN(gtk_drop_down_new(G_LIST_MODEL(account_list), nullptr));
  g_signal_connect(ui.accounts_dd, "notify::selected",
                   G_CALLBACK(on_accounts_dd_changed), nullptr);
  gtk_box_append(GTK_BOX(col),
                 settings_row(gtk_label_new(l10n::t("ManageAccounts").c_str()),
                              GTK_WIDGET(ui.accounts_dd)));
  GtkBox* remove_row = GTK_BOX(gtk_box_new(GTK_ORIENTATION_HORIZONTAL, 0));
  ui.remove_account_btn = GTK_BUTTON(
      gtk_button_new_with_label(l10n::t("RemoveAccount").c_str()));
  g_signal_connect(
      ui.remove_account_btn, "clicked",
      G_CALLBACK(+[](GtkButton*, gpointer) { remove_active_account(); }),
      nullptr);
  gtk_box_append(remove_row, GTK_WIDGET(ui.remove_account_btn));
  gtk_box_append(GTK_BOX(col), GTK_WIDGET(remove_row));

  gtk_box_append(GTK_BOX(col),
                 gtk_separator_new(GTK_ORIENTATION_HORIZONTAL));

  // add-account form
  gtk_box_append(GTK_BOX(col), section_title("SectionAccount"));
  ui.account_name = GTK_ENTRY(gtk_entry_new());
  gtk_entry_set_placeholder_text(ui.account_name,
                                 l10n::t("AccountNamePlaceholder").c_str());
  gtk_box_append(GTK_BOX(col),
                 settings_row(gtk_label_new(l10n::t("LblAccountName").c_str()),
                              GTK_WIDGET(ui.account_name)));

  ui.platform_dd = make_dropdown(
      {l10n::t("PlatformCn"), l10n::t("PlatformIntl"), l10n::t("PlatformCodex"),
       l10n::t("PlatformClaude")},
      0, G_CALLBACK(on_platform_changed));
  gtk_box_append(GTK_BOX(col),
                 settings_row(gtk_label_new(l10n::t("LblPlatform").c_str()),
                              GTK_WIDGET(ui.platform_dd)));

  ui.api_key = GTK_ENTRY(gtk_entry_new());
  gtk_entry_set_visibility(ui.api_key, FALSE);
  gtk_entry_set_placeholder_text(ui.api_key,
                                 l10n::t("KeyPlaceholder").c_str());
  gtk_box_append(GTK_BOX(col),
                 settings_row(gtk_label_new(l10n::t("LblApiKey").c_str()),
                              GTK_WIDGET(ui.api_key)));

  GtkBox* save_row = GTK_BOX(gtk_box_new(GTK_ORIENTATION_HORIZONTAL, 8));
  ui.save_account_btn =
      GTK_BUTTON(gtk_button_new_with_label(l10n::t("BtnSave").c_str()));
  gtk_widget_add_css_class(GTK_WIDGET(ui.save_account_btn),
                           "suggested-action");
  g_signal_connect(ui.save_account_btn, "clicked",
                   G_CALLBACK(on_save_account_clicked), nullptr);
  ui.account_message =
      GTK_LABEL(gtk_label_new(l10n::t("AccountDesc").c_str()));
  gtk_widget_add_css_class(GTK_WIDGET(ui.account_message), "bf-dim");
  gtk_label_set_wrap(ui.account_message, TRUE);
  gtk_label_set_xalign(ui.account_message, 0.0);
  gtk_widget_set_hexpand(GTK_WIDGET(ui.account_message), TRUE);
  gtk_box_append(save_row, GTK_WIDGET(ui.save_account_btn));
  gtk_box_append(save_row, GTK_WIDGET(ui.account_message));
  gtk_box_append(GTK_BOX(col), GTK_WIDGET(save_row));

  return make_scroll(col);
}

GtkWidget* build_notifications_tab() {
  Widgets& ui = g.ui;
  GtkWidget* col = make_column();

  ui.notify_check = GTK_CHECK_BUTTON(
      gtk_check_button_new_with_label(l10n::t("ChkNotify").c_str()));
  gtk_check_button_set_active(ui.notify_check, g.settings.notify_enabled);
  g_signal_connect(ui.notify_check, "toggled", G_CALLBACK(on_notify_toggled),
                   nullptr);
  gtk_box_append(GTK_BOX(col), GTK_WIDGET(ui.notify_check));

  ui.threshold_value = GTK_LABEL(gtk_label_new(
      (std::string(l10n::t("LblThreshold")) + ": " +
       std::to_string(g.settings.notify_threshold) + "%").c_str()));
  gtk_widget_set_halign(GTK_WIDGET(ui.threshold_value), GTK_ALIGN_START);
  ui.threshold_scale = GTK_SCALE(
      gtk_scale_new_with_range(GTK_ORIENTATION_HORIZONTAL, 10, 99, 1));
  gtk_range_set_value(GTK_RANGE(ui.threshold_scale),
                      static_cast<gdouble>(g.settings.notify_threshold));
  gtk_widget_set_hexpand(GTK_WIDGET(ui.threshold_scale), TRUE);
  g_signal_connect(ui.threshold_scale, "value-changed",
                   G_CALLBACK(on_threshold_changed), nullptr);
  gtk_box_append(GTK_BOX(col), GTK_WIDGET(ui.threshold_value));
  gtk_box_append(GTK_BOX(col), GTK_WIDGET(ui.threshold_scale));

  GtkLabel* desc =
      GTK_LABEL(gtk_label_new(l10n::t("NotificationsDesc").c_str()));
  gtk_widget_add_css_class(GTK_WIDGET(desc), "bf-dim");
  gtk_label_set_wrap(desc, TRUE);
  gtk_label_set_xalign(desc, 0.0);
  gtk_box_append(GTK_BOX(col), GTK_WIDGET(desc));

  return make_scroll(col);
}

GtkWidget* build_software_tab() {
  Widgets& ui = g.ui;
  GtkWidget* col = make_column();

  std::string const version_text =
      std::string(l10n::t("UpdateCurrentVersion")) + ": BrainFuel " +
      rivet_app::kVersion;
  char* version_markup =
      g_markup_printf_escaped("<b>%s</b>", version_text.c_str());
  GtkLabel* version = GTK_LABEL(gtk_label_new(nullptr));
  gtk_label_set_markup(version, version_markup);
  g_free(version_markup);
  gtk_widget_set_halign(GTK_WIDGET(version), GTK_ALIGN_START);
  gtk_box_append(GTK_BOX(col), GTK_WIDGET(version));

  GtkLabel* desc = GTK_LABEL(gtk_label_new(l10n::t("SoftwareDesc").c_str()));
  gtk_label_set_wrap(desc, TRUE);
  gtk_label_set_xalign(desc, 0.0);
  gtk_widget_add_css_class(GTK_WIDGET(desc), "bf-dim");
  gtk_box_append(GTK_BOX(col), GTK_WIDGET(desc));

  gtk_box_append(GTK_BOX(col),
                 gtk_separator_new(GTK_ORIENTATION_HORIZONTAL));

  gtk_box_append(GTK_BOX(col), section_title("SoftwareTrustTitle"));
  GtkLabel* trust =
      GTK_LABEL(gtk_label_new(l10n::t("SoftwareTrustDesc").c_str()));
  gtk_label_set_wrap(trust, TRUE);
  gtk_label_set_xalign(trust, 0.0);
  gtk_widget_add_css_class(GTK_WIDGET(trust), "bf-dim");
  gtk_box_append(GTK_BOX(col), GTK_WIDGET(trust));

  GtkBox* buttons = GTK_BOX(gtk_box_new(GTK_ORIENTATION_HORIZONTAL, 8));
  ui.copy_diagnostics =
      GTK_BUTTON(gtk_button_new_with_label(l10n::t("DiagnosticsCopy").c_str()));
  g_signal_connect(ui.copy_diagnostics, "clicked",
                   G_CALLBACK(on_copy_diagnostics), nullptr);
  GtkWidget* downloads = gtk_link_button_new_with_label(
      "https://github.com/turinglambdaai/brainfuel/releases",
      l10n::t("UpdateOpenDownloads").c_str());
  gtk_box_append(buttons, GTK_WIDGET(ui.copy_diagnostics));
  gtk_box_append(buttons, downloads);
  gtk_box_append(GTK_BOX(col), GTK_WIDGET(buttons));

  ui.software_status = GTK_LABEL(gtk_label_new(""));
  gtk_widget_add_css_class(GTK_WIDGET(ui.software_status), "bf-dim");
  gtk_widget_set_halign(GTK_WIDGET(ui.software_status), GTK_ALIGN_START);
  gtk_box_append(GTK_BOX(col), GTK_WIDGET(ui.software_status));

  return make_scroll(col);
}

void update_settings_values() {
  Widgets& ui = g.ui;
  if (ui.settings_window == nullptr) return;
  g.applying_settings = true;
  gtk_drop_down_set_selected(ui.language_dd,
                             g.settings.language == "en" ? 1 : 0);
  gtk_drop_down_set_selected(
      ui.theme_dd,
      g.settings.theme == "light" ? 1 : g.settings.theme == "dark" ? 2 : 0);
  char const* const palettes[] = {"classic", "teal", "forest", "violet",
                                  "mono"};
  for (guint i = 0; i < 5; ++i) {
    if (g.settings.ring_palette == palettes[i]) {
      gtk_drop_down_set_selected(ui.palette_dd, i);
    }
  }
  gtk_range_set_value(GTK_RANGE(ui.opacity_scale),
                      static_cast<gdouble>(g.settings.card_opacity_bp / 100));
  gtk_check_button_set_active(ui.topmost_check, g.settings.always_on_top);
  gint const intervals[] = {1, 5, 10, 15, 30, 60};
  for (guint i = 0; i < 6; ++i) {
    if (g.settings.refresh_interval_minutes == intervals[i]) {
      gtk_drop_down_set_selected(ui.interval_dd, i);
    }
  }
  gtk_check_button_set_active(ui.hourly_remaining_check,
                              g.settings.hourly_remaining);
  gtk_check_button_set_active(ui.weekly_remaining_check,
                              g.settings.weekly_remaining);
  gtk_check_button_set_active(ui.autostart_check, g.settings.autostart);
  gtk_check_button_set_active(ui.notify_check, g.settings.notify_enabled);
  gtk_range_set_value(GTK_RANGE(ui.threshold_scale),
                      static_cast<gdouble>(g.settings.notify_threshold));
  gtk_label_set_text(
      ui.threshold_value,
      (std::string(l10n::t("LblThreshold")) + ": " +
       std::to_string(g.settings.notify_threshold) + "%").c_str());
  gtk_editable_set_text(GTK_EDITABLE(ui.account_name), "");
  gtk_editable_set_text(GTK_EDITABLE(ui.api_key), "");
  gtk_drop_down_set_selected(ui.platform_dd, 0);
  gtk_widget_set_sensitive(GTK_WIDGET(ui.remove_account_btn),
                           !g.accounts.empty() &&
                               !g.active_account_id.empty());
  g.applying_settings = false;
}

void open_settings_window() {
  Widgets& ui = g.ui;
  if (!g.ready) return;
  if (ui.settings_window != nullptr) {
    gtk_window_present(ui.settings_window);
    return;
  }
  GtkWindow* window = GTK_WINDOW(gtk_application_window_new(
      GTK_APPLICATION(g_application_get_default())));
  gtk_window_set_title(window, l10n::t("SettingsTitle").c_str());
  gtk_window_set_default_size(window, 540, 620);
  ui.settings_window = window;
  g_signal_connect(window, "destroy", G_CALLBACK(on_settings_window_destroy),
                   nullptr);

  GtkNotebook* notebook = GTK_NOTEBOOK(gtk_notebook_new());
  ui.settings_notebook = notebook;
  gtk_notebook_append_page(
      notebook, build_general_tab(),
      gtk_label_new(l10n::t("TabGeneral").c_str()));
  gtk_notebook_append_page(notebook, build_account_tab(),
                           gtk_label_new(l10n::t("TabAccount").c_str()));
  gtk_notebook_append_page(
      notebook, build_notifications_tab(),
      gtk_label_new(l10n::t("TabNotifications").c_str()));
  gtk_notebook_append_page(notebook, build_software_tab(),
                           gtk_label_new(l10n::t("TabSoftware").c_str()));
  gtk_window_set_child(window, GTK_WIDGET(notebook));

  update_settings_values();
  rebuild_accounts_ui();
  keep_window_above(window, g.settings.always_on_top);
  gtk_window_present(window);
}

void on_system_theme_changed(GObject*, GParamSpec*, gpointer) {
  post_main([] {
    if (g.settings.theme != "dark" && g.settings.theme != "light") {
      apply_css();
      if (g.ui.rings != nullptr) {
        gtk_widget_queue_draw(GTK_WIDGET(g.ui.rings));
      }
      if (g.ui.mini_ring != nullptr) {
        gtk_widget_queue_draw(GTK_WIDGET(g.ui.mini_ring));
      }
    }
  });
}

// -------------------------------------------------------------- activation

void on_activate(GtkApplication* app, gpointer) {
  if (g.ui.window != nullptr) {
    gtk_window_present(g.ui.window);
    return;
  }

  g.settings = default_settings();
  l10n::language() = g.settings.language;
  install_actions(app);
  build_card_window(app);
  build_standard_card();
  apply_css();
  gtk_window_set_default_size(g.ui.window, 392, 250);
  gtk_window_present(g.ui.window);
  update_card();

  GtkSettings* gtk_settings = gtk_settings_get_default();
  g_signal_connect(gtk_settings, "notify::gtk-application-prefer-dark-theme",
                   G_CALLBACK(on_system_theme_changed), nullptr);

  g_timeout_add(30'000, on_tick, nullptr);
  start_backend();
}

void on_shutdown(GApplication*, gpointer) {
  g.shutting_down.store(true, std::memory_order_release);
  if (g.single_click_timer != 0) {
    g_source_remove(g.single_click_timer);
    g.single_click_timer = 0;
  }
  if (g.startup_thread.joinable()) {
    g.startup_thread.join();
  }
  std::unique_ptr<rivet::linux_runtime::Backend> startup_backend;
  {
    std::lock_guard lock(g.startup_mutex);
    startup_backend = std::move(g.startup_backend);
  }
  if (startup_backend != nullptr) {
    startup_backend->stop();
  }
  g.api.reset();
  if (g.backend != nullptr) {
    g.backend->stop();
    g.backend.reset();
  }
}

}  // namespace

int main(int argc, char** argv) {
  // Single instance: second launches surface the primary instead of
  // stacking cards (same contract as the macOS RivetSingleInstance use).
  g.lease = std::make_unique<rivet::system::SingleInstanceLease>(
      kApplicationId);
  if (!g.lease->is_primary()) {
    (void)g.lease->forward_arguments(rivet::system::ActivationArguments());
    return 0;
  }
  g.lease->set_activation_handler([](std::vector<std::string> const&) {
    post_main([] {
      if (g.ui.window != nullptr) {
        gtk_window_present(g.ui.window);
      }
    });
  });

  auto* app = gtk_application_new(kApplicationId, G_APPLICATION_NON_UNIQUE);
  g_signal_connect(app, "activate", G_CALLBACK(on_activate), nullptr);
  g_signal_connect(app, "shutdown", G_CALLBACK(on_shutdown), nullptr);
  int const status = g_application_run(G_APPLICATION(app), argc, argv);
  g_object_unref(app);
  g.lease.reset();
  return status;
}
