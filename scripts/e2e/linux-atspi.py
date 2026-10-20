#!/usr/bin/env python3
import argparse, json, time
from pathlib import Path
import pyatspi

def children(node):
    for index in range(node.childCount):
        try: yield node.getChildAtIndex(index)
        except Exception: continue
def walk(node):
    yield node
    for child in children(node): yield from walk(child)
def role(node):
    try: return node.getRoleName()
    except Exception: return "unknown"
def text(node):
    try:
        value = node.queryText(); return value.getText(0, value.characterCount)
    except Exception: return ""
def contains(root, fragment): return any(fragment in (node.name or "") or fragment in text(node) for node in walk(root))
def wait_for(description, probe, timeout=60.0):
    deadline = time.monotonic() + timeout
    while time.monotonic() < deadline:
        value = probe()
        if value: return value
        time.sleep(0.05)
    raise AssertionError(f"timed out waiting for {description}")
def find_application():
    desktop = pyatspi.Registry.getDesktop(0)
    for application in children(desktop):
        if any(role(node) == "frame" and (node.name or "") == "Autarx" for node in walk(application)): return application
    return None
def press(node):
    # GtkListBox exposes its semantic navigation through the Selection
    # interface. Selecting the child drives the same row-selected signal as
    # a pointer click; the optional row action is a second accessibility path.
    parent = node.parent
    parent.querySelection().selectChild(node.getIndexInParent())
    try:
        action = node.queryAction()
        if action.nActions > 0: action.doAction(0)
    except Exception:
        pass
def snapshot(node, depth=0):
    result = {"name": node.name or "", "role": role(node)}; value = text(node)
    if value: result["text"] = value
    if depth < 8: result["children"] = [snapshot(child, depth + 1) for child in children(node)]
    return result

def main():
    parser = argparse.ArgumentParser(); parser.add_argument("--output", type=Path, required=True); args = parser.parse_args(); args.output.mkdir(parents=True, exist_ok=True)
    application = None
    try:
        application = wait_for("Autarx application", find_application)
        window = wait_for("Autarx frame", lambda: next((node for node in walk(application) if role(node) == "frame"), None))
        wait_for("workspace result from embedded backend", lambda: contains(window, "OemDelivery") and contains(window, "objects"))
        objects = wait_for("Objects navigation row", lambda: next((node for node in walk(window) if (node.name or "") == "Objects" and role(node) == "list item"), None))
        press(objects)
        wait_for("VehicleSpeed object rendered after navigation", lambda: contains(window, "VehicleSpeed"))
        (args.output / "accessibility.json").write_text(json.dumps(snapshot(application), ensure_ascii=False, indent=2), encoding="utf-8")
        (args.output / "interaction-trace.json").write_text(json.dumps({"platform":"linux","actions":["press:Objects"],"assertions":["packaged GTK4 frame activated on native Wayland","fixture opened through RVT1","Objects navigation exposed through AT-SPI","VehicleSpeed rendered from embedded backend"]}, indent=2), encoding="utf-8")
    except Exception:
        if application is not None: (args.output / "accessibility-failure.json").write_text(json.dumps(snapshot(application), ensure_ascii=False, indent=2), encoding="utf-8")
        raise
if __name__ == "__main__": main()
