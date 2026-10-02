#include "pch.h"
#include "MainWindow.xaml.h"
#if __has_include("MainWindow.g.cpp")
#include "MainWindow.g.cpp"
#endif
#include "GeneratedBackend.hpp"
#include "GeneratedStrings.hpp"
#include "system_services.hpp"

#include <chrono>
#include <cmath>
#include <cstdio>
#include <shobjidl_core.h>
#include <stdexcept>

namespace winrt::RivetHost::implementation {
namespace {

// WinAppSDK interop: the Window's HWND. Declared here because the header
// that ships it is not part of the C++/WinRT projection includes.
struct __declspec(uuid("EE3163B0-4986-44AC-8B2D-BB7C1D6935FF"))
    IWindowNative : ::IUnknown {
  virtual HRESULT __stdcall get_WindowHandle(HWND* hwnd) = 0;
  virtual HRESULT __stdcall put_MessageDialog(HSTRING value) = 0;
};

// ------------------------------------------------------------------ helpers

std::filesystem::path executable_path() {
  std::wstring buffer(32768, L'\0');
  auto const length = ::GetModuleFileNameW(nullptr, buffer.data(),
                                          static_cast<DWORD>(buffer.size()));
  if (length == 0 || length == buffer.size()) {
    throw std::runtime_error("GetModuleFileNameW failed");
  }
  buffer.resize(length);
  return std::filesystem::path(buffer);
}

std::string utf8(std::filesystem::path const& path) {
  auto const wide = path.wstring();
  if (wide.empty()) {
    return {};
  }
  auto const size = ::WideCharToMultiByte(CP_UTF8, WC_ERR_INVALID_CHARS,
                                          wide.data(),
                                          static_cast<int>(wide.size()),
                                          nullptr, 0, nullptr, nullptr);
  if (size <= 0) {
    throw std::runtime_error("WideCharToMultiByte failed");
  }
  std::string result(static_cast<std::size_t>(size), '\0');
  if (::WideCharToMultiByte(CP_UTF8, WC_ERR_INVALID_CHARS,
                            wide.data(), static_cast<int>(wide.size()),
                            result.data(), size, nullptr, nullptr) != size) {
    throw std::runtime_error("WideCharToMultiByte failed");
  }
  return result;
}

std::wstring wide(std::string const& text) {
  return winrt::to_hstring(text).c_str();
}

rivet::windows::RacketRuntimeConfig runtime_config() {
  auto const exe = executable_path();
  auto const root = exe.parent_path();
  auto const runtime = root / L"runtime";

  rivet::windows::RacketRuntimeConfig config;
  config.executable_path = utf8(exe);
  config.petite_boot = utf8(runtime / L"petite.boot");
  config.scheme_boot = utf8(runtime / L"scheme.boot");
  config.racket_boot = utf8(runtime / L"racket.boot");
  config.backend_bundle = utf8(root / L"res" / L"core.zo");
  config.module_name = rivet_app::kModuleName;
  config.entry_symbol = rivet_app::kEntryName;
  config.dll_dir = runtime.wstring();
  return config;
}

std::int64_t now_ms() {
  return std::chrono::duration_cast<std::chrono::milliseconds>(
             std::chrono::system_clock::now().time_since_epoch())
      .count();
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

// ------------------------------------------------------------------ palette

struct Palette {
  Windows::UI::Color card_bg;
  Windows::UI::Color text_primary;
  Windows::UI::Color text_secondary;
  Windows::UI::Color text_muted;
  Windows::UI::Color track;
};

Windows::UI::Color color(std::uint8_t r, std::uint8_t g, std::uint8_t b,
                         std::uint8_t a = 255) {
  return Windows::UI::Color{a, r, g, b};
}

bool system_dark() {
  // WinUI apps follow the OS app light/dark setting through the default
  // theme; read the registry value the shell maintains for it.
  DWORD value = 1;
  DWORD size = sizeof(value);
  if (::RegGetValueW(HKEY_CURRENT_USER,
                     L"Software\\Microsoft\\Windows\\CurrentVersion\\Themes"
                     L"\\Personalize",
                     L"AppsUseLightTheme", RRF_RT_REG_DWORD, nullptr, &value,
                     &size) == ERROR_SUCCESS) {
    return value == 0;
  }
  return true;
}

Palette palette_for_theme(std::string const& theme) {
  std::string effective = theme;
  if (effective != "dark" && effective != "light") {
    effective = system_dark() ? "dark" : "light";
  }
  if (effective == "light") {
    return Palette{color(242, 240, 233), color(31, 31, 30),
                   color(110, 105, 97), color(138, 134, 126),
                   color(222, 219, 210)};
  }
  return Palette{color(31, 31, 30),    color(240, 238, 230),
                 color(163, 158, 148), color(155, 150, 139),
                 color(58, 57, 55)};
}

// Calm ring colors per palette id: (weekly, hourly). Amber/red severity
// overrides never follow the ring palette (parity with the old widget).
std::pair<Windows::UI::Color, Windows::UI::Color> ring_colors(
    std::string const& palette_id) {
  if (palette_id == "teal")
    return {color(42, 161, 152), color(143, 211, 202)};
  if (palette_id == "forest")
    return {color(91, 140, 90), color(167, 199, 161)};
  if (palette_id == "violet")
    return {color(139, 123, 216), color(201, 191, 242)};
  if (palette_id == "mono")
    return {color(138, 134, 128), color(201, 196, 186)};
  return {color(194, 94, 62), color(181, 137, 90)};  // classic
}

Windows::UI::Color severity_color(rivet_app::Severity severity,
                                  Windows::UI::Color calm) {
  switch (severity) {
    case rivet_app::Severity::amber: return color(245, 166, 35);
    case rivet_app::Severity::red: return color(229, 72, 77);
    case rivet_app::Severity::calm: return calm;
  }
  return calm;
}

Microsoft::UI::Xaml::Media::SolidColorBrush brush(Windows::UI::Color c,
                                                  double opacity = 1.0) {
  Microsoft::UI::Xaml::Media::SolidColorBrush b(c);
  b.Opacity(opacity);
  return b;
}

// Render an Ellipse as a progress arc: dash lengths are multiples of the
// stroke thickness, so dash = fraction * circumference / thickness.
void set_arc(Microsoft::UI::Xaml::Shapes::Ellipse const& ellipse,
             double diameter_px, double thickness_px, double fraction) {
  double const circumference = 3.14159265358979323846 * diameter_px;
  double const unit = circumference / thickness_px;
  double const dash = std::clamp(fraction, 0.0, 1.0) * unit;
  Microsoft::UI::Xaml::Media::DoubleCollection dashes;
  dashes.Append(dash);
  dashes.Append(unit - dash);
  ellipse.StrokeDashArray(dashes);
}

// Settings window controls, kept in a registry the handlers can reach
// without retaining cycles through event delegates.
struct SettingsUi {
  Microsoft::UI::Xaml::Window window{nullptr};
  Microsoft::UI::Xaml::Controls::ComboBox language;
  Microsoft::UI::Xaml::Controls::ComboBox theme;
  Microsoft::UI::Xaml::Controls::ComboBox palette;
  Microsoft::UI::Xaml::Controls::Slider opacity;
  Microsoft::UI::Xaml::Controls::CheckBox topmost;
  Microsoft::UI::Xaml::Controls::ComboBox interval;
  Microsoft::UI::Xaml::Controls::CheckBox hourly_remaining;
  Microsoft::UI::Xaml::Controls::CheckBox weekly_remaining;
  Microsoft::UI::Xaml::Controls::CheckBox autostart;
  Microsoft::UI::Xaml::Controls::ComboBox accounts;
  Microsoft::UI::Xaml::Controls::Button remove_account;
  Microsoft::UI::Xaml::Controls::TextBox account_name;
  Microsoft::UI::Xaml::Controls::ComboBox platform;
  Microsoft::UI::Xaml::Controls::PasswordBox api_key;
  Microsoft::UI::Xaml::Controls::TextBlock account_message;
  Microsoft::UI::Xaml::Controls::Button save_account;
  Microsoft::UI::Xaml::Controls::CheckBox notify;
  Microsoft::UI::Xaml::Controls::Slider threshold;
  Microsoft::UI::Xaml::Controls::TextBlock threshold_value;
  Microsoft::UI::Xaml::Controls::Button copy_diagnostics;
  bool filled = false;

  void reset() { *this = SettingsUi{}; }
};
SettingsUi g_settings_ui;

}  // namespace

// ----------------------------------------------------------------- lifecycle

MainWindow::MainWindow() {
  InitializeComponent();
  l10n::language() = "zh";
  ApplyPalette();
  ApplySettingsUi();

  // The window shows the fixed-size card; size the frame around it.
  if (auto window_native = try_as<IWindowNative>()) {
    HWND hwnd = nullptr;
    if (SUCCEEDED(window_native->get_WindowHandle(&hwnd))) {
      ::SetWindowPos(hwnd, nullptr, 0, 0, 392, 250,
                     SWP_NOMOVE | SWP_NOZORDER | SWP_NOACTIVATE);
    }
  }

  Closed([weak = get_weak()](auto&&, auto&&) {
    if (auto window = weak.get()) {
      window->settings_window_ = nullptr;
      g_settings_ui.reset();
    }
  });

  InitializeBackendAsync();
}

winrt::fire_and_forget MainWindow::InitializeBackendAsync() {
  auto const dispatcher = DispatcherQueue();
  auto const weak = get_weak();
  auto backend = std::make_shared<rivet::windows::Backend>(runtime_config());

  try {
    // Booting the embedded runtime can block on file I/O, so only startup is
    // moved off the UI thread. RPC/State traffic below is completion-driven.
    co_await winrt::resume_background();
    backend->start();

    dispatcher.TryEnqueue([weak, backend = std::move(backend)]() mutable {
      if (auto window = weak.get()) {
        window->backend_ = std::move(backend);
        try {
          auto const event_dispatcher = window->DispatcherQueue();
          window->backend_->set_event_handler(
              [event_dispatcher, weak](std::string const& name,
                                       rivet::Value const& value) {
                // TryEnqueue takes a no-argument handler, so carry the
                // event payload into the capture.
                auto carried_name = name;
                auto carried_value = value;
                event_dispatcher.TryEnqueue(
                    [weak, name = std::move(carried_name),
                     value = std::move(carried_value)]() {
                      if (auto current = weak.get()) {
                        current->HandleBackendEvent(name, value);
                      }
                    });
              });
          window->Bootstrap();
        } catch (std::exception const& e) {
          window->ShowErrorUi(e.what());
        }
      } else {
        // Never destroy the last Backend reference on its own reader thread.
        std::thread([backend = std::move(backend)]() mutable {
          backend->stop();
        }).detach();
      }
    });
  } catch (std::exception const& e) {
    auto message = std::string(e.what());
    dispatcher.TryEnqueue([weak, message = std::move(message)] {
      if (auto window = weak.get()) {
        window->ShowErrorUi(message);
      }
    });
  }
}

// Fetch the four bootstrap states in sequence, then publish them together.
// The completion chain runs on the backend reader thread: it captures the
// backend shared_ptr (never `this`) and re-dispatches only at the end.
void MainWindow::Bootstrap() {
  using Accounts = std::vector<rivet_app::Account>;
  using Snap = std::optional<rivet_app::QuotaSnapshot>;
  auto const backend = backend_;
  auto const dispatcher = DispatcherQueue();
  auto const weak = get_weak();
  auto accounts = std::make_shared<std::optional<Accounts>>();
  auto active = std::make_shared<std::optional<std::string>>();
  auto snapshot = std::make_shared<std::optional<Snap>>();

  try {
    rivet_app::API api(*backend);
    (void)api.initialize_async(
        [backend, dispatcher, weak, accounts, active,
         snapshot](rivet_app::Result<void> result) {
          auto const fail = [&](std::string const& message) {
            dispatcher.TryEnqueue([weak, message] {
              if (auto window = weak.get()) {
                window->ShowErrorUi(message);
              }
            });
          };
          try {
            result.get();
          } catch (std::exception const& e) {
            fail(e.what());
            return;
          }
          // initialize starts the backend refresh scheduler (mirrors macOS).
          try {
            rivet_app::API inner(*backend);
            (void)inner.get_accounts_async(
                [backend, dispatcher, weak, accounts, active, snapshot](
                    rivet_app::Result<Accounts> r) {
                  if (r.succeeded()) *accounts = *r.value;
                  try {
                    rivet_app::API inner2(*backend);
                    (void)inner2.get_active_account_id_async(
                        [backend, dispatcher, weak, accounts, active,
                         snapshot](rivet_app::Result<std::string> r2) {
                          if (r2.succeeded()) *active = *r2.value;
                          try {
                            rivet_app::API inner3(*backend);
                            (void)inner3.get_snapshot_async(
                                [backend, dispatcher, weak, accounts, active,
                                 snapshot](rivet_app::Result<Snap> r3) {
                                  if (r3.succeeded()) *snapshot = *r3.value;
                                  rivet_app::API inner4(*backend);
                                  (void)inner4.get_settings_async(
                                      [dispatcher, weak, accounts, active,
                                       snapshot](rivet_app::Result<
                                           rivet_app::SettingsData> r4) {
                                        dispatcher.TryEnqueue(
                                            [weak, accounts, active, snapshot,
                                             result = std::move(
                                                 r4)]() mutable {
                                              if (auto window =
                                                      weak.get()) {
                                                try {
                                                  window->settings_ =
                                                      result.get();
                                                } catch (std::exception const&
                                                             e) {
                                                  window->ShowErrorUi(
                                                      e.what());
                                                  return;
                                                }
                                                l10n::language() =
                                                    window->settings_.language;
                                                window->accounts_ =
                                                    accounts->value_or({});
                                                window->active_account_id_ =
                                                    active->value_or("");
                                                window->snapshot_ =
                                                    snapshot->value_or(
                                                        std::nullopt);
                                                window->ready_ = true;
                                                window->ApplyPalette();
                                                window->ApplySettingsUi();
                                                window->ApplySnapshot();
                                                window->UpdateMenuAccounts();
                                              }
                                            });
                                      });
                                });
                          } catch (std::exception const& e) {
                            dispatcher.TryEnqueue([weak, message = std::string(
                                                             e.what())] {
                              if (auto window = weak.get()) {
                                window->ShowErrorUi(message);
                              }
                            });
                          }
                        });
                  } catch (std::exception const& e) {
                    dispatcher.TryEnqueue([weak, message = std::string(
                                                     e.what())] {
                      if (auto window = weak.get()) {
                        window->ShowErrorUi(message);
                      }
                    });
                  }
                });
          } catch (std::exception const& e) {
            fail(e.what());
          }
        });
  } catch (std::exception const& e) {
    ShowErrorUi(e.what());
  }
}

void MainWindow::HandleBackendEvent(std::string const& name,
                                    rivet::Value const& value) {
  try {
    auto event = rivet_app::decode_event(name, value);
    if (auto* snap =
            std::get_if<rivet_app::Quota_updatedEvent>(&event)) {
      snapshot_ = snap->value;
      ApplySnapshot();
    } else if (auto* alert =
                   std::get_if<rivet_app::Alert_triggeredEvent>(&event)) {
      // The backend owns thresholds and hysteresis; the host delivers.
      // Windows v1 has no tray contract yet (rivet #118 covers the Linux
      // one), so the alert surfaces on the freshness line until the next
      // snapshot refresh rewrites it.
      FooterText().Text(winrt::to_hstring(alert->value.message));
    }
  } catch (...) {
    // unknown Rivet event names are ignored
  }
}

// ---------------------------------------------------------------- ui updates

std::wstring MainWindow::SubtitleTextValue() const {
  if (accounts_.size() > 1) {
    auto const* account = &accounts_.front();
    for (auto const& a : accounts_) {
      if (a.id == active_account_id_) {
        account = &a;
        break;
      }
    }
    return wide(account->name.empty() ? account->base_domain : account->name);
  }
  return wide(l10n::t("CardSubtitle"));
}

void MainWindow::ApplyPalette() {
  auto const palette = palette_for_theme(settings_.theme);
  CardBorder().Background(brush(palette.card_bg, static_cast<double>(
                                               settings_.card_opacity_bp) /
                                               10'000.0));
  TitleText().Foreground(brush(palette.text_primary));
  SubtitleText().Foreground(brush(palette.text_secondary));
  FooterText().Foreground(brush(palette.text_muted));
  HourlyTitle().Foreground(brush(palette.text_secondary));
  WeeklyTitle().Foreground(brush(palette.text_secondary));
  HourlyReset().Foreground(brush(palette.text_muted));
  WeeklyReset().Foreground(brush(palette.text_muted));
  MiniLabel().Foreground(brush(palette.text_muted));
  MiniPercent().Foreground(brush(palette.text_primary));
  WeeklyTrack().Stroke(brush(palette.track));
  HourlyTrack().Stroke(brush(palette.track));
  MiniTrack().Stroke(brush(palette.track));
  MenuButton().Foreground(brush(palette.text_secondary));
  RefreshButton().Foreground(brush(palette.text_secondary));

  auto const classic = ring_colors("classic");
  HourlyDot().Fill(brush(severity_color(
      snapshot_ ? snapshot_->severity : rivet_app::Severity::calm,
      classic.second)));
  WeeklyDot().Fill(brush(severity_color(
      snapshot_ ? snapshot_->severity : rivet_app::Severity::calm,
      classic.first)));
}

std::string MainWindow::ChipPercent(bool hourly) const {
  if (!snapshot_.has_value()) return "--";
  auto const& usage = hourly ? snapshot_->hourly : snapshot_->weekly;
  bool const remaining =
      hourly ? settings_.hourly_remaining : settings_.weekly_remaining;
  return format_percent(usage.used_bp, remaining);
}

void MainWindow::ApplySnapshot() {
  if (!ready_) return;
  auto const severity = snapshot_.has_value()
                            ? snapshot_->severity
                            : rivet_app::Severity::calm;
  auto const classic = ring_colors(settings_.ring_palette);
  auto const weekly_color = severity_color(severity, classic.first);
  auto const hourly_color = severity_color(severity, classic.second);

  double const weekly_fraction =
      snapshot_.has_value() && snapshot_->weekly.used_bp.has_value()
          ? std::clamp(static_cast<double>(*snapshot_->weekly.used_bp) /
                           10'000.0,
                       0.0, 1.0)
          : 0.0;
  double const hourly_fraction =
      snapshot_.has_value() && snapshot_->hourly.used_bp.has_value()
          ? std::clamp(static_cast<double>(*snapshot_->hourly.used_bp) /
                           10'000.0,
                       0.0, 1.0)
          : 0.0;
  set_arc(WeeklyRing(), 88.0, 9.0, weekly_fraction);
  set_arc(HourlyRing(), 62.0, 7.0, hourly_fraction);
  WeeklyRing().Stroke(brush(weekly_color));
  HourlyRing().Stroke(brush(hourly_color));

  std::int64_t const now = now_ms();
  bool const mini = settings_.size_mode == "mini";

  if (!mini) {
    HourlyTitle().Text(winrt::to_hstring(l10n::t("LblHourly")));
    WeeklyTitle().Text(winrt::to_hstring(l10n::t("LblWeekly")));
    HourlyPercent().Text(winrt::to_hstring(ChipPercent(true)));
    WeeklyPercent().Text(winrt::to_hstring(ChipPercent(false)));
    HourlyPercent().Foreground(brush(severity_color(severity, classic.second)));
    WeeklyPercent().Foreground(brush(severity_color(severity, classic.first)));
    HourlyDot().Fill(brush(severity_color(severity, classic.second)));
    WeeklyDot().Fill(brush(severity_color(severity, classic.first)));
    auto const& hourly = snapshot_ ? snapshot_->hourly : rivet_app::WindowUsage{};
    auto const& weekly = snapshot_ ? snapshot_->weekly : rivet_app::WindowUsage{};
    HourlyReset().Text(
        hourly.reset_at_ms.has_value()
            ? winrt::to_hstring(l10n::t(
                  "DetailResets",
                  {relative_time(*hourly.reset_at_ms, now)}))
            : winrt::to_hstring(L"--"));
    WeeklyReset().Text(
        weekly.reset_at_ms.has_value()
            ? winrt::to_hstring(l10n::t(
                  "DetailResets",
                  {relative_time(*weekly.reset_at_ms, now)}))
            : winrt::to_hstring(L"--"));
    if (snapshot_.has_value() && snapshot_->failure.has_value()) {
      FooterText().Text(winrt::to_hstring(
          l10n::t(failure_key(snapshot_->failure->kind)) + ": " +
          snapshot_->failure->message));
    } else {
      std::int64_t const at = snapshot_.has_value()
                                  ? snapshot_->fetched_at_ms
                                  : now;
      FooterText().Text(winrt::to_hstring(
          l10n::t("QuotaDataStatus") + " · " + relative_time(at, now)));
    }
    SubtitleText().Text(SubtitleTextValue());
  }

  // mini card: the closer-to-exhaustion window drives the single ring
  std::int64_t const hourly_used =
      snapshot_.has_value() ? snapshot_->hourly.used_bp.value_or(0) : 0;
  std::int64_t const weekly_used =
      snapshot_.has_value() ? snapshot_->weekly.used_bp.value_or(0) : 0;
  bool const show_hourly = hourly_used >= weekly_used;
  double const mini_fraction =
      (show_hourly ? hourly_fraction : weekly_fraction);
  set_arc(MiniRing(), 60.0, 8.0, mini_fraction);
  MiniRing().Stroke(brush(show_hourly ? hourly_color : weekly_color));
  MiniPercent().Text(winrt::to_hstring(ChipPercent(show_hourly)));
  MiniLabel().Text(winrt::to_hstring(
      l10n::t(show_hourly ? "LblHourly" : "LblWeekly")));

  UpdateMenuState();
}

void MainWindow::ApplySettingsUi() {
  bool const mini = settings_.size_mode == "mini";
  CardBorder().Visibility(mini || !ready_
                              ? Microsoft::UI::Xaml::Visibility::Collapsed
                              : Microsoft::UI::Xaml::Visibility::Visible);
  MiniBorder().Visibility(mini && ready_
                             ? Microsoft::UI::Xaml::Visibility::Visible
                             : Microsoft::UI::Xaml::Visibility::Collapsed);
  BootPanel().Visibility(!ready_ ? Microsoft::UI::Xaml::Visibility::Visible
                                 : Microsoft::UI::Xaml::Visibility::Collapsed);

  if (auto window_native = try_as<IWindowNative>()) {
    if (HWND hwnd = nullptr; SUCCEEDED(window_native->get_WindowHandle(&hwnd))) {
      ::SetWindowPos(hwnd, nullptr, 0, 0, mini ? 142 : 392, mini ? 142 : 250,
                     SWP_NOMOVE | SWP_NOZORDER | SWP_NOACTIVATE);
      ::SetWindowPos(hwnd, settings_.always_on_top ? HWND_TOPMOST
                                                   : HWND_NOTOPMOST,
                     0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
    }
  }
  RefreshSettingsControls();
}

void MainWindow::SetMiniMode(bool mini) {
  rivet_app::SettingsData next = settings_;
  next.size_mode = mini ? "mini" : "standard";
  SaveSettings(next, true);
}

void MainWindow::UpdateMenuState() {
  MenuTopmost().IsChecked(settings_.always_on_top);
}

// ------------------------------------------------------------------ rpc glue

void MainWindow::OnRefreshClick(
    winrt::Windows::Foundation::IInspectable const&,
    Microsoft::UI::Xaml::RoutedEventArgs const&) {
  RefreshNow();
}

void MainWindow::OnMenuRefreshClick(
    winrt::Windows::Foundation::IInspectable const&,
    Microsoft::UI::Xaml::RoutedEventArgs const&) {
  RefreshNow();
}

void MainWindow::OnMenuDetailClick(
    winrt::Windows::Foundation::IInspectable const&,
    Microsoft::UI::Xaml::RoutedEventArgs const&) {
  ShowDetailsDialog();
}

void MainWindow::OnMenuSettingsClick(
    winrt::Windows::Foundation::IInspectable const&,
    Microsoft::UI::Xaml::RoutedEventArgs const&) {
  OpenSettingsWindow();
}

void MainWindow::OnMenuTopmostClick(
    winrt::Windows::Foundation::IInspectable const&,
    Microsoft::UI::Xaml::RoutedEventArgs const&) {
  rivet_app::SettingsData next = settings_;
  next.always_on_top = MenuTopmost().IsChecked();
  SaveSettings(next, false);
}

void MainWindow::OnMenuMiniClick(
    winrt::Windows::Foundation::IInspectable const&,
    Microsoft::UI::Xaml::RoutedEventArgs const&) {
  SetMiniMode(settings_.size_mode != "mini");
}

void MainWindow::OnMiniDoubleTapped(
    winrt::Windows::Foundation::IInspectable const&,
    Microsoft::UI::Xaml::Input::DoubleTappedRoutedEventArgs const&) {
  SetMiniMode(false);
}

void MainWindow::OnMenuQuitClick(
    winrt::Windows::Foundation::IInspectable const&,
    Microsoft::UI::Xaml::RoutedEventArgs const&) {
  Close();
}

void MainWindow::OnMenuAccountClick(
    winrt::Windows::Foundation::IInspectable const& sender,
    Microsoft::UI::Xaml::RoutedEventArgs const&) {
  auto item = sender.try_as<Microsoft::UI::Xaml::Controls::MenuFlyoutItem>();
  if (item == nullptr) return;
  auto const id = unbox_value<winrt::hstring>(item.Tag());
  SwitchAccount(winrt::to_string(id));
}

void MainWindow::RefreshNow() {
  if (!ready_ || refreshing_ || backend_ == nullptr) return;
  refreshing_ = true;
  auto const dispatcher = DispatcherQueue();
  auto const weak = get_weak();
  rivet_app::API api(*backend_);
  (void)api.refresh_now_async(
      [dispatcher, weak](rivet_app::Result<void> result) {
        dispatcher.TryEnqueue([weak, result = std::move(result)]() mutable {
          if (auto window = weak.get()) {
            window->refreshing_ = false;
            try {
              result.get();
            } catch (std::exception const& e) {
              window->ShowErrorUi(e.what());
            }
          }
        });
      });
}

void MainWindow::SwitchAccount(std::string const& id) {
  if (!ready_ || backend_ == nullptr) return;
  active_account_id_ = id;
  ApplySnapshot();
  UpdateMenuAccounts();
  auto const dispatcher = DispatcherQueue();
  auto const weak = get_weak();
  auto const backend = backend_;
  rivet_app::API api(*backend);
  (void)api.switch_account_async(
      id, [backend, dispatcher, weak](rivet_app::Result<void> result) {
        try {
          result.get();
          rivet_app::API inner(*backend);
          (void)inner.get_snapshot_async(
              [dispatcher, weak](rivet_app::Result<
                  std::optional<rivet_app::QuotaSnapshot>> snap) {
                dispatcher.TryEnqueue(
                    [weak, snap = std::move(snap)]() mutable {
                      if (auto window = weak.get()) {
                        try {
                          window->snapshot_ = snap.get();
                        } catch (...) {
                        }
                        window->ApplySnapshot();
                      }
                    });
              });
        } catch (...) {
        }
      });
}

void MainWindow::UpdateMenuAccounts() {
  MenuAccounts().Items().Clear();
  if (accounts_.size() < 2) return;
  for (auto const& account : accounts_) {
    Microsoft::UI::Xaml::Controls::MenuFlyoutItem item;
    std::string label =
        account.name.empty() ? account.base_domain : account.name;
    if (!account.configured) label += " · " + l10n::t("NotConfigured");
    item.Text(winrt::to_hstring(label));
    item.Tag(box_value(winrt::to_hstring(account.id)));
    item.Click({this, &MainWindow::OnMenuAccountClick});
    MenuAccounts().Items().Append(item);
  }
}

void MainWindow::RemoveActiveAccount() {
  if (!ready_ || backend_ == nullptr || active_account_id_.empty()) return;
  auto const dispatcher = DispatcherQueue();
  auto const weak = get_weak();
  rivet_app::API api(*backend_);
  (void)api.remove_account_async(
      active_account_id_,
      [dispatcher, weak](rivet_app::Result<std::vector<rivet_app::Account>>
                             result) {
        dispatcher.TryEnqueue([weak, result = std::move(result)]() mutable {
          if (auto window = weak.get()) {
            try {
              window->accounts_ = result.get();
            } catch (std::exception const& e) {
              window->ShowErrorUi(e.what());
              return;
            }
            bool const active_gone =
                std::none_of(window->accounts_.begin(),
                             window->accounts_.end(),
                             [&window](rivet_app::Account const& a) {
                               return a.id == window->active_account_id_;
                             });
            if (active_gone) {
              if (!window->accounts_.empty()) {
                window->SwitchAccount(window->accounts_.front().id);
              } else {
                window->active_account_id_.clear();
                window->snapshot_.reset();
                window->ApplySnapshot();
              }
            }
            window->UpdateMenuAccounts();
            window->RefreshSettingsControls();
          }
        });
      });
}

void MainWindow::SaveAccountForm(std::string const& name,
                                 std::string const& platform,
                                 std::string const& key) {
  if (backend_ == nullptr) return;
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
  auto const dispatcher = DispatcherQueue();
  auto const weak = get_weak();
  rivet_app::API api(*backend_);
  (void)api.save_account_async(
      draft,
      [dispatcher, weak](rivet_app::Result<std::vector<rivet_app::Account>>
                             result) {
        dispatcher.TryEnqueue([weak, result = std::move(result)]() mutable {
          if (auto window = weak.get()) {
            try {
              window->accounts_ = result.get();
            } catch (std::exception const& e) {
              window->ShowErrorUi(e.what());
              return;
            }
            if (window->active_account_id_.empty() &&
                !window->accounts_.empty()) {
              window->SwitchAccount(window->accounts_.front().id);
            }
            window->UpdateMenuAccounts();
            window->RefreshSettingsControls();
          }
        });
      });
}

void MainWindow::SaveSettings(rivet_app::SettingsData next, bool size_changed) {
  if (!ready_ || backend_ == nullptr) return;
  auto const previous = settings_;
  bool const autostart_changed = next.autostart != previous.autostart;
  settings_ = next;
  l10n::language() = next.language;
  if (size_changed) {
    ApplySettingsUi();
  }
  ApplyPalette();
  ApplySnapshot();
  RefreshSettingsControls();

  auto const dispatcher = DispatcherQueue();
  auto const weak = get_weak();
  rivet_app::API api(*backend_);
  (void)api.save_settings_async(
      next, [dispatcher, weak, previous, autostart_changed](
                rivet_app::Result<rivet_app::SettingsData> result) {
        dispatcher.TryEnqueue(
            [weak, previous, autostart_changed,
             result = std::move(result)]() mutable {
              if (auto window = weak.get()) {
                try {
                  auto const saved = result.get();
                  auto const old_autostart = window->settings_.autostart;
                  window->settings_ = saved;
                  l10n::language() = saved.language;
                  window->ApplyPalette();
                  window->ApplySnapshot();
                  window->RefreshSettingsControls();
                  if (autostart_changed && old_autostart != saved.autostart) {
                    rivet::system::Autostart::SetEnabled(
                        L"site.jrtx.brainfuel", executable_path().wstring(),
                        saved.autostart);
                  }
                } catch (std::exception const& e) {
                  window->settings_ = previous;
                  l10n::language() = previous.language;
                  window->ApplyPalette();
                  window->ApplySnapshot();
                  window->ShowErrorUi(e.what());
                }
              }
            });
      });
}

// ------------------------------------------------------------ details dialog

void MainWindow::ShowDetailsDialog() {
  if (!ready_ || backend_ == nullptr) return;
  auto const dispatcher = DispatcherQueue();
  auto const weak = get_weak();
  rivet_app::API api(*backend_);
  (void)api.get_details_async(
      active_account_id_,
      [dispatcher, weak](rivet_app::Result<rivet_app::Details> result) {
        dispatcher.TryEnqueue([weak, result = std::move(result)]() mutable {
          if (auto window = weak.get()) {
            window->ShowDetailsDialogContent(std::move(result));
          }
        });
      });
}

void MainWindow::ShowDetailsDialogContent(
    rivet_app::Result<rivet_app::Details> result) {
  namespace mx = Microsoft::UI::Xaml;
  auto const weak = get_weak();

  Microsoft::UI::Xaml::Controls::ContentDialog dialog;
  dialog.Title(box_value(winrt::to_hstring(
      snapshot_.has_value() && !snapshot_->plan_level.empty()
          ? snapshot_->plan_level
          : std::string(l10n::t("CardSubtitle")))));
  dialog.CloseButtonText(winrt::to_hstring(l10n::t("BtnCancel")));
  dialog.DefaultButton(
      Microsoft::UI::Xaml::Controls::ContentDialogButton::Close);

  auto const stack = Microsoft::UI::Xaml::Controls::StackPanel();
  stack.Spacing(8);
  if (!result.succeeded()) {
    Microsoft::UI::Xaml::Controls::TextBlock line;
    line.Text(winrt::to_hstring("details failed"));
    stack.Children().Append(line);
  } else {
    auto const& details = *result.value;
    auto const add_row = [&](std::string const& title,
                             rivet_app::WindowUsage const& usage,
                             rivet_app::BurnInfo const& burn) {
      Microsoft::UI::Xaml::Controls::TextBlock head;
      std::int64_t const now = now_ms();
      std::string text = title;
      if (usage.reset_at_ms.has_value()) {
        text += " · " +
                l10n::t("DetailResets",
                        {relative_time(*usage.reset_at_ms, now)});
      }
      head.Text(winrt::to_hstring(text));
      Microsoft::UI::Xaml::Controls::TextBlock percent;
      percent.FontSize(20);
      percent.Text(winrt::to_hstring(format_percent(usage.used_bp)));
      stack.Children().Append(head);
      stack.Children().Append(percent);
      if (burn.rate_bp_per_hour.has_value()) {
        std::string percent_text = format_percent(burn.rate_bp_per_hour);
        percent_text.erase(percent_text.find('%'));
        std::string exhaustion = "--";
        if (burn.minutes_to_empty.has_value()) {
          exhaustion =
              relative_time(now + *burn.minutes_to_empty * 60'000, now);
        }
        std::string line_text = l10n::t("TipBurn", {percent_text, exhaustion});
        if (burn.comparison.has_value() && !burn.comparison->empty()) {
          line_text += " " + *burn.comparison;
        }
        Microsoft::UI::Xaml::Controls::TextBlock burn_line;
        burn_line.Text(winrt::to_hstring(line_text));
        stack.Children().Append(burn_line);
      }
    };
    add_row(l10n::t("LblHourly"), details.hourly, details.hourly_burn);
    add_row(l10n::t("LblWeekly"), details.weekly, details.weekly_burn);
    if (!details.history_note.empty()) {
      Microsoft::UI::Xaml::Controls::TextBlock note;
      note.Text(winrt::to_hstring(details.history_note));
      note.FontSize(10);
      note.TextWrapping(mx::TextWrapping::Wrap);
      stack.Children().Append(note);
    }
  }
  dialog.Content(stack);
  dialog.XamlRoot(Content().XamlRoot());
  [dialog]() -> winrt::fire_and_forget { co_await dialog.ShowAsync(); }(
      std::move(dialog));
}

// ----------------------------------------------------------- settings window

namespace {

Microsoft::UI::Xaml::Controls::StackPanel settings_column() {
  auto col = Microsoft::UI::Xaml::Controls::StackPanel();
  col.Spacing(10);
  Microsoft::UI::Xaml::Thickness margin{14, 14, 14, 14};
  col.Margin(margin);
  return col;
}

Microsoft::UI::Xaml::Controls::TextBlock bold_label(winrt::hstring const& text) {
  auto label = Microsoft::UI::Xaml::Controls::TextBlock();
  label.Text(text);
  label.FontWeight(Microsoft::UI::Text::FontWeights::Bold());
  return label;
}

}  // namespace

void MainWindow::RefreshSettingsControls() {
  if (g_settings_ui.window == nullptr) return;
  applying_settings_ = true;
  g_settings_ui.language.SelectedIndex(settings_.language == "en" ? 1 : 0);
  g_settings_ui.theme.SelectedIndex(settings_.theme == "light"  ? 1
                                    : settings_.theme == "dark" ? 2
                                                                : 0);
  std::string const palettes[] = {"classic", "teal", "forest", "violet",
                                  "mono"};
  for (std::size_t i = 0; i < 5; ++i) {
    if (settings_.ring_palette == palettes[i]) {
      g_settings_ui.palette.SelectedIndex(static_cast<std::int32_t>(i));
    }
  }
  g_settings_ui.opacity.Value(static_cast<double>(settings_.card_opacity_bp /
                                                  100));
  g_settings_ui.topmost.IsChecked(settings_.always_on_top);
  std::int32_t const intervals[] = {1, 5, 10, 15, 30, 60};
  for (std::int32_t i = 0; i < 6; ++i) {
    if (settings_.refresh_interval_minutes == intervals[i]) {
      g_settings_ui.interval.SelectedIndex(i);
    }
  }
  g_settings_ui.hourly_remaining.IsChecked(settings_.hourly_remaining);
  g_settings_ui.weekly_remaining.IsChecked(settings_.weekly_remaining);
  g_settings_ui.autostart.IsChecked(settings_.autostart);
  g_settings_ui.notify.IsChecked(settings_.notify_enabled);
  g_settings_ui.threshold.Value(static_cast<double>(settings_.notify_threshold));
  g_settings_ui.threshold_value.Text(winrt::to_hstring(
      std::string(l10n::t("LblThreshold")) + ": " +
      std::to_string(settings_.notify_threshold) + "%"));
  g_settings_ui.account_name.Text({});
  g_settings_ui.api_key.Password({});
  g_settings_ui.platform.SelectedIndex(0);
  g_settings_ui.remove_account.IsEnabled(!accounts_.empty() &&
                                         !active_account_id_.empty());
  // account picker
  auto picker = g_settings_ui.accounts;
  picker.Items().Clear();
  std::int32_t active_index = 0;
  for (std::size_t i = 0; i < accounts_.size(); ++i) {
    auto const& account = accounts_[i];
    std::string label =
        account.name.empty() ? account.base_domain : account.name;
    if (!account.configured) label += " · " + l10n::t("NotConfigured");
    picker.Items().Append(winrt::box_value(winrt::to_hstring(label)));
    if (account.id == active_account_id_) {
      active_index = static_cast<std::int32_t>(i);
    }
  }
  picker.SelectedIndex(accounts_.empty() ? -1 : active_index);
  applying_settings_ = false;
}

void MainWindow::OpenSettingsWindow() {
  if (!ready_) return;
  if (settings_window_ != nullptr) {
    settings_window_.Activate();
    return;
  }
  using namespace Microsoft::UI::Xaml;
  using namespace Microsoft::UI::Xaml::Controls;

  auto const l10 = [](char const* key) { return winrt::to_hstring(l10n::t(key)); };

  SettingsUi ui;
  auto const window = Window();
  window.Title(l10("SettingsTitle"));
  g_settings_ui = std::move(ui);
  g_settings_ui.window = window;
  settings_window_ = window;

  auto const root = Pivot();
  // ---- general
  auto const general = settings_column();
  general.Children().Append(bold_label(l10("SectionAppearance")));
  auto const language = ComboBox();
  language.Items().Append(box_value(winrt::to_hstring(L"中文")));
  language.Items().Append(box_value(winrt::to_hstring(L"English")));
  g_settings_ui.language = language;
  general.Children().Append(language);
  auto const theme = ComboBox();
  theme.Items().Append(box_value(l10("ThemeSystem")));
  theme.Items().Append(box_value(l10("ThemeLight")));
  theme.Items().Append(box_value(l10("ThemeDark")));
  g_settings_ui.theme = theme;
  general.Children().Append(theme);
  auto const palette = ComboBox();
  for (char const* name : {"Classic", "Teal", "Forest", "Violet", "Mono"}) {
    palette.Items().Append(box_value(winrt::to_hstring(name)));
  }
  g_settings_ui.palette = palette;
  general.Children().Append(palette);
  auto const opacity = Slider();
  opacity.Minimum(30);
  opacity.Maximum(100);
  opacity.StepFrequency(5);
  g_settings_ui.opacity = opacity;
  general.Children().Append(opacity);

  general.Children().Append(bold_label(l10("SectionDesktopBehavior")));
  auto const topmost = CheckBox();
  topmost.Content(box_value(l10("ChkAlwaysOnTop")));
  g_settings_ui.topmost = topmost;
  general.Children().Append(topmost);
  auto const interval = ComboBox();
  for (int minutes : {1, 5, 10, 15, 30, 60}) {
    interval.Items().Append(box_value(
        winrt::to_hstring(std::to_string(minutes) + " " +
                          l10n::t("UnitMinutes"))));
  }
  g_settings_ui.interval = interval;
  general.Children().Append(interval);

  general.Children().Append(bold_label(l10("SectionQuotaDisplay")));
  auto const hourly_remaining = CheckBox();
  hourly_remaining.Content(box_value(l10("ChkHourlyRemaining")));
  g_settings_ui.hourly_remaining = hourly_remaining;
  general.Children().Append(hourly_remaining);
  auto const weekly_remaining = CheckBox();
  weekly_remaining.Content(box_value(l10("ChkWeeklyRemaining")));
  g_settings_ui.weekly_remaining = weekly_remaining;
  general.Children().Append(weekly_remaining);

  general.Children().Append(bold_label(l10("SectionStartup")));
  auto const autostart = CheckBox();
  autostart.Content(box_value(l10("ChkAutostart")));
  g_settings_ui.autostart = autostart;
  general.Children().Append(autostart);

  auto const general_item = PivotItem();
  general_item.Header(box_value(l10("TabGeneral")));
  general_item.Content(general);
  root.Items().Append(general_item);

  // ---- account
  auto const account = settings_column();
  account.Children().Append(bold_label(l10("ManageAccounts")));
  auto const accounts_pick = ComboBox();
  g_settings_ui.accounts = accounts_pick;
  account.Children().Append(accounts_pick);
  auto const remove = Button();
  remove.Content(box_value(l10("RemoveAccount")));
  g_settings_ui.remove_account = remove;
  account.Children().Append(remove);
  account.Children().Append(bold_label(l10("SectionAccount")));
  auto const name_box = TextBox();
  name_box.PlaceholderText(l10("AccountNamePlaceholder"));
  g_settings_ui.account_name = name_box;
  account.Children().Append(name_box);
  auto const platform = ComboBox();
  platform.Items().Append(box_value(l10("PlatformCn")));
  platform.Items().Append(box_value(l10("PlatformIntl")));
  platform.Items().Append(box_value(l10("PlatformCodex")));
  platform.Items().Append(box_value(l10("PlatformClaude")));
  platform.SelectedIndex(0);
  g_settings_ui.platform = platform;
  account.Children().Append(platform);
  auto const key_box = PasswordBox();
  key_box.PlaceholderText(l10("KeyPlaceholder"));
  g_settings_ui.api_key = key_box;
  account.Children().Append(key_box);
  auto const save = Button();
  save.Content(box_value(l10("BtnSave")));
  g_settings_ui.save_account = save;
  account.Children().Append(save);
  auto const message = TextBlock();
  message.Text(l10("AccountDesc"));
  message.TextWrapping(TextWrapping::Wrap);
  g_settings_ui.account_message = message;
  account.Children().Append(message);

  auto const account_item = PivotItem();
  account_item.Header(box_value(l10("TabAccount")));
  account_item.Content(account);
  root.Items().Append(account_item);

  // ---- notifications
  auto const notifications = settings_column();
  auto const notify = CheckBox();
  notify.Content(box_value(l10("ChkNotify")));
  g_settings_ui.notify = notify;
  notifications.Children().Append(notify);
  auto const threshold_value = TextBlock();
  g_settings_ui.threshold_value = threshold_value;
  notifications.Children().Append(threshold_value);
  auto const threshold = Slider();
  threshold.Minimum(10);
  threshold.Maximum(99);
  threshold.StepFrequency(1);
  g_settings_ui.threshold = threshold;
  notifications.Children().Append(threshold);
  auto const notify_desc = TextBlock();
  notify_desc.Text(l10("NotificationsDesc"));
  notify_desc.TextWrapping(TextWrapping::Wrap);
  notifications.Children().Append(notify_desc);

  auto const notifications_item = PivotItem();
  notifications_item.Header(box_value(l10("TabNotifications")));
  notifications_item.Content(notifications);
  root.Items().Append(notifications_item);

  // ---- software
  auto const software = settings_column();
  auto const version = TextBlock();
  version.Text(winrt::to_hstring(
      std::string(l10n::t("UpdateCurrentVersion")) + ": BrainFuel " +
      rivet_app::kVersion));
  software.Children().Append(version);
  auto const desc = TextBlock();
  desc.Text(l10("SoftwareDesc"));
  desc.TextWrapping(TextWrapping::Wrap);
  software.Children().Append(desc);
  software.Children().Append(bold_label(l10("SoftwareTrustTitle")));
  auto const trust = TextBlock();
  trust.Text(l10("SoftwareTrustDesc"));
  trust.TextWrapping(TextWrapping::Wrap);
  software.Children().Append(trust);
  auto const copy = Button();
  copy.Content(box_value(l10("DiagnosticsCopy")));
  g_settings_ui.copy_diagnostics = copy;
  software.Children().Append(copy);

  auto const software_item = PivotItem();
  software_item.Header(box_value(l10("TabSoftware")));
  software_item.Content(software);
  root.Items().Append(software_item);

  window.Content(root);
  RefreshSettingsControls();

  auto const weak = get_weak();
  // change handlers apply immediately; the backend clamps and persists
  language.SelectionChanged([weak](auto&&, auto&&) {
    if (auto w = weak.get()) {
      if (w->applying_settings_) return;
      rivet_app::SettingsData next = w->settings_;
      next.language =
          g_settings_ui.language.SelectedIndex() == 1 ? "en" : "zh";
      w->SaveSettings(next, false);
    }
  });
  theme.SelectionChanged([weak](auto&&, auto&&) {
    if (auto w = weak.get()) {
      if (w->applying_settings_) return;
      rivet_app::SettingsData next = w->settings_;
      auto const selected = g_settings_ui.theme.SelectedIndex();
      next.theme = selected == 1 ? "light" : selected == 2 ? "dark" : "system";
      w->SaveSettings(next, false);
    }
  });
  palette.SelectionChanged([weak](auto&&, auto&&) {
    if (auto w = weak.get()) {
      if (w->applying_settings_) return;
      char const* const palettes[] = {"classic", "teal", "forest", "violet",
                                      "mono"};
      rivet_app::SettingsData next = w->settings_;
      auto const selected = g_settings_ui.palette.SelectedIndex();
      next.ring_palette =
          palettes[std::clamp<std::int32_t>(selected, 0, 4)];
      w->SaveSettings(next, false);
    }
  });
  opacity.ValueChanged([weak](auto&&, auto&&) {
    if (auto w = weak.get()) {
      if (w->applying_settings_) return;
      rivet_app::SettingsData next = w->settings_;
      next.card_opacity_bp = static_cast<std::int64_t>(
          std::lround(g_settings_ui.opacity.Value())) * 100;
      w->SaveSettings(next, false);
    }
  });
  topmost.Click([weak](auto&&, auto&&) {
    if (auto w = weak.get()) {
      if (w->applying_settings_) return;
      rivet_app::SettingsData next = w->settings_;
      next.always_on_top = g_settings_ui.topmost.IsChecked().GetBoolean();
      w->SaveSettings(next, false);
    }
  });
  interval.SelectionChanged([weak](auto&&, auto&&) {
    if (auto w = weak.get()) {
      if (w->applying_settings_) return;
      std::int32_t const intervals[] = {1, 5, 10, 15, 30, 60};
      rivet_app::SettingsData next = w->settings_;
      auto const selected = g_settings_ui.interval.SelectedIndex();
      next.refresh_interval_minutes =
          intervals[std::clamp<std::int32_t>(selected, 0, 5)];
      w->SaveSettings(next, false);
    }
  });
  hourly_remaining.Click([weak](auto&&, auto&&) {
    if (auto w = weak.get()) {
      if (w->applying_settings_) return;
      rivet_app::SettingsData next = w->settings_;
      next.hourly_remaining =
          g_settings_ui.hourly_remaining.IsChecked().GetBoolean();
      w->SaveSettings(next, false);
    }
  });
  weekly_remaining.Click([weak](auto&&, auto&&) {
    if (auto w = weak.get()) {
      if (w->applying_settings_) return;
      rivet_app::SettingsData next = w->settings_;
      next.weekly_remaining =
          g_settings_ui.weekly_remaining.IsChecked().GetBoolean();
      w->SaveSettings(next, false);
    }
  });
  autostart.Click([weak](auto&&, auto&&) {
    if (auto w = weak.get()) {
      if (w->applying_settings_) return;
      rivet_app::SettingsData next = w->settings_;
      next.autostart = g_settings_ui.autostart.IsChecked().GetBoolean();
      w->SaveSettings(next, false);
    }
  });
  notify.Click([weak](auto&&, auto&&) {
    if (auto w = weak.get()) {
      if (w->applying_settings_) return;
      rivet_app::SettingsData next = w->settings_;
      next.notify_enabled = g_settings_ui.notify.IsChecked().GetBoolean();
      w->SaveSettings(next, false);
    }
  });
  threshold.ValueChanged([weak](auto&&, auto&&) {
    if (auto w = weak.get()) {
      if (w->applying_settings_) return;
      rivet_app::SettingsData next = w->settings_;
      next.notify_threshold = static_cast<std::int64_t>(
          std::lround(g_settings_ui.threshold.Value()));
      g_settings_ui.threshold_value.Text(winrt::to_hstring(
          std::string(l10n::t("LblThreshold")) + ": " +
          std::to_string(next.notify_threshold) + "%"));
      w->SaveSettings(next, false);
    }
  });
  platform.SelectionChanged([weak](auto&&, auto&&) {
    if (auto w = weak.get()) {
      auto const selected = g_settings_ui.platform.SelectedIndex();
      bool const cli = selected == 2 || selected == 3;
      g_settings_ui.api_key.Visibility(
          cli ? Microsoft::UI::Xaml::Visibility::Collapsed
              : Microsoft::UI::Xaml::Visibility::Visible);
      g_settings_ui.account_message.Text(
          winrt::to_hstring(l10n::t(cli ? "CliLoginHint" : "AccountDesc")));
    }
  });
  save.Click([weak](auto&&, auto&&) {
    if (auto w = weak.get()) {
      char const* const platforms[] = {"cn", "intl", "codex", "claude"};
      auto const selected = std::clamp<std::int32_t>(
          g_settings_ui.platform.SelectedIndex(), 0, 3);
      std::string const key = winrt::to_string(g_settings_ui.api_key.Password());
      if (selected < 2 && key.empty()) return;
      w->SaveAccountForm(
          winrt::to_string(g_settings_ui.account_name.Text()),
          platforms[selected], key);
    }
  });
  remove.Click([weak](auto&&, auto&&) {
    if (auto w = weak.get()) {
      w->RemoveActiveAccount();
    }
  });
  accounts_pick.SelectionChanged([weak](auto&&, auto&&) {
    if (auto w = weak.get()) {
      if (w->applying_settings_) return;
      auto const selected = g_settings_ui.accounts.SelectedIndex();
      if (selected >= 0 &&
          selected < static_cast<std::int32_t>(w->accounts_.size())) {
        w->SwitchAccount(w->accounts_[static_cast<std::size_t>(selected)].id);
      }
    }
  });
  copy.Click([weak](auto&&, auto&&) {
    if (auto w = weak.get()) {
      w->CopyDiagnostics();
    }
  });
  window.Closed([weak](auto&&, auto&&) {
    g_settings_ui.reset();
    if (auto w = weak.get()) {
      w->settings_window_ = nullptr;
    }
  });

  window.Activate();
}

void MainWindow::CopyDiagnostics() {
  if (backend_ == nullptr) return;
  auto const dispatcher = DispatcherQueue();
  auto const weak = get_weak();
  rivet_app::API api(*backend_);
  (void)api.get_diagnostics_async(
      [dispatcher, weak](rivet_app::Result<std::string> result) {
        dispatcher.TryEnqueue([weak, result = std::move(result)]() mutable {
          if (auto window = weak.get()) {
            std::string text;
            try {
              text = result.get();
            } catch (...) {
            }
            auto const data_plane =
                Windows::ApplicationModel::DataTransfer::DataPackage();
            data_plane.SetText(winrt::to_hstring(text));
            Windows::ApplicationModel::DataTransfer::Clipboard::SetContent(
                data_plane);
            g_settings_ui.copy_diagnostics.Content(
                box_value(winrt::to_hstring(l10n::t("DiagnosticsCopied"))));
          }
        });
      });
}

void MainWindow::ShowErrorUi(std::string const& message) {
  StatusBar().Severity(
      Microsoft::UI::Xaml::Controls::InfoBarSeverity::Error);
  StatusBar().Message(winrt::to_hstring(message));
}

}  // namespace winrt::RivetHost::implementation
