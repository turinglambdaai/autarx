// Autarx Linux host — GTK4 window over one embedded Racket CS backend.
//
// Interaction contract mirrors the macOS SwiftUI workbench element for
// element: the five-section semantic workspace (Workspace summary, Objects,
// ECUs, Unresolved references, Communication), the references inspector,
// progress events as a status-bar chip, and the shared/spect/UPDATE.md
// update flow through the backend's check_updates / start_download /
// update_state RPCs. Only the widget toolkit is platform-native; the host
// is English-only like the macOS workbench.
//
// Threading: boot the runtime off the UI thread; every completion/event is
// dispatched back to the main loop (g_idle_add) before touching widgets.
#include <gtk/gtk.h>

#include <algorithm>
#include <atomic>
#include <cerrno>
#include <cstdint>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <ctime>
#include <filesystem>
#include <functional>
#include <memory>
#include <mutex>
#include <optional>
#include <string>
#include <thread>
#include <type_traits>
#include <unistd.h>
#include <vector>

#include "GeneratedBackend.hpp"
#include "system_services.hpp"

namespace {


enum class Section { Workspace, Objects, Ecus, Unresolved, Comm };

struct ListPage {
  GtkStack* stack{nullptr};
  GtkListBox* list{nullptr};
  GtkLabel* empty_icon{nullptr};
  GtkLabel* empty_text{nullptr};
};

// Forward declarations.
GtkWidget* make_object_row(rivet_app::SemanticObjectDto const& object);
GtkWidget* make_reference_row(rivet_app::ReferenceDto const& ref,
                              bool arrow_form);
void rebuild_objects();
void rebuild_ecus();
void rebuild_unresolved();
void rebuild_comm();
void rebuild_summary();
void set_page_page(ListPage& page, bool connected, char const* icon,
                   char const* text);
void set_status(char const* message);
void set_status_error(std::string const& message);
void flash_status(std::string const& message);
void select_section(Section section);
void load_refs(std::string const& path);

// ---------------------------------------------------------------------------
// UI dispatch (completions/events arrive on the backend reader thread)
// ---------------------------------------------------------------------------

struct UiTask {
  std::function<void()> run;
};

int run_ui_task(gpointer user_data) {
  auto* task = static_cast<UiTask*>(user_data);
  task->run();
  delete task;
  return G_SOURCE_REMOVE;
}

void dispatch_ui(std::function<void()> run) {
  g_idle_add(run_ui_task, new UiTask{std::move(run)});
}

template <typename T, typename Then>
void on_result(rivet_app::Result<T> result, Then then) {
  dispatch_ui([result = std::move(result),
               then = std::move(then)]() mutable {
    if (!result.succeeded()) {
      try {
        std::rethrow_exception(result.error);
      } catch (std::exception const& e) {
        set_status_error(e.what());
      } catch (...) {
        set_status_error("unknown backend failure");
      }
      return;
    }
    try {
      if constexpr (std::is_void_v<T>) {
        then();
      } else {
        then(result.get());
      }
    } catch (std::exception const& e) {
      set_status_error(e.what());
    }
  });
}

// ---------------------------------------------------------------------------
// Helpers
// ---------------------------------------------------------------------------

std::filesystem::path executable_path() {
  return std::filesystem::read_symlink("/proc/self/exe");
}

// Install shape for the update flow (UPDATE.md): the AppImage runtime
// exports APPIMAGE pointing at the running image; deb lands under
// /opt/autarx and rpm under /usr — those upgrade through the package
// manager; everything else is the portable tarball layout.
std::string install_shape() {
  char const* appimage = getenv("APPIMAGE");
  if (appimage != nullptr && appimage[0] != '\0') return "appimage";
  auto const exe = executable_path().string();
  if (exe.rfind("/opt/autarx/", 0) == 0 || exe.rfind("/usr/", 0) == 0) {
    return "package";
  }
  return "tarball";
}

// The 4-hour silent-check throttle stamp lives host-side (the macOS host
// uses UserDefaults, Windows %LOCALAPPDATA%): XDG_STATE_HOME/autarx/
// last-update-check, unix epoch seconds as a text line.
std::filesystem::path throttle_file() {
  char const* state_home = getenv("XDG_STATE_HOME");
  std::filesystem::path base;
  if (state_home != nullptr && state_home[0] != '\0') {
    base = state_home;
  } else {
    char const* home = getenv("HOME");
    if (home == nullptr || home[0] == '\0') return {};
    base = std::filesystem::path(home) / ".local" / "state";
  }
  std::filesystem::path dir = base / "autarx";
  std::error_code ec;
  std::filesystem::create_directories(dir, ec);
  return dir / "last-update-check";
}

long long read_last_check() {
  auto const path = throttle_file();
  std::error_code ec;
  if (path.empty() || !std::filesystem::exists(path, ec)) return 0;
  FILE* file = std::fopen(path.c_str(), "r");
  if (file == nullptr) return 0;
  long long value = 0;
  if (std::fscanf(file, "%lld", &value) != 1) value = 0;
  std::fclose(file);
  return value;
}

void write_last_check(long long stamp) {
  auto const path = throttle_file();
  if (path.empty()) return;
  FILE* file = std::fopen(path.c_str(), "w");
  if (file == nullptr) return;
  std::fprintf(file, "%lld\n", stamp);
  std::fclose(file);
}

// ---------------------------------------------------------------------------
// Theme palettes (the family warm accent; light + dark).
// ---------------------------------------------------------------------------

struct Palette {
  char const* background;
  char const* sidebar;
  char const* accent;
  char const* secondary;
  char const* muted;
  char const* divider;
  char const* input_border;
  char const* surface;
  char const* hover;
  char const* selected;
  char const* warn;
  char const* text;
  char const* accent_soft;
};

Palette const& system_palette();

constexpr Palette kLight{
    "#FFFFFF", "#F5F3F1", "#C15F3C", "#797775", "#A19F9D",
    "#E1DFDD", "#C7C2BE", "#FFFFFF", "rgba(0,0,0,0.05)",
    "#EFE7E2", "#B0681E",
    "#1B1B1B", "rgba(193,95,60,0.10)",
};

constexpr Palette kDark{
    "#262323", "#2C2828", "#E08D6D", "#A8A5A3", "#7A7775",
    "#3B3939", "#4A4440", "#323234", "rgba(255,255,255,0.07)",
    "rgba(224,141,109,0.16)", "#D8A24E",
    "#F5F5F7", "rgba(224,141,109,0.18)",
};

// ---------------------------------------------------------------------------
// View model + widgets
// ---------------------------------------------------------------------------

struct AppState {
  GtkWindow* window{nullptr};
  GtkBox* menubar_box{nullptr};
  GtkListBox* nav_list{nullptr};
  GtkStack* pages{nullptr};
  ListPage workspace_page;
  ListPage objects_page;
  ListPage ecus_page;
  ListPage unresolved_page;
  ListPage comm_page;
  GtkSearchEntry* search_box{nullptr};
  GtkBox* inspector{nullptr};
  GtkLabel* inspector_title{nullptr};
  GtkLabel* inspector_subtitle{nullptr};
  GtkLabel* inspector_path{nullptr};
  GtkListBox* inspector_refs{nullptr};
  GtkLabel* status_text{nullptr};
  GtkLabel* progress_text{nullptr};

  std::unique_ptr<rivet::linux_runtime::Backend> backend;
  std::unique_ptr<rivet_app::API> api;
  std::mutex startup_mutex;
  std::thread startup_thread;
  std::unique_ptr<rivet::linux_runtime::Backend> startup_backend;
  std::string startup_error;
  std::atomic<bool> shutting_down{false};
  // Arbitrates on_shutdown between the GTK shutdown signal and the
  // SIGTERM/SIGINT hook — both paths must tear down exactly once.
  std::atomic<bool> teardown_started{false};

