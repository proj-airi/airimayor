"""Check one session record (ADR 0046) and judge its writes (ADR 0047).

Usage:
    python scripts/check-session.py                 # newest record under ModsData/airimayor/sessions
    python scripts/check-session.py <session dir>   # one record; owned conversations are checked too

Structure checks fail the run: a record without `ended`, broken envelope
order, a tool call that started and never ended, anchors out of order, and
references to files the record does not hold.

Each write is judged from the ledger read before and after it:
  confirmed     the city was paused on one frame, and the ledger moved as the
                write claimed (success moved an expected field; failure moved nothing)
  diverged      paused on one frame, and the claim and the ledger disagree
  undetermined  the simulation ran during the write, the ledger does not see
                this kind of write, or the record holds no evidence
Only `diverged` fails the run. EXPECTED is this check's assumption about
which ledger fields each write moves.

Standard library only. Exit code 0 = no failures, 1 = failures, 2 = no record.
"""
import json
import os
import sys
from pathlib import Path

SCHEMA = 1
LEDGER_FIELDS = ("money", "developmentPoints", "buildings", "networkEdges", "transitLines")
EXPECTED = {
    "place_building": ("buildings", "money"),
    "build_network": ("networkEdges", "money"),
    "replace_road_type": ("networkEdges", "money"),
    "set_road_features": ("money", "networkEdges"),
    "demolish": ("buildings", "networkEdges"),
    "set_facility_upgrade": ("buildings", "money"),
    "add_transit_line": ("transitLines",),
    "remove_transit_line": ("transitLines",),
    "expand_operational_area": ("money",),
    "buy_tiles": ("money",),
    "purchase_development_node": ("developmentPoints",),
}
UNSEEN = {"zone", "set_budget", "set_policy"}


def newest_record():
    user_data = os.environ.get("CSII_USERDATAPATH") or str(
        Path.home() / "AppData" / "LocalLow" / "Colossal Order" / "Cities Skylines II")
    root = Path(user_data) / "ModsData" / "airimayor" / "sessions"
    records = [path for path in root.glob("*--*") if (path / "session.json").exists()] if root.exists() else []
    return max(records, key=lambda path: path.name) if records else None


def read_lines(path):
    if not path.exists():
        return []
    lines = []
    for number, line in enumerate(path.read_text(encoding="utf-8").splitlines(), 1):
        if line.strip():
            lines.append((number, json.loads(line)))
    return lines


class Report:
    def __init__(self):
        self.failures = 0
        self.verdicts = {"confirmed": 0, "diverged": 0, "undetermined": 0}

    def fail(self, where, message):
        self.failures += 1
        print(f"FAIL  {where}: {message}")

    def note(self, where, message):
        print(f"note  {where}: {message}")

    def verdict(self, where, verdict, message):
        self.verdicts[verdict] += 1
        if verdict == "diverged":
            self.failures += 1
        print(f"{verdict:<12}  {where}: {message}")


def check_record(directory, report, label):
    session_file = directory / "session.json"
    if not session_file.exists():
        report.fail(label, "no session.json")
        return
    facts = json.loads(session_file.read_text(encoding="utf-8"))
    head = facts.get("head")
    print(f"\n== {label}  session {facts.get('session')}  head {head}  city {facts.get('city')}")
    if facts.get("parent"):
        parent = facts["parent"]
        print(f"   opened by session {parent.get('session')} turn {parent.get('turn')} conversation {parent.get('conversation')}")
    for gap in facts.get("knownGaps", []):
        report.note(label, f"known gap: {gap}")
    if "ended" not in facts:
        report.fail(label, "session.json has no `ended`: the record was cut short")
    prompt = facts.get("prompt") or {}
    if prompt.get("blob") and not (directory / prompt["blob"]).exists():
        report.fail(label, f"prompt blob {prompt['blob']} is missing")

    timeline = read_lines(directory / "timeline.jsonl")
    model_calls = read_lines(directory / "model-calls.jsonl")
    check_envelope(timeline, report, f"{label}/timeline.jsonl")
    check_envelope(model_calls, report, f"{label}/model-calls.jsonl")
    seqs = [line["seq"] for _, line in timeline] + [line["seq"] for _, line in model_calls]
    if len(seqs) != len(set(seqs)):
        report.fail(label, "a seq appears in both logs")

    model_call_ids = set()
    for number, line in model_calls:
        data = line["data"]
        model_call_ids.add(data.get("id"))
        where = f"{label}/model-calls.jsonl:{number}"
        catalog = data.get("request", {}).get("catalog")
        if catalog and not (directory / "catalogs" / f"{catalog}.json").exists():
            report.fail(where, f"catalog {catalog} is missing")
        for message in data.get("request", {}).get("messages", []):
            for part in message.get("content", []):
                if part.get("blob") and not (directory / part["blob"]).exists():
                    report.fail(where, f"blob {part['blob']} is missing")
        status = data.get("outcome", {}).get("status")
        if status != "completed":
            report.note(where, f"model call {data.get('id')} ended {status}")

    check_timeline(directory, timeline, model_call_ids, facts, report, label)

    conversations = directory / "conversations"
    if conversations.exists():
        for child in sorted(conversations.iterdir()):
            if child.is_dir():
                check_record(child, report, f"{label}/conversations/{child.name}")


