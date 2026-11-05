// Pure transcript reducer: the only place ChatLine[] is built.
// No React, no bindings, no window globals — snapshots hydrate on session
// switch, live wire events append. A tool event with callId patches that
// ACP row. A tool event without one still closes the last running row.

import type { AgentWireEvent, ChatLine, StateMessage, ToolRowState } from "./chat-types";
import { readPlaces } from "./place";

export interface Transcript {
  lines: ChatLine[];
  nextId: number;
}

export const emptyTranscript: Transcript = { lines: [], nextId: 0 };

const maxTextLength = 4000;

const truncate = (text: string): string =>
  text.length > maxTextLength ? text.slice(0, maxTextLength) + "…" : text;

const push = (
  transcript: Transcript,
  lines: ChatLine[],
  make: (id: number) => ChatLine,
): Transcript => ({
  lines: [...lines, make(transcript.nextId)],
  nextId: transcript.nextId + 1,
});

const finalizeStreaming = (lines: ChatLine[]): ChatLine[] =>
  lines.map((line) =>
    line.kind === "assistant" && line.streaming ? { ...line, streaming: false } : line,
  );

const settleRunningTools = (lines: ChatLine[], state: ToolRowState): ChatLine[] =>
  lines.map((line) =>
    line.kind === "tool" && line.state === "running" ? { ...line, state } : line,
  );

export function hydrateTranscript(messages: StateMessage[]): Transcript {
  const lines: ChatLine[] = [];
  messages.forEach((message) => {
    const text = (message.text ?? "").trim();
    const places = readPlaces(message.places);
    if (message.role === "tool") {
      lines.push({
        id: lines.length,
        kind: "tool",
        name: message.tool ?? "tool",
        args: "",
        result: text.length > 0 ? text : null,
        output: null,
        image: null,
        state: "done",
      });
      return;
    }
    if (text.length === 0 && places.length === 0) {
      return;
    }
    if (message.role === "user") {
      lines.push({ id: lines.length, kind: "user", text, places });
    } else if (message.role === "error") {
      lines.push({ id: lines.length, kind: "error", text });
    } else {
      lines.push({ id: lines.length, kind: "assistant", text, streaming: false });
    }
  });
  return { lines, nextId: lines.length };
}

const acpToolState = (status: string | undefined): ToolRowState => {
  if (status === "Error") {
    return "error";
  }
  if (status === "Working") {
    return "running";
  }
  return "done";
};

const toPatchedResult = (result: string | undefined): string | null | undefined => {
  if (result === undefined) {
    return undefined;
  }
  return result.length > 0 ? truncate(result) : null;
};

const applyAcpTool = (transcript: Transcript, event: AgentWireEvent): Transcript => {
  const lines = [...transcript.lines];
  const index = lines.findIndex((line) => line.kind === "tool" && line.callId === event.callId);
  const result = toPatchedResult(event.result);
  if (index >= 0) {
    const open = lines[index];
    if (open.kind !== "tool") {
      return transcript;
    }
    lines[index] = {
      ...open,
      name: event.tool ?? open.name,
      args: truncate(event.text ?? ""),
      result: result === undefined ? open.result : result,
      output: event.output ?? open.output,
      image: event.image ? event.image : open.image,
      state: acpToolState(event.status),
    };
    return { ...transcript, lines };
  }
  return push(transcript, lines, (id) => ({
    id,
    kind: "tool",
    callId: event.callId,
    name: event.tool ?? "tool",
    args: truncate(event.text ?? ""),
    result: result === undefined ? null : result,
    output: event.output ?? null,
    image: event.image ?? null,
    state: acpToolState(event.status),
  }));
};

export function applyWireEvent(
  transcript: Transcript,
  event: AgentWireEvent,
): Transcript {
  const text = event.text ?? "";
  switch (event.kind) {
    case "user": {
      const places = readPlaces(event.places);
      if (text.trim().length === 0 && places.length === 0) {
        return transcript;
      }
      return push(transcript, finalizeStreaming(transcript.lines), (id) => ({
        id,
        kind: "user",
        text: text.trim(),
        places,
      }));
    }
    case "delta": {
      if (text.length === 0) {
        return transcript;
      }
      const lines = [...transcript.lines];
      const last = lines[lines.length - 1];
      if (last && last.kind === "assistant" && last.streaming) {
        lines[lines.length - 1] = { ...last, text: truncate(last.text + text) };
        return { ...transcript, lines };
      }
      return push(transcript, lines, (id) => ({
        id,
        kind: "assistant",
        text: truncate(text),
        streaming: true,
      }));
    }
    case "tool": {
      if (event.callId) {
        return applyAcpTool(transcript, event);
      }
      const lines = [...transcript.lines];
      let openIndex = -1;
      for (let index = lines.length - 1; index >= 0; index--) {
        const line = lines[index];
        if (line.kind === "tool" && line.state === "running") {
          openIndex = index;
          break;
        }
      }
      if (openIndex >= 0) {
        const open = lines[openIndex];
        if (open.kind !== "tool") {
          return transcript;
        }
        lines[openIndex] = {
          ...open,
          result: text.length > 0 ? truncate(text) : null,
          image: event.image ? event.image : null,
          state: event.status === "Error" ? "error" : "done",
        };
        return { ...transcript, lines };
      }
      return push(transcript, lines, (id) => ({
        id,
        kind: "tool",
        name: event.tool ?? "tool",
        args: truncate(text),
        result: null,
        output: null,
        image: null,
        state: "running",
      }));
    }
    case "status": {
      const status = event.status ?? "";
      if (status === "Idle" || status === "Interrupted" || status === "Error") {
        return {
          ...transcript,
          lines: settleRunningTools(
            finalizeStreaming(transcript.lines),
            status === "Interrupted" ? "interrupted" : "done",
          ),
        };
      }
      return transcript;
    }
    case "turn": {
      return {
        ...transcript,
        lines: settleRunningTools(finalizeStreaming(transcript.lines), "done"),
      };
    }
    case "error": {
      const settled = settleRunningTools(finalizeStreaming(transcript.lines), "error");
      if (text.trim().length === 0) {
        return { ...transcript, lines: settled };
      }
      return push(transcript, settled, (id) => ({ id, kind: "error", text }));
    }
    case "plan":
    default: {
      return transcript;
    }
  }
}