  std::optional<rivet_app::WorkspaceSummary> summary;
  std::vector<rivet_app::SemanticObjectDto> objects;
  std::vector<rivet_app::SemanticObjectDto> ecus;
  std::vector<rivet_app::ReferenceDto> unresolved;
  std::optional<rivet_app::CommunicationDto> comm;
  std::string current_search;
  std::string inspected_path;
  Section section{Section::Workspace};
  bool suppress_selection{false};
};

AppState g_state;
Palette const* g_state_palette = &kLight;

// Forward declarations.
GtkWidget* make_object_row(rivet_app::SemanticObjectDto const& object);
GtkWidget* make_reference_row(rivet_app::ReferenceDto const& ref,
                              bool arrow_form);
void rebuild_objects();
void rebuild_ecus();
void rebuild_unresolved();
void rebuild_comm();
void rebuild_summary();
void set_page_page(ListPage& page, bool connected, char const* icon,
                   char const* text);
void set_status(char const* message);
void set_status_error(std::string const& message);
void flash_status(std::string const& message);
void select_section(Section section);
void load_refs(std::string const& path);

// ---------------------------------------------------------------------------
// Status bar
// ---------------------------------------------------------------------------

void set_status(char const* message) {
  gtk_label_set_text(g_state.status_text, message);
}

void set_status_error(std::string const& message) {
  set_status(message.c_str());
}

guint g_status_flash_source = 0;

int on_flash_expired(gpointer) {
  g_status_flash_source = 0;
  set_status("");
  return G_SOURCE_REMOVE;
}

void flash_status(std::string const& message) {
  set_status(message.c_str());
  if (g_status_flash_source != 0) {
    g_source_remove(g_status_flash_source);
  }
  g_status_flash_source = g_timeout_add_seconds(3, on_flash_expired, nullptr);
}

// Progress events render as a right-aligned status-bar chip; 100% hides it
// (the macOS capsule contract).
void show_progress(char const* text) {
  gtk_label_set_text(g_state.progress_text, text);
  gtk_widget_set_visible(GTK_WIDGET(g_state.progress_text), TRUE);
}

void hide_progress() {
  gtk_widget_set_visible(GTK_WIDGET(g_state.progress_text), FALSE);
}

// ---------------------------------------------------------------------------
// CSS — regenerated per palette.
// ---------------------------------------------------------------------------

void apply_css(Palette const& p) {
  auto* provider = gtk_css_provider_new();
  std::string css;
  css += ".autarx-root { background: " + std::string(p.background) + "; color: " + std::string(p.text) + "; }\n";
  css += ".autarx-sidebar { background: " + std::string(p.sidebar) + "; }\n";
  css += ".autarx-divider { background: " + std::string(p.divider) + "; min-width: 1px; min-height: 1px; }\n";
  css += ".autarx-statusbar { background: " + std::string(p.sidebar) + "; min-height: 28px; }\n";
  css += ".autarx-statusbar label { color: " + std::string(p.secondary) + "; font-size: 12px; }\n";
  css += "menubar { min-height: 30px; padding: 0 6px; background: " + std::string(p.background) + "; font-size: 13px; color: " + std::string(p.text) + "; }\n";
  css += "menubar > item { min-height: 26px; padding: 3px 10px; border-radius: 6px; color: " + std::string(p.text) + "; }\n";
  css += "menubar > item:hover { background: " + std::string(p.hover) + "; }\n";
  css += ".section-caption { color: " + std::string(p.muted) + "; font-size: 12px; font-weight: 600; padding: 0 16px; }\n";
  css += ".meta-text { color: " + std::string(p.secondary) + "; font-size: 12px; }\n";
  css += ".mono { font-family: monospace; font-size: 11px; color: " + std::string(p.secondary) + "; }\n";
  css += ".mono-strong { font-family: monospace; font-size: 13px; font-weight: 600; color: " + std::string(p.text) + "; }\n";
  css += ".warn-text { color: " + std::string(p.warn) + "; font-size: 11px; }\n";
  css += ".chip { font-size: 10px; color: " + std::string(p.accent) + "; background: " + std::string(p.accent_soft) + "; border-radius: 4px; padding: 1px 6px; }\n";
  css += ".nav-caption { color: " + std::string(p.secondary) + "; font-size: 13px; font-weight: 600; }\n";
  css += "listbox.nav-list { background: transparent; }\n";
  css += "listbox.nav-list > row { min-height: 36px; border-radius: 8px; margin: 1px 8px; padding: 4px 10px; background: " + std::string(p.surface) + "; border: 1px solid " + std::string(p.divider) + "; box-shadow: none; color: " + std::string(p.text) + "; font-size: 13px; }\n";
  css += "listbox.nav-list > row:hover { background: " + std::string(p.hover) + "; }\n";
  css += "listbox.nav-list > row:selected { background: " + std::string(p.selected) + "; color: " + std::string(p.text) + "; }\n";
  css += "listbox.data-list { background: " + std::string(p.background) + "; }\n";
  css += "listbox.data-list > row { min-height: 44px; padding: 8px 16px; background: " + std::string(p.background) + "; border-bottom: 1px solid " + std::string(p.divider) + "; }\n";
  css += "listbox.data-list > row:hover { background: " + std::string(p.hover) + "; }\n";
  css += "listbox.data-list > row.inspector-row { min-height: 36px; padding: 6px 4px; border-bottom: none; }\n";
  css += "listbox.summary-list { background: " + std::string(p.background) + "; }\n";
  css += "listbox.summary-list > row { min-height: 34px; padding: 5px 4px; background: " + std::string(p.background) + "; border-bottom: none; }\n";
  css += ".empty-icon { font-size: 40px; color: " + std::string(p.muted) + "; }\n";
  css += ".empty-text { color: " + std::string(p.secondary) + "; font-size: 13px; }\n";
  css += "entry.search { min-height: 30px; }\n";
  gtk_css_provider_load_from_string(provider, css.c_str());
  gtk_style_context_add_provider_for_display(
      gdk_display_get_default(), GTK_STYLE_PROVIDER(provider),
      GTK_STYLE_PROVIDER_PRIORITY_APPLICATION);
  g_object_unref(provider);
}

// The SwiftUI/WinUI hosts resolve "system" through the OS; on GNOME the
// closest equivalent is the desktop color-scheme setting.
Palette const& system_palette() {
  GSettingsSchemaSource* source = g_settings_schema_source_get_default();
  GSettingsSchema* schema = nullptr;
  if (source != nullptr) {
    schema = g_settings_schema_source_lookup(
        source, "org.gnome.desktop.interface", TRUE);
  }
  if (schema == nullptr) return kLight;
  GSettings* settings = g_settings_new_full(schema, nullptr, nullptr);
  gchar* scheme = g_settings_get_string(settings, "color-scheme");
  bool const dark = scheme != nullptr && std::string(scheme) == "prefer-dark";
  g_free(scheme);
  g_object_unref(settings);
  g_settings_schema_unref(schema);
  if (dark) return kDark;
  // Without libadwaita, GTK4 picks its light/dark variant from the theme
  // NAME, not the portal: a "-dark" theme forces dark widgets regardless.
  gchar* theme_name = nullptr;
  g_object_get(gtk_settings_get_default(), "gtk-theme-name", &theme_name,
               nullptr);
  bool const dark_widgets =
      theme_name != nullptr && std::strstr(theme_name, "-dark") != nullptr;
  g_free(theme_name);
  return dark_widgets ? kDark : kLight;
}

void apply_theme() {
  Palette const& palette = system_palette();
  g_state_palette = &palette;
  apply_css(palette);
  gboolean prefer_dark = &palette == &kDark ? TRUE : FALSE;
  g_object_set(gtk_settings_get_default(),
               "gtk-application-prefer-dark-theme", prefer_dark, nullptr);
}

// ---------------------------------------------------------------------------
// Row builders
// ---------------------------------------------------------------------------

GtkWidget* make_section_caption(char const* text) {
  auto* label = gtk_label_new(text);
  gtk_label_set_xalign(GTK_LABEL(label), 0.0f);
  gtk_widget_add_css_class(label, "section-caption");
  gtk_widget_set_margin_start(label, 16);
  gtk_widget_set_margin_top(label, 10);
  gtk_widget_set_margin_bottom(label, 4);
  gtk_widget_set_halign(label, GTK_ALIGN_START);
  return label;
}

// Object row: bold SHORT-NAME + kind chip + unresolved warning over the
// mono absolute path (the macOS ObjectRow).
GtkWidget* make_object_row(rivet_app::SemanticObjectDto const& object) {
  auto* row = gtk_list_box_row_new();
  auto* box = gtk_box_new(GTK_ORIENTATION_VERTICAL, 2);

  auto* head = gtk_box_new(GTK_ORIENTATION_HORIZONTAL, 6);
  auto* name = gtk_label_new(object.short_name.c_str());
  gtk_label_set_xalign(GTK_LABEL(name), 0.0f);
  gtk_widget_add_css_class(name, "mono-strong");
  gtk_widget_set_halign(name, GTK_ALIGN_START);
  gtk_widget_set_valign(name, GTK_ALIGN_CENTER);
  gtk_box_append(GTK_BOX(head), name);
  auto* chip = gtk_label_new(object.semantic_kind.c_str());
  gtk_widget_add_css_class(chip, "chip");
  gtk_widget_set_valign(chip, GTK_ALIGN_CENTER);
  gtk_box_append(GTK_BOX(head), chip);
  if (object.unresolved_references > 0) {
    auto* warn = gtk_label_new(
        (std::to_string(object.unresolved_references) + " unresolved")
            .c_str());
    gtk_widget_add_css_class(warn, "warn-text");
    gtk_widget_set_valign(warn, GTK_ALIGN_CENTER);
    gtk_widget_set_tooltip_text(
        warn, "unresolved outgoing references");
    gtk_box_append(GTK_BOX(head), warn);
  }
  gtk_box_append(GTK_BOX(box), head);

  auto* path = gtk_label_new(object.absolute_path.c_str());
  gtk_label_set_xalign(GTK_LABEL(path), 0.0f);
  gtk_widget_add_css_class(path, "mono");
  gtk_label_set_ellipsize(GTK_LABEL(path), PANGO_ELLIPSIZE_END);
  gtk_box_append(GTK_BOX(box), path);

  gtk_list_box_row_set_child(GTK_LIST_BOX_ROW(row), box);
  g_object_set_data_full(G_OBJECT(row), "row-path",
                         new std::string(object.absolute_path),
                         +[](gpointer data) {
                           delete static_cast<std::string*>(data);
                         });
  return row;
}

// Reference row: inspector form (kind over source → target, unresolved
// marker) or the unresolved page's arrow form.
GtkWidget* make_reference_row(rivet_app::ReferenceDto const& ref,
                              bool arrow_form) {
  auto* row = gtk_list_box_row_new();
  auto* box = gtk_box_new(GTK_ORIENTATION_VERTICAL, 2);
  if (arrow_form) {
    auto* source = gtk_label_new(ref.source_element_path.c_str());
    gtk_label_set_xalign(GTK_LABEL(source), 0.0f);
    gtk_widget_add_css_class(source, "mono-strong");
    gtk_label_set_wrap(GTK_LABEL(source), TRUE);
    gtk_box_append(GTK_BOX(box), source);
    auto* edge = gtk_label_new(
        ("-[" + ref.kind + "]-> " + ref.target_path).c_str());
    gtk_label_set_xalign(GTK_LABEL(edge), 0.0f);
    gtk_widget_add_css_class(edge, "mono");
    gtk_label_set_wrap(GTK_LABEL(edge), TRUE);
    gtk_box_append(GTK_BOX(box), edge);
  } else {
    auto* kind = gtk_label_new(ref.kind.c_str());
    gtk_label_set_xalign(GTK_LABEL(kind), 0.0f);
    gtk_widget_add_css_class(kind, "nav-caption");
    gtk_box_append(GTK_BOX(box), kind);
    auto* edge =
        gtk_label_new((ref.source_path + " → " + ref.target_path).c_str());
    gtk_label_set_xalign(GTK_LABEL(edge), 0.0f);
    gtk_widget_add_css_class(edge, "mono");
    gtk_label_set_wrap(GTK_LABEL(edge), TRUE);
    gtk_box_append(GTK_BOX(box), edge);
    if (!ref.resolved) {
      auto* unresolved = gtk_label_new("unresolved");
      gtk_label_set_xalign(GTK_LABEL(unresolved), 0.0f);
      gtk_widget_add_css_class(unresolved, "warn-text");
      gtk_box_append(GTK_BOX(box), unresolved);
    }
  }
  gtk_widget_add_css_class(row, "inspector-row");
  gtk_list_box_row_set_child(GTK_LIST_BOX_ROW(row), box);
  return row;
}

GtkWidget* make_plain_row(char const* text, char const* css_class,
                          bool selectable_text) {
  auto* row = gtk_list_box_row_new();
  gtk_widget_set_focusable(row, FALSE);
  auto* label = gtk_label_new(text);
  gtk_label_set_xalign(GTK_LABEL(label), 0.0f);
  gtk_label_set_wrap(GTK_LABEL(label), TRUE);
  gtk_label_set_selectable(GTK_LABEL(label), selectable_text);
  gtk_widget_add_css_class(label, css_class);
  gtk_widget_set_margin_start(label, 8);
  gtk_widget_set_margin_end(label, 8);
  gtk_widget_set_margin_top(label, 4);
  gtk_widget_set_margin_bottom(label, 4);
  gtk_list_box_row_set_child(GTK_LIST_BOX_ROW(row), label);
  return row;
}

// ---------------------------------------------------------------------------
// Page rendering
// ---------------------------------------------------------------------------

// Pages are GtkStacks: the data list plus a centered empty state. `icon`
// is ✓ for a clean connected state, 📂 when nothing is open.
void set_page_page(ListPage& page, bool connected, char const* icon,
                   char const* text) {
  gtk_label_set_text(page.empty_icon, icon);
  gtk_label_set_text(page.empty_text, text);
  gtk_stack_set_visible_child_name(
      page.stack, connected ? "list" : "empty");
}

void rebuild_summary() {
  auto* list = g_state.workspace_page.list;
  gtk_list_box_remove_all(list);
  auto const connected = g_state.summary.has_value();
  set_page_page(g_state.workspace_page, connected, connected ? "" : "📦",
                connected ? ""
                          : "No workspace — open an OEM delivery directory or "
                            "a single ARXML file to index it.");
  if (!connected) return;
  auto const& s = *g_state.summary;
  bool const warn =
      s.unresolved_count > 0 || s.duplicate_count > 0 || s.file_error_count > 0;
  struct SummaryRow {
    char const* label;
    std::string value;
    bool highlight;
  };
  std::vector<SummaryRow> const rows = {
      {"Source", s.source_path, false},
      {"Files", std::to_string(s.file_count), false},
      {"Identifiables", std::to_string(s.identifiable_count), false},
      {"Packages", std::to_string(s.package_count), false},
      {"References", std::to_string(s.reference_count), false},
      {"Unresolved", std::to_string(s.unresolved_count),
       warn && s.unresolved_count > 0},
      {"Duplicate paths", std::to_string(s.duplicate_count),
       warn && s.duplicate_count > 0},
      {"File errors", std::to_string(s.file_error_count),
       warn && s.file_error_count > 0},
      {"AUTOSAR release",
       s.autosar_release.has_value() ? *s.autosar_release : "unknown", false},
      {"Schema", s.mixed_schema ? "mixed flavours!" : "uniform", false},
  };
  for (auto const& row_data : rows) {
    auto* row = gtk_list_box_row_new();
    gtk_widget_set_focusable(row, FALSE);
    auto* line = gtk_box_new(GTK_ORIENTATION_HORIZONTAL, 12);
    gtk_widget_set_margin_start(line, 4);
    gtk_widget_set_margin_end(line, 4);
    auto* label = gtk_label_new(row_data.label);
    gtk_label_set_xalign(GTK_LABEL(label), 0.0f);
    gtk_widget_add_css_class(label, "meta-text");
    gtk_widget_set_halign(label, GTK_ALIGN_START);
    gtk_widget_set_valign(label, GTK_ALIGN_CENTER);
    gtk_box_append(GTK_BOX(line), label);
    auto* value = gtk_label_new(row_data.value.c_str());
    gtk_label_set_xalign(GTK_LABEL(value), 1.0f);
    gtk_label_set_wrap(GTK_LABEL(value), TRUE);
    gtk_label_set_selectable(GTK_LABEL(value), TRUE);
    gtk_widget_add_css_class(value, row_data.highlight ? "warn-text" : "meta-text");
    if (row_data.highlight) {
      // keep the number readable at warning size
      gtk_widget_set_valign(value, GTK_ALIGN_CENTER);
    }
    gtk_widget_set_hexpand(value, TRUE);
    gtk_widget_set_valign(value, GTK_ALIGN_CENTER);
    gtk_box_append(GTK_BOX(line), value);
    gtk_list_box_row_set_child(GTK_LIST_BOX_ROW(row), line);
    gtk_list_box_insert(GTK_LIST_BOX(list), row, -1);
  }
}

void rebuild_objects() {
  auto* list = g_state.objects_page.list;
  gtk_list_box_remove_all(list);
  auto const connected = g_state.summary.has_value();
  std::string const empty_text =
      !connected
          ? "No workspace open — the object index appears here."
          : (g_state.current_search.empty()
                 ? "No objects in this workspace."
                 : "No objects match \"" + g_state.current_search + "\".");
  set_page_page(g_state.objects_page, connected && !g_state.objects.empty(), "📦",
                empty_text.c_str());
  for (auto const& object : g_state.objects) {
    gtk_list_box_insert(list, make_object_row(object), -1);
  }
}

void rebuild_ecus() {
  auto* list = g_state.ecus_page.list;
  gtk_list_box_remove_all(list);
  auto const connected = g_state.summary.has_value();
  set_page_page(g_state.ecus_page, connected && !g_state.ecus.empty(), "📦",
                !connected
                    ? "No workspace open — discovered ECUs appear here."
                    : "No ECU instances in this workspace.");
  for (auto const& ecu : g_state.ecus) {
    gtk_list_box_insert(list, make_object_row(ecu), -1);
  }
}

void rebuild_unresolved() {
  auto* list = g_state.unresolved_page.list;
  gtk_list_box_remove_all(list);
  auto const connected = g_state.summary.has_value();
  set_page_page(g_state.unresolved_page,
                connected && !g_state.unresolved.empty(),
                connected ? "✓" : "📦",
                !connected ? "No workspace open."
                           : "No unresolved references");
  for (auto const& ref : g_state.unresolved) {
    gtk_list_box_insert(list, make_reference_row(ref, /*arrow_form=*/true),
                        -1);
  }
}

void rebuild_comm() {
  auto* list = g_state.comm_page.list;
  gtk_list_box_remove_all(list);
  auto const connected = g_state.comm.has_value();
  bool const any_content =
      connected &&
      (!g_state.comm->clusters.empty() || !g_state.comm->orphan_frames.empty() ||
       !g_state.comm->orphan_pdus.empty() || !g_state.comm->orphan_signals.empty());
  set_page_page(g_state.comm_page, any_content, "📦",
                !connected
                    ? "No workspace open."
                    : "No communication content in this workspace.");
  if (!connected) return;
  auto const& comm = *g_state.comm;
  if (!comm.clusters.empty()) {
    gtk_list_box_insert(list, make_section_caption("Clusters"), -1);
    for (auto const& cluster : comm.clusters) {
      auto* row = gtk_list_box_row_new();
      gtk_widget_set_focusable(row, FALSE);
      auto* box = gtk_box_new(GTK_ORIENTATION_VERTICAL, 2);
      gtk_widget_set_margin_start(box, 4);
      auto* path = gtk_label_new(cluster.path.c_str());
      gtk_label_set_xalign(GTK_LABEL(path), 0.0f);
      gtk_widget_add_css_class(path, "mono-strong");
      gtk_label_set_wrap(GTK_LABEL(path), TRUE);
      gtk_box_append(GTK_BOX(box), path);
      if (!cluster.connected_ecus.empty()) {
        std::string ecus = "ECUs: ";
        for (std::size_t i = 0; i < cluster.connected_ecus.size(); ++i) {
          if (i > 0) ecus += ", ";
          ecus += cluster.connected_ecus[i];
        }
        auto* ecu_line = gtk_label_new(ecus.c_str());
        gtk_label_set_xalign(GTK_LABEL(ecu_line), 0.0f);
        gtk_widget_add_css_class(ecu_line, "meta-text");
        gtk_label_set_wrap(GTK_LABEL(ecu_line), TRUE);
        gtk_box_append(GTK_BOX(box), ecu_line);
      }
      for (auto const& frame : cluster.frames) {
        auto* frame_line =
            gtk_label_new(("frame " + frame).c_str());
        gtk_label_set_xalign(GTK_LABEL(frame_line), 0.0f);
        gtk_widget_add_css_class(frame_line, "mono");
        gtk_label_set_ellipsize(GTK_LABEL(frame_line), PANGO_ELLIPSIZE_END);
        gtk_box_append(GTK_BOX(box), frame_line);
      }
      gtk_list_box_row_set_child(GTK_LIST_BOX_ROW(row), box);
      gtk_list_box_insert(GTK_LIST_BOX(list), row, -1);
    }
  }
  struct OrphanSpec {
    std::vector<std::string> const* items;
    char const* kind;
    char const* note;
  };
  OrphanSpec const orphans[] = {
      {&comm.orphan_frames, "frame", " — not attached to any cluster"},
      {&comm.orphan_pdus, "pdu", " — not attached to any frame"},
      {&comm.orphan_signals, "signal", " — not attached to any PDU"},
  };
  bool const any_orphans = !comm.orphan_frames.empty() ||
                           !comm.orphan_pdus.empty() ||
                           !comm.orphan_signals.empty();
  if (any_orphans) {
    gtk_list_box_insert(list, make_section_caption("Orphans"), -1);
    for (auto const& spec : orphans) {
      for (auto const& item : *spec.items) {
        gtk_list_box_insert(
            list,
            make_plain_row((spec.kind + item + spec.note).c_str(), "warn-text",
                           true),
            -1);
      }
    }
  }
}

// ---------------------------------------------------------------------------
// Inspector
// ---------------------------------------------------------------------------

void clear_inspector() {
  g_state.inspected_path.clear();
  gtk_label_set_text(g_state.inspector_title, "Inspector");
  gtk_label_set_text(g_state.inspector_subtitle, "");
  gtk_label_set_text(g_state.inspector_path, "");
  gtk_list_box_remove_all(g_state.inspector_refs);
}

void load_refs(std::string const& path) {
  if (g_state.api == nullptr) return;
  (void)g_state.api->get_refs_async(
      path, [path](rivet_app::Result<std::vector<rivet_app::ReferenceDto>> result) {
        on_result(
            std::move(result),
            [path](std::vector<rivet_app::ReferenceDto> const& refs) {
              // Drop stale completions: only the inspected path renders.
              if (g_state.inspected_path != path) return;
              auto* list = g_state.inspector_refs;
              gtk_list_box_remove_all(list);
              if (refs.empty()) {
                gtk_list_box_insert(
                    list, make_plain_row("No references touch this object.",
                                         "meta-text", false),
                    -1);
                return;
              }
              for (auto const& ref : refs) {
                gtk_list_box_insert(list, make_reference_row(ref, false), -1);
              }
            });
      });
}

void inspect_object(rivet_app::SemanticObjectDto const& object) {
  g_state.inspected_path = object.absolute_path;
  gtk_label_set_text(g_state.inspector_title, object.short_name.c_str());
  gtk_label_set_text(
      g_state.inspector_subtitle,
      (object.element_type + " · " + object.semantic_kind).c_str());
  gtk_label_set_text(g_state.inspector_path, object.absolute_path.c_str());
  auto* list = g_state.inspector_refs;
  gtk_list_box_remove_all(list);
  gtk_list_box_insert(list, make_plain_row("Loading references…", "meta-text",
                                           false),
                      -1);
  load_refs(object.absolute_path);
}

void on_object_row_selected(GtkListBox*, GtkListBoxRow* row, gpointer) {
  if (g_state.suppress_selection || row == nullptr) return;
  auto const* path =
      static_cast<std::string const*>(g_object_get_data(G_OBJECT(row),
                                                        "row-path"));
  if (path == nullptr) return;
  for (auto const& object : g_state.objects) {
    if (object.absolute_path == *path) {
      inspect_object(object);
      return;
    }
  }
}

void on_ecu_row_selected(GtkListBox*, GtkListBoxRow* row, gpointer) {
  if (g_state.suppress_selection || row == nullptr) return;
  auto const* path =
      static_cast<std::string const*>(g_object_get_data(G_OBJECT(row),
                                                        "row-path"));
  if (path == nullptr) return;
  for (auto const& ecu : g_state.ecus) {
    if (ecu.absolute_path == *path) {
      inspect_object(ecu);
      return;
    }
  }
}

// ---------------------------------------------------------------------------
// Data flow
// ---------------------------------------------------------------------------

void open_path(std::string const& path) {
  if (g_state.api == nullptr || path.empty()) return;
  (void)g_state.api->open_workspace_async(
      path, [path](rivet_app::Result<rivet_app::WorkspaceSummary> result) {
        on_result(std::move(result),
                  [path](rivet_app::WorkspaceSummary const& summary) {
                    g_state.summary = summary;
                    set_status((path + " — " +
                                std::to_string(summary.identifiable_count) +
                                " objects, " +
                                std::to_string(summary.unresolved_count) +
                                " unresolved")
                                   .c_str());
                    clear_inspector();
                    if (g_state.api == nullptr) return;
                    auto& api = *g_state.api;
                    (void)api.list_objects_async(
                        "all",
                        [](rivet_app::Result<
                            std::vector<rivet_app::SemanticObjectDto>> result) {
                          on_result(std::move(result),
                                    [](std::vector<
                                        rivet_app::SemanticObjectDto> const&
                                           objects) {
                                      g_state.objects = objects;
                                      if (g_state.section ==
                                          Section::Objects) {
                                        rebuild_objects();
                                      }
                                    });
                        });
                    (void)api.list_ecus_async(
                        [](rivet_app::Result<
                            std::vector<rivet_app::SemanticObjectDto>> result) {
                          on_result(std::move(result),
                                    [](std::vector<
                                        rivet_app::SemanticObjectDto> const&
                                           ecus) {
                                      g_state.ecus = ecus;
                                      if (g_state.section == Section::Ecus) {
                                        rebuild_ecus();
                                      }
                                    });
                        });
                    (void)api.unresolved_references_async(
                        [](rivet_app::Result<
                            std::vector<rivet_app::ReferenceDto>> result) {
                          on_result(std::move(result),
                                    [](std::vector<
                                        rivet_app::ReferenceDto> const&
                                           refs) {
                                      g_state.unresolved = refs;
                                      if (g_state.section ==
                                          Section::Unresolved) {
                                        rebuild_unresolved();
                                      }
                                    });
                        });
                    (void)api.communication_async(
                        [](rivet_app::Result<rivet_app::CommunicationDto> result) {
                          on_result(std::move(result),
                                    [](rivet_app::CommunicationDto const& comm) {
                                      g_state.comm = comm;
                                      if (g_state.section == Section::Comm) {
                                        rebuild_comm();
                                      }
                                    });
                        });
                  });
      });
}

void close_workspace() {
  if (g_state.api == nullptr) return;
  (void)g_state.api->close_workspace_async([](rivet_app::Result<bool> result) {
    on_result(std::move(result), [](bool const&) {
      g_state.summary.reset();
      g_state.objects.clear();
      g_state.ecus.clear();
      g_state.unresolved.clear();
      g_state.comm.reset();
      g_state.current_search.clear();
      gtk_editable_set_text(GTK_EDITABLE(g_state.search_box), "");
      clear_inspector();
      rebuild_summary();
      rebuild_objects();
      rebuild_ecus();
      rebuild_unresolved();
      rebuild_comm();
      set_status("Workspace closed");
    });
  });
}

void run_search(std::string const& pattern) {
  if (g_state.api == nullptr) return;
  g_state.current_search = pattern;
  if (pattern.empty()) {
    (void)g_state.api->list_objects_async(
        "all",
        [](rivet_app::Result<std::vector<rivet_app::SemanticObjectDto>> result) {
          on_result(std::move(result),
                    [](std::vector<rivet_app::SemanticObjectDto> const& objects) {
                      g_state.objects = objects;
                      rebuild_objects();
                    });
        });
    return;
  }
  (void)g_state.api->find_objects_async(
      pattern,
      [pattern](rivet_app::Result<std::vector<rivet_app::SemanticObjectDto>> result) {
        on_result(
            std::move(result),
            [pattern](std::vector<rivet_app::SemanticObjectDto> const& objects) {
              // Drop stale completions: only the newest pattern renders.
              if (g_state.current_search != pattern) return;
              g_state.objects = objects;
              rebuild_objects();
            });
      });
}

// ---------------------------------------------------------------------------
// Online update flow (shared/spec/UPDATE.md; the taskly Linux host's flow):
// one silent throttled launch check, a Help ▸ Check for Updates… entry that
// bypasses the throttle, a consent dialog, 400 ms progress polling, and a
// hand-off dialog with an open-folder action. The Linux artifact is a
// tar.gz, so installation stays manual (extract the archive over the Autarx
// directory) — nothing ever relaunches; AppImage installs re-exec the
// replaced image on consent. Every dialog parents to the main window.
// ---------------------------------------------------------------------------

struct UpdateFlowState {
  guint auto_check_source{0};  // one-shot 3 s launch-check timer
  guint poll_source{0};        // 400 ms update_state poll
  GtkWindow* progress_dialog{nullptr};
  GtkProgressBar* progress_bar{nullptr};
  GtkLabel* progress_label{nullptr};
  bool check_in_flight{false};
  bool download_in_flight{false};
};

UpdateFlowState g_update;

constexpr long long kUpdateThrottleSeconds = 4 * 60 * 60;

void start_update_download();

void stop_update_poll() {
  if (g_update.poll_source != 0) {
    g_source_remove(g_update.poll_source);
    g_update.poll_source = 0;
  }
}

void close_update_progress() {
  if (g_update.progress_dialog != nullptr) {
    gtk_window_destroy(g_update.progress_dialog);
    g_update.progress_dialog = nullptr;
    g_update.progress_bar = nullptr;
    g_update.progress_label = nullptr;
  }
}

void show_update_message(std::string const& title, std::string const& body) {
  auto* dialog = gtk_dialog_new_with_buttons(
      title.c_str(), g_state.window,
      static_cast<GtkDialogFlags>(GTK_DIALOG_MODAL |
                                  GTK_DIALOG_DESTROY_WITH_PARENT),
      "OK", GTK_RESPONSE_CLOSE, nullptr);
  auto* area = gtk_dialog_get_content_area(GTK_DIALOG(dialog));
  auto* text = gtk_label_new(body.c_str());
  gtk_label_set_wrap(GTK_LABEL(text), TRUE);
  // Error text must be copyable — a bug report starts from this string.
  gtk_label_set_selectable(GTK_LABEL(text), TRUE);
  gtk_label_set_max_width_chars(GTK_LABEL(text), 60);
  gtk_widget_set_margin_top(text, 12);
  gtk_widget_set_margin_bottom(text, 6);
  gtk_widget_set_margin_start(text, 12);
  gtk_widget_set_margin_end(text, 12);
  gtk_box_append(GTK_BOX(area), text);
  g_signal_connect(
      dialog, "response",
      G_CALLBACK(+[](GtkDialog* dialog, gint, gpointer) {
        gtk_window_destroy(GTK_WINDOW(dialog));
      }),
      nullptr);
  gtk_window_present(GTK_WINDOW(dialog));
}

void show_update_error(std::string const& message) {
  show_update_message("Autarx", "Update check failed: " + message);
}

// AppImage: the backend already swapped the sibling .AppImage over the
// running image atomically (checksummed against the release sidecar), so
// the only step left is the user's consent to re-exec. Declining keeps the
// current (old) process running; the next launch is the new version
// regardless.
void show_appimage_restart_dialog() {
  auto* dialog = gtk_dialog_new_with_buttons(
      "Update downloaded", g_state.window,
      static_cast<GtkDialogFlags>(GTK_DIALOG_MODAL |
                                  GTK_DIALOG_DESTROY_WITH_PARENT),
      "Restart and update", GTK_RESPONSE_ACCEPT, "Cancel",
      GTK_RESPONSE_CLOSE, nullptr);
  auto* area = gtk_dialog_get_content_area(GTK_DIALOG(dialog));
  auto* content = gtk_box_new(GTK_ORIENTATION_VERTICAL, 8);
  gtk_widget_set_margin_top(content, 12);
  gtk_widget_set_margin_bottom(content, 6);
  gtk_widget_set_margin_start(content, 12);
  gtk_widget_set_margin_end(content, 12);
  gtk_box_append(GTK_BOX(area), content);
  auto* hint = gtk_label_new(
      "The update is in place — restart Autarx to switch to the new version.");
  gtk_label_set_wrap(GTK_LABEL(hint), TRUE);
  gtk_label_set_max_width_chars(GTK_LABEL(hint), 60);
  gtk_box_append(GTK_BOX(content), hint);
  g_signal_connect(
      dialog, "response",
      G_CALLBACK(+[](GtkDialog* dialog, gint response, gpointer) {
        gtk_window_destroy(GTK_WINDOW(dialog));
        if (response != GTK_RESPONSE_ACCEPT) return;
        char const* self = getenv("APPIMAGE");
        if (self == nullptr || self[0] == '\0') return;
        execl(self, self, static_cast<char*>(nullptr));
        // execl only returns on failure — surface it, the old image keeps
        // running either way.
        show_update_error(std::string("re-exec failed: ") +
                          std::strerror(errno));
      }),
      nullptr);
  gtk_window_present(GTK_WINDOW(dialog));
}

// deb/rpm installs: the download cannot self-swap a system-managed prefix,
// so the dialog explains the upgrade path and still opens the file's folder
// for whoever wants the archive itself.
void show_package_manager_dialog(std::string const& path) {
  auto* dialog = gtk_dialog_new_with_buttons(
      "Update downloaded", g_state.window,
      static_cast<GtkDialogFlags>(GTK_DIALOG_MODAL |
                                  GTK_DIALOG_DESTROY_WITH_PARENT),
      "OK", GTK_RESPONSE_CLOSE, nullptr);
  auto* area = gtk_dialog_get_content_area(GTK_DIALOG(dialog));
  auto* content = gtk_box_new(GTK_ORIENTATION_VERTICAL, 8);
  gtk_widget_set_margin_top(content, 12);
  gtk_widget_set_margin_bottom(content, 6);
  gtk_widget_set_margin_start(content, 12);
  gtk_widget_set_margin_end(content, 12);
  gtk_box_append(GTK_BOX(area), content);
  auto* hint = gtk_label_new(
      "This copy came from the system package manager — use it to upgrade "
      "Autarx.");
  gtk_label_set_wrap(GTK_LABEL(hint), TRUE);
  gtk_label_set_max_width_chars(GTK_LABEL(hint), 60);
  gtk_box_append(GTK_BOX(content), hint);
  if (!path.empty()) {
    auto* path_label = gtk_label_new(path.c_str());
    gtk_label_set_selectable(GTK_LABEL(path_label), TRUE);
    gtk_label_set_wrap(GTK_LABEL(path_label), TRUE);
    gtk_widget_add_css_class(path_label, "meta-text");
    gtk_box_append(GTK_BOX(content), path_label);
  }
  g_signal_connect(
      dialog, "response",
      G_CALLBACK(+[](GtkDialog* dialog, gint, gpointer) {
        gtk_window_destroy(GTK_WINDOW(dialog));
      }),
      nullptr);
  gtk_window_present(GTK_WINDOW(dialog));
}

// Downloaded: hand off the verified tar.gz. Installation stays manual, so
// the dialog explains the step and points at the file's folder; nothing is
// ever executed or moved by the host.
void show_downloaded_dialog(std::string const& path) {
  auto* dialog = gtk_dialog_new_with_buttons(
      "Update downloaded", g_state.window,
      static_cast<GtkDialogFlags>(GTK_DIALOG_MODAL |
                                  GTK_DIALOG_DESTROY_WITH_PARENT),
      "OK", GTK_RESPONSE_CLOSE, nullptr);
  auto* area = gtk_dialog_get_content_area(GTK_DIALOG(dialog));
  auto* content = gtk_box_new(GTK_ORIENTATION_VERTICAL, 8);
  gtk_widget_set_margin_top(content, 12);
  gtk_widget_set_margin_bottom(content, 6);
  gtk_widget_set_margin_start(content, 12);
  gtk_widget_set_margin_end(content, 12);
  gtk_box_append(GTK_BOX(area), content);

  auto* hint = gtk_label_new(
      "To install, extract the downloaded archive over your current Autarx "
      "folder.");
  gtk_label_set_wrap(GTK_LABEL(hint), TRUE);
  gtk_label_set_max_width_chars(GTK_LABEL(hint), 60);
  gtk_box_append(GTK_BOX(content), hint);

  // The backend stages the archive under <temp>/autarx/updates/.
  if (!path.empty()) {
    auto* path_label = gtk_label_new(path.c_str());
    gtk_label_set_selectable(GTK_LABEL(path_label), TRUE);
    gtk_label_set_wrap(GTK_LABEL(path_label), TRUE);
    gtk_widget_add_css_class(path_label, "meta-text");
    gtk_box_append(GTK_BOX(content), path_label);

    auto* open = gtk_button_new_with_label("Open Folder");
    g_object_set_data_full(G_OBJECT(open), "update-open-path",
                           new std::string(path),
                           +[](gpointer data) {
                             delete static_cast<std::string*>(data);
                           });
    g_signal_connect(
        open, "clicked",
        G_CALLBACK(+[](GtkButton* button, gpointer) {
          auto const* p = static_cast<std::string const*>(
              g_object_get_data(G_OBJECT(button), "update-open-path"));
          if (p == nullptr) return;
          // Parent directory of the file, POSIX paths only: plain string
          // arithmetic keeps this noexcept inside a GTK callback.
          auto const slash = p->find_last_of('/');
          if (slash == std::string::npos) return;
          std::string const dir = p->substr(0, slash);
          if (dir.empty()) return;
          GFile* folder = g_file_new_for_path(dir.c_str());
          gchar* uri = g_file_get_uri(folder);
          gtk_show_uri(g_state.window, uri, GDK_CURRENT_TIME);
          g_free(uri);
          g_object_unref(folder);
        }),
        nullptr);
    gtk_box_append(GTK_BOX(content), open);
  }

  g_signal_connect(
      dialog, "response",
      G_CALLBACK(+[](GtkDialog* dialog, gint, gpointer) {
        gtk_window_destroy(GTK_WINDOW(dialog));
      }),
      nullptr);
  gtk_window_present(GTK_WINDOW(dialog));
}

int on_update_poll_tick(gpointer) {
  if (g_state.api == nullptr || g_update.progress_dialog == nullptr) {
    g_update.poll_source = 0;
    return G_SOURCE_REMOVE;
  }
  (void)g_state.api->update_state_async(
      [](rivet_app::Result<rivet_app::UpdateState> result) {
        dispatch_ui([result = std::move(result)]() mutable {
          if (g_update.progress_dialog == nullptr) return;  // detached UI
          if (!result.succeeded()) return;  // keep polling through hiccups
          rivet_app::UpdateState const& state = result.get();
          auto const percent = std::clamp<std::int64_t>(state.percent, 0, 100);
          if (g_update.progress_bar != nullptr) {
            gtk_progress_bar_set_fraction(g_update.progress_bar,
                                          static_cast<double>(percent) /
                                              100.0);
          }
          if (g_update.progress_label != nullptr) {
            gtk_label_set_text(
                g_update.progress_label,
                ("Downloading the new version… " + std::to_string(percent) +
                 "%")
                    .c_str());
          }
          if (state.phase != "downloaded" && state.phase != "error") {
            return;
          }
          stop_update_poll();
          close_update_progress();
          g_update.download_in_flight = false;
          if (state.phase == "error") {
            show_update_error(state.message.value_or("unknown failure"));
            return;
          }
          auto const downloaded = state.downloaded_path.value_or("");
          auto const shape = install_shape();
          if (shape == "appimage") {
            show_appimage_restart_dialog();
            return;
          }
          if (shape == "package") {
            show_package_manager_dialog(downloaded);
            return;
          }
          show_downloaded_dialog(downloaded);
        });
      });
  return G_SOURCE_CONTINUE;
}

void start_update_download() {
  if (g_state.api == nullptr || g_update.download_in_flight) return;
  g_update.download_in_flight = true;

  auto* dialog = gtk_dialog_new_with_buttons(
      "Downloading the new version…", g_state.window,
      static_cast<GtkDialogFlags>(GTK_DIALOG_MODAL |
                                  GTK_DIALOG_DESTROY_WITH_PARENT),
      "Cancel", GTK_RESPONSE_CANCEL, nullptr);
  auto* area = gtk_dialog_get_content_area(GTK_DIALOG(dialog));
  auto* content = gtk_box_new(GTK_ORIENTATION_VERTICAL, 8);
  gtk_widget_set_margin_top(content, 12);
  gtk_widget_set_margin_bottom(content, 6);
  gtk_widget_set_margin_start(content, 12);
  gtk_widget_set_margin_end(content, 12);
  gtk_box_append(GTK_BOX(area), content);
  auto* bar = gtk_progress_bar_new();
  gtk_widget_set_size_request(bar, 300, -1);
  gtk_box_append(GTK_BOX(content), bar);
  auto* label = gtk_label_new("Downloading the new version…");
  gtk_box_append(GTK_BOX(content), label);

  // Canceling only detaches the progress UI — the backend keeps downloading
  // (family parity: there is no cancel RPC).
  g_signal_connect(
      dialog, "response",
      G_CALLBACK(+[](GtkDialog* dialog, gint, gpointer) {
        stop_update_poll();
        g_update.progress_dialog = nullptr;
        g_update.progress_bar = nullptr;
        g_update.progress_label = nullptr;
        g_update.download_in_flight = false;
        gtk_window_destroy(GTK_WINDOW(dialog));
      }),
      nullptr);
  // DESTROY_WITH_PARENT: if the main window goes away the dialog dies with
  // it — clear the raw pointers and the poll so no tick touches dead
  // widgets.
  g_signal_connect(
      dialog, "destroy",
      G_CALLBACK(+[](GtkWidget*, gpointer) {
        g_update.progress_dialog = nullptr;
        g_update.progress_bar = nullptr;
        g_update.progress_label = nullptr;
        stop_update_poll();
      }),
      nullptr);
  gtk_window_present(GTK_WINDOW(dialog));
  g_update.progress_dialog = GTK_WINDOW(dialog);
  g_update.progress_bar = GTK_PROGRESS_BAR(bar);
  g_update.progress_label = GTK_LABEL(label);
  g_update.poll_source = g_timeout_add(400, on_update_poll_tick, nullptr);

  (void)g_state.api->start_download_async(
      [](rivet_app::Result<void> result) {
        dispatch_ui([result = std::move(result)]() mutable {
          if (g_update.progress_dialog == nullptr) return;  // detached UI
          if (result.succeeded()) return;  // the poll owns the rest
          // The download never started; stop staring at a 0% bar.
          stop_update_poll();
          close_update_progress();
          g_update.download_in_flight = false;
          std::string message = "unknown failure";
          try {
            std::rethrow_exception(result.error);
          } catch (std::exception const& e) {
            message = e.what();
          } catch (...) {
          }
          show_update_message(
              "Autarx",
              "Installing the update failed: " + message +
                  ". Your current version is unaffected.");
        });
      });
}

void show_update_consent(rivet_app::UpdateCheck const& check) {
  std::string const version = check.available_version.value_or("?");
  if (install_shape() == "package") {
    // deb/rpm upgrade through the package manager; never download or exec.
    show_update_message("Update available",
                        "This copy came from the system package manager — use "
                        "it to upgrade Autarx.");
    return;
  }
  auto* dialog = gtk_dialog_new_with_buttons(
      "Update available", g_state.window,
      static_cast<GtkDialogFlags>(GTK_DIALOG_MODAL |
                                  GTK_DIALOG_DESTROY_WITH_PARENT),
      "Cancel", GTK_RESPONSE_CANCEL, "Update and restart", GTK_RESPONSE_ACCEPT,
      nullptr);
  auto* area = gtk_dialog_get_content_area(GTK_DIALOG(dialog));
  auto* text = gtk_label_new(
      ("Version " + version +
       " is available. Install and restart now?")
          .c_str());
  gtk_label_set_wrap(GTK_LABEL(text), TRUE);
  gtk_label_set_max_width_chars(GTK_LABEL(text), 60);
  gtk_widget_set_margin_top(text, 12);
  gtk_widget_set_margin_bottom(text, 6);
  gtk_widget_set_margin_start(text, 12);
  gtk_widget_set_margin_end(text, 12);
  gtk_box_append(GTK_BOX(area), text);
  g_signal_connect(
      dialog, "response",
      G_CALLBACK(+[](GtkDialog* dialog, gint response, gpointer) {
        bool const go = response == GTK_RESPONSE_ACCEPT;
        gtk_window_destroy(GTK_WINDOW(dialog));
        if (go) {
          start_update_download();
        }
      }),
      nullptr);
  gtk_window_present(GTK_WINDOW(dialog));
}

// Check for updates. `silent` keeps every outcome except an available
// update invisible (startup auto-check — never nag); a manual check reports
// everything. Any settled check records the throttle stamp, so the next
// launch's silent attempt stays inside the 4-hour window (UPDATE.md).
void check_for_updates(bool silent) {
  if (g_state.api == nullptr || g_update.check_in_flight ||
      g_update.download_in_flight) {
    return;
  }
  g_update.check_in_flight = true;
  if (!silent) {
    flash_status("Checking for updates…");
  }
  (void)g_state.api->check_updates_async(
      [silent](rivet_app::Result<rivet_app::UpdateCheck> result) {
        dispatch_ui([result = std::move(result), silent]() mutable {
          g_update.check_in_flight = false;
          if (!result.succeeded()) {
            // Transport-level failure: the backend is going away, so do not
            // pretend a check happened.
            if (silent) return;
            std::string message = "unknown failure";
            try {
              std::rethrow_exception(result.error);
            } catch (std::exception const& e) {
              message = e.what();
            } catch (...) {
            }
            show_update_error(message);
            return;
          }
          // The check settled (any status): record the timestamp so the next
          // launch's silent attempt stays inside the 4-hour throttle.
          write_last_check(static_cast<long long>(std::time(nullptr)));
          rivet_app::UpdateCheck const& check = result.get();
          if (check.status == "available") {
            show_update_consent(check);
            return;
          }
          if (silent) return;  // auto-check only speaks when there is news
          if (check.status == "error") {
            show_update_error(check.error.value_or("unknown failure"));
            return;
          }
          // "up-to-date": nothing to do.
          show_update_message("Autarx", "You're up to date");
        });
      });
}

void on_menu_check_updates(GSimpleAction*, GVariant*, gpointer) {
  check_for_updates(/*silent=*/false);
}

// Silent launch check: once per process, 3 s after the backend is up, gated
// by the 4-hour throttle. A failed throttle read counts as "never checked" —
// silent mode ignores errors by contract. Dev copies never phone home.
gboolean on_update_auto_check(gpointer) {
  g_update.auto_check_source = 0;
  if (g_state.api == nullptr ||
      g_state.shutting_down.load(std::memory_order_acquire)) {
    return G_SOURCE_REMOVE;
  }
  std::error_code ec;
  bool const dev_copy = !std::filesystem::exists(
      executable_path().parent_path() / "res" / "core.zo", ec);
  if (dev_copy) return G_SOURCE_REMOVE;
  auto const now = static_cast<long long>(std::time(nullptr));
  if (now - read_last_check() < kUpdateThrottleSeconds) return G_SOURCE_REMOVE;
  check_for_updates(/*silent=*/true);
  return G_SOURCE_REMOVE;
}

// ---------------------------------------------------------------------------
// Dialogs
// ---------------------------------------------------------------------------

void show_about_dialog() {
  auto* dialog = gtk_about_dialog_new();
  gtk_about_dialog_set_program_name(GTK_ABOUT_DIALOG(dialog), "Autarx");
  gtk_about_dialog_set_version(GTK_ABOUT_DIALOG(dialog), rivet_app::kVersion);
  gtk_about_dialog_set_comments(GTK_ABOUT_DIALOG(dialog),
                                "AUTOSAR Integration Workbench");
  gtk_about_dialog_set_copyright(GTK_ABOUT_DIALOG(dialog),
                                 "© 2026 TuringLambdaAI");
  gtk_about_dialog_set_license(
      GTK_ABOUT_DIALOG(dialog), "Proprietary — all rights reserved");
  gtk_about_dialog_set_website(GTK_ABOUT_DIALOG(dialog),
                               "https://autarx.jrtx.site");
  gtk_about_dialog_set_website_label(GTK_ABOUT_DIALOG(dialog), "Website");
  gtk_window_set_transient_for(GTK_WINDOW(dialog), g_state.window);
  gtk_window_set_modal(GTK_WINDOW(dialog), TRUE);
  gtk_window_present(GTK_WINDOW(dialog));
}

// ---------------------------------------------------------------------------
// Navigation
// ---------------------------------------------------------------------------

char const* section_page_name(Section section) {
  switch (section) {
    case Section::Workspace: return "workspace";
    case Section::Objects: return "objects";
    case Section::Ecus: return "ecus";
    case Section::Unresolved: return "unresolved";
    case Section::Comm: return "comm";
  }
  return "workspace";
}

void select_section(Section section) {
  g_state.section = section;
  gtk_stack_set_visible_child_name(g_state.pages, section_page_name(section));
  // The inspector belongs to Objects/ECUs selections; other pages hide it.
  bool const inspector =
      section == Section::Objects || section == Section::Ecus;
  gtk_widget_set_visible(GTK_WIDGET(g_state.inspector),
                         inspector ? TRUE : FALSE);
  switch (section) {
    case Section::Workspace: rebuild_summary(); break;
    case Section::Objects: rebuild_objects(); break;
    case Section::Ecus: rebuild_ecus(); break;
    case Section::Unresolved: rebuild_unresolved(); break;
    case Section::Comm: rebuild_comm(); break;
  }
}

void on_nav_row_selected(GtkListBox*, GtkListBoxRow* row, gpointer) {
  if (g_state.suppress_selection || row == nullptr) return;
  auto const section = static_cast<Section>(
      GPOINTER_TO_INT(g_object_get_data(G_OBJECT(row), "section")));
  select_section(section);
}

// ---------------------------------------------------------------------------
// Menu actions
// ---------------------------------------------------------------------------

void on_menu_open_folder(GSimpleAction*, GVariant*, gpointer) {
  auto* chooser = gtk_file_chooser_native_new(
      "Open Delivery Folder", g_state.window,
      GTK_FILE_CHOOSER_ACTION_SELECT_FOLDER, "Open", "Cancel");
  g_signal_connect(
      chooser, "response",
      G_CALLBACK(+[](GtkNativeDialog* native, gint response, gpointer) {
        if (response == GTK_RESPONSE_ACCEPT) {
          auto* file = gtk_file_chooser_get_file(GTK_FILE_CHOOSER(native));
          if (file != nullptr) {
            gchar* path = g_file_get_path(file);
            if (path != nullptr) {
              open_path(path);
              g_free(path);
            }
            g_object_unref(file);
          }
        }
        g_object_unref(native);
      }),
      nullptr);
  gtk_native_dialog_show(GTK_NATIVE_DIALOG(chooser));
}

void on_menu_open_file(GSimpleAction*, GVariant*, gpointer) {
  auto* chooser = gtk_file_chooser_native_new(
      "Open ARXML File", g_state.window, GTK_FILE_CHOOSER_ACTION_OPEN, "Open",
      "Cancel");
  auto* filter = gtk_file_filter_new();
  gtk_file_filter_set_name(filter, "ARXML");
  gtk_file_filter_add_pattern(filter, "*.arxml");
  gtk_file_filter_add_pattern(filter, "*.xml");
  gtk_file_chooser_add_filter(GTK_FILE_CHOOSER(chooser), filter);
  g_object_unref(filter);
  g_signal_connect(
      chooser, "response",
      G_CALLBACK(+[](GtkNativeDialog* native, gint response, gpointer) {
        if (response == GTK_RESPONSE_ACCEPT) {
          auto* file = gtk_file_chooser_get_file(GTK_FILE_CHOOSER(native));
          if (file != nullptr) {
            gchar* path = g_file_get_path(file);
            if (path != nullptr) {
              open_path(path);
              g_free(path);
            }
            g_object_unref(file);
          }
        }
        g_object_unref(native);
      }),
      nullptr);
  gtk_native_dialog_show(GTK_NATIVE_DIALOG(chooser));
}

void on_menu_close_workspace(GSimpleAction*, GVariant*, gpointer) {
  close_workspace();
}

void on_menu_quit(GSimpleAction*, GVariant*, gpointer) {
  gtk_window_destroy(g_state.window);
}

void on_menu_about(GSimpleAction*, GVariant*, gpointer) {
  show_about_dialog();
}

void add_window_action(GtkApplicationWindow* window, char const* name,
                       void (*handler)(GSimpleAction*, GVariant*, gpointer)) {
  auto* action = g_simple_action_new(name, nullptr);
  g_signal_connect(action, "activate", G_CALLBACK(handler), nullptr);
  g_action_map_add_action(G_ACTION_MAP(window), G_ACTION(action));
  g_object_unref(action);
}

void build_menubar() {
  auto* menu = g_menu_new();
  auto* file_menu = g_menu_new();
  g_menu_append(file_menu, "Open Delivery Folder…", "win.open-folder");
  g_menu_append(file_menu, "Open ARXML File…", "win.open-file");
  g_menu_append(file_menu, "Close Workspace", "win.close-workspace");
  g_menu_append(file_menu, "Quit", "win.quit");
  g_menu_append_submenu(menu, "File", G_MENU_MODEL(file_menu));
  g_object_unref(file_menu);

  auto* help_menu = g_menu_new();
  g_menu_append(help_menu, "Check for Updates…", "win.check-updates");
  g_menu_append(help_menu, "About Autarx", "win.about");
  g_menu_append_submenu(menu, "Help", G_MENU_MODEL(help_menu));
  g_object_unref(help_menu);

  gtk_box_append(g_state.menubar_box,
                 gtk_popover_menu_bar_new_from_model(G_MENU_MODEL(menu)));
  g_object_unref(menu);
}

// ---------------------------------------------------------------------------
// Startup (same contract as the taskly Linux host)
// ---------------------------------------------------------------------------

struct RuntimeLayout {
  std::filesystem::path petite_boot;
  std::filesystem::path scheme_boot;
  std::filesystem::path racket_boot;
  std::filesystem::path core;
};

std::optional<RuntimeLayout> discover_runtime_layout() {
  std::filesystem::path const exe = executable_path();
  std::filesystem::path const roots[] = {exe.parent_path()};
  for (auto const& root : roots) {
    RuntimeLayout layout{
        root / "runtime" / "petite.boot",
        root / "runtime" / "scheme.boot",
        root / "runtime" / "racket.boot",
        root / "res" / "core.zo",
    };
    if (std::filesystem::exists(layout.petite_boot) &&
        std::filesystem::exists(layout.scheme_boot) &&
        std::filesystem::exists(layout.racket_boot) &&
        std::filesystem::exists(layout.core)) {
      return layout;
    }
  }
  return std::nullopt;
}

void on_shutdown(GApplication*, gpointer);
void perform_orderly_shutdown();

int on_backend_finished(gpointer) {
  if (g_state.startup_thread.joinable()) {
    g_state.startup_thread.join();
  }

  std::unique_ptr<rivet::linux_runtime::Backend> backend;
  std::string error;
  {
    std::lock_guard lock(g_state.startup_mutex);
    backend = std::move(g_state.startup_backend);
    error = std::move(g_state.startup_error);
  }

  if (g_state.shutting_down.load(std::memory_order_acquire)) {
    if (backend != nullptr) {
      backend->stop();
    }
    return G_SOURCE_REMOVE;
  }

  if (!error.empty()) {
    set_status_error("Backend error: " + error);
    return G_SOURCE_REMOVE;
  }
  if (backend == nullptr) {
    set_status_error("Backend error: startup completed without a backend");
    return G_SOURCE_REMOVE;
  }

  g_state.backend = std::move(backend);
  g_state.api = std::make_unique<rivet_app::API>(*g_state.backend);

  // The shutdown hook goes here, not in main(): the embedded Racket runtime
  // installs its own SIGTERM handler while booting and would leave ours
  // inert (syncpilot issue #3). Booting is done at this point, so installing
  // now is the last word.
  try {
    rivet::system::InstallShutdownHook(perform_orderly_shutdown);
  } catch (std::exception const& hook_error) {
    std::fputs((std::string{"shutdown hook unavailable: "} +
                hook_error.what() + "\n")
                   .c_str(),
               stderr);
  }

  // Long operations (workspace indexing, diffing) publish progress; the
  // status-bar chip mirrors the macOS capsule and hides at 100%.
  g_state.backend->set_event_handler([](std::string const& name,
                                        rivet::Value const& value) {
    if (name != "progress") return;
    auto const* text = std::get_if<std::string>(&value.data);
    if (text == nullptr) return;
    std::string const copy = *text;
    dispatch_ui([copy] {
      if (copy.size() >= 4 && copy.compare(copy.size() - 4, 4, "100%") == 0) {
        hide_progress();
      } else {
        show_progress(copy.c_str());
      }
    });
  });

  set_status("Backend ready — open a delivery to begin");
  // Update flow: one silent check 3 s after the backend is up, gated by the
  // 4-hour throttle (UPDATE.md).
  g_update.auto_check_source =
      g_timeout_add_seconds(3, on_update_auto_check, nullptr);

  // AUTARX_OPEN=<path> pre-opens a workspace (dev convenience, the macOS
  // workbench contract).
  char const* preset = getenv("AUTARX_OPEN");
  if (preset != nullptr && preset[0] != '\0') {
    open_path(preset);
  }
  return G_SOURCE_REMOVE;
}

void start_backend() {
  auto layout = discover_runtime_layout();
  if (!layout.has_value()) {
    set_status(
        "Missing Rivet runtime layout (runtime/*.boot, res/core.zo) next to "
        "the executable. Build with raco rivet build/dev.");
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
  g_state.startup_thread = std::thread([config = std::move(config)]() mutable {
    auto backend =
        std::make_unique<rivet::linux_runtime::Backend>(std::move(config));
    try {
      backend->start();
      {
        std::lock_guard lock(g_state.startup_mutex);
        g_state.startup_backend = std::move(backend);
      }
    } catch (std::exception const& e) {
      std::lock_guard lock(g_state.startup_mutex);
      g_state.startup_error = e.what();
    }
    g_idle_add(on_backend_finished, nullptr);
  });
}

// ---------------------------------------------------------------------------
// Window
// ---------------------------------------------------------------------------

ListPage make_list_page(GtkStack* stack, char const* list_css_class) {
  ListPage page{};
  page.stack = stack;

  auto* scroll = gtk_scrolled_window_new();
  gtk_scrolled_window_set_policy(GTK_SCROLLED_WINDOW(scroll),
                                 GTK_POLICY_AUTOMATIC, GTK_POLICY_AUTOMATIC);
  gtk_widget_set_vexpand(scroll, TRUE);
  auto* list = gtk_list_box_new();
  gtk_list_box_set_selection_mode(GTK_LIST_BOX(list), GTK_SELECTION_SINGLE);
  gtk_widget_add_css_class(list, list_css_class);
  gtk_widget_set_valign(list, GTK_ALIGN_START);
  gtk_scrolled_window_set_child(GTK_SCROLLED_WINDOW(scroll), list);
  page.list = GTK_LIST_BOX(list);
  gtk_stack_add_named(stack, scroll, "list");

  auto* empty = gtk_box_new(GTK_ORIENTATION_VERTICAL, 8);
  gtk_widget_set_valign(empty, GTK_ALIGN_CENTER);
  gtk_widget_set_halign(empty, GTK_ALIGN_CENTER);
  auto* icon = gtk_label_new("📦");
  gtk_widget_add_css_class(icon, "empty-icon");
  gtk_box_append(GTK_BOX(empty), icon);
  auto* text = gtk_label_new("");
  gtk_widget_add_css_class(text, "empty-text");
  gtk_label_set_wrap(GTK_LABEL(text), TRUE);
  gtk_label_set_max_width_chars(GTK_LABEL(text), 48);
  gtk_label_set_justify(GTK_LABEL(text), GTK_JUSTIFY_CENTER);
  gtk_box_append(GTK_BOX(empty), text);
  page.empty_icon = GTK_LABEL(icon);
  page.empty_text = GTK_LABEL(text);
  gtk_stack_add_named(stack, empty, "empty");
  gtk_stack_set_visible_child_name(stack, "empty");
  return page;
}

GtkWidget* make_nav_row(char const* title, Section section) {
  auto* row = gtk_list_box_row_new();
  auto* label = gtk_label_new(title);
  gtk_label_set_xalign(GTK_LABEL(label), 0.0f);
  gtk_list_box_row_set_child(GTK_LIST_BOX_ROW(row), label);
  g_object_set_data(G_OBJECT(row), "section",
                    GINT_TO_POINTER(static_cast<gint>(section)));
  return row;
}

void on_activate(GtkApplication* app, gpointer) {
  if (g_state.window != nullptr) {
    gtk_window_present(g_state.window);
    return;
  }

  auto* window = gtk_application_window_new(app);
  gtk_window_set_title(GTK_WINDOW(window), "Autarx");
  gtk_window_set_default_size(GTK_WINDOW(window), 1180, 820);
  gtk_widget_set_size_request(window, 860, 560);

  auto* root = gtk_box_new(GTK_ORIENTATION_VERTICAL, 0);
  gtk_widget_add_css_class(root, "autarx-root");
  auto* menubar_box = gtk_box_new(GTK_ORIENTATION_VERTICAL, 0);
  gtk_box_append(GTK_BOX(root), menubar_box);

  // Body: sidebar | pages + inspector.
  auto* body = gtk_paned_new(GTK_ORIENTATION_HORIZONTAL);
  gtk_widget_set_vexpand(body, TRUE);
  g_signal_connect(body, "notify::position",
                   G_CALLBACK(+[](GObject* obj, gpointer) {
                     auto* paned = GTK_PANED(obj);
                     auto const pos = gtk_paned_get_position(paned);
                     if (pos > 380) {
                       gtk_paned_set_position(paned, 380);
                     } else if (pos < 190) {
                       gtk_paned_set_position(paned, 190);
                     }
                   }),
                   nullptr);

  auto* sidebar = gtk_box_new(GTK_ORIENTATION_VERTICAL, 0);
  gtk_widget_add_css_class(sidebar, "autarx-sidebar");
  gtk_widget_set_size_request(sidebar, 190, -1);
  gtk_box_append(GTK_BOX(sidebar), make_section_caption("AUTARX"));
  auto* nav_scroll = gtk_scrolled_window_new();
  gtk_scrolled_window_set_policy(GTK_SCROLLED_WINDOW(nav_scroll),
                                 GTK_POLICY_NEVER, GTK_POLICY_AUTOMATIC);
  gtk_widget_set_vexpand(nav_scroll, TRUE);
  auto* nav_list = gtk_list_box_new();
  gtk_list_box_set_selection_mode(GTK_LIST_BOX(nav_list),
                                  GTK_SELECTION_SINGLE);
  gtk_widget_add_css_class(nav_list, "nav-list");
  static struct {
    char const* title;
    Section section;
  } const nav_specs[] = {
      {"Workspace", Section::Workspace},
      {"Objects", Section::Objects},
      {"ECUs", Section::Ecus},
      {"Unresolved", Section::Unresolved},
      {"Communication", Section::Comm},
  };
  for (auto const& spec : nav_specs) {
    gtk_list_box_insert(GTK_LIST_BOX(nav_list),
                        make_nav_row(spec.title, spec.section), -1);
  }
  gtk_scrolled_window_set_child(GTK_SCROLLED_WINDOW(nav_scroll), nav_list);
  gtk_box_append(GTK_BOX(sidebar), nav_scroll);
  gtk_paned_set_start_child(GTK_PANED(body), sidebar);

  // Detail: pages stack | divider | inspector.
  auto* detail = gtk_box_new(GTK_ORIENTATION_HORIZONTAL, 0);

  auto* pages = gtk_stack_new();
  gtk_widget_set_vexpand(pages, TRUE);
  gtk_stack_set_hhomogeneous(GTK_STACK(pages), FALSE);
  gtk_stack_set_vhomogeneous(GTK_STACK(pages), FALSE);
  g_state.pages = pages;

  // Each page gets its own inner stack so empty states work per page; the
  // outer pages stack switches on section_page_name().

  // Workspace summary page.
  {
    auto* inner = gtk_stack_new();
    gtk_widget_set_vexpand(inner, TRUE);
    g_state.workspace_page = make_list_page(GTK_STACK(inner), "summary-list");
    gtk_stack_add_named(GTK_STACK(pages), inner, "workspace");
  }

  // Objects page: search header above the list.
  GtkWidget* search = nullptr;
  {
    auto* page_box = gtk_box_new(GTK_ORIENTATION_VERTICAL, 0);
    auto* header = gtk_box_new(GTK_ORIENTATION_HORIZONTAL, 8);
    gtk_widget_set_margin_top(header, 10);
    gtk_widget_set_margin_bottom(header, 6);
    gtk_widget_set_margin_start(header, 16);
    gtk_widget_set_margin_end(header, 16);
    search = gtk_search_entry_new();
    gtk_widget_add_css_class(search, "search");
    gtk_widget_set_hexpand(search, TRUE);
    gtk_widget_set_valign(search, GTK_ALIGN_CENTER);
    gtk_box_append(GTK_BOX(header), search);
    auto* find = gtk_button_new_with_label("Find");
    gtk_widget_set_valign(find, GTK_ALIGN_CENTER);
    gtk_box_append(GTK_BOX(header), find);
    gtk_box_append(GTK_BOX(page_box), header);
    auto* inner = gtk_stack_new();
    gtk_widget_set_vexpand(inner, TRUE);
    gtk_box_append(GTK_BOX(page_box), inner);
    g_state.objects_page = make_list_page(GTK_STACK(inner), "data-list");
    g_signal_connect(search, "activate",
                     G_CALLBACK(+[](GtkEntry* entry, gpointer) {
                       run_search(gtk_editable_get_text(GTK_EDITABLE(entry)));
                     }),
                     nullptr);
    g_signal_connect(
        find, "clicked",
        G_CALLBACK(+[](GtkButton*, gpointer user_data) {
          auto* entry = GTK_ENTRY(user_data);
          run_search(gtk_editable_get_text(GTK_EDITABLE(entry)));
        }),
        search);
    gtk_stack_add_named(GTK_STACK(pages), page_box, "objects");
  }

  // ECUs / Unresolved / Communication pages: plain list pages.
  {
    auto* inner = gtk_stack_new();
    gtk_widget_set_vexpand(inner, TRUE);
    g_state.ecus_page = make_list_page(GTK_STACK(inner), "data-list");
    gtk_stack_add_named(GTK_STACK(pages), inner, "ecus");
  }
  {
    auto* inner = gtk_stack_new();
    gtk_widget_set_vexpand(inner, TRUE);
    g_state.unresolved_page = make_list_page(GTK_STACK(inner), "data-list");
    gtk_stack_add_named(GTK_STACK(pages), inner, "unresolved");
  }
  {
    auto* inner = gtk_stack_new();
    gtk_widget_set_vexpand(inner, TRUE);
    g_state.comm_page = make_list_page(GTK_STACK(inner), "data-list");
    gtk_stack_add_named(GTK_STACK(pages), inner, "comm");
  }
  gtk_box_append(GTK_BOX(detail), pages);

  auto* divider = gtk_separator_new(GTK_ORIENTATION_HORIZONTAL);
  gtk_widget_add_css_class(divider, "autarx-divider");
  gtk_box_append(GTK_BOX(detail), divider);

  // Inspector: selected object + its references (the macOS inspector).
  auto* inspector = gtk_box_new(GTK_ORIENTATION_VERTICAL, 0);
  gtk_widget_add_css_class(inspector, "autarx-sidebar");
  gtk_widget_set_size_request(inspector, 300, -1);
  auto* inspector_title = gtk_label_new("Inspector");
  gtk_label_set_xalign(GTK_LABEL(inspector_title), 0.0f);
  gtk_widget_add_css_class(inspector_title, "nav-caption");
  gtk_widget_set_margin_top(inspector_title, 12);
  gtk_widget_set_margin_start(inspector_title, 14);
  gtk_widget_set_margin_end(inspector_title, 14);
  gtk_label_set_wrap(GTK_LABEL(inspector_title), TRUE);
  gtk_box_append(GTK_BOX(inspector), inspector_title);
  auto* inspector_subtitle = gtk_label_new("");
  gtk_label_set_xalign(GTK_LABEL(inspector_subtitle), 0.0f);
  gtk_widget_add_css_class(inspector_subtitle, "meta-text");
  gtk_widget_set_margin_start(inspector_subtitle, 14);
  gtk_widget_set_margin_end(inspector_subtitle, 14);
  gtk_box_append(GTK_BOX(inspector), inspector_subtitle);
  auto* inspector_path = gtk_label_new("");
  gtk_label_set_xalign(GTK_LABEL(inspector_path), 0.0f);
  gtk_widget_add_css_class(inspector_path, "mono");
  gtk_label_set_wrap(GTK_LABEL(inspector_path), TRUE);
  gtk_widget_set_margin_start(inspector_path, 14);
  gtk_widget_set_margin_end(inspector_path, 14);
  gtk_widget_set_margin_bottom(inspector_path, 8);
  gtk_box_append(GTK_BOX(inspector), inspector_path);
  gtk_box_append(GTK_BOX(inspector),
                 make_section_caption("References"));
  auto* refs_scroll = gtk_scrolled_window_new();
  gtk_scrolled_window_set_policy(GTK_SCROLLED_WINDOW(refs_scroll),
                                 GTK_POLICY_NEVER, GTK_POLICY_AUTOMATIC);
  gtk_widget_set_vexpand(refs_scroll, TRUE);
  auto* inspector_refs = gtk_list_box_new();
  gtk_list_box_set_selection_mode(GTK_LIST_BOX(inspector_refs),
                                  GTK_SELECTION_NONE);
  gtk_widget_add_css_class(inspector_refs, "data-list");
  gtk_scrolled_window_set_child(GTK_SCROLLED_WINDOW(refs_scroll),
                                inspector_refs);
  gtk_box_append(GTK_BOX(inspector), refs_scroll);
  gtk_box_append(GTK_BOX(detail), inspector);
  g_state.inspector = GTK_BOX(inspector);
  g_state.inspector_title = GTK_LABEL(inspector_title);
  g_state.inspector_subtitle = GTK_LABEL(inspector_subtitle);
  g_state.inspector_path = GTK_LABEL(inspector_path);
  g_state.inspector_refs = GTK_LIST_BOX(inspector_refs);

  gtk_paned_set_end_child(GTK_PANED(body), detail);
  gtk_paned_set_position(GTK_PANED(body), 260);
  gtk_box_append(GTK_BOX(root), body);

  // Status bar: persistent status + progress chip on the right.
  auto* statusbar = gtk_box_new(GTK_ORIENTATION_HORIZONTAL, 0);
  gtk_widget_add_css_class(statusbar, "autarx-statusbar");
  auto* status_text = gtk_label_new("");
  gtk_label_set_ellipsize(GTK_LABEL(status_text), PANGO_ELLIPSIZE_END);
  gtk_label_set_xalign(GTK_LABEL(status_text), 0.0f);
  gtk_widget_set_margin_top(status_text, 4);
  gtk_widget_set_margin_bottom(status_text, 4);
  gtk_widget_set_margin_start(status_text, 12);
  gtk_widget_set_hexpand(status_text, TRUE);
  gtk_box_append(GTK_BOX(statusbar), status_text);
  auto* progress_text = gtk_label_new("");
  gtk_label_set_xalign(GTK_LABEL(progress_text), 1.0f);
  gtk_widget_set_margin_top(progress_text, 4);
  gtk_widget_set_margin_bottom(progress_text, 4);
  gtk_widget_set_margin_end(progress_text, 12);
  gtk_widget_set_visible(progress_text, FALSE);
  gtk_box_append(GTK_BOX(statusbar), progress_text);
  gtk_box_append(GTK_BOX(root), statusbar);

  gtk_window_set_child(GTK_WINDOW(window), root);

  g_state.window = GTK_WINDOW(window);
  g_state.menubar_box = GTK_BOX(menubar_box);
  g_state.nav_list = GTK_LIST_BOX(nav_list);
  g_state.search_box = GTK_SEARCH_ENTRY(search);
  g_state.status_text = GTK_LABEL(status_text);
  g_state.progress_text = GTK_LABEL(progress_text);

  add_window_action(GTK_APPLICATION_WINDOW(window), "open-folder",
                    on_menu_open_folder);
  add_window_action(GTK_APPLICATION_WINDOW(window), "open-file",
                    on_menu_open_file);
  add_window_action(GTK_APPLICATION_WINDOW(window), "close-workspace",
                    on_menu_close_workspace);
  add_window_action(GTK_APPLICATION_WINDOW(window), "quit", on_menu_quit);
  add_window_action(GTK_APPLICATION_WINDOW(window), "about", on_menu_about);
  add_window_action(GTK_APPLICATION_WINDOW(window), "check-updates",
                    on_menu_check_updates);

  g_signal_connect(nav_list, "row-selected",
                   G_CALLBACK(on_nav_row_selected), nullptr);
  g_signal_connect(g_state.objects_page.list, "row-selected",
                   G_CALLBACK(on_object_row_selected), nullptr);
  g_signal_connect(g_state.ecus_page.list, "row-selected",
                   G_CALLBACK(on_ecu_row_selected), nullptr);

  build_menubar();
  apply_theme();
  // Default selection paints the first nav row as active.
  gtk_list_box_select_row(GTK_LIST_BOX(nav_list),
                          gtk_list_box_get_row_at_index(GTK_LIST_BOX(nav_list),
                                                        0));
  select_section(Section::Workspace);
  set_status("Ready");
  gtk_window_present(GTK_WINDOW(window));
  start_backend();
}

void on_shutdown(GApplication*, gpointer) {
  // Exactly-once teardown: the GTK shutdown signal and the signal hook can
  // both reach this; whichever lands first does the work.
  if (g_state.teardown_started.exchange(true, std::memory_order_acq_rel)) {
    return;
  }
  g_state.shutting_down.store(true, std::memory_order_release);
  // Update flow first: its poll and the pending launch check must not fire
  // an RPC against a stopped backend, and the progress dialog is modal to
  // the main window that is about to go away (family contract).
  if (g_update.auto_check_source != 0) {
    g_source_remove(g_update.auto_check_source);
    g_update.auto_check_source = 0;
  }
  stop_update_poll();
  close_update_progress();
  if (g_state.startup_thread.joinable()) {
    g_state.startup_thread.join();
  }

  std::unique_ptr<rivet::linux_runtime::Backend> startup_backend;
  {
    std::lock_guard lock(g_state.startup_mutex);
    startup_backend = std::move(g_state.startup_backend);
  }
  if (startup_backend != nullptr) {
    startup_backend->stop();
  }
  if (g_state.backend != nullptr) {
    g_state.backend->stop();
  }
}

// SIGTERM/SIGINT bypass the GTK main loop, so the shutdown signal alone
// leaves the window alive over a dead backend. The hook's watcher thread
// runs this same teardown and then exits the process; keep it synchronous
// and allocation-light.
void perform_orderly_shutdown() { on_shutdown(nullptr, nullptr); }

}  // namespace

int main(int argc, char** argv) {
  auto* app =
      gtk_application_new("site.jrtx.autarx.Autarx", G_APPLICATION_DEFAULT_FLAGS);
  g_signal_connect(app, "activate", G_CALLBACK(on_activate), nullptr);
  g_signal_connect(app, "shutdown", G_CALLBACK(on_shutdown), nullptr);
  int const status = g_application_run(G_APPLICATION(app), argc, argv);
  g_object_unref(app);
  return status;
}
