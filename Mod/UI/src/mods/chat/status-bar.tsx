import { useChatText } from "./locale";
import styles from "./chat.module.scss";

const statusDotClass = (status: string, busy: boolean): string => {
  if (busy) {
    return status === "Thinking" ? styles.dotThinking : styles.dotRunning;
  }
  switch (status) {
    case "Error":
      return styles.dotError;
    case "Interrupted":
      return styles.dotInterrupted;
    default:
      return styles.dotDone;
  }
};

interface StatusBarProps {
  status: string;
  busy: boolean;
  pending: number;
}

export const StatusBar = ({ status, busy, pending }: StatusBarProps) => {
  const text = useChatText();
  // The wire protocol keeps the English enum names; only the display is localized.
  const statusName = (value: string): string => {
    switch (value) {
      case "Idle":
        return text("Status.Idle", "Idle");
      case "Thinking":
        return text("Status.Thinking", "Thinking");
      case "Working":
        return text("Status.Working", "Working");
      case "Interrupted":
        return text("Status.Interrupted", "Interrupted");
      case "Error":
        return text("Status.Error", "Error");
      default:
        return value;
    }
  };
  return (
    <div className={styles.statusBar}>
      <span className={`${styles.statusDot} ${statusDotClass(status, busy)}`} />
      <span className={styles.statusText}>{statusName(status)}</span>
      {pending > 0 ? (
        <span className={styles.statusMeta}>{`${pending} ${text("Status.Queued", "queued")}`}</span>
      ) : null}
    </div>
  );
};
