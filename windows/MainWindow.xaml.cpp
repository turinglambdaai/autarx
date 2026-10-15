#include "pch.h"
#include "MainWindow.xaml.h"
#if __has_include("MainWindow.g.cpp")
#include "MainWindow.g.cpp"
#endif
#include "GeneratedBackend.hpp"

#include <shobjidl_core.h>
#include <stdexcept>

namespace winrt::RivetHost::implementation {

namespace mx = winrt::Microsoft::UI::Xaml;
namespace mt = winrt::Microsoft::UI::Text;
namespace mxc = winrt::Microsoft::UI::Xaml::Controls;

// ---------------------------------------------------------------------------
// Host helpers shared with MainWindow.Update.cpp (taskly's HostHelpers
// pattern): executable path and UTF conversion.
// ---------------------------------------------------------------------------

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
  auto const wide_path = path.wstring();
  if (wide_path.empty()) {
    return {};
  }
  auto const size = ::WideCharToMultiByte(CP_UTF8, WC_ERR_INVALID_CHARS,
                                          wide_path.data(),
                                          static_cast<int>(wide_path.size()),
                                          nullptr, 0, nullptr, nullptr);
  if (size <= 0) {
    throw std::runtime_error("WideCharToMultiByte failed");
  }
  std::string result(static_cast<std::size_t>(size), '\0');
  if (::WideCharToMultiByte(CP_UTF8, WC_ERR_INVALID_CHARS,
                            wide_path.data(), static_cast<int>(wide_path.size()),
                            result.data(), size, nullptr, nullptr) != size) {
    throw std::runtime_error("WideCharToMultiByte failed");
  }
  return result;
}

std::wstring wide(std::string const& text) {
  if (text.empty()) {
    return {};
  }
  auto const size = ::MultiByteToWideChar(CP_UTF8, 0, text.data(),
                                          static_cast<int>(text.size()),
                                          nullptr, 0);
  if (size <= 0) {
    return {};
  }
  std::wstring result(static_cast<std::size_t>(size), L'\0');
  ::MultiByteToWideChar(CP_UTF8, 0, text.data(), static_cast<int>(text.size()),
                        result.data(), size);
  return result;
}

namespace {

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

std::wstring replace_all(std::wstring text, std::wstring const& from,
                         std::wstring const& to) {
  std::size_t position = 0;
  while ((position = text.find(from, position)) != std::wstring::npos) {
    text.replace(position, from.size(), to);
    position += to.size();
  }
  return text;
}

}  // namespace

// ---------------------------------------------------------------------------
// Construction
// ---------------------------------------------------------------------------

MainWindow::MainWindow() {
  InitializeComponent();
  Title(L"Autarx");
  // The macOS workbench defaults to 1180×820 with a 860×560 minimum.
  auto const window_native = try_as<::IWindowNative>();
  HWND hwnd = nullptr;
  winrt::check_hresult(window_native->get_WindowHandle(&hwnd));
  auto const dpi = ::GetDpiForWindow(hwnd);
  AppWindow().Resize(
      {1180 * static_cast<std::int32_t>(dpi) / 96,
       820 * static_cast<std::int32_t>(dpi) / 96});
  auto const presenter =
      AppWindow().Presenter().as<winrt::Microsoft::UI::Windowing::OverlappedPresenter>();
  presenter.PreferredMinimumWidth(860 * static_cast<std::int32_t>(dpi) / 96);
  presenter.PreferredMinimumHeight(560 * static_cast<std::int32_t>(dpi) / 96);

  SetStatus(L"Ready");
  ApplySection(Section::Workspace);
  RenderNav();
  // Hand-built rows take their brushes from code at build time, so a theme
  // flip must re-render them; the title bar colors ride the same event.
  RootGrid().ActualThemeChanged(
      [weak = get_weak()](auto&&, auto&&) {
        if (auto window = weak.get()) {
          window->RenderNav();
          window->ApplySection(window->section_);
          window->RenderSummary();
          window->RenderObjects();
          window->RenderEcus();
          window->RenderUnresolved();
          window->RenderComm();
        }
      });
  InitializeBackendAsync();
}

// ---------------------------------------------------------------------------
// Startup
// ---------------------------------------------------------------------------

