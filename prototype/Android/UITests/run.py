#!/usr/bin/env python3
"""Exercise an installed AppNav Compose probe on an Android emulator."""

import argparse
from datetime import datetime
import os
from pathlib import Path
import re
import subprocess
import time
from xml.etree import ElementTree

PACKAGE = "com.adamessenmacher.appnav.probe"
ACTIVITY = f"{PACKAGE}/.MainActivity"
DUMP_PATH = "/data/local/tmp/appnav-probe-window.xml"


class Probe:
    def __init__(self, serial):
        sdk = os.environ.get("ANDROID_HOME")
        self.adb = [str(Path(sdk) / "platform-tools" / "adb") if sdk else "adb"]
        if serial:
            self.adb += ["-s", serial]
        self.pid = ""

    def run(self, *args, timeout=30):
        return subprocess.run(
            self.adb + list(args), check=True, text=True, stdout=subprocess.PIPE, timeout=timeout
        ).stdout.strip()

    def reset(self, activity=ACTIVITY, split=False):
        # Start each scenario from a new managed router and restrict logs to that process.
        self.run("shell", "am", "force-stop", PACKAGE)
        self.activity = activity
        self.run("shell", "am", "start", "-W", "-a", "android.intent.action.MAIN",
                 "-c", "android.intent.category.LAUNCHER", "-n", activity, "--ez", "split", str(split).lower())
        self.pid = self.run("shell", "pidof", "-s", PACKAGE)

    def fold(self, folded):
        """Fold or unfold the foldable emulator and bring the probe's task back to the front.

        The emulator sleeps when it folds, so wake it and resume the task with an intent that matches its root.
        Requires `adb shell locksettings set-disabled true` so no keyguard stands in front of the task.
        """
        self.run("emu", "fold" if folded else "unfold")
        time.sleep(2)
        self.run("shell", "input", "keyevent", "KEYCODE_WAKEUP")
        self.run("shell", "wm", "dismiss-keyguard")
        time.sleep(1)
        self.run("shell", "am", "start", "-a", "android.intent.action.MAIN", "-c", "android.intent.category.LAUNCHER",
                 "-f", "0x10000000", "-n", self.activity)
        state = self.run("shell", "cmd", "device_state", "print-state")
        assert state == ("0" if folded else "2"), f"Device state {state}"

    def missing(self, *texts):
        visible = self.wait_for_texts()
        present = [text for text in texts if text.casefold() in (label.casefold() for label in visible)]
        assert not present, f"Expected {present} to be hidden; visible texts were {list(visible)}"

    def focused(self):
        return self.run("shell", "dumpsys", "window").split("mCurrentFocus=", 1)[1].split("\n", 1)[0]

    def wait_for_texts(self, *expected):
        deadline = time.monotonic() + 75
        expected_labels = {label.casefold() for label in expected}
        texts = {}
        while time.monotonic() < deadline:
            try:
                result = self.run("shell", "uiautomator", "dump", DUMP_PATH)
            except subprocess.TimeoutExpired:
                continue
            if "dumped to" not in result:
                continue
            xml = self.run("shell", "cat", DUMP_PATH)
            if not xml:
                continue
            nodes = ElementTree.fromstring(xml).iter("node")
            texts = {node.attrib["text"]: node.attrib["bounds"] for node in nodes if node.attrib.get("text")}
            if expected_labels.issubset(label.casefold() for label in texts):
                return texts
        raise AssertionError(f"Expected {expected}, visible texts were {list(texts)}")

    def tap(self, text):
        texts = self.wait_for_texts(text)
        bounds = next(bounds for label, bounds in texts.items() if label.casefold() == text.casefold())
        x0, y0, x1, y1 = map(int, re.findall(r"\d+", bounds))
        self.run("shell", "input", "tap", str((x0 + x1) // 2), str((y0 + y1) // 2))

    def back(self):
        self.run("shell", "input", "keyevent", "KEYCODE_BACK")

    def predictive_back(self, fraction, *, cancel=False):
        mode = self.run("shell", "settings", "get", "secure", "navigation_mode")
        if mode != "2":
            raise AssertionError("Predictive Back checks require Android's gesture navigation mode")
        display = self.run("shell", "wm", "size")
        width, height = map(int, re.findall(r"(\d+)x(\d+)", display)[-1])
        y = round(height * 0.48)
        x = round(width * fraction)
        if not cancel:
            self.run("shell", "input", "touchscreen", "swipe", "2", str(y), str(x), str(y), "1000")
            return
        # Hold the native preview, then reverse the touch to the edge and lift there.
        edge = 2
        down = f"input touchscreen motionevent DOWN {edge} {y}"
        forward = [f"input touchscreen motionevent MOVE {round(edge + (x - edge) * step / 4)} {y}"
                   for step in range(1, 5)]
        reverse = [f"input touchscreen motionevent MOVE {round(edge + (x - edge) * step / 6)} {y}"
                   for step in range(5, -1, -1)]
        up = f"input touchscreen motionevent UP {edge} {y}"
        self.run("shell", " && ".join([down, *forward, "sleep .50", *reverse, "sleep .40", up]))

    def logs(self):
        return self.run("logcat", "-d", "--pid", self.pid, "-s", "AppNavProbe:V", "*:S")

    def wait_for_log(self, pattern, after=0):
        deadline = time.monotonic() + 20
        while time.monotonic() < deadline:
            log = self.logs()
            if match := re.search(pattern, log[after:]):
                return match
            time.sleep(0.25)
        raise AssertionError(f"Missing log pattern {pattern!r}\n{log}")


def verify_branch_switch(probe):
    probe.reset()
    probe.wait_for_texts("Page: home-detail")
    probe.wait_for_log(r"history=1\b")
    probe.tap("Local count 0")
    probe.wait_for_texts("Local count 1")
    probe.tap("Deep link")
    probe.wait_for_texts("Page: detail")
    probe.wait_for_log(r"catalog\[catalog,group,item,detail\].*history=2\b")
    probe.back()
    probe.wait_for_texts("Page: item")
    probe.wait_for_log(r"catalog\[catalog,group,item\].*history=3\b")
    probe.tap("Home")
    probe.wait_for_texts("Page: home-detail")
    probe.wait_for_texts("Local count 1")
    probe.wait_for_log(r"home\[home,home-detail\].*history=4\b")
    probe.tap("Catalog")
    probe.wait_for_texts("Page: item")
    probe.wait_for_log(r"catalog\[catalog,group,item\].*history=5\b")
    print("PASS branch-switch deep link, native Back, and retained branches")


def verify_recreation(probe):
    probe.reset()
    probe.wait_for_texts("Page: home-detail")
    probe.wait_for_log(r"history=1\b")
    probe.tap("Recreate + link")
    withheld = probe.wait_for_log(r"withheld epoch=(\d+) rev=(\d+)")
    epoch, revision = map(int, withheld.groups())
    probe.wait_for_log(
        rf"(?s)surface epoch={epoch + 1} rev={revision}\b.*?"
        rf"stale ignored epoch={epoch} rev={revision} history=1\b.*?"
        r"catalog\[catalog,group,item,detail\][^\n]*history=2\b"
    )
    probe.wait_for_texts("Page: detail")
    probe.back()
    probe.wait_for_texts("Page: item")
    probe.wait_for_log(r"catalog\[catalog,group,item\].*history=3\b")
    print("PASS Activity recreation during a pending deep link and stale observation fencing")


def verify_modal_stack(probe):
    probe.reset()
    probe.wait_for_texts("Page: home-detail")
    probe.wait_for_log(r"history=1\b")
    probe.tap("Modal")
    probe.wait_for_texts("Page: review")
    probe.wait_for_log(r"surface .*home \[home,home-detail\] modal=editor \[edit,review\]")
    probe.wait_for_log(r"history=2\b")
    probe.back()
    probe.wait_for_texts("Page: edit")
    probe.wait_for_log(r"surface .*home \[home,home-detail\] modal=editor \[edit\]")
    probe.wait_for_log(r"history=3\b")
    probe.back()
    probe.wait_for_texts("Page: home-detail")
    probe.wait_for_log(r"surface .*home \[home,home-detail\] modal= \[\]")
    probe.wait_for_log(r"history=4\b")
    print("PASS modal stack Back and dismissal reveal the unchanged underlying branch")


def verify_managed_back(probe):
    probe.reset()
    probe.wait_for_log(r"home\[home,home-detail\][^\n]*history=1\b")
    probe.tap("Play Game")
    probe.wait_for_log(GAME_HISTORY)
    probe.tap("Managed Back: off")
    probe.wait_for_texts("Managed Back: on")
    probe.tap("Step Back test")

    start = len(probe.logs())
    probe.predictive_back(0.70)
    denied = probe.wait_for_log(r"policy back requested id=(\d+) history=2\b", after=start)
    probe.run("shell", "input", "keyevent", "KEYCODE_BACK", "KEYCODE_BACK")
    probe.tap("Step Back test")
    probe.wait_for_log(r"policy back Canceled history=2\b", after=start)
    probe.wait_for_log(rf"policy back finished id={denied[1]}\b", after=start)
    probe.wait_for_texts("Page: game")

    retry = len(probe.logs())
    probe.back()
    accepted = probe.wait_for_log(r"policy back requested id=(\d+) history=2\b", after=retry)
    probe.run("shell", "input", "keyevent", "KEYCODE_BACK", "KEYCODE_BACK")
    probe.tap("Step Back test")
    probe.wait_for_log(r"policy back Completed history=3\b", after=retry)
    # The previous request still owns the gate even after the new stack has settled.
    probe.run("shell", "input", "keyevent", "KEYCODE_BACK", "KEYCODE_BACK")
    probe.tap("Step Back test")
    probe.wait_for_log(rf"policy back finished id={accepted[1]}\b", after=retry)
    probe.wait_for_texts("Page: home-detail")
    policy_log = probe.logs()[start:]
    assert len(re.findall(r"policy back requested", policy_log)) == 2, policy_log
    assert "game predictive transform" not in policy_log, policy_log
    assert "game shared predictive preview" not in policy_log, policy_log
    assert not re.search(r"history=[4-9]\d*\b", policy_log), policy_log
    print("PASS managed Back suppresses predictive animation and duplicate requests, then reopens after denial")


def verify_managed_back_recovery(probe):
    probe.reset()
    probe.wait_for_texts("Page: home-detail")
    probe.wait_for_log(r"history=1\b")
    probe.tap("Deep link")
    probe.wait_for_texts("Page: detail")
    probe.wait_for_log(r"history=2\b")
    probe.tap("Managed Back: off")
    probe.wait_for_texts("Managed Back: on")

    probe.tap("Throw on next Back")
    probe.back()
    probe.wait_for_log(r"policy back failed: Probe Back callback failed")
    probe.wait_for_texts("Page: detail")
    assert "policy back requested" not in probe.logs(), probe.logs()

    probe.tap("Drop Back completion")
    probe.wait_for_log(r"policy back completion drop armed")
    probe.back()
    probe.wait_for_log(r"policy back completion dropped id=\d+")
    probe.wait_for_log(r"policy back failed: timeout")
    probe.wait_for_texts("Page: detail")

    retry = len(probe.logs())
    probe.back()
    denied = probe.wait_for_log(r"policy back requested id=(\d+) history=2\b", after=retry)
    probe.wait_for_log(r"policy back Canceled history=2\b", after=retry)
    probe.wait_for_log(rf"policy back finished id={denied[1]}\b", after=retry)
    probe.wait_for_texts("Page: detail")

    probe.tap("Step Back test")
    probe.tap("Time out next Back")
    pending = len(probe.logs())
    probe.back()
    original = probe.wait_for_log(r"policy back requested id=(\d+) history=2\b", after=pending)
    probe.wait_for_log(r"policy back evaluating", after=pending)
    probe.wait_for_log(r"policy back failed: timeout", after=pending)
    probe.wait_for_texts("Page: detail")

    probe.back()
    rejected = probe.wait_for_log(r"policy back rejected id=(\d+)", after=pending)
    assert rejected[1] != original[1]
    probe.wait_for_log(rf"policy back rejection observed id={rejected[1]}\b", after=pending)
    assert not re.search(r"policy back (finished|Completed)", probe.logs()[pending:]), probe.logs()

    probe.tap("Step Back test")
    probe.wait_for_log(r"policy back Completed history=3\b", after=pending)
    probe.tap("Step Back test")
    probe.wait_for_log(rf"policy back finished id={original[1]}\b", after=pending)
    probe.wait_for_texts("Page: item")
    trace = probe.logs()[pending:]
    assert len(re.findall(r"policy back requested", trace)) == 1, trace
    assert len(re.findall(r"policy back Completed", trace)) == 1, trace
    assert not re.search(r"history=[4-9]\d*\b", trace), trace
    print("PASS managed Back reports callback errors and timeouts, rejects a retry during a pending policy, and pops once")


def verify_managed_back_recreation(probe):
    probe.reset()
    probe.wait_for_texts("Page: home-detail")
    probe.wait_for_log(r"history=1\b")
    probe.tap("Deep link")
    probe.wait_for_texts("Page: detail")
    probe.wait_for_log(r"history=2\b")
    probe.tap("Managed Back: off")
    probe.tap("Step Back test")
    start = len(probe.logs())
    probe.back()
    original = probe.wait_for_log(r"policy back requested id=(\d+) history=2\b", after=start)
    probe.wait_for_log(r"policy back evaluating", after=start)
    probe.tap("Recreate Activity")
    probe.wait_for_log(rf"policy back waiter canceled id={original[1]}\b", after=start)
    probe.wait_for_log(r"attached epoch=2\b", after=start)
    texts = probe.wait_for_texts("AppNav Compose probe")
    if "Managed Back: off" in texts:
        probe.tap("Managed Back: off")
    # The replacement's refresh waits behind the retained router's pending Back policy.
    assert not re.search(r"policy back (finished|Completed)", probe.logs()[start:]), probe.logs()
    probe.tap("Step Back test")
    probe.wait_for_log(r"policy back Canceled history=2\b", after=start)
    probe.wait_for_log(rf"policy back finished id={original[1]}\b", after=start)
    probe.wait_for_log(r"surface epoch=2 .*catalog \[catalog,group,item,detail\]", after=start)
    probe.wait_for_texts("Page: detail")

    retry = len(probe.logs())
    probe.back()
    replacement = probe.wait_for_log(r"policy back requested id=(\d+) history=2\b", after=retry)
    assert replacement[1] != original[1]
    probe.wait_for_log(r"policy back evaluating", after=retry)
    probe.run("shell", "input", "keyevent", "KEYCODE_BACK", "KEYCODE_BACK")
    probe.tap("Step Back test")
    probe.wait_for_log(r"policy back Completed history=3\b", after=retry)
    probe.tap("Step Back test")
    probe.wait_for_log(rf"policy back finished id={replacement[1]}\b", after=retry)
    probe.wait_for_texts("Page: item")
    trace = probe.logs()[start:]
    assert "policy back failed" not in trace, trace
    assert len(re.findall(r"policy back requested", trace)) == 2, trace
    assert len(re.findall(r"policy back Completed", trace)) == 1, trace
    assert not re.search(r"history=[4-9]\d*\b", trace), trace
    print("PASS Activity recreation quietly cancels the native waiter and keeps the replacement Back gate independent")


GAME_HISTORY = r"home\[home,home-detail,game\][^\n]*history=2\b"
DISPLAY_RESIZE = r"display size transform from=(\d+)x(\d+) to=(\d+)x(\d+)"
HOME_RETURN_HISTORY = r"home\[home,home-detail\][^\n]*history=3\b[^\n]*HostReconciliation@home\[home,home-detail\]"


def wait_for_game_preview(probe, after):
    match = probe.wait_for_log(r"game shared predictive preview revision=(\d+) progress=([\d.eE+-]+)", after=after)
    assert 0 < float(match[2]) < 1, match[0]
    return int(match[1])


def verify_game_resize(probe):
    # Compare timestamps from the same app process so ADB polling cannot affect elapsed time.
    timestamps = r"(\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d+) [^\n]*"
    match = probe.wait_for_log(
        rf"(?m)^{timestamps}{DISPLAY_RESIZE}[^\n]*\n"
        rf"(?s:.*?)^{timestamps}game origin scene disposed\n"
        rf"(?s:.*?)^{timestamps}{GAME_HISTORY}"
    )
    width, height, target_width, target_height = map(int, match.groups()[1:5])
    assert 0 < width == target_width and 0 < height < target_height, match[0]
    start, disposed, committed = (
        datetime.strptime(match[index], "%m-%d %H:%M:%S.%f") for index in (1, 6, 7)
    )
    assert (disposed - start).total_seconds() >= 1.1 and committed >= disposed, match[0]
    print("PASS Nav3 resized the scene before disposing outgoing content and committing the AppNav push")


def verify_game_predictive_back(probe):
    probe.reset()
    probe.wait_for_log(r"home\[home,home-detail\][^\n]*history=1\b")
    probe.tap("Play Game")
    probe.wait_for_log(r"game shared active operation=push revision=\d+")
    verify_game_resize(probe)
    probe.wait_for_texts("Page: game")

    before_cancel = len(probe.logs())
    probe.predictive_back(0.12, cancel=True)
    revision = wait_for_game_preview(probe, before_cancel)
    probe.wait_for_log(r"game predictive transform from=game to=home-detail key=game-cover edge=left", after=before_cancel)
    probe.wait_for_log(rf"game shared predictive settled revision={revision}\b", after=before_cancel)
    probe.wait_for_texts("Page: game")
    after_cancel = probe.logs()
    assert "HostReconciliation" not in after_cancel[before_cancel:], after_cancel
    assert not re.search(r"history=[3-9]\d*\b", after_cancel[before_cancel:]), after_cancel

    probe.predictive_back(0.70)
    wait_for_game_preview(probe, len(after_cancel))
    probe.wait_for_log(HOME_RETURN_HISTORY, after=len(after_cancel))
    probe.wait_for_texts("Page: home-detail")
    assert len(re.findall(r"HostReconciliation@", probe.logs()[len(after_cancel):])) == 1, probe.logs()
    print("PASS real predictive Back preview, cancellation without commit, and one completed Game return")


def verify_game_recreation(probe):
    probe.reset()
    probe.wait_for_log(r"home\[home,home-detail\][^\n]*history=1\b")
    probe.tap("Recreate during Game push")
    interrupted = probe.wait_for_log(
        r"(?s)game shared active operation=push revision=(\d+)\b.*?game push interrupted epoch=(\d+) history=1\b"
    )
    revision, epoch = map(int, interrupted.groups())
    probe.wait_for_log(
        rf"(?s)detached epoch={epoch}\b.*?attached epoch={epoch + 1}\b.*?"
        rf"game surface epoch={epoch + 1} rev={revision} operation=reattach sourceKey=''.*?"
        rf"surface epoch={epoch + 1} rev={revision} home \[home,home-detail,game\].*?{GAME_HISTORY}"
    )
    probe.wait_for_log(rf"game surface epoch={epoch + 1} rev={revision + 1} operation=refresh sourceKey=''")
    probe.wait_for_texts("Page: game")
    log = probe.logs()
    assert len(re.findall(GAME_HISTORY, log)) == 1, log
    assert len(re.findall(r"game shared active operation=push revision=\d+", log)) == 1, log
    probe.back()
    probe.wait_for_log(HOME_RETURN_HISTORY)
    probe.wait_for_texts("Page: home-detail")
    print("PASS Activity recreation interrupts the shared Game push and the replacement commits exactly once")


HANDLED = f"{PACKAGE}/.HandledActivity"
SPLIT_HOME = r"split\[home\]@primary history=1\b"
GAME_A = r"split\[home\]\+game-a-stack\[game-a\]@secondary history=2\b"
GAME_A_PAGE = r"split\[home\]\+game-a-stack\[game-a,game-a-page-1\]@secondary history=3\b"


def start_split(probe, folded, activity=HANDLED):
    probe.run("emu", "fold" if folded else "unfold")
    time.sleep(2)
    probe.run("shell", "input", "keyevent", "KEYCODE_WAKEUP")
    probe.run("shell", "wm", "dismiss-keyguard")
    probe.reset(activity, split=True)
    probe.wait_for_log(SPLIT_HOME)


def split_history(probe):
    return [int(count) for count in re.findall(r"split\[[^\n]*history=(\d+)\b", probe.logs())][-1]


def verify_split_unfolded(probe):
    start_split(probe, folded=False)
    probe.wait_for_texts("Page: home", "Pick a game")
    probe.tap("Game A")
    probe.wait_for_log(GAME_A)
    probe.wait_for_log(r"surface [^\n]*home-stack \[home\] modal= \[\] secondary=\[game-a\] compact=secondary")
    probe.wait_for_texts("Page: home", "Page: game-a")
    probe.missing("Up", "Pick a game")
    probe.tap("Game page")
    probe.wait_for_log(GAME_A_PAGE)
    probe.wait_for_texts("Page: home", "Page: game-a-page-1", "Up")

    before = len(probe.logs())
    probe.predictive_back(0.12, cancel=True)
    time.sleep(1.5)
    probe.wait_for_texts("Page: game-a-page-1", "Page: home")
    assert "HostReconciliation" not in probe.logs()[before:], probe.logs()[before:]
    probe.predictive_back(0.70)
    probe.wait_for_log(r"split\[home\]\+game-a-stack\[game-a\]@secondary history=4\b[^\n]*HostReconciliation", after=before)
    probe.wait_for_texts("Page: home", "Page: game-a")

    # Side by side, Back at the game's root is not intercepted: the system leaves the app as from Home.
    probe.back()
    time.sleep(2)
    assert PACKAGE not in probe.focused(), probe.focused()
    assert split_history(probe) == 4, probe.logs()
    print("PASS two panes: list beside detail, in-pane push, predictive cancel and pop, root Back leaves the app")


def verify_split_game_switch(probe):
    start_split(probe, folded=False)
    probe.tap("Game A")
    probe.wait_for_log(GAME_A)
    probe.tap("Game B")
    probe.wait_for_log(r"split\[home\]\+game-b-stack\[game-b\]@secondary history=3\b")
    probe.wait_for_texts("Page: home", "Page: game-b")
    probe.missing("Page: game-a")
    print("PASS two panes: selecting another game replaces the detail pane beside Home")


def verify_split_fold_mid_navigation(probe):
    start_split(probe, folded=False)
    probe.tap("Game A")
    probe.wait_for_log(GAME_A)
    probe.tap("Game page")
    probe.wait_for_log(GAME_A_PAGE)
    probe.tap("Local count 0")
    probe.wait_for_texts("Local count 1")
    readings = len(re.findall(r"surface epoch=", probe.logs()))

    probe.fold(True)
    probe.wait_for_texts("Page: game-a-page-1", "Local count 1", "Up")
    probe.missing("Page: home")
    probe.unfolded_readings = len(re.findall(r"surface epoch=", probe.logs()))
    assert probe.unfolded_readings == readings, "A fold sent a reading"
    before = len(probe.logs())
    probe.predictive_back(0.70)
    probe.wait_for_log(r"split\[home\]\+game-a-stack\[game-a\]@secondary history=4\b", after=before)
    probe.wait_for_texts("Page: game-a", "Up")
    probe.back()
    probe.wait_for_log(r"split\[home\]@primary history=5\b[^\n]*HostReconciliation@split\[home\]@primary", after=before)
    probe.wait_for_texts("Page: home")

    probe.fold(False)
    probe.wait_for_texts("Page: home", "Pick a game")
    assert split_history(probe) == 5, probe.logs()
    print("PASS fold mid-navigation keeps the game and its state; folded Back pops, then closes the column")


def verify_split_folded_selection(probe):
    start_split(probe, folded=True)
    probe.wait_for_texts("Page: home")
    probe.tap("Game A")
    probe.wait_for_log(GAME_A)
    probe.wait_for_texts("Page: game-a", "Up")
    probe.missing("Page: home")
    before = len(probe.logs())
    probe.predictive_back(0.12, cancel=True)
    time.sleep(1.5)
    probe.wait_for_texts("Page: game-a")
    assert "HostReconciliation" not in probe.logs()[before:], probe.logs()[before:]
    probe.predictive_back(0.70)
    probe.wait_for_log(r"split\[home\]@primary history=3\b[^\n]*HostReconciliation", after=before)
    probe.wait_for_texts("Page: home")
    probe.fold(False)
    print("PASS one pane: selection stacks the game, predictive cancel keeps it, Back at its root closes the column")


def verify_split_display_default(probe):
    start_split(probe, folded=False)
    probe.tap("More Home")
    probe.wait_for_log(r"split\[home,home-more\]@primary history=2\b")
    probe.tap("Default A")
    probe.wait_for_log(r"split\[home,home-more\]\+game-a-stack\[game-a\]@primary history=3\b")
    probe.wait_for_texts("Page: home-more", "Page: game-a")
    probe.fold(True)
    probe.wait_for_texts("Page: home-more")
    probe.missing("Page: game-a")
    probe.back()
    probe.wait_for_log(r"split\[home\]@primary history=4\b[^\n]*HostReconciliation")
    probe.wait_for_texts("Page: home")
    probe.fold(False)
    probe.wait_for_texts("Page: home", "Pick a game")
    print("PASS a default game shows beside Home but not over it folded; Back pops Home and closes the default")


def verify_split_modal(probe):
    start_split(probe, folded=False)
    probe.tap("Game A")
    probe.wait_for_log(GAME_A)
    probe.tap("Modal")
    probe.wait_for_log(r"split\[home\]\+game-a-stack\[game-a\]\+modal:editor@secondary history=3\b")
    probe.wait_for_texts("Page: edit")
    probe.back()
    probe.wait_for_log(r"split\[home\]\+game-a-stack\[game-a\]@secondary history=4\b[^\n]*HostReconciliation")
    probe.wait_for_texts("Page: home", "Page: game-a")
    print("PASS a modal covers the split and Back dismisses it, leaving both panes")


def verify_split_sideways(probe):
    start_split(probe, folded=False)
    probe.tap("Game A")
    probe.wait_for_log(GAME_A)
    probe.run("shell", "settings", "put", "system", "accelerometer_rotation", "0")
    try:
        probe.run("shell", "settings", "put", "system", "user_rotation", "1")
        time.sleep(3)
        probe.wait_for_texts("Page: home", "Page: game-a")
    finally:
        probe.run("shell", "settings", "put", "system", "user_rotation", "0")
    print("PASS unfolded sideways (883x852 dp) keeps two panes")


def verify_split_fold_recreation(probe):
    start_split(probe, folded=False, activity=ACTIVITY)
    probe.tap("Game A")
    probe.wait_for_log(GAME_A)
    attached = probe.wait_for_log(r"attached epoch=(\d+)")
    probe.fold(True)
    probe.wait_for_log(rf"attached epoch={int(attached[1]) + 1}\b")
    probe.wait_for_texts("Page: game-a", "Up")
    probe.back()
    probe.wait_for_log(r"split\[home\]@primary history=\d+\b[^\n]*HostReconciliation@split\[home\]@primary")
    probe.wait_for_texts("Page: home")
    probe.fold(False)
    print("PASS a fold that recreates the Activity reattaches the split and folded Back closes the column")


SPLIT_SCENARIOS = {
    "split-unfolded": verify_split_unfolded,
    "split-switch": verify_split_game_switch,
    "split-fold": verify_split_fold_mid_navigation,
    "split-folded": verify_split_folded_selection,
    "split-default": verify_split_display_default,
    "split-modal": verify_split_modal,
    "split-sideways": verify_split_sideways,
    "split-recreate": verify_split_fold_recreation,
}

SCENARIOS = {
    "branches": verify_branch_switch,
    "recreate-link": verify_recreation,
    "modal": verify_modal_stack,
    "policy-back": verify_managed_back,
    "policy-recovery": verify_managed_back_recovery,
    "policy-recreate": verify_managed_back_recreation,
    "game-back": verify_game_predictive_back,
    "recreate-game": verify_game_recreation,
}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--serial", default=os.environ.get("ANDROID_SERIAL"))
    parser.add_argument("--apk", type=Path, help="Install this APK before testing")
    parser.add_argument("--case", action="append", choices=[*SCENARIOS, *SPLIT_SCENARIOS],
                        help="Run only the named scenario; may be repeated")
    parser.add_argument("--split", action="store_true", help="Run the split scenarios, which need a foldable emulator")
    args = parser.parse_args()
    probe = Probe(args.serial)
    if args.apk:
        probe.run("install", "-r", str(args.apk), timeout=120)
    scenarios = {**SCENARIOS, **SPLIT_SCENARIOS}
    for name in args.case or (SPLIT_SCENARIOS if args.split else SCENARIOS):
        scenarios[name](probe)
        log = probe.logs()
        assert "FAULT" not in log, log


if __name__ == "__main__":
    main()
