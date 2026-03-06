import { BoxRenderable, TextRenderable, ScrollBoxRenderable, type CliRenderer } from "@opentui/core";
import { setOverlayActive } from "../state";
import pc from "picocolors";

export interface MultiselectOption {
  label: string;
  value: string;
  hint?: string;
}

export function showMultiselect(
  renderer: CliRenderer,
  title: string,
  options: MultiselectOption[]
): Promise<string[] | null> {
  return new Promise((resolve) => {
    if (options.length === 0) {
      resolve(null);
      return;
    }

    setOverlayActive(true);

    let cursor = 0;
    const selected = new Set<string>();

    const overlay = new BoxRenderable(renderer, {
      id: "multiselect-overlay",
      position: "absolute",
      width: "60%",
      height: "60%",
      top: "20%",
      left: "20%",
      border: true,
      borderStyle: "rounded",
      borderColor: "#00AAFF",
      backgroundColor: "#1a1a2e",
      flexDirection: "column",
      paddingX: 1,
      zIndex: 100,
    });

    const titleText = new TextRenderable(renderer, {
      id: "ms-title",
      content: pc.bold(title),
      height: 1,
    });

    const scrollBox = new ScrollBoxRenderable(renderer, {
      id: "ms-scroll",
      width: "100%",
      flexGrow: 1,
      scrollY: true,
      viewportCulling: true,
    });

    const hintText = new TextRenderable(renderer, {
      id: "ms-hint",
      content: pc.dim("Space toggle · a select all · Enter confirm · Esc cancel"),
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
        const isSel = selected.has(opt.value);
        const isCur = i === cursor;
        const check = isSel ? pc.green("[x]") : "[ ]";
        const prefix = isCur ? "> " : "  ";
        const label = isCur ? opt.label : pc.dim(opt.label);
        const hint = opt.hint ? pc.dim(` ${opt.hint}`) : "";

        const line = new TextRenderable(renderer, {
          id: `ms-opt-${i}`,
          content: `${prefix}${check} ${label}${hint}`,
          bg: isCur ? "#334455" : undefined,
        });
        scrollBox.add(line);
      }
      renderer.requestRender();
    }

    renderItems();

    const cleanup = () => {
      renderer.root.remove("multiselect-overlay");
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
        const result = [...selected];
        cleanup();
        resolve(result.length > 0 ? result : null);
        return;
      }

      if (key.name === "up" || key.name === "k") {
        cursor = Math.max(0, cursor - 1);
        renderItems();
      } else if (key.name === "down" || key.name === "j") {
        cursor = Math.min(options.length - 1, cursor + 1);
        renderItems();
      } else if (key.name === "space") {
        const val = options[cursor].value;
        if (selected.has(val)) selected.delete(val);
        else selected.add(val);
        renderItems();
      } else if (key.name === "a") {
        if (selected.size === options.length) {
          selected.clear();
        } else {
          for (const opt of options) selected.add(opt.value);
        }
        renderItems();
      }
    };

    renderer.keyInput.on("keypress", handler);
  });
}
