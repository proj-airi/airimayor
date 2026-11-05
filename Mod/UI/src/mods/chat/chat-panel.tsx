import type { ReactNode } from "react";
import { useRef, useState } from "react";
import type { CSSProperties, MouseEvent as ReactMouseEvent } from "react";
import { FOCUS_AUTO, Panel, Portal } from "cs2/ui";
import { useChat } from "./use-chat";
import { useChatText } from "./locale";
import { MessageList } from "./message-list";
import { PlanStrip } from "./plan-strip";
import { StatusBar } from "./status-bar";
import { Composer } from "./composer";
import { setPanelOpen, usePanelOpen } from "./panel-visibility";
import {
  clampPanelFrame,
  movePanelFrame,
  PANEL_MIN_HEIGHT,
  PANEL_MIN_WIDTH,
  resizePanelFrame,
  type PanelFrame,
  type PanelSizeLimits,
  type ResizeEdge,
} from "./panel-frame";
import styles from "./chat.module.scss";

const FRAME_KEY = "cityagent.panel.frame";
const LIMITS: PanelSizeLimits = { minWidth: PANEL_MIN_WIDTH, minHeight: PANEL_MIN_HEIGHT };

const loadFrame = (): PanelFrame | null => {
  try {
    const raw = localStorage.getItem(FRAME_KEY);
    if (raw) {
      const parsed = JSON.parse(raw) as Partial<PanelFrame>;
      if (
        typeof parsed.x === "number" &&
        typeof parsed.y === "number" &&
        typeof parsed.width === "number" &&
        typeof parsed.height === "number"
      ) {
        return clampPanelFrame(
          { x: parsed.x, y: parsed.y, width: parsed.width, height: parsed.height },
          { width: window.innerWidth, height: window.innerHeight },
          LIMITS,
        );
      }
    }
  } catch {
    // Unknown shape or unavailable storage falls back to the anchored dock.
  }
  return null;
};

const RESIZE_HANDLES: { edge: ResizeEdge; className: string }[] = [
  { edge: "n", className: styles.edgeN },
  { edge: "s", className: styles.edgeS },
  { edge: "e", className: styles.edgeE },
  { edge: "w", className: styles.edgeW },
  { edge: "ne", className: styles.edgeNE },
  { edge: "nw", className: styles.edgeNW },
  { edge: "se", className: styles.edgeSE },
  { edge: "sw", className: styles.edgeSW },
];

const readParentBounds = (node: HTMLElement | null): { width: number; height: number } => {
  const parent = node?.offsetParent;
  if (!(parent instanceof HTMLElement)) {
    return { width: window.innerWidth, height: window.innerHeight };
  }
  const rect = parent.getBoundingClientRect();
  return { width: rect.width, height: rect.height };
};

const measureFrame = (node: HTMLElement): PanelFrame => {
  const parent = node.offsetParent;
  const panelRect = node.getBoundingClientRect();
  if (!(parent instanceof HTMLElement)) {
    return clampPanelFrame(
      { x: panelRect.left, y: panelRect.top, width: panelRect.width, height: panelRect.height },
      { width: window.innerWidth, height: window.innerHeight },
      LIMITS,
    );
  }
  const parentRect = parent.getBoundingClientRect();
  return clampPanelFrame(
    {
      x: panelRect.left - parentRect.left,
      y: panelRect.top - parentRect.top,
      width: panelRect.width,
      height: panelRect.height,
    },
    { width: parentRect.width, height: parentRect.height },
    LIMITS,
  );
};

