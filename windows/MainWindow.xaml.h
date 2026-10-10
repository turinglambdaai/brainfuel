#pragma once

#include "pch.h"
#include "MainWindow.g.h"
#include "GeneratedBackend.hpp"

#include <optional>
#include <string>
#include <vector>

namespace winrt::RivetHost::implementation {

// Progress/status copy for the update flow, shown on the settings window's
// software tab when it is open (defined in MainWindow.xaml.cpp, where the
// settings control registry lives).
void SetUpdateStatus(std::wstring const& message);

struct MainWindow : MainWindowT<MainWindow> {
  MainWindow();

  // XAML event handlers
  void OnRefreshClick(winrt::Windows::Foundation::IInspectable const&,
                      Microsoft::UI::Xaml::RoutedEventArgs const&);
  void OnMenuDetailClick(winrt::Windows::Foundation::IInspectable const&,
                         Microsoft::UI::Xaml::RoutedEventArgs const&);
  void OnMenuRefreshClick(winrt::Windows::Foundation::IInspectable const&,
                          Microsoft::UI::Xaml::RoutedEventArgs const&);
  void OnMenuSettingsClick(winrt::Windows::Foundation::IInspectable const&,
                           Microsoft::UI::Xaml::RoutedEventArgs const&);
  void OnMenuTopmostClick(winrt::Windows::Foundation::IInspectable const&,
                          Microsoft::UI::Xaml::RoutedEventArgs const&);
  void OnMenuMiniClick(winrt::Windows::Foundation::IInspectable const&,
                       Microsoft::UI::Xaml::RoutedEventArgs const&);
  void OnMenuQuitClick(winrt::Windows::Foundation::IInspectable const&,
                       Microsoft::UI::Xaml::RoutedEventArgs const&);
  void OnMiniDoubleTapped(winrt::Windows::Foundation::IInspectable const&,
                          Microsoft::UI::Xaml::Input::DoubleTappedRoutedEventArgs const&);
  void OnMenuAccountClick(winrt::Windows::Foundation::IInspectable const&,
                          Microsoft::UI::Xaml::RoutedEventArgs const&);
  void OnCheckUpdates(winrt::Windows::Foundation::IInspectable const&,
                      Microsoft::UI::Xaml::RoutedEventArgs const&);

 private:
  void Initialize();
  static void Stage(char const* stage);
  static void ReportStartupFailure(char const* message);
  winrt::fire_and_forget InitializeBackendAsync();
  void Bootstrap();
  void HandleBackendEvent(std::string const& name, rivet::Value const& value);

  void RefreshNow();
  void SwitchAccount(std::string const& id);
  void RemoveActiveAccount();
  void SaveAccountForm(std::string const& name, std::string const& platform,
                       std::string const& key);
  void SaveSettings(rivet_app::SettingsData next, bool size_changed);

  void ApplySnapshot();
  void ApplySettingsUi();
  void ApplyPalette();
  void UpdateRings();
  void SetMiniMode(bool mini);
  void UpdateMenuAccounts();
  void UpdateMenuState();
  void ShowDetailsDialog();
  void ShowDetailsDialogContent(rivet_app::Result<rivet_app::Details> result);
  void ShowErrorUi(std::string const& message);
  std::wstring SubtitleTextValue() const;
  std::string ChipPercent(bool hourly) const;

  // settings window built in code (no XAML class, no IDL entry)
  void OpenSettingsWindow();
  void RefreshSettingsControls();
  void CopyDiagnostics();

  // Updates (shared/spec/UPDATE.md; flow lives in MainWindow.Update.cpp)
  winrt::fire_and_forget AutoCheckUpdatesAsync();
  void RunSilentUpdateCheck();
  void RunUpdateCheck(bool silent);
  void HandleUpdateCheckResult(bool ok, rivet_app::UpdateCheck const& check,
                               std::string const& failure, bool silent);
  void RecordUpdateCheck();
  void StartDownload();
  void PollUpdateState(
      std::shared_ptr<rivet::windows::Backend> const& backend);
  void HandleUpdatePoll(rivet_app::Result<rivet_app::UpdateState> const& result);
  void FailDownload(std::string const& message);
  winrt::fire_and_forget ShowInstallConsent(std::wstring const& path);
  void InstallDownloadedUpdate(std::wstring const& zip_path);
  void HandleInstallMarkers();
  bool IsDevCopy();
  bool IsMsiInstall();
  winrt::fire_and_forget ShowUpdateDialog(
      std::wstring const& title, std::wstring const& body,
      std::wstring const& primary_button, std::wstring const& close_button,
      std::function<void()> on_primary);

  std::shared_ptr<rivet::windows::Backend> backend_;
  bool ready_ = false;
  bool refreshing_ = false;
  bool applying_settings_ = false;  // suppress control handlers during refresh
  std::vector<rivet_app::Account> accounts_;
  std::string active_account_id_;
  std::optional<rivet_app::QuotaSnapshot> snapshot_;
  rivet_app::SettingsData settings_{};
  Microsoft::UI::Xaml::Window settings_window_{nullptr};
  winrt::Microsoft::UI::Xaml::DispatcherTimer update_timer_{nullptr};
  bool update_downloading_{false};
};

}  // namespace winrt::RivetHost::implementation

namespace winrt::RivetHost::factory_implementation {

struct MainWindow : MainWindowT<MainWindow, implementation::MainWindow> {};

}  // namespace winrt::RivetHost::factory_implementation
