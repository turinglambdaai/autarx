#pragma once

#include "pch.h"
#include "GeneratedBackend.hpp"
#include "MainWindow.g.h"

namespace winrt::RivetHost::implementation {

// Host helpers shared with MainWindow.Update.cpp: executable path and UTF
// conversion (taskly's HostHelpers pattern).
std::filesystem::path executable_path();
std::string utf8(std::filesystem::path const& path);
std::wstring wide(std::string const& text);

struct MainWindow : MainWindowT<MainWindow> {
  MainWindow();

  // Menu handlers
  winrt::fire_and_forget OnOpenFolder(
      winrt::Windows::Foundation::IInspectable const&,
      Microsoft::UI::Xaml::RoutedEventArgs const&);
  winrt::fire_and_forget OnOpenFile(
      winrt::Windows::Foundation::IInspectable const&,
      Microsoft::UI::Xaml::RoutedEventArgs const&);
  void OnCloseWorkspace(winrt::Windows::Foundation::IInspectable const&,
                        Microsoft::UI::Xaml::RoutedEventArgs const&);
  void OnExit(winrt::Windows::Foundation::IInspectable const&,
              Microsoft::UI::Xaml::RoutedEventArgs const&);
  void OnCheckUpdates(winrt::Windows::Foundation::IInspectable const&,
                      Microsoft::UI::Xaml::RoutedEventArgs const&);
  winrt::fire_and_forget OnAbout(
      winrt::Windows::Foundation::IInspectable const&,
      Microsoft::UI::Xaml::RoutedEventArgs const&);

  // Sidebar + pages
  void OnNavSelectionChanged(winrt::Windows::Foundation::IInspectable const&,
                             Microsoft::UI::Xaml::Controls::SelectionChangedEventArgs const&);
  void OnSearchKeyDown(winrt::Windows::Foundation::IInspectable const&,
                       Microsoft::UI::Xaml::Input::KeyRoutedEventArgs const&);
  void OnFindClick(winrt::Windows::Foundation::IInspectable const&,
                   Microsoft::UI::Xaml::RoutedEventArgs const&);
  void OnObjectSelectionChanged(winrt::Windows::Foundation::IInspectable const&,
                                Microsoft::UI::Xaml::Controls::SelectionChangedEventArgs const&);
  void OnEcuSelectionChanged(winrt::Windows::Foundation::IInspectable const&,
                             Microsoft::UI::Xaml::Controls::SelectionChangedEventArgs const&);

private:
  enum class Section { Workspace, Objects, Ecus, Unresolved, Comm };

  winrt::fire_and_forget InitializeBackendAsync();

  void ApplySection(Section section);
  void RenderNav();
  void RenderSummary();
  void RenderObjects();
  void RenderEcus();
  void RenderUnresolved();
  void RenderComm();
  void ClearInspector();
  void RenderInspector(rivet_app::SemanticObjectDto const& object);
  Microsoft::UI::Xaml::Media::Brush ThemeBrush(wchar_t const* key);
  Microsoft::UI::Xaml::Controls::Grid MakeObjectRow(
      rivet_app::SemanticObjectDto const& object);
  Microsoft::UI::Xaml::Controls::Grid MakeReferenceRow(
      rivet_app::ReferenceDto const& ref, bool arrow_form);
  void SetStatus(std::wstring const& message);
  void SetError(std::string const& message);
  void ShowProgress(std::wstring const& label);
  void HideProgress();

  winrt::fire_and_forget OpenWorkspaceAsync(std::wstring const& path);
  winrt::fire_and_forget CloseWorkspaceAsync();
  winrt::fire_and_forget ReloadAllAsync();
  winrt::fire_and_forget RunSearchAsync(std::wstring const& pattern);
  winrt::fire_and_forget LoadRefsAsync(std::wstring const& path);

  // Updates (MainWindow.Update.cpp; shared/spec/UPDATE.md flow)
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
  // copy_button: when non-empty, a secondary button copies the dialog body
  // (the failure message) to the clipboard — update errors must leave the
  // screen with the user (family rule: error text is always copyable).
  winrt::fire_and_forget ShowUpdateDialog(
      std::wstring const& title, std::wstring const& body,
      std::wstring const& primary_button, std::wstring const& close_button,
      std::function<void()> on_primary,
      std::wstring const& copy_button = L"");

  std::shared_ptr<rivet::windows::Backend> backend_;
  std::unique_ptr<rivet_app::API> api_;
  std::optional<rivet_app::WorkspaceSummary> summary_;
  std::vector<rivet_app::SemanticObjectDto> objects_;
  std::vector<rivet_app::SemanticObjectDto> ecus_;
  std::vector<rivet_app::ReferenceDto> unresolved_;
  std::optional<rivet_app::CommunicationDto> comm_;
  std::wstring current_search_;
  Section section_{Section::Workspace};
  std::wstring inspected_path_;
  bool suppress_nav_{false};
  bool update_downloading_{false};
  // Wall-clock start of the active download; a poll past the total limit
  // fails the download even if the backend state machine went quiet.
  std::int64_t update_download_start_{0};
  Microsoft::UI::Xaml::DispatcherTimer update_timer_{nullptr};
};

}  // namespace winrt::RivetHost::implementation

namespace winrt::RivetHost::factory_implementation {

struct MainWindow : MainWindowT<MainWindow, implementation::MainWindow> {};

}  // namespace winrt::RivetHost::factory_implementation