export const ChatPanel = ({ children }: { children?: ReactNode }) => {
  const chat = useChat();
  const text = useChatText();
  const open = usePanelOpen();
  const [frame, setFrame] = useState<PanelFrame | null>(loadFrame);
  const dockRef = useRef<HTMLDivElement>(null);
  const frameRef = useRef<PanelFrame | null>(null);
  frameRef.current = frame;

  const captureFrame = (): PanelFrame => {
    if (frameRef.current === null) {
      const node = dockRef.current;
      const measured = node === null ? frame : measureFrame(node);
      frameRef.current =
        measured ??
        clampPanelFrame(
          { x: 0, y: 0, width: PANEL_MIN_WIDTH, height: PANEL_MIN_HEIGHT },
          readParentBounds(null),
          LIMITS,
        );
      setFrame(frameRef.current);
    }
    return frameRef.current;
  };

  const beginGesture = (
    edge: ResizeEdge | null,
    event: ReactMouseEvent<HTMLElement>,
  ): void => {
    if (event.button !== 0) {
      return;
    }
    event.preventDefault();
    let current = captureFrame();
    let lastX = event.clientX;
    let lastY = event.clientY;
    const onMove = (moveEvent: MouseEvent): void => {
      const deltaX = moveEvent.clientX - lastX;
      const deltaY = moveEvent.clientY - lastY;
      lastX = moveEvent.clientX;
      lastY = moveEvent.clientY;
      const bounds = readParentBounds(dockRef.current);
      current =
        edge === null
          ? movePanelFrame(current, deltaX, deltaY, bounds, LIMITS)
          : resizePanelFrame(current, edge, deltaX, deltaY, bounds, LIMITS);
      frameRef.current = current;
      setFrame(current);
    };
    const onUp = (): void => {
      window.removeEventListener("mousemove", onMove);
      window.removeEventListener("mouseup", onUp);
      try {
        localStorage.setItem(FRAME_KEY, JSON.stringify(current));
      } catch {
        // Storage is best-effort; the live frame still applies.
      }
    };
    window.addEventListener("mousemove", onMove);
    window.addEventListener("mouseup", onUp);
  };

  const onResizeStart = (edge: ResizeEdge, event: ReactMouseEvent<HTMLElement>): void => {
    event.stopPropagation();
    beginGesture(edge, event);
  };

  // The header bar drags the panel. Message text (selectable), status bar,
  // composer, and buttons keep their own behavior.
  const onDockMouseDown = (event: ReactMouseEvent<HTMLDivElement>): void => {
    const target = event.target as HTMLElement | null;
    if (
      target?.closest(
        `.${styles.messageList}, .${styles.statusBar}, .${styles.planStrip}, .${styles.composer}, button, textarea, input`,
      )
    ) {
      return;
    }
    beginGesture(null, event);
  };

  if (!open) {
    return <>{children}</>;
  }

  const frameStyle: CSSProperties | undefined = frame === null
    ? undefined
    : {
        width: frame.width,
        height: frame.height,
        top: frame.y,
        left: frame.x,
        right: "auto",
        bottom: "auto",
      };

  return (
    <>
      {children}
      <Portal>
        <div
          ref={dockRef}
          className={styles.dock}
          data-airimayor-chat=""
          style={frameStyle}
          onMouseDown={onDockMouseDown}
        >
          <Panel
            header={text("Title", "AIRI Mayor")}
            focusKey={FOCUS_AUTO}
            className={styles.panelColumn}
            onClose={() => setPanelOpen(false)}
            footer={
              <Composer
                loading={chat.session === ""}
                busy={chat.busy}
                onSend={chat.send}
                onInterrupt={chat.interrupt}
              />
            }
          >
            <StatusBar status={chat.status} busy={chat.busy} pending={chat.pending} />
            <PlanStrip plan={chat.plan} />
            <MessageList lines={chat.lines} busy={chat.busy} />
          </Panel>
          {RESIZE_HANDLES.map((handle) => (
            <span
              key={handle.edge}
              aria-hidden="true"
              className={`${styles.resizeHandle} ${handle.className}`}
              onMouseDown={(event) => onResizeStart(handle.edge, event)}
            />
          ))}
        </div>
      </Portal>
    </>
  );
};
