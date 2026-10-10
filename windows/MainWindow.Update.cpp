// Online update flow for the BrainFuel window (shared/spec/UPDATE.md),
// ported from taskly's MainWindow.Update.cpp — the family shape: silent
// throttled launch check, manual check, 400 ms progress polling, failure
// reporting on the next launch. The install handoff is ZIP-distributed:
// the handoff script swaps the extracted zip over the install directory
// itself; MSI installs under Program Files get guidance to the releases
// page instead of a swap they cannot make.

#include "pch.h"
#include "MainWindow.xaml.h"
#include "GeneratedBackend.hpp"
#include "GeneratedStrings.hpp"

#include <shellapi.h>

#include <chrono>
#include <cwctype>
#include <fstream>
#include <functional>
#include <string>

namespace winrt::RivetHost::implementation {
namespace {

namespace mx = winrt::Microsoft::UI::Xaml;
namespace mxc = winrt::Microsoft::UI::Xaml::Controls;

constexpr wchar_t kReleasesUrl[] =
    L"https://github.com/turinglambdaai/brainfuel/releases";

std::int64_t epoch_seconds() {
  FILETIME now{};
  ::GetSystemTimeAsFileTime(&now);
  ULARGE_INTEGER value{};
  value.LowPart = now.dwLowDateTime;
  value.HighPart = now.dwHighDateTime;
  // FILETIME is 100 ns ticks since 1601-01-01; the constant converts to
  // unix epoch seconds.
  return static_cast<std::int64_t>(
      (value.QuadPart - 116444736000000000ULL) / 10000000ULL);
}

std::wstring lowercase(std::wstring text) {
  for (auto& c : text) {
    c = static_cast<wchar_t>(::towlower(c));
  }
  return text;
}

std::wstring t(char const* key) { return winrt::to_hstring(l10n::t(key)); }

std::wstring tf(char const* key, std::wstring const& arg0) {
  return winrt::to_hstring(l10n::t(key, {winrt::to_string(arg0)}));
}

// %TEMP%\brainfuel-update — handoff script, extract dir, and markers live
// here.
std::filesystem::path update_work_dir() {
  wchar_t buffer[MAX_PATH]{};
  std::filesystem::path base;
  auto const length = ::GetEnvironmentVariableW(L"TEMP", buffer, MAX_PATH);
  if (length > 0 && length < MAX_PATH) {
    base = std::filesystem::path(buffer);
  } else {
    auto const fallback = ::GetEnvironmentVariableW(L"TMP", buffer, MAX_PATH);
    if (fallback > 0 && fallback < MAX_PATH) {
      base = std::filesystem::path(buffer);
    }
  }
  if (base.empty()) {
    return {};
  }
  auto const dir = base / L"brainfuel-update";
  std::error_code ec;
  std::filesystem::create_directories(dir, ec);
  return dir;
}

std::filesystem::path update_failure_marker() {
  return update_work_dir() / L"update-failed.txt";
}

std::filesystem::path update_success_marker() {
  return update_work_dir() / L"update-success.txt";
}

// The detached handoff batch. ASCII on purpose: cmd parses batches in the
// ANSI code page, so paths travel as arguments instead of being embedded in
// the script. Args: %1 zip, %2 install dir, %3 exe, %4 failure marker,
// %5 success marker. The zip is flat (release.yml packs `dist\*`, so
// RivetHost.exe / res / runtime sit at the archive root). Order matters:
// extract and verify BEFORE touching the old install, so a corrupt zip can
// never break the running copy.
std::string update_handoff_batch(unsigned long pid) {
  // Pure ASCII: every path travels as a quoted %~1…%~5 argument, never in
  // the script body, so no codepage conversion is needed (cmd.exe reads
  // batch files in the ANSI codepage — keep it that way).
  std::string const pid_text = std::to_string(pid);
  std::string batch;
  batch += "@echo off\r\n";
  batch += "rem BrainFuel update handoff - auto-generated, safe to delete.\r\n";
  batch += "set /a n=0\r\n";
  batch += ":wait\r\n";
  batch += "tasklist /FI \"PID eq " + pid_text + "\" 2>nul | find \"" +
           pid_text + "\" >nul\r\n";
  batch += "if errorlevel 1 goto extract\r\n";
  batch += "ping -n 2 127.0.0.1 >nul\r\n";
  batch += "set /a n+=1\r\n";
  batch += "if %n% LSS 30 goto wait\r\n";
  // The app never freed itself within 30 s: leave it running and report.
  batch += "> \"%~4\" echo process-exit-timeout\r\n";
  batch += "exit /b 1\r\n";
  batch += ":extract\r\n";
  batch += "set \"extract=%~dp0extract\"\r\n";
  batch += "if exist \"%extract%\" rmdir /s /q \"%extract%\"\r\n";
  batch += "mkdir \"%extract%\" 2>nul\r\n";
  batch += "tar -xf \"%~1\" -C \"%extract%\"\r\n";
  batch += "if errorlevel 1 goto fail_extract\r\n";
  batch += "if exist \"%~2.old\" rmdir /s /q \"%~2.old\"\r\n";
  batch += "move \"%~2\" \"%~2.old\" >nul 2>&1\r\n";
  batch += "if errorlevel 1 goto fail_rename\r\n";
  batch += "robocopy \"%extract%\" \"%~2\" /E /MOVE /NFL /NDL /NJH /NJS /NP >nul\r\n";
  batch += "if errorlevel 8 goto fail_swap\r\n";
  batch += "start \"\" \"%~3\"\r\n";
  batch += "> \"%~5\" echo ok\r\n";
  batch += "exit /b 0\r\n";
  batch += ":fail_swap\r\n";
  batch += "> \"%~4\" echo swap-failed (%errorlevel%)\r\n";
  batch += "move \"%~2.old\" \"%~2\" >nul 2>&1\r\n";
  batch += "goto restart_old\r\n";
  batch += ":fail_rename\r\n";
  batch += "> \"%~4\" echo rename-failed (%errorlevel%)\r\n";
  batch += "goto restart_old\r\n";
  batch += ":fail_extract\r\n";
  batch += "> \"%~4\" echo extract-failed (%errorlevel%)\r\n";
  batch += ":restart_old\r\n";
  batch += "start \"\" \"%~3\"\r\n";
  batch += "exit /b 1\r\n";
  return batch;
}

void open_releases_page() {
  ::ShellExecuteW(nullptr, L"open", kReleasesUrl, nullptr, nullptr,
                  SW_SHOWNORMAL);
}

}  // namespace

// ---------- entry points ------------------------------------------------------

void MainWindow::OnCheckUpdates(
    winrt::Windows::Foundation::IInspectable const&,
    Microsoft::UI::Xaml::RoutedEventArgs const&) {
  RunUpdateCheck(/*silent=*/false);
}

// Silent launch check: once, 3 s after the settings land, throttled to one
// attempt per 4 h (UPDATE.md triggers); failures never nag. resume_after
// resumes on the threadpool, so the continuation re-marshals through the
// dispatcher before touching window state.
winrt::fire_and_forget MainWindow::AutoCheckUpdatesAsync() {
  auto const weak = get_weak();
  auto const dispatcher = DispatcherQueue();
  co_await winrt::resume_after(std::chrono::seconds{3});
  dispatcher.TryEnqueue([weak] {
    if (auto window = weak.get()) {
      window->RunSilentUpdateCheck();
    }
  });
}

void MainWindow::RunSilentUpdateCheck() {
  if (backend_ == nullptr || !backend_->running() || update_downloading_) {
    return;
  }
  if (IsDevCopy()) {
    return;  // dev copies never phone home (UPDATE.md silent mode)
  }
  if (IsMsiInstall()) {
    return;  // MSI installs take the manual path, never the silent one
  }
  // Host-side throttle: at most one silent check per 4 h, persisted as the
  // `last-update-check` updater-state key (unix epoch seconds, shared with
  // the other platforms' hosts). Manual checks bypass this entirely.
  auto const dispatcher = DispatcherQueue();
  auto const weak = get_weak();
  auto backend = backend_;
  try {
    rivet_app::API api(*backend);
    (void)api.get_setting_async(
        "last-update-check",
        [dispatcher, weak](rivet_app::Result<std::string> result) {
          std::int64_t last = 0;
          try {
            last = std::stoll(result.get());
          } catch (...) {
            last = 0;  // empty or non-numeric → never checked
          }
          if (epoch_seconds() - last < 4 * 60 * 60) {
            return;  // throttled
          }
          dispatcher.TryEnqueue([weak] {
            if (auto window = weak.get()) {
              window->RunUpdateCheck(/*silent=*/true);
            }
          });
        });
  } catch (...) {
  }
}

// A manual check surfaces every outcome; a silent (launch-time) check never
// nags — it only reports an available update through the consent dialog.
void MainWindow::RunUpdateCheck(bool silent) {
  if (backend_ == nullptr || !backend_->running() || update_downloading_) {
    return;
  }
  if (IsDevCopy()) {
    if (!silent) {
      ShowUpdateDialog(L"BrainFuel", t("UpdateNotInstalled"),
                       t("UpdateOpenReleases"), t("BtnOK"),
                       [] { open_releases_page(); });
    }
    return;
  }
  if (IsMsiInstall()) {
    if (!silent) {
      ShowUpdateDialog(L"BrainFuel", t("UpdateMsiInstalled"),
                       t("UpdateOpenReleases"), t("BtnOK"),
                       [] { open_releases_page(); });
    }
    return;
  }
  auto const dispatcher = DispatcherQueue();
  auto const weak = get_weak();
  auto backend = backend_;
  if (!silent) {
    SetUpdateStatus(t("UpdateChecking"));
  }
  try {
    rivet_app::API api(*backend);
    (void)api.check_updates_async(
        [dispatcher, weak, silent](
            rivet_app::Result<rivet_app::UpdateCheck> result) {
          // Stamp the throttle on every terminal path (UPDATE.md triggers).
          dispatcher.TryEnqueue([weak] {
            if (auto window = weak.get()) {
              window->RecordUpdateCheck();
            }
          });
          bool ok = false;
          rivet_app::UpdateCheck check{};
          std::string failure;
          try {
            check = result.get();
            ok = true;
          } catch (std::exception const& e) {
            failure = e.what();
          }
          dispatcher.TryEnqueue(
              [weak, silent, ok, check, failure = std::move(failure)] {
                if (auto window = weak.get()) {
                  window->HandleUpdateCheckResult(ok, check, failure, silent);
                }
              });
        });
  } catch (std::exception const& e) {
    RecordUpdateCheck();
    if (!silent) {
      SetUpdateStatus(L"");
      ShowUpdateDialog(L"BrainFuel", tf("UpdateCheckFailed", wide(e.what())),
                       L"", t("BtnOK"), nullptr);
    }
  }
}

void MainWindow::HandleUpdateCheckResult(
    bool ok, rivet_app::UpdateCheck const& check, std::string const& failure,
    bool silent) {
  if (!silent) {
    SetUpdateStatus(L"");  // clear the "Checking for updates…" line
  }
  if (!ok) {
    if (!silent) {
      ShowUpdateDialog(L"BrainFuel", tf("UpdateCheckFailed", wide(failure)),
                       L"", t("BtnOK"), nullptr);
    }
    return;
  }
  if (check.status == "available") {
    auto const version =
        check.available_version.value_or(check.current_version);
    ShowUpdateDialog(
        t("UpdateAvailableTitle"), tf("UpdateAvailableBody", wide(version)),
        t("UpdateRestart"), t("BtnCancel"),
        [weak = get_weak()] {
          if (auto window = weak.get()) {
            window->StartDownload();
          }
        });
    return;
  }
  if (silent) {
    return;  // up-to-date / errors never nag in silent mode
  }
  if (check.status == "up-to-date") {
    ShowUpdateDialog(L"BrainFuel", t("UpdateUpToDate"), L"", t("BtnOK"),
                     nullptr);
    return;
  }
  auto const detail = check.error.value_or(check.status);
  ShowUpdateDialog(L"BrainFuel", tf("UpdateCheckFailed", wide(detail)), L"",
                   t("BtnOK"), nullptr);
}

void MainWindow::RecordUpdateCheck() {
  if (backend_ == nullptr || !backend_->running()) {
    return;
  }
  auto backend = backend_;
  try {
    rivet_app::API api(*backend);
    (void)api.set_setting_async(
        "last-update-check", std::to_string(epoch_seconds()),
        [backend](rivet_app::Result<void> result) {
          try {
            result.get();
          } catch (...) {
            // Throttle stamping is best-effort.
          }
        });
  } catch (...) {
  }
}

// ---------- download -----------------------------------------------------------

// Nothing downloads before the user picks "Update and restart" — an update
// is an offer, never an ambient side effect.
void MainWindow::StartDownload() {
  if (backend_ == nullptr || !backend_->running() || update_downloading_) {
    return;
  }
  update_downloading_ = true;
  SetUpdateStatus(t("UpdateDownloading"));

  auto const dispatcher = DispatcherQueue();
  auto const weak = get_weak();
  auto backend = backend_;
  try {
    rivet_app::API api(*backend);
    (void)api.start_download_async(
        [dispatcher, weak](rivet_app::Result<void> result) {
          std::string failure;
          try {
            result.get();
          } catch (std::exception const& e) {
            failure = e.what();
          }
          if (!failure.empty()) {
            dispatcher.TryEnqueue([weak, failure = std::move(failure)] {
              if (auto window = weak.get()) {
                window->FailDownload(failure);
              }
            });
          }
        });
  } catch (std::exception const& e) {
    FailDownload(e.what());
    return;
  }

  // Poll update_state while the backend downloads (the family 400 ms timer).
  update_timer_ = mx::DispatcherTimer();
  update_timer_.Interval(std::chrono::milliseconds{400});
  update_timer_.Tick([weak, backend](auto&&, auto&&) {
    if (auto window = weak.get()) {
      window->PollUpdateState(backend);
    }
  });
  update_timer_.Start();
}

void MainWindow::PollUpdateState(
    std::shared_ptr<rivet::windows::Backend> const& backend) {
  auto const dispatcher = DispatcherQueue();
  auto const weak = get_weak();
  try {
    rivet_app::API api(*backend);
    (void)api.update_state_async(
        [dispatcher, weak](rivet_app::Result<rivet_app::UpdateState> result) {
          dispatcher.TryEnqueue([weak, result] {
            if (auto window = weak.get()) {
              window->HandleUpdatePoll(result);
            }
          });
        });
  } catch (...) {
    // A transient RPC failure is not a download failure; keep polling.
  }
}

void MainWindow::HandleUpdatePoll(
    rivet_app::Result<rivet_app::UpdateState> const& result) {
  if (!update_downloading_) {
    return;
  }
  rivet_app::UpdateState state{};
  try {
    state = result.get();
  } catch (...) {
    return;  // transient poll error: the next tick retries
  }
  if (state.phase == "downloaded") {
    if (update_timer_) {
      update_timer_.Stop();
      update_timer_ = nullptr;
    }
    update_downloading_ = false;
    auto const path = state.downloaded_path.value_or("");
    if (path.empty()) {
      FailDownload("downloaded file path missing");
      return;
    }
    SetUpdateStatus(t("UpdateDownloaded"));
    ShowInstallConsent(wide(path));
    return;
  }
  if (state.phase == "error") {
    if (update_timer_) {
      update_timer_.Stop();
      update_timer_ = nullptr;
    }
    update_downloading_ = false;
    FailDownload(state.message.value_or("download failed"));
    return;
  }
  SetUpdateStatus(
      tf("UpdateDownloadPercent", std::to_wstring(state.percent)));
}

void MainWindow::FailDownload(std::string const& message) {
  update_downloading_ = false;
  if (update_timer_) {
    update_timer_.Stop();
    update_timer_ = nullptr;
  }
  SetUpdateStatus(L"");
  ShowUpdateDialog(L"BrainFuel", tf("UpdateInstallFailed", wide(message)),
                   L"", t("BtnOK"), nullptr);
}

// ---------- install handoff ------------------------------------------------------

// Installing is a separate, explicit step: the download is complete and
// verified (backend-side, family mode), and only the user's "Quit and
// install" closes the app.
winrt::fire_and_forget MainWindow::ShowInstallConsent(
    std::wstring const& path) {
  ShowUpdateDialog(
      t("UpdateDownloaded"), t("UpdateReadyBody"), t("UpdateQuitAndInstall"),
      t("BtnCancel"),
      [weak = get_weak(), path] {
        if (auto window = weak.get()) {
          window->InstallDownloadedUpdate(path);
        }
      });
  co_return;
}

void MainWindow::InstallDownloadedUpdate(std::wstring const& zip_path) {
  auto const work = update_work_dir();
  if (work.empty()) {
    ShowUpdateDialog(L"BrainFuel", tf("UpdateInstallFailed", L"temp"), L"",
                     t("BtnOK"), nullptr);
    return;
  }
  std::error_code ec;
  auto const script = work / L"update-install.cmd";
  // Stale markers from earlier attempts must not survive this one.
  std::filesystem::remove(update_failure_marker(), ec);
  std::filesystem::remove(update_success_marker(), ec);

  {
    std::ofstream file(script, std::ios::binary | std::ios::trunc);
    if (!file) {
      ShowUpdateDialog(L"BrainFuel", tf("UpdateInstallFailed", L"script"), L"",
                       t("BtnOK"), nullptr);
      return;
    }
    auto const batch = update_handoff_batch(::GetCurrentProcessId());
    file.write(batch.data(), static_cast<std::streamsize>(batch.size()));
    file.close();
    if (!file) {
      ShowUpdateDialog(L"BrainFuel", tf("UpdateInstallFailed", L"script"), L"",
                       t("BtnOK"), nullptr);
      return;
    }
  }

  std::wstring const install_dir =
      executable_path().parent_path().wstring();
  std::wstring const exe = executable_path().wstring();
  std::wstring const parameters =
      L"/c call \"" + script.wstring() + L"\" \"" + zip_path + L"\" \"" +
      install_dir + L"\" \"" + exe + L"\" \"" +
      update_failure_marker().wstring() + L"\" \"" +
      update_success_marker().wstring() + L"\"";
  SHELLEXECUTEINFOW info{};
  info.cbSize = sizeof(info);
  info.fMask = SEE_MASK_NOASYNC | SEE_MASK_FLAG_NO_UI;
  info.lpVerb = L"open";
  info.lpFile = L"cmd.exe";
  info.lpParameters = parameters.c_str();
  info.nShow = SW_HIDE;
  if (!ShellExecuteExW(&info)) {
    ShowUpdateDialog(L"BrainFuel", tf("UpdateInstallFailed", L"handoff"), L"",
                     t("BtnOK"), nullptr);
    return;
  }
  // The handoff script takes it from here: it waits for this process to
  // exit, swaps the zip in, relaunches, and leaves markers behind either way
  // (a failure marker is HandleInstallMarkers's input on the next launch).
  winrt::Microsoft::UI::Xaml::Application::Current().Exit();
}

// Surface a previous failed install once: read the error the handoff script
// left behind, clear the marker, and report (the family pattern). A success
// marker means the swapped-in version launched — retire the .old fallback
// copy.
void MainWindow::HandleInstallMarkers() {
  auto const work = update_work_dir();
  if (work.empty()) {
    return;
  }
  std::error_code ec;
  auto const success = update_success_marker();
  if (std::filesystem::exists(success, ec)) {
    std::filesystem::remove(success, ec);
    try {
      auto const install_dir = executable_path().parent_path();
      std::filesystem::remove_all(
          std::filesystem::path(install_dir.wstring() + L".old"), ec);
    } catch (...) {
      // The fallback copy is expendable; never block startup on it.
    }
    return;
  }
  auto const marker = update_failure_marker();
  if (!std::filesystem::exists(marker, ec)) {
    return;
  }
  std::ifstream file(marker);
  std::string code;
  std::getline(file, code);
  file.close();
  std::filesystem::remove(marker, ec);
  ShowUpdateDialog(
      L"BrainFuel",
      tf("UpdateInstallFailed",
         wide(code.empty() ? std::string("unknown") : code)),
      L"", t("BtnOK"), nullptr);
}

// ZIP便携版语义: a copy without the packaged layout (embedded backend at
// res/core.zo) or one running from a temp path is a dev copy — auto-update
// is unavailable there.
bool MainWindow::IsDevCopy() {
  try {
    auto const install_dir = executable_path().parent_path();
    std::error_code ec;
    if (!std::filesystem::exists(install_dir / L"res" / L"core.zo", ec)) {
      return true;
    }
    return lowercase(install_dir.wstring()).find(L"temp") !=
           std::wstring::npos;
  } catch (...) {
    return true;
  }
}

// MSI 安装版语义: the MSI lands under Program Files, where the in-place
// zip swap has no write access — those installs get the manual path
// (per-user zip installs stay fully automatic).
bool MainWindow::IsMsiInstall() {
  wchar_t buffer[32768]{};
  auto const under_program_files = [&](wchar_t const* variable) {
    DWORD const length = ::GetEnvironmentVariableW(variable, buffer, 32768);
    if (length == 0 || length >= 32768) return false;
    std::wstring program_files = lowercase(std::wstring{buffer});
    while (!program_files.empty() && program_files.back() == L'\\') {
      program_files.pop_back();
    }
    if (program_files.empty()) return false;
    std::wstring const directory =
        lowercase(executable_path().parent_path().wstring());
    return directory == program_files ||
           directory.rfind(program_files + L"\\", 0) == 0;
  };
  return under_program_files(L"ProgramFiles") ||
         under_program_files(L"ProgramFiles(x86)") ||
         under_program_files(L"ProgramW6432");
}

// ---------- update dialogs -------------------------------------------------------

// One dialog helper for the whole update flow. An empty primary label hides
// the primary button (WinUI), so info dialogs pass L"". A second dialog
// while one is open throws — swallow it instead of stacking modals.
winrt::fire_and_forget MainWindow::ShowUpdateDialog(
    std::wstring const& title, std::wstring const& body,
    std::wstring const& primary_button, std::wstring const& close_button,
    std::function<void()> on_primary) {
  auto dialog = mxc::ContentDialog{};
  dialog.Title(winrt::box_value(title));
  dialog.Content(winrt::box_value(body));
  if (!primary_button.empty()) {
    dialog.PrimaryButtonText(primary_button);
    dialog.DefaultButton(mxc::ContentDialogButton::Primary);
  }
  dialog.CloseButtonText(close_button);
  dialog.XamlRoot(Content().XamlRoot());

  auto const weak = get_weak();
  mxc::ContentDialogResult result{mxc::ContentDialogResult::None};
  try {
    result = co_await dialog.ShowAsync();
  } catch (...) {
    co_return;  // another dialog is already up; the flow can be retried
  }
  if (result == mxc::ContentDialogResult::Primary && on_primary &&
      weak.get()) {
    on_primary();
  }
}

}  // namespace winrt::RivetHost::implementation