winrt::fire_and_forget MainWindow::InitializeBackendAsync() {
  auto const dispatcher = DispatcherQueue();
  auto const weak = get_weak();
  auto backend = std::make_shared<rivet::windows::Backend>(runtime_config());

  try {
    // Booting the embedded runtime can block on file I/O, so only startup is
    // moved off the UI thread. RPC traffic below is completion-driven.
    co_await winrt::resume_background();
    backend->start();

    dispatcher.TryEnqueue([weak, backend = std::move(backend)]() mutable {
      if (auto window = weak.get()) {
        window->backend_ = std::move(backend);
        try {
          auto& backend_ref = *window->backend_;
          window->api_ = std::make_unique<rivet_app::API>(backend_ref);
          auto const event_dispatcher = window->DispatcherQueue();
          auto const event_weak = window->get_weak();
          // Long operations (workspace indexing, diffing) publish progress;
          // the floating capsule mirrors the macOS workbench's chip and
          // hides at 100%.
          backend_ref.set_event_handler(
              [event_dispatcher, event_weak](std::string const& name,
                                             rivet::Value const& value) {
                if (name != "progress") {
                  return;
                }
                auto const* text = std::get_if<std::string>(&value.data);
                if (text == nullptr) {
                  return;
                }
                std::string const copy = *text;
                event_dispatcher.TryEnqueue([event_weak, copy] {
                  if (auto window = event_weak.get()) {
                    if (copy.size() >= 4 &&
                        copy.compare(copy.size() - 4, 4, "100%") == 0) {
                      window->HideProgress();
                    } else {
                      window->ShowProgress(wide(copy));
                    }
                  }
                });
              });

          window->SetStatus(
              L"Backend ready — open a delivery to begin");
          // AUTARX_OPEN=<path> pre-opens a workspace (dev convenience, the
          // macOS workbench contract).
          wchar_t buffer[32768]{};
          auto const length =
              ::GetEnvironmentVariableW(L"AUTARX_OPEN", buffer, 32768);
          if (length > 0 && length < 32768) {
            window->OpenWorkspaceAsync(std::wstring(buffer));
          }
          // A previous update run may have left a failure report
          // (shared/spec/UPDATE.md); surface it once, then start the
          // throttled silent check.
          window->HandleInstallMarkers();
          // Update flow: one silent check 3 s after startup, gated by the
          // 4-hour throttle (shared/spec/UPDATE.md).
          window->AutoCheckUpdatesAsync();
        } catch (std::exception const& e) {
          window->SetError(e.what());
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
        window->SetError(message);
      }
    });
  }
}

// ---------------------------------------------------------------------------
// Rendering
// ---------------------------------------------------------------------------

mx::Media::Brush MainWindow::ThemeBrush(wchar_t const* key) {
  return RootGrid().Resources().Lookup(winrt::box_value(key))
      .as<mx::Media::Brush>();
}

void MainWindow::SetStatus(std::wstring const& message) {
  StatusText().Text(message);
}

void MainWindow::SetError(std::string const& message) {
  SetStatus(wide(message));
}

void MainWindow::ShowProgress(std::wstring const& label) {
  ProgressText().Text(label);
  ProgressCapsule().Visibility(mx::Visibility::Visible);
}

void MainWindow::HideProgress() {
  ProgressCapsule().Visibility(mx::Visibility::Collapsed);
}

// Sidebar rows: glyph + section title (the macOS NavigationSplitView list).
void MainWindow::RenderNav() {
  suppress_nav_ = true;
  struct Restore {
    MainWindow* window;
    ~Restore() { window->suppress_nav_ = false; }
  } restore{this};

  struct NavSpec {
    Section section;
    wchar_t const* glyph;
    wchar_t const* title;
  };
  static NavSpec const nav_specs[] = {
      {Section::Workspace, L"\uE8B7", L"Workspace"},
      {Section::Objects, L"\uE8A9", L"Objects"},
      {Section::Ecus, L"\uE950", L"ECUs"},
      {Section::Unresolved, L"\uE7BA", L"Unresolved"},
      {Section::Comm, L"\uE701", L"Communication"},
  };

  SidebarCaption().Text(L"AUTARX");
  NavList().Items().Clear();
  for (auto const& spec : nav_specs) {
    auto row = mxc::Grid{};
    row.ColumnDefinitions().Append(mxc::ColumnDefinition{});
    row.ColumnDefinitions().Append(mxc::ColumnDefinition{});
    row.ColumnDefinitions().GetAt(0).Width(
        mx::GridLength{0, mx::GridUnitType::Auto});
    row.ColumnDefinitions().GetAt(1).Width(
        mx::GridLength{1, mx::GridUnitType::Star});

    auto glyph = mxc::FontIcon{};
    glyph.Glyph(spec.glyph);
    glyph.FontSize(14);
    glyph.Foreground(ThemeBrush(L"AutarxSecondaryTextBrush"));
    mxc::Grid::SetColumn(glyph, 0);
    row.Children().Append(glyph);

    auto text = mxc::TextBlock{};
    text.Text(spec.title);
    text.Margin(mx::ThicknessHelper::FromLengths(10, 0, 0, 0));
    text.VerticalAlignment(mx::VerticalAlignment::Center);
    mxc::Grid::SetColumn(text, 1);
    row.Children().Append(text);

    row.Tag(winrt::box_value(static_cast<std::int32_t>(spec.section)));
    NavList().Items().Append(row);
    if (section_ == spec.section) {
      NavList().SelectedIndex(NavList().Items().Size() - 1);
    }
  }
}

// One page visible at a time; the page's own render pass runs here so a
// theme flip (which rebuilds every row) and section switches share a path.
void MainWindow::ApplySection(Section section) {
  section_ = section;
  auto const show = [section](Section candidate) {
    return section == candidate ? mx::Visibility::Visible
                                : mx::Visibility::Collapsed;
  };
  WorkspacePanel().Visibility(show(Section::Workspace));
  ObjectsPanel().Visibility(show(Section::Objects));
  EcusPanel().Visibility(show(Section::Ecus));
  UnresolvedPanel().Visibility(show(Section::Unresolved));
  CommPanel().Visibility(show(Section::Comm));

  // The inspector belongs to Objects/ECUs selections; other pages hide it.
  bool const inspector = section == Section::Objects || section == Section::Ecus;
  InspectorPanel().Visibility(inspector ? mx::Visibility::Visible
                                        : mx::Visibility::Collapsed);
  InspectorDivider().Visibility(inspector ? mx::Visibility::Visible
                                          : mx::Visibility::Collapsed);

  switch (section) {
    case Section::Workspace:
      RenderSummary();
      break;
    case Section::Objects:
      RenderObjects();
      break;
    case Section::Ecus:
      RenderEcus();
      break;
    case Section::Unresolved:
      RenderUnresolved();
      break;
    case Section::Comm:
      RenderComm();
      break;
  }
}

// Summary rows: the macOS WorkspaceSummaryView's ten key/value lines, the
// value selectable so paths and counts can leave the screen.
void MainWindow::RenderSummary() {
  SummaryList().Children().Clear();
  auto const connected = summary_.has_value();
  WorkspaceEmpty().Visibility(connected ? mx::Visibility::Collapsed
                                        : mx::Visibility::Visible);
  if (!connected) {
    return;
  }
  auto const& s = *summary_;
  struct Row {
    wchar_t const* label;
    std::wstring value;
  };
  std::vector<Row> const rows = {
      {L"Source", wide(s.source_path)},
      {L"Files", std::to_wstring(s.file_count)},
      {L"Identifiables", std::to_wstring(s.identifiable_count)},
      {L"Packages", std::to_wstring(s.package_count)},
      {L"References", std::to_wstring(s.reference_count)},
      {L"Unresolved", std::to_wstring(s.unresolved_count)},
      {L"Duplicate paths", std::to_wstring(s.duplicate_count)},
      {L"File errors", std::to_wstring(s.file_error_count)},
      {L"AUTOSAR release",
       s.autosar_release.has_value() ? wide(*s.autosar_release)
                                     : std::wstring(L"unknown")},
      {L"Schema", s.mixed_schema ? std::wstring(L"mixed flavours!")
                                 : std::wstring(L"uniform")},
  };

  bool const warn =
      s.unresolved_count > 0 || s.duplicate_count > 0 || s.file_error_count > 0;
  for (auto const& row : rows) {
    auto line = mxc::Grid{};
    line.ColumnDefinitions().Append(mxc::ColumnDefinition{});
    line.ColumnDefinitions().Append(mxc::ColumnDefinition{});
    line.ColumnDefinitions().GetAt(0).Width(
        mx::GridLength{0, mx::GridUnitType::Auto});
    line.ColumnDefinitions().GetAt(1).Width(
        mx::GridLength{1, mx::GridUnitType::Star});
    line.Padding(mx::ThicknessHelper::FromLengths(0, 5, 0, 5));

    auto label = mxc::TextBlock{};
    label.Text(row.label);
    label.Foreground(ThemeBrush(L"AutarxSecondaryTextBrush"));
    label.VerticalAlignment(mx::VerticalAlignment::Center);
    mxc::Grid::SetColumn(label, 0);
    line.Children().Append(label);

    auto value = mxc::TextBlock{};
    value.Text(row.value);
    value.TextAlignment(mx::TextAlignment::Right);
    value.TextWrapping(mx::TextWrapping::Wrap);
    value.IsTextSelectionEnabled(true);
    value.VerticalAlignment(mx::VerticalAlignment::Center);
    if (warn && (row.label == L"Unresolved" || row.label == L"Duplicate paths" ||
                 row.label == L"File errors") &&
        row.value != L"0") {
      value.Foreground(ThemeBrush(L"AutarxWarnBrush"));
    }
    mxc::Grid::SetColumn(value, 1);
    line.Children().Append(value);

    SummaryList().Children().Append(line);
  }
}

// Object row: bold SHORT-NAME, kind chip, unresolved warning, mono path —
// the macOS ObjectRow, linearized for a list row.
mxc::Grid MainWindow::MakeObjectRow(rivet_app::SemanticObjectDto const& object) {
  auto row = mxc::Grid{};
  row.ColumnDefinitions().Append(mxc::ColumnDefinition{});
  row.ColumnDefinitions().GetAt(0).Width(
      mx::GridLength{1, mx::GridUnitType::Star});
  row.Tag(winrt::box_value(wide(object.absolute_path)));

  auto content = mxc::StackPanel{};
  content.Spacing(2);
  mxc::Grid::SetColumn(content, 0);
  row.Children().Append(content);

  auto head = mxc::StackPanel{};
  head.Orientation(mxc::Orientation::Horizontal);
  head.Spacing(6);
  content.Children().Append(head);

  auto name = mxc::TextBlock{};
  name.Text(wide(object.short_name));
  name.FontSize(14);
  name.FontWeight(mt::FontWeights::SemiBold());
  name.TextTrimming(mx::TextTrimming::CharacterEllipsis);
  name.VerticalAlignment(mx::VerticalAlignment::Center);
  head.Children().Append(name);

  auto chip = mxc::Border{};
  chip.Padding(mx::ThicknessHelper::FromLengths(6, 1, 6, 1));
  chip.CornerRadius(mx::CornerRadius{4});
  chip.Background(ThemeBrush(L"AutarxAccentSoftBrush"));
  chip.VerticalAlignment(mx::VerticalAlignment::Center);
  auto chip_text = mxc::TextBlock{};
  chip_text.Text(wide(object.semantic_kind));
  chip_text.FontSize(10);
  chip_text.Foreground(ThemeBrush(L"AutarxAccentBrush"));
  chip.Child(chip_text);
  head.Children().Append(chip);

  if (object.unresolved_references > 0) {
    auto warn = mxc::StackPanel{};
    warn.Orientation(mxc::Orientation::Horizontal);
    warn.Spacing(3);
    warn.VerticalAlignment(mx::VerticalAlignment::Center);
    auto warn_icon = mxc::FontIcon{};
    warn_icon.Glyph(L"\uE7BA");
    warn_icon.FontSize(11);
    warn_icon.Foreground(ThemeBrush(L"AutarxWarnBrush"));
    warn.Children().Append(warn_icon);
    auto warn_text = mxc::TextBlock{};
    warn_text.Text(std::to_wstring(object.unresolved_references));
    warn_text.FontSize(11);
    warn_text.Foreground(ThemeBrush(L"AutarxWarnBrush"));
    warn.Children().Append(warn_text);
    mxc::ToolTipService::SetToolTip(
        warn, winrt::box_value(std::to_wstring(object.unresolved_references) +
                               L" unresolved outgoing references"));
    head.Children().Append(warn);
  }

  auto path = mxc::TextBlock{};
  path.Text(wide(object.absolute_path));
  path.FontSize(11);
  path.FontFamily(mx::Media::FontFamily(L"Consolas"));
  path.Foreground(ThemeBrush(L"AutarxSecondaryTextBrush"));
  path.TextTrimming(mx::TextTrimming::CharacterEllipsis);
  content.Children().Append(path);

  return row;
}

void MainWindow::RenderObjects() {
  suppress_nav_ = true;
  ObjectList().Items().Clear();
  for (auto const& object : objects_) {
    ObjectList().Items().Append(MakeObjectRow(object));
  }
  if (objects_.empty()) {
    auto text = mxc::TextBlock{};
    text.Text(summary_.has_value()
                  ? L"No objects match."
                  : L"No workspace open — the object index appears here.");
    text.Foreground(ThemeBrush(L"AutarxSecondaryTextBrush"));
    text.Margin(mx::ThicknessHelper::FromLengths(8, 8, 8, 8));
    ObjectList().Items().Append(text);
  }
  suppress_nav_ = false;
}

void MainWindow::RenderEcus() {
  suppress_nav_ = true;
  EcuList().Items().Clear();
  for (auto const& ecu : ecus_) {
    EcuList().Items().Append(MakeObjectRow(ecu));
  }
  if (ecus_.empty()) {
    auto text = mxc::TextBlock{};
    text.Text(summary_.has_value()
                  ? L"No ECU instances in this workspace."
                  : L"No workspace open — discovered ECUs appear here.");
    text.Foreground(ThemeBrush(L"AutarxSecondaryTextBrush"));
    text.Margin(mx::ThicknessHelper::FromLengths(8, 8, 8, 8));
    EcuList().Items().Append(text);
  }
  suppress_nav_ = false;
}

// Reference row: kind over an arrow-shaped source→target line; the
// unresolved marker rides the second line (macOS RefInspector / the
// UnresolvedView's "-[kind]->" shape).
mxc::Grid MainWindow::MakeReferenceRow(rivet_app::ReferenceDto const& ref,
                                       bool arrow_form) {
  auto row = mxc::Grid{};
  row.ColumnDefinitions().Append(mxc::ColumnDefinition{});
  row.ColumnDefinitions().GetAt(0).Width(
      mx::GridLength{1, mx::GridUnitType::Star});

  auto content = mxc::StackPanel{};
  content.Spacing(2);
  mxc::Grid::SetColumn(content, 0);
  row.Children().Append(content);

  if (arrow_form) {
    // Unresolved list form: "-[kind]-> target" under the source element.
    auto source = mxc::TextBlock{};
    source.Text(wide(ref.source_element_path));
    source.FontSize(12);
    source.FontFamily(mx::Media::FontFamily(L"Consolas"));
    source.TextWrapping(mx::TextWrapping::Wrap);
    content.Children().Append(source);

    auto edge = mxc::TextBlock{};
    edge.Text(L"-[" + wide(ref.kind) + L"]-> " + wide(ref.target_path));
    edge.FontSize(11);
    edge.FontFamily(mx::Media::FontFamily(L"Consolas"));
    edge.Foreground(ThemeBrush(L"AutarxSecondaryTextBrush"));
    edge.TextWrapping(mx::TextWrapping::Wrap);
    content.Children().Append(edge);
    return row;
  }

  auto kind = mxc::TextBlock{};
  kind.Text(wide(ref.kind));
  kind.FontSize(12);
  kind.FontWeight(mt::FontWeights::Bold());
  content.Children().Append(kind);

  auto edge = mxc::TextBlock{};
  edge.Text(wide(ref.source_path) + L" \u2192 " + wide(ref.target_path));
  edge.FontSize(11);
  edge.FontFamily(mx::Media::FontFamily(L"Consolas"));
  edge.Foreground(ThemeBrush(L"AutarxSecondaryTextBrush"));
  edge.TextWrapping(mx::TextWrapping::Wrap);
  edge.MaxLines(2);
  content.Children().Append(edge);

  if (!ref.resolved) {
    auto unresolved = mxc::TextBlock{};
    unresolved.Text(L"unresolved");
    unresolved.FontSize(10);
    unresolved.Foreground(ThemeBrush(L"AutarxWarnBrush"));
    content.Children().Append(unresolved);
  }
  return row;
}

void MainWindow::RenderUnresolved() {
  suppress_nav_ = true;
  UnresolvedList().Items().Clear();
  for (auto const& ref : unresolved_) {
    UnresolvedList().Items().Append(MakeReferenceRow(ref, /*arrow_form=*/true));
  }
  if (unresolved_.empty()) {
    auto row = mxc::StackPanel{};
    row.HorizontalAlignment(mx::HorizontalAlignment::Center);
    row.Margin(mx::ThicknessHelper::FromLengths(0, 40, 0, 0));
    row.Spacing(6);
    auto icon = mxc::TextBlock{};
    icon.Text(L"\u2713");
    icon.FontSize(28);
    icon.HorizontalAlignment(mx::HorizontalAlignment::Center);
    icon.Foreground(ThemeBrush(L"AutarxMutedTextBrush"));
    row.Children().Append(icon);
    auto text = mxc::TextBlock{};
    text.Text(summary_.has_value() ? L"No unresolved references"
                                   : L"No workspace open.");
    text.HorizontalAlignment(mx::HorizontalAlignment::Center);
    text.Foreground(ThemeBrush(L"AutarxSecondaryTextBrush"));
    row.Children().Append(text);
    UnresolvedList().Items().Append(row);
  }
  suppress_nav_ = false;
}

void MainWindow::RenderComm() {
  suppress_nav_ = true;
  CommList().Items().Clear();
  auto const connected = comm_.has_value();
  if (connected) {
    auto const& comm = *comm_;
    if (!comm.clusters.empty()) {
      auto caption = mxc::TextBlock{};
      caption.Text(L"Clusters");
      caption.FontSize(13);
      caption.FontWeight(mt::FontWeights::SemiBold());
      caption.Foreground(ThemeBrush(L"AutarxSecondaryTextBrush"));
      caption.Margin(mx::ThicknessHelper::FromLengths(8, 8, 8, 2));
      CommList().Items().Append(caption);
      for (auto const& cluster : comm.clusters) {
        auto content = mxc::StackPanel{};
        content.Spacing(2);
        content.Margin(mx::ThicknessHelper::FromLengths(8, 4, 8, 4));
        auto path = mxc::TextBlock{};
        path.Text(wide(cluster.path));
        path.FontSize(13);
        path.FontWeight(mt::FontWeights::SemiBold());
        path.FontFamily(mx::Media::FontFamily(L"Consolas"));
        path.TextWrapping(mx::TextWrapping::Wrap);
        content.Children().Append(path);
        if (!cluster.connected_ecus.empty()) {
          std::wstring ecus = L"ECUs: ";
          for (std::size_t i = 0; i < cluster.connected_ecus.size(); ++i) {
            if (i > 0) {
              ecus += L", ";
            }
            ecus += wide(cluster.connected_ecus[i]);
          }
          auto ecu_line = mxc::TextBlock{};
          ecu_line.Text(ecus);
          ecu_line.FontSize(12);
          ecu_line.Foreground(ThemeBrush(L"AutarxSecondaryTextBrush"));
          ecu_line.TextWrapping(mx::TextWrapping::Wrap);
          content.Children().Append(ecu_line);
        }
        for (auto const& frame : cluster.frames) {
          auto frame_line = mxc::TextBlock{};
          frame_line.Text(L"frame " + wide(frame));
          frame_line.FontSize(11);
          frame_line.FontFamily(mx::Media::FontFamily(L"Consolas"));
          frame_line.Foreground(ThemeBrush(L"AutarxSecondaryTextBrush"));
          frame_line.TextTrimming(mx::TextTrimming::CharacterEllipsis);
          content.Children().Append(frame_line);
        }
        CommList().Items().Append(content);
      }
    }
    struct OrphanSpec {
      std::vector<std::string> const* items;
      wchar_t const* kind;
      wchar_t const* note;
    };
    OrphanSpec const orphans[] = {
        {&comm.orphan_frames, L"frame", L" — not attached to any cluster"},
        {&comm.orphan_pdus, L"pdu", L" — not attached to any frame"},
        {&comm.orphan_signals, L"signal", L" — not attached to any PDU"},
    };
    bool const any_orphans =
        !comm.orphan_frames.empty() || !comm.orphan_pdus.empty() ||
        !comm.orphan_signals.empty();
    if (any_orphans) {
      auto caption = mxc::TextBlock{};
      caption.Text(L"Orphans");
      caption.FontSize(13);
      caption.FontWeight(mt::FontWeights::SemiBold());
      caption.Foreground(ThemeBrush(L"AutarxSecondaryTextBrush"));
      caption.Margin(mx::ThicknessHelper::FromLengths(8, 12, 8, 2));
      CommList().Items().Append(caption);
      for (auto const& spec : orphans) {
        for (auto const& item : *spec.items) {
          auto line = mxc::TextBlock{};
          line.Text(spec.kind + wide(item) + spec.note);
          line.FontSize(11);
          line.FontFamily(mx::Media::FontFamily(L"Consolas"));
          line.Foreground(ThemeBrush(L"AutarxWarnBrush"));
          line.TextWrapping(mx::TextWrapping::Wrap);
          line.Margin(mx::ThicknessHelper::FromLengths(8, 2, 8, 2));
          CommList().Items().Append(line);
        }
      }
    }
  }
  if (!connected ||
      (comm_->clusters.empty() && comm_->orphan_frames.empty() &&
       comm_->orphan_pdus.empty() && comm_->orphan_signals.empty())) {
    auto text = mxc::TextBlock{};
    text.Text(connected ? L"No communication content in this workspace."
                        : L"No workspace open.");
    text.Foreground(ThemeBrush(L"AutarxSecondaryTextBrush"));
    text.Margin(mx::ThicknessHelper::FromLengths(8, 8, 8, 8));
    CommList().Items().Append(text);
  }
  suppress_nav_ = false;
}

void MainWindow::ClearInspector() {
  inspected_path_.clear();
  InspectorTitle().Text(L"Inspector");
  InspectorSubtitle().Text(L"");
  InspectorPath().Text(L"");
  InspectorRefs().Items().Clear();
}

void MainWindow::RenderInspector(rivet_app::SemanticObjectDto const& object) {
  InspectorTitle().Text(wide(object.short_name));
  InspectorSubtitle().Text(
      wide(object.element_type) + L" \u00B7 " + wide(object.semantic_kind));
  InspectorPath().Text(wide(object.absolute_path));
  InspectorRefs().Items().Clear();
  auto loading = mxc::TextBlock{};
  loading.Text(L"Loading references\u2026");
  loading.FontSize(12);
  loading.Foreground(ThemeBrush(L"AutarxSecondaryTextBrush"));
  InspectorRefs().Items().Append(loading);
  LoadRefsAsync(wide(object.absolute_path));
}

// ---------------------------------------------------------------------------
// Data flow
// ---------------------------------------------------------------------------

winrt::fire_and_forget MainWindow::OpenWorkspaceAsync(std::wstring const& path) {
  if (api_ == nullptr || path.empty()) {
    co_return;
  }
  auto const dispatcher = DispatcherQueue();
  auto const weak = get_weak();
  try {
    rivet_app::API& api = *api_;
    (void)api.open_workspace_async(
        winrt::to_string(path),
        [dispatcher, weak](rivet_app::Result<rivet_app::WorkspaceSummary> result) {
          try {
            auto const summary = result.get();
            dispatcher.TryEnqueue([weak, summary] {
              if (auto window = weak.get()) {
                window->summary_ = summary;
                window->SetStatus(
                    wide(summary.source_path) + L" — " +
                    std::to_wstring(summary.identifiable_count) +
                    L" objects, " + std::to_wstring(summary.unresolved_count) +
                    L" unresolved");
                window->ClearInspector();
                window->ReloadAllAsync();
              }
            });
          } catch (std::exception const& e) {
            auto message = std::string(e.what());
            dispatcher.TryEnqueue([weak, message = std::move(message)] {
              if (auto window = weak.get()) {
                window->SetError("Open failed: " + message);
              }
            });
          }
        });
  } catch (std::exception const& e) {
    SetError(e.what());
  }
}

winrt::fire_and_forget MainWindow::ReloadAllAsync() {
  if (api_ == nullptr || !summary_.has_value()) {
    co_return;
  }
  auto const dispatcher = DispatcherQueue();
  auto const weak = get_weak();
  try {
    rivet_app::API& api = *api_;
    (void)api.list_objects_async(
        "all", [dispatcher, weak](
                   rivet_app::Result<std::vector<rivet_app::SemanticObjectDto>> result) {
          try {
            auto const objects = result.get();
            dispatcher.TryEnqueue([weak, objects] {
              if (auto window = weak.get()) {
                window->objects_ = objects;
                if (window->section_ == Section::Objects) {
                  window->RenderObjects();
                }
              }
            });
          } catch (...) {
          }
        });
    (void)api.list_ecus_async(
        [dispatcher, weak](
            rivet_app::Result<std::vector<rivet_app::SemanticObjectDto>> result) {
          try {
            auto const ecus = result.get();
            dispatcher.TryEnqueue([weak, ecus] {
              if (auto window = weak.get()) {
                window->ecus_ = ecus;
                if (window->section_ == Section::Ecus) {
                  window->RenderEcus();
                }
              }
            });
          } catch (...) {
          }
        });
    (void)api.unresolved_references_async(
        [dispatcher, weak](
            rivet_app::Result<std::vector<rivet_app::ReferenceDto>> result) {
          try {
            auto const refs = result.get();
            dispatcher.TryEnqueue([weak, refs] {
              if (auto window = weak.get()) {
                window->unresolved_ = refs;
                if (window->section_ == Section::Unresolved) {
                  window->RenderUnresolved();
                }
              }
            });
          } catch (...) {
          }
        });
    (void)api.communication_async(
        [dispatcher, weak](rivet_app::Result<rivet_app::CommunicationDto> result) {
          try {
            auto const comm = result.get();
            dispatcher.TryEnqueue([weak, comm] {
              if (auto window = weak.get()) {
                window->comm_ = comm;
                if (window->section_ == Section::Comm) {
                  window->RenderComm();
                }
              }
            });
          } catch (...) {
          }
        });
  } catch (std::exception const& e) {
    SetError(e.what());
  }
}

winrt::fire_and_forget MainWindow::CloseWorkspaceAsync() {
  if (api_ == nullptr) {
    co_return;
  }
  auto const dispatcher = DispatcherQueue();
  auto const weak = get_weak();
  try {
    rivet_app::API& api = *api_;
    (void)api.close_workspace_async(
        [dispatcher, weak](rivet_app::Result<bool> result) {
          try {
            result.get();
          } catch (...) {
          }
          dispatcher.TryEnqueue([weak] {
            if (auto window = weak.get()) {
              window->summary_.reset();
              window->objects_.clear();
              window->ecus_.clear();
              window->unresolved_.clear();
              window->comm_.reset();
              window->current_search_.clear();
              window->ClearInspector();
              window->RenderSummary();
              window->RenderObjects();
              window->RenderEcus();
              window->RenderUnresolved();
              window->RenderComm();
              window->SetStatus(L"Workspace closed");
            }
          });
        });
  } catch (std::exception const& e) {
    SetError(e.what());
  }
}

winrt::fire_and_forget MainWindow::RunSearchAsync(std::wstring const& pattern) {
  if (api_ == nullptr) {
    co_return;
  }
  auto const dispatcher = DispatcherQueue();
  auto const weak = get_weak();
  try {
    rivet_app::API& api = *api_;
    if (pattern.empty()) {
      (void)api.list_objects_async(
          "all",
          [dispatcher, weak](
              rivet_app::Result<std::vector<rivet_app::SemanticObjectDto>> result) {
            try {
              auto const objects = result.get();
              dispatcher.TryEnqueue([weak, objects] {
                if (auto window = weak.get()) {
                  window->objects_ = objects;
                  window->RenderObjects();
                }
              });
            } catch (std::exception const& e) {
              auto message = std::string(e.what());
              dispatcher.TryEnqueue([weak, message = std::move(message)] {
                if (auto window = weak.get()) {
                  window->SetError(message);
                }
              });
            }
          });
      co_return;
    }
    (void)api.find_objects_async(
        winrt::to_string(pattern),
        [dispatcher, weak, pattern](
            rivet_app::Result<std::vector<rivet_app::SemanticObjectDto>> result) {
          try {
            auto const objects = result.get();
            dispatcher.TryEnqueue([weak, objects, pattern] {
              if (auto window = weak.get()) {
                // Drop stale completions: only the newest pattern renders.
                if (window->current_search_ != pattern) {
                  return;
                }
                window->objects_ = objects;
                window->RenderObjects();
              }
            });
          } catch (std::exception const& e) {
            auto message = std::string(e.what());
            dispatcher.TryEnqueue([weak, message = std::move(message)] {
              if (auto window = weak.get()) {
                window->SetError(message);
              }
            });
          }
        });
  } catch (std::exception const& e) {
    SetError(e.what());
  }
}

winrt::fire_and_forget MainWindow::LoadRefsAsync(std::wstring const& path) {
  if (api_ == nullptr) {
    co_return;
  }
  auto const dispatcher = DispatcherQueue();
  auto const weak = get_weak();
  try {
    rivet_app::API& api = *api_;
    (void)api.get_refs_async(
        winrt::to_string(path),
        [dispatcher, weak, path](
            rivet_app::Result<std::vector<rivet_app::ReferenceDto>> result) {
          try {
            auto const refs = result.get();
            dispatcher.TryEnqueue([weak, refs, path] {
              if (auto window = weak.get()) {
                // Drop stale completions: only the inspected path renders.
                if (window->inspected_path_ != path) {
                  return;
                }
                window->InspectorRefs().Items().Clear();
                if (refs.empty()) {
                  auto text = mxc::TextBlock{};
                  text.Text(L"No references touch this object.");
                  text.FontSize(12);
                  text.Foreground(window->ThemeBrush(L"AutarxSecondaryTextBrush"));
                  window->InspectorRefs().Items().Append(text);
                  return;
                }
                for (auto const& ref : refs) {
                  window->InspectorRefs().Items().Append(
                      window->MakeReferenceRow(ref, /*arrow_form=*/false));
                }
              }
            });
          } catch (std::exception const& e) {
            auto message = std::string(e.what());
            dispatcher.TryEnqueue([weak, message = std::move(message)] {
              if (auto window = weak.get()) {
                window->SetError(message);
              }
            });
          }
        });
  } catch (std::exception const& e) {
    SetError(e.what());
  }
}

// ---------------------------------------------------------------------------
// Menu handlers
// ---------------------------------------------------------------------------

winrt::fire_and_forget MainWindow::OnOpenFolder(
    winrt::Windows::Foundation::IInspectable const&,
    Microsoft::UI::Xaml::RoutedEventArgs const&) {
  auto picker = winrt::Windows::Storage::Pickers::FolderPicker{};
  auto window_native = try_as<::IWindowNative>();
  if (window_native) {
    HWND hwnd = nullptr;
    if (SUCCEEDED(window_native->get_WindowHandle(&hwnd))) {
      if (auto initializer = picker.try_as<::IInitializeWithWindow>()) {
        initializer->Initialize(hwnd);
      }
    }
  }
  auto const folder = co_await picker.PickSingleFolderAsync();
  if (folder != nullptr) {
    OpenWorkspaceAsync(std::wstring(folder.Path()));
  }
}

winrt::fire_and_forget MainWindow::OnOpenFile(
    winrt::Windows::Foundation::IInspectable const&,
    Microsoft::UI::Xaml::RoutedEventArgs const&) {
  auto picker = winrt::Windows::Storage::Pickers::FileOpenPicker{};
  auto window_native = try_as<::IWindowNative>();
  if (window_native) {
    HWND hwnd = nullptr;
    if (SUCCEEDED(window_native->get_WindowHandle(&hwnd))) {
      if (auto initializer = picker.try_as<::IInitializeWithWindow>()) {
        initializer->Initialize(hwnd);
      }
    }
  }
  picker.FileTypeFilter().Append(L".arxml");
  picker.FileTypeFilter().Append(L".xml");
  auto const file = co_await picker.PickSingleFileAsync();
  if (file != nullptr) {
    OpenWorkspaceAsync(std::wstring(file.Path()));
  }
}

void MainWindow::OnCloseWorkspace(
    winrt::Windows::Foundation::IInspectable const&,
    Microsoft::UI::Xaml::RoutedEventArgs const&) {
  CloseWorkspaceAsync();
}

void MainWindow::OnExit(winrt::Windows::Foundation::IInspectable const&,
                        Microsoft::UI::Xaml::RoutedEventArgs const&) {
  Close();
}

winrt::fire_and_forget MainWindow::OnAbout(
    winrt::Windows::Foundation::IInspectable const&,
    Microsoft::UI::Xaml::RoutedEventArgs const&) {
  auto panel = mxc::StackPanel{};
  panel.Spacing(4);
  auto tagline = mxc::TextBlock{};
  tagline.Text(L"AUTOSAR Integration Workbench");
  tagline.TextAlignment(mx::TextAlignment::Center);
  tagline.Margin({0, 0, 0, 8});
  auto copyright = mxc::TextBlock{};
  copyright.Text(L"\u00A9 2026 TuringLambdaAI");
  copyright.TextAlignment(mx::TextAlignment::Center);
  copyright.FontSize(12);
  copyright.Opacity(0.75);
  auto license = mxc::TextBlock{};
  license.Text(L"Proprietary — all rights reserved");
  license.TextAlignment(mx::TextAlignment::Center);
  license.FontSize(12);
  license.Opacity(0.75);
  auto website = mxc::HyperlinkButton{};
  website.Content(winrt::box_value(L"Website"));
  website.NavigateUri(winrt::Windows::Foundation::Uri{
      L"https://autarx.jrtx.site"});
  website.HorizontalAlignment(mx::HorizontalAlignment::Center);
  website.Padding({0, 0, 0, 0});
  auto issue = mxc::HyperlinkButton{};
  issue.Content(winrt::box_value(L"Report an Issue"));
  issue.NavigateUri(winrt::Windows::Foundation::Uri{
      L"https://github.com/turinglambdaai/autarx/issues"});
  issue.HorizontalAlignment(mx::HorizontalAlignment::Center);
  issue.Padding({0, 0, 0, 0});
  panel.Children().Append(tagline);
  panel.Children().Append(copyright);
  panel.Children().Append(license);
  panel.Children().Append(website);
  panel.Children().Append(issue);

  auto dialog = mxc::ContentDialog{};
  dialog.Title(
      winrt::box_value(L"Autarx v" + wide(rivet_app::kVersion)));
  dialog.Content(winrt::box_value(panel));
  dialog.CloseButtonText(L"OK");
  dialog.DefaultButton(mxc::ContentDialogButton::Close);
  dialog.XamlRoot(Content().XamlRoot());
  co_await dialog.ShowAsync();
}

// ---------------------------------------------------------------------------
// Sidebar + page handlers
// ---------------------------------------------------------------------------

void MainWindow::OnNavSelectionChanged(
    winrt::Windows::Foundation::IInspectable const&,
    Microsoft::UI::Xaml::Controls::SelectionChangedEventArgs const&) {
  if (suppress_nav_ || NavList().SelectedIndex() < 0) {
    return;
  }
  auto const tag =
      NavList()
          .Items()
          .GetAt(static_cast<std::uint32_t>(NavList().SelectedIndex()))
          .as<mxc::Grid>()
          .Tag();
  auto const section =
      static_cast<Section>(winrt::unbox_value<std::int32_t>(tag));
  ApplySection(section);
}

void MainWindow::OnSearchKeyDown(
    winrt::Windows::Foundation::IInspectable const&,
    Microsoft::UI::Xaml::Input::KeyRoutedEventArgs const& args) {
  if (args.Key() == winrt::Windows::System::VirtualKey::Enter) {
    current_search_ = std::wstring(SearchBox().Text());
    RunSearchAsync(current_search_);
  }
}

void MainWindow::OnFindClick(winrt::Windows::Foundation::IInspectable const&,
                             Microsoft::UI::Xaml::RoutedEventArgs const&) {
  current_search_ = std::wstring(SearchBox().Text());
  RunSearchAsync(current_search_);
}

void MainWindow::OnObjectSelectionChanged(
    winrt::Windows::Foundation::IInspectable const&,
    Microsoft::UI::Xaml::Controls::SelectionChangedEventArgs const&) {
  if (suppress_nav_ || ObjectList().SelectedIndex() < 0) {
    return;
  }
  auto const row =
      ObjectList()
          .Items()
          .GetAt(static_cast<std::uint32_t>(ObjectList().SelectedIndex()))
          .as<mxc::Grid>();
  auto const path = winrt::unbox_value<winrt::hstring>(row.Tag());
  for (auto const& object : objects_) {
    if (wide(object.absolute_path) == std::wstring(path)) {
      inspected_path_ = std::wstring(path);
      RenderInspector(object);
      return;
    }
  }
}

void MainWindow::OnEcuSelectionChanged(
    winrt::Windows::Foundation::IInspectable const&,
    Microsoft::UI::Xaml::Controls::SelectionChangedEventArgs const&) {
  if (suppress_nav_ || EcuList().SelectedIndex() < 0) {
    return;
  }
  auto const row =
      EcuList()
          .Items()
          .GetAt(static_cast<std::uint32_t>(EcuList().SelectedIndex()))
          .as<mxc::Grid>();
  auto const path = winrt::unbox_value<winrt::hstring>(row.Tag());
  for (auto const& ecu : ecus_) {
    if (wide(ecu.absolute_path) == std::wstring(path)) {
      inspected_path_ = std::wstring(path);
      RenderInspector(ecu);
      return;
    }
  }
}

}  // namespace winrt::RivetHost::implementation
