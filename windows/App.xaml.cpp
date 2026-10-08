#include "pch.h"
#include "App.xaml.h"
#include "MainWindow.xaml.h"

#include "system_services.hpp"

namespace winrt::RivetHost::implementation {

namespace {
// Second launches surface the primary instead of stacking cards (same
// contract as the Linux host's SingleInstanceLease use).
std::unique_ptr<rivet::system::SingleInstanceLease> g_lease;
}  // namespace

App::App() {
  InitializeComponent();

#if defined(_DEBUG) && !defined(DISABLE_XAML_GENERATED_BREAK_ON_UNHANDLED_EXCEPTION)
  UnhandledException([](winrt::Windows::Foundation::IInspectable const&,
                        Microsoft::UI::Xaml::UnhandledExceptionEventArgs const& e) {
    if (::IsDebuggerPresent()) {
      auto const message = e.Message();
      (void)message;
      __debugbreak();
    }
  });
#endif
}

void App::OnLaunched(Microsoft::UI::Xaml::LaunchActivatedEventArgs const&) {
  std::fprintf(stderr, "stage onlaunched-enter\n");
  std::fflush(stderr);
  g_lease =
      std::make_unique<rivet::system::SingleInstanceLease>(L"site.jrtx.brainfuel");
  if (!g_lease->is_primary()) {
    ::ExitProcess(0);
  }
  std::fprintf(stderr, "stage lease-ok\n");
  std::fflush(stderr);
  window_ = winrt::make<MainWindow>();
  std::fprintf(stderr, "stage window-made\n");
  std::fflush(stderr);
  window_.Activate();
  std::fprintf(stderr, "stage activated\n");
  std::fflush(stderr);
}

}  // namespace winrt::RivetHost::implementation