def check_envelope(lines, report, label):
    last_seq = 0
    last_frame = None
    for number, line in lines:
        where = f"{label}:{number}"
        if line.get("schema") != SCHEMA:
            report.fail(where, f"schema {line.get('schema')} is not {SCHEMA}")
        seq = line.get("seq", 0)
        if seq <= last_seq:
            report.fail(where, f"seq {seq} does not increase after {last_seq}")
        last_seq = seq
        frame = (line.get("clock") or {}).get("frame")
        if frame is not None:
            if last_frame is not None and frame < last_frame:
                report.fail(where, f"clock frame {frame} goes back from {last_frame}")
            last_frame = frame


def check_timeline(directory, timeline, model_call_ids, facts, report, label):
    started_calls = {}
    open_turns = {}
    for number, line in timeline:
        where = f"{label}/timeline.jsonl:{number}"
        kind = line["type"]
        data = line.get("data", {})
        call = line.get("call")
        if kind == "turn.start":
            open_turns[line.get("turn")] = where
        elif kind == "turn.finish":
            open_turns.pop(line.get("turn"), None)
        elif kind == "function.start":
            started_calls[call] = (where, data.get("tool"))
        elif kind == "function":
            started_calls.pop(call, None)
            check_function(directory, where, line, data, report)
        elif kind == "generation":
            model_call = data.get("modelCall")
            if model_call and model_call not in model_call_ids:
                report.fail(where, f"generation names model call {model_call}, which is not in model-calls.jsonl")
    for call, (where, tool) in started_calls.items():
        report.fail(where, f"{tool} call {call} started and never ended")
    if "ended" in facts:
        for turn, where in open_turns.items():
            report.fail(where, f"turn {turn} started and never finished")


def check_function(directory, where, line, data, report):
    tool = data.get("tool")
    anchors = data.get("timeline", {})
    frames = [anchors[name]["frame"] for name in ("submitted", "applied", "completed") if name in anchors]
    if frames != sorted(frames):
        report.fail(where, f"{tool} anchors out of order: {frames}")
    for path in data.get("files", []):
        if not (directory / path).exists():
            report.fail(where, f"{tool} file {path} is missing")
    if tool in EXPECTED or tool in UNSEEN:
        judge_write(where, line.get("call"), tool, data, report)


def judge_write(where, call, tool, data, report):
    label = f"{where} {tool} {call}"
    success = bool(data.get("success"))
    claim = "succeeded" if success else "failed"
    evidence = data.get("evidence")
    if tool in UNSEEN:
        report.verdict(label, "undetermined", f"{claim}; the ledger does not see this write")
        return
    if not evidence:
        report.verdict(label, "undetermined", f"{claim}; the record holds no ledger evidence")
        return
    before, after = evidence["before"], evidence["after"]
    moved = [field for field in LEDGER_FIELDS if before.get(field) != after.get(field)]
    change = ", ".join(f"{field} {before.get(field)}->{after.get(field)}" for field in moved) or "nothing moved"
    clean = before.get("paused") and after.get("paused") and before.get("frame") == after.get("frame")
    if not clean:
        report.verdict(label, "undetermined",
                       f"{claim}; simulation ran (frames {before.get('frame')}->{after.get('frame')}); {change}")
        return
    if success:
        expected = [field for field in EXPECTED[tool] if field in moved]
        if expected:
            report.verdict(label, "confirmed", f"succeeded; {change}")
        else:
            report.verdict(label, "diverged",
                           f"reported success but none of {', '.join(EXPECTED[tool])} moved; {change}")
    elif moved:
        report.verdict(label, "diverged", f"reported failure but the city changed; {change}")
    else:
        report.verdict(label, "confirmed", "failed; nothing moved")


def main(argv):
    directory = Path(argv[1]) if len(argv) > 1 else newest_record()
    if directory is None or not directory.exists():
        print("No session record found. Pass a record directory or set CSII_USERDATAPATH.")
        return 2
    report = Report()
    check_record(directory, report, directory.name)
    counts = ", ".join(f"{count} {verdict}" for verdict, count in report.verdicts.items())
    print(f"\nwrites: {counts}; failures: {report.failures}")
    return 1 if report.failures else 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
