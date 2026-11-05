// Single owner of chat view state. Rule: a fresh mount hydrates the
// transcript from the state snapshot; afterwards lines only grow via live
// events, same-session snapshots only patch the chrome (status/busy/pending/
// plan). This keeps the two C# sources from diverging in the UI.

import { useEffect, useRef, useState } from "react";
import { useValue } from "cs2/api";
import { agentEvents$, agentState$, interruptTurn, sendChatMessage } from "mods/bindings";
import type { AgentSnapshot, AgentWireEvent, ChatLine, PlanEntry } from "./chat-types";
import { applyWireEvent, emptyTranscript, hydrateTranscript } from "./transcript";

export interface ChatModel {
  session: string;
  lines: ChatLine[];
  status: string;
  busy: boolean;
  pending: number;
  plan: PlanEntry[] | null;
  send: (text: string) => void;
  interrupt: () => void;
}

const isActiveStatus = (status: string): boolean =>
  status === "Thinking" || status === "Working";

const parseSnapshot = (json: string): AgentSnapshot | null => {
  try {
    const parsed = JSON.parse(json) as AgentSnapshot;
    if (!parsed || typeof parsed !== "object" || !Array.isArray(parsed.messages)) {
      return null;
    }
    return parsed;
  } catch {
    return null;
  }
};

const parsePlan = (value: unknown): PlanEntry[] | null => {
  if (typeof value === "string") {
    try {
      value = JSON.parse(value);
    } catch {
      return null;
    }
  }
  if (value == null || typeof value !== "object") {
    return null;
  }
  const raw = (value as { entries?: unknown }).entries;
  if (!Array.isArray(raw)) {
    return null;
  }
  const entries: PlanEntry[] = [];
  for (const item of raw) {
    if (item == null || typeof item !== "object") {
      continue;
    }
    const record = item as { content?: unknown; priority?: unknown; status?: unknown };
    const priority = record.priority;
    const status = record.status;
    if (typeof record.content !== "string" || record.content.trim().length === 0) {
      continue;
    }
    if (priority !== "high" && priority !== "medium" && priority !== "low") {
      continue;
    }
    if (status !== "pending" && status !== "in_progress" && status !== "completed") {
      continue;
    }
    entries.push({ content: record.content, priority, status });
  }
  return entries.length === 0 ? null : entries;
};

const parseEvent = (json: string): AgentWireEvent | null => {
  try {
    const parsed = JSON.parse(json) as AgentWireEvent;
    if (!parsed || typeof parsed.kind !== "string") {
      return null;
    }
    return parsed;
  } catch {
    return null;
  }
};

export const useChat = (): ChatModel => {
  const [session, setSession] = useState("");
  const [lines, setLines] = useState<ChatLine[]>(emptyTranscript.lines);
  const [status, setStatus] = useState("Idle");
  const [busy, setBusy] = useState(false);
  const [pending, setPending] = useState(0);
  const [plan, setPlan] = useState<PlanEntry[] | null>(null);
  const sessionRef = useRef("");
  const nextIdRef = useRef(0);
  const snapshotJson = useValue(agentState$);
  const subscribed = useRef(false);

  useEffect(() => {
    if (subscribed.current) {
      return;
    }
    subscribed.current = true;
    const subscription = agentEvents$.subscribe((json) => {
      const event = parseEvent(json);
      if (!event) {
        return;
      }
      setLines((current) => {
        const next = applyWireEvent({ lines: current, nextId: nextIdRef.current }, event);
        nextIdRef.current = next.nextId;
        return next.lines;
      });
      switch (event.kind) {
        case "user":
          setBusy(true);
          break;
        case "delta":
          setBusy(true);
          break;
        case "tool":
          setBusy(true);
          break;
        case "status": {
          const next = event.status ?? event.text;
          setStatus(next);
          setBusy(isActiveStatus(next));
          break;
        }
        case "error":
          setBusy(false);
          break;
        case "turn":
          setBusy(false);
          break;
        case "plan":
          setPlan(parsePlan(event.text));
          break;
      }
    });
    return () => {
      subscribed.current = false;
      subscription.dispose();
    };
  }, []);

  useEffect(() => {
    const snapshot = parseSnapshot(snapshotJson);
    if (!snapshot) {
      return;
    }
    if (!snapshot.session) {
      if (sessionRef.current) {
        sessionRef.current = "";
        nextIdRef.current = 0;
        setSession("");
        setLines([]);
        setStatus("Idle");
        setBusy(false);
        setPending(0);
        setPlan(null);
      }
      return;
    }
    if (snapshot.session !== sessionRef.current) {
      sessionRef.current = snapshot.session;
      const hydrated = hydrateTranscript(snapshot.messages ?? []);
      nextIdRef.current = hydrated.nextId;
      setSession(snapshot.session);
      setStatus(snapshot.status);
      setBusy(snapshot.busy);
      setPending(snapshot.pendingInputs ?? 0);
      setPlan(parsePlan(snapshot.plan));
      setLines(hydrated.lines);
      return;
    }
    setStatus(snapshot.status);
    setBusy(snapshot.busy);
    setPending(snapshot.pendingInputs ?? 0);
    setPlan(parsePlan(snapshot.plan));
  }, [snapshotJson]);

  return {
    session,
    lines,
    status,
    busy,
    pending,
    plan,
    send: (text: string) => {
      if (text.trim().length === 0 || !sessionRef.current) {
        return;
      }
      sendChatMessage(text);
      setBusy(true);
    },
    interrupt: () => {
      interruptTurn();
    },
  };
};
