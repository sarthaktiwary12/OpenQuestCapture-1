import { BoxRenderable, TextRenderable, ScrollBoxRenderable, type CliRenderer } from "@opentui/core";
import { setOverlayActive } from "../state";
import pc from "picocolors";

export interface SelectOverlayOption {
  label: string;
  value: string;
}

export function showSelect(
  renderer: CliRenderer,
  title: string,
  options: SelectOverlayOption[]
): Promise<string | null> {
  return new Promise((resolve) => {
    if (options.length === 0) {
      resolve(null);
      return;
    }

    setOverlayActive(true);

    let cursor = 0;

    const overlay = new BoxRenderable(renderer, {
      id: "select-overlay",
      position: "absolute",
      width: "50%",
      height: Math.min(options.length + 4, 15),
      top: "25%",
      left: "25%",
      border: true,
      borderStyle: "rounded",
      borderColor: "#00AAFF",
      backgroundColor: "#1a1a2e",
      flexDirection: "column",
      paddingX: 1,
      zIndex: 100,
    });

    const titleText = new TextRenderable(renderer, {
      id: "sel-title",
      content: pc.bold(title),
      height: 1,
    });

    const scrollBox = new ScrollBoxRenderable(renderer, {
      id: "sel-scroll",
      width: "100%",
      flexGrow: 1,
      scrollY: true,
      viewportCulling: true,
    });

    const hintText = new TextRenderable(renderer, {
      id: "sel-hint",
      content: pc.dim("↑↓ navigate · Enter select · Esc cancel"),
      height: 1,
    });

    overlay.add(titleText);
    overlay.add(scrollBox);
    overlay.add(hintText);
    renderer.root.add(overlay);

    function renderItems() {
      const children = scrollBox.getChildren();
      for (const child of children) {
        scrollBox.remove(child.id);
      }

      for (let i = 0; i < options.length; i++) {
        const opt = options[i];
        const isCur = i === cursor;
        const prefix = isCur ? "> " : "  ";
        const label = isCur ? opt.label : pc.dim(opt.label);

        const line = new TextRenderable(renderer, {
          id: `sel-opt-${i}`,
          content: `${prefix}${label}`,
          bg: isCur ? "#334455" : undefined,
        });
        scrollBox.add(line);
      }
      renderer.requestRender();
    }

    renderItems();

    const cleanup = () => {
      renderer.root.remove("select-overlay");
      renderer.keyInput.removeListener("keypress", handler);
      setOverlayActive(false);
      renderer.requestRender();
    };

    const handler = (key: any) => {
      if (key.name === "escape") {
        cleanup();
        resolve(null);
        return;
      }

      if (key.name === "return") {
        const val = options[cursor].value;
        cleanup();
        resolve(val);
        return;
      }

      if (key.name === "up" || key.name === "k") {
        cursor = Math.max(0, cursor - 1);
        renderItems();
      } else if (key.name === "down" || key.name === "j") {
        cursor = Math.min(options.length - 1, cursor + 1);
        renderItems();
      }
    };

    renderer.keyInput.on("keypress", handler);
  });
}
