"""Sends one C11 command to the running Space Engineers plugin (default) or to the Subnautica plugin (--to sn) and
prints its reply (no game window is touched).

 python -I tools/lab/se_cmd.py de.spawnplanet '{}'
 python -I tools/lab/se_cmd.py de.spawnplanet dryRun=true
 python -I tools/lab/se_cmd.py de.spawnplanet centre=-1000000,250000,1500000 size=120000 seed=4546
 python -I tools/lab/se_cmd.py de.g2e3 '{}'
 python -I tools/lab/se_cmd.py de.site rebind=true
 python -I tools/lab/se_cmd.py de.state
 python -I tools/lab/se_cmd.py --to sn de.status # Subnautica: C11c CtlToSn / C11d CtlFromSn
 python -I tools/lab/se_cmd.py --to sn de.capture name=s1manual/n9 # S1.N9 capture
 python -I tools/lab/se_cmd.py --selftest # transport check against the Python stand-ins (no game)

--to sn needs a live Subnautica on the section (snPid, heartbeat; the plugin beats every 3 s in play and every 120 s
while a save loads) and goes through the same harness bit; the first de.ping is sent to Subnautica (its C11c tail is
re-synchronised at every new link epoch, so an earlier command could be dropped). The launcher's harness and this tool
cannot run at the same time (one devControl bit 0).

Arguments: one JSON object, or key=value tokens (the value is read as JSON when it parses - true, 120000, [1,2,3] -,
as a list of numbers when it is a comma list, else as a string). Windows PowerShell 5.1 strips the double quotes
inside an argument passed to a program ('{"dryRun": true}' arrives as {dryRun: true}): there, use the key=value form
or escape them ('{\"dryRun\": true}'); '{}' needs nothing.

How it reaches SE (CONTRACT 5.1/5.4 C11; the same helpers as launcher.py and the stand-ins, tests/wire/section_py.py):
1. opens the section `Local\\DeepEngineers_v1` (open only, never creates). Someone must have created it:
 - Subnautica started in bridge mode (launcher.py), or
 - the stand-in host, holding the section without writing anything after creating it:
 python -I tests/standins/fake_sn_host.py --no-token-check --passive --hold-seconds 900
 (start it before or after SE; it can run in a second terminal or in the background).
 The SE plugin opens the section once per second (every 5 s after its first minute), validates it and attaches
 (`DE.Link UP`). Its C11 drain needs only the link state Up and devControl bit 0 (DevChannel.Tick), not a live
 peer: with the passive host SE also logs one `DE.Link STALL` after 120 s (no heartbeat), which is harmless, and the
 link stays Up even after the passive host exits (SE keeps the section and its own heartbeat).
2. waits until the SE block of C0 shows a live SE process with a fresh heartbeat;
3. takes devControl bit 0 by compare-and-swap (refused while another harness holds it, or while the SE chat relay
 holds bit 1: up to 60 s after the last `/de sn...` typed in SE's chat);
4. sends `de.ping` once per second until SE answers: SE re-syncs its C11a tail when it attaches or sees a new peer,
 so a command written before that would be dropped; only then the command itself is sent (C11a, JSON) and its reply
 read from C11b;
5. releases bit 0 and closes the section.
Each phase (SE reachable, first pong, the command's reply) has its own --timeout (default 30 s). `de.spawnplanet`
blocks SE's update thread while the planet's maps load (seconds); its reply comes after that, so its reply phase
waits at least SPAWN_REPLY_MIN_S (180 s) unless it is a dry run.

When a reply does not arrive, the C11a counters tell which case it is (read only, never written by this tool):
- SE's C11a tail passed the command: SE took it and is still running it, or ran it and the reply was lost; it will
 NOT run again (look for its DE.Lab / DE.G2E3 lines in the SE log, or run `de.state`);
- the tail did not reach it: the command is still queued in C11a and runs at the next harness attach (SE drains
 C11a only while bit 0 is set). Before attaching, this tool lists commands an earlier run left queued.

A harness killed without its `finally` (e.g. TerminateProcess) leaves devControl bit 0 set, and every later run is
refused ("another harness is attached"). CONTRACT 5.1: "clear the word by hand once the harness is gone":
`--clear-stale-harness-bit` clears bit 0 only (compare-and-swap, bit 1 untouched); use it only when no other harness
(launcher.py, another se_cmd.py) is running.

Output: the reply as text (`--json`: the raw reply JSON only). `de.g2e3` prints its rows as a table.
Exit codes: 0 reply ok (for de.g2e3 also 0 FAIL rows); 1 reply ok=false, or de.g2e3 with FAIL rows; 2 no reply
within the timeout; 3 SE not reachable (no section, invalid section, no live SE, harness refused); 4 usage.
Stdlib only.
"""
from __future__ import annotations

import argparse
import json
import os
import struct
import subprocess
import sys
import tempfile
import time

_HERE = os.path.dirname(os.path.abspath(__file__))
_ROOT = os.path.normpath(os.path.join(_HERE, "..", ".."))
_WIRE = os.path.join(_ROOT, "tests", "wire")
if _WIRE not in sys.path:
    sys.path.insert(0, _WIRE)
import layout_py as L  # noqa: E402
import section_py as S  # noqa: E402

EXIT_OK, EXIT_FAIL, EXIT_NO_REPLY, EXIT_UNREACHABLE, EXIT_USAGE = 0, 1, 2, 3, 4
SE_STALE_MS = 5000          # an SE heartbeat older than this is not a live SE (the plugin beats every tick)
SN_STALE_MS = 10000         # Subnautica beats every 3 s in play (CONTRACT: 120 s while a save loads, see SN_LOADING_STALE_MS)
SN_LOADING_STALE_MS = 130000
SN_LOADING_PHASES = ("Booting", "LoadingSave")
SPAWN_REPLY_MIN_S = 180.0   # de.spawnplanet (not a dry run): SE loads the 12 planet maps inside the command
TO_SE = "C11a"
TO_SN = "C11c"
TO_RING = {"se": TO_SE, "sn": TO_SN}
PEER_NAME = {"se": "Space Engineers", "sn": "Subnautica"}

def reply_timeout(cmd: str, args: dict, timeout: float) -> float:
    """The reply phase's wait: --timeout, but at least SPAWN_REPLY_MIN_S for a real de.spawnplanet."""
    if cmd == "de.spawnplanet" and not (isinstance(args, dict) and args.get("dryRun") is True):
        return max(timeout, SPAWN_REPLY_MIN_S)
    return timeout

def queued_commands(sec, limit: int = 16, ring: str = TO_SE):
    """Read-only look at CtlToSe (or CtlToSn) between the peer's tail and the head (no counter is written):
 ([(id, cmd)], queued bytes). These are commands an earlier harness wrote and the peer has not taken yet; it runs
 them first at the next attach."""
    g = L.REGION[ring]
    head, tail = sec.u64(L.ring_head_offset(ring)), sec.u64(L.ring_tail_offset(ring))
    used = (head - tail) & S.MASK64
    out = []
    if used == 0 or used > g.size or used & 7 or tail & 7:
        return out, used
    t = tail
    while t != head and len(out) < limit and ((t - tail) & S.MASK64) < used:
        off = t & (g.size - 1)
        mtype, n = struct.unpack("<II", sec.read(g.offset + off, 8))
        if mtype == L.PAD_TYPE:
            t = (t + g.size - off) & S.MASK64
            continue
        need = L.framed_size(n)
        if need > g.size - off or need > g.size // 2:
            break
        try:
            obj = json.loads(sec.read(g.offset + off + 8, n).decode("utf-8"))
            out.append((obj.get("id"), obj.get("cmd")))
        except (ValueError, UnicodeDecodeError, AttributeError):
            out.append((None, "?"))
        t = (t + need) & S.MASK64
    return out, used

def se_took(sec, end: int, ring: str = TO_SE) -> bool:
    """True once the peer's consumer tail of `ring` (CtlToSe, or CtlToSn with --to sn) has reached `end` (the head
 right after our message): the peer consumed the message."""
    tail = sec.u64(L.ring_tail_offset(ring))
    return ((tail - end) & S.MASK64) < (1 << 63)

def clear_stale_harness_bit(name: str) -> int:
    """CONTRACT 5.1 "clear the word by hand once the harness is gone": devControl bit 0 by compare-and-swap."""
    sec = S.Section.open(name)
    if sec is None:
        say("UNREACHABLE section %s absent" % name)
        return EXIT_UNREACHABLE
    try:
        before = sec.hdr("devControl")
        cleared = S.release_harness_bit(sec)
        say("devControl %#x -> %#x (%s)" % (before, sec.hdr("devControl"),
                                            "bit 0 cleared" if cleared else "bit 0 was not set: nothing changed"))
        return EXIT_OK
    finally:
        sec.close()

class Unreachable(Exception):
    pass

def say(msg: str, quiet: bool = False):
    if not quiet:
        sys.stderr.write(msg + "\n")
        sys.stderr.flush()

def open_section(name: str, deadline: float, quiet: bool):
    """The section, valid (CONTRACT 6 steps 1-5), or Unreachable."""
    announced = False
    while True:
        sec = S.Section.open(name)
        if sec is not None:
            break
        if time.monotonic() >= deadline:
            raise Unreachable("section %s absent: start Subnautica in bridge mode (launcher.py) or "
                              "`python -I tests/standins/fake_sn_host.py --no-token-check --passive --hold-seconds 900`"
                              % name)
        if not announced:
            say("waiting for the section %s ..." % name, quiet)
            announced = True
        time.sleep(0.5)
    if sec.mapped_size < L.SECTION_SIZE:
        sec.close()
        raise Unreachable("section %s maps %d bytes (expected %d)" % (name, sec.mapped_size, L.SECTION_SIZE))
    while True:
        r = S.validate(sec)
        if r["outcome"] == "ok":
            return sec
        if r["outcome"] == "reject" or time.monotonic() >= deadline:
            sec.close()
            raise Unreachable("section %s %s at step %s field %s: expected %s found %s"
                              % (name, r["outcome"], r["step"], r["field"], r["expected"], r["found"]))
        time.sleep(0.2)   # not_ready: the creator is initialising

def wait_for_peer(sec, side: str, deadline: float, quiet: bool) -> dict:
    """The SE (or SN) block once it shows a live process with a fresh heartbeat, or Unreachable. For SE that means the
 plugin linked; for Subnautica, that its bridge created or holds the section (it beats every 3 s in play, every
 120 s while a save loads: the stale limit follows the phase)."""
    announced = False
    phases = L.SE_PHASES if side == "se" else L.SN_PHASES
    while True:
        pid, gen = sec.identity(side)
        hb = sec.hdr(side + "HeartbeatMs")
        age = (S.tick64() - hb) if hb else None
        phase = phases.get(sec.hdr(side + "Phase"), sec.hdr(side + "Phase"))
        stale = SE_STALE_MS if side == "se" else (SN_LOADING_STALE_MS if phase in SN_LOADING_PHASES else SN_STALE_MS)
        if pid and age is not None and age <= stale and S.is_process_alive(pid):
            return {"pid": pid, "generation": gen, "heartbeatAgeMs": age, "phase": phase}
        if time.monotonic() >= deadline:
            hint = ("is SE running with the plugin, and has it linked (DE.Link UP in its log)?" if side == "se" else
                    "is Subnautica running in bridge mode (launcher.py), with its plugin past Booting?")
            raise Unreachable("no live %s on the section (%sPid %s, heartbeat age %s ms, %sPhase %s): %s"
                              % (PEER_NAME[side], side, pid or "none", age, side, phase, hint))
        if not announced:
            say("waiting for the %s plugin (%sPid %s, heartbeat age %s ms) ..." % (PEER_NAME[side], side, pid or "none", age),
                quiet)
            announced = True
        time.sleep(0.25)

def wait_for_se(sec, deadline: float, quiet: bool) -> dict:
    """Kept for callers of the S1 tooling: the SE block as {sePid, seGeneration, seHeartbeatAgeMs, sePhase}."""
    p = wait_for_peer(sec, "se", deadline, quiet)
    return {"sePid": p["pid"], "seGeneration": p["generation"], "seHeartbeatAgeMs": p["heartbeatAgeMs"], "sePhase": p["phase"]}

def run(name: str, cmd: str, args: dict, timeout: float, quiet: bool, to: str = "se"):
    """(exit code, reply or None). `to` is "se" (C11a/C11b) or "sn" (C11c/C11d)."""
    if to not in TO_RING:
        raise ValueError("to must be se or sn")
    ring, peer = TO_RING[to], PEER_NAME[to]
    ring_name = L.REGION[ring].title
    try:
        sec = open_section(name, time.monotonic() + timeout, quiet)
    except Unreachable as e:
        say("UNREACHABLE " + str(e))
        return EXIT_UNREACHABLE, None
    h = None
    try:
        try:
            p = wait_for_peer(sec, to, time.monotonic() + timeout, quiet)
        except Unreachable as e:
            say("UNREACHABLE " + str(e))
            return EXIT_UNREACHABLE, None
        say("%s on the section: %sPid %d %sGeneration %d %sPhase %s heartbeat %d ms"
            % (peer, to, p["pid"], to, p["generation"], to, p["phase"], p["heartbeatAgeMs"]), quiet)
        queued, qbytes = queued_commands(sec, ring=ring)
        if qbytes:
            say("WARNING %s holds %d bytes from an earlier harness that %s has not taken: %s; it runs them first, "
                "as soon as this run sets devControl bit 0"
                % (ring_name, qbytes, peer, ", ".join("%s id=%s" % (c, i) for i, c in queued) or "?"))
        h = S.Harness(sec)
        # ids unique per run: a reply to a command left in the ring by an earlier run carries an old id
        h.next_id = (int(time.time() * 1000) & 0x3FFFFFFF) or 1
        ok, why = h.attach()
        if not ok:
            say("UNREACHABLE harness not attached: " + why +
                ("" if "bit 1" in why else " (if that harness was killed and nothing else runs: --clear-stale-harness-bit)"))
            h = None
            return EXIT_UNREACHABLE, None
        to_ring = h.to_sn if to == "sn" else h.to_se

        # first pong: the peer drains its ring (SE: link Up and bit 0 seen; SN: bit 0 seen, tail re-synchronised)
        deadline = time.monotonic() + timeout
        pong = None
        while pong is None and time.monotonic() < deadline:
            rid, st = h.send(to, "de.ping", {})
            if st != S.RW_OK:
                say("UNREACHABLE %s write %s" % (ring_name, st))
                return EXIT_UNREACHABLE, None
            pong = h.wait_reply(rid, min(1.0, max(0.05, deadline - time.monotonic())))
        if pong is None:
            say("NO REPLY to de.ping within %.0f s: a release build, %s still loading, or %s not draining C11 "
                "(devControl %#x)" % (timeout, peer, peer, sec.hdr("devControl")))
            return EXIT_NO_REPLY, None
        say("de.ping answered (%sGeneration %s, linkEpoch %s)" % (to, pong.get(to + "Generation"), pong.get("linkEpoch")), quiet)

        t0 = time.monotonic()
        rid, st = h.send(to, cmd, args)
        if st != S.RW_OK:
            say("UNREACHABLE %s write %s" % (ring_name, st))
            return EXIT_UNREACHABLE, None
        end = to_ring.producer_head          # the peer's consumer tail reaches this once it has taken the command
        wait = reply_timeout(cmd, args, timeout)
        if wait > timeout:
            say("waiting up to %.0f s for the reply (%s blocks SE while the planet maps load)" % (wait, cmd), quiet)
        rep = h.wait_reply(rid, wait)
        if rep is None:
            if se_took(sec, end, ring):
                say("NO REPLY to %s within %.0f s, but %s took it from %s: it is still running or its reply was lost; "
                    "it will NOT run again (see its lines in the %s log, or run de.state)" % (cmd, wait, peer, ring_name, peer))
            else:
                say("NO REPLY to %s within %.0f s and %s has not taken it: it stays queued in %s and runs at the next "
                    "harness attach" % (cmd, wait, peer, ring_name))
            return EXIT_NO_REPLY, None
        rep["_ms"] = round((time.monotonic() - t0) * 1000.0, 1)
        if not rep.get("ok"):
            return EXIT_FAIL, rep
        if cmd == "de.g2e3" and rep.get("fail", 0):
            return EXIT_FAIL, rep
        return EXIT_OK, rep
    finally:
        if h is not None:
            h.detach()
        sec.close()

def show(cmd: str, rep: dict):
    """The reply as text: de.g2e3 as a table, the rest as line + fields."""
    out = sys.stdout
    rows = rep.get("rows") if cmd == "de.g2e3" else None
    if rows:
        w = [max(len(str(r.get(k) or "")) for r in rows) for k in ("check", "value", "expected")]
        for r in rows:
            note = r.get("note") or ""
            status = r.get("status") + (" (%s)" % r["manual"] if r.get("manual") else "")
            out.write("%-*s  %-*s  %-*s  %-12s %s\n" % (w[0], r.get("check"), w[1], r.get("value"), w[2], r.get("expected"),
                                                         status, note))
    line = rep.get("line")
    if line:
        out.write(line + "\n")
    rest = {k: v for k, v in rep.items() if k not in ("line", "rows")}
    out.write(json.dumps(rest, sort_keys=True) + "\n")

# --------------------------------------------------------------------------------------------------------- selftest
def selftest() -> int:
    """Transport only, against the Python stand-ins on a private section (no game, no CONTRACT names):
 fake_sn_host.py --passive creates it, fake_se_producer.py plays SE (it answers de.ping and de.state and refuses
 unknown commands, so de.g2e3 must come back ok=false). Also: absent section -> exit 3."""
    py = sys.executable
    name = "Local\\DeepEngineers_secmd_%d" % os.getpid()
    host = [py, "-I", os.path.join(_ROOT, "tests", "standins", "fake_sn_host.py"), "--name", name, "--no-token-check",
            "--passive", "--hold-seconds", "60", "--quiet"]
    se = [py, "-I", os.path.join(_ROOT, "tests", "standins", "fake_se_producer.py"), "--name", name, "--run-seconds", "60",
          "--quiet", "--churn-ms", "0", "--model-stream-ms", "0", "--recolor-hz", "0", "--grids", "0"]
    results = []

    def check(label, cond, detail=""):
        results.append(cond)
        print("SELFTEST %-44s %s %s" % (label, "PASS" if cond else "FAIL", detail))

    rc, _ = run(name, "de.ping", {}, 2.0, True)
    check("absent section -> exit 3", rc == EXIT_UNREACHABLE, "rc=%d" % rc)
    check("reply wait: spawn >= %.0f s, dry run and others --timeout" % SPAWN_REPLY_MIN_S,
          reply_timeout("de.spawnplanet", {}, 30.0) == SPAWN_REPLY_MIN_S
          and reply_timeout("de.spawnplanet", {"dryRun": True}, 30.0) == 30.0
          and reply_timeout("de.g2e3", {}, 30.0) == 30.0 and reply_timeout("de.spawnplanet", {}, 600.0) == 600.0)
    logs = tempfile.mkdtemp(prefix="se_cmd_selftest_")
    procs = []
    try:
        with open(os.path.join(logs, "host.jsonl"), "w") as hout, open(os.path.join(logs, "se.jsonl"), "w") as sout:
            procs.append(subprocess.Popen(host, stdout=hout, stderr=subprocess.STDOUT, cwd=_ROOT))
            sec = None
            for _ in range(100):
                sec = S.Section.open(name)
                if sec is not None and S.validate(sec)["outcome"] == "ok":
                    break
                if sec is not None:
                    sec.close()
                    sec = None
                time.sleep(0.05)
            if sec is None:
                check("stand-in host created the section", False)
                procs.append(subprocess.Popen(se, stdout=sout, stderr=subprocess.STDOUT, cwd=_ROOT))
            else:
                try:
                    # a harness killed after writing a command (no finally ran): the command stays queued (no SE drains
 # C11a yet) and devControl bit 0 stays set
                    h = S.Harness(sec)
                    h.next_id = 4242
                    ok1, _ = h.attach()
                    _, st = h.send("se", "de.ping", {})
                    end = h.to_se.producer_head
                    q, qb = queued_commands(sec)
                    check("queued command listed read-only", ok1 and st == S.RW_OK and q == [(4242, "de.ping")] and qb > 0
                          and not se_took(sec, end), "%s %d bytes" % (q, qb))
                    rc = clear_stale_harness_bit(name)
                    check("--clear-stale-harness-bit clears bit 0 only", rc == EXIT_OK
                          and not sec.hdr("devControl") & (S.HARNESS_BIT | S.RELAY_BIT), "devControl=%#x" % sec.hdr("devControl"))
                    # another killed harness, now with a live SE: a normal run is refused at attach until the bit is cleared
                    ok2, _ = S.Harness(sec).attach()
                    procs.append(subprocess.Popen(se, stdout=sout, stderr=subprocess.STDOUT, cwd=_ROOT))
                    rc, _ = run(name, "de.state", {}, 20.0, True)
                    check("stale bit 0 with a live SE -> refused (exit 3)", ok2 and rc == EXIT_UNREACHABLE, "rc=%d" % rc)
                    clear_stale_harness_bit(name)
                finally:
                    sec.close()
            rc, rep = run(name, "de.state", {}, 20.0, True)
            check("stand-in SE answers de.state -> exit 0", rc == EXIT_OK and rep and rep.get("ok") is True, "rc=%d %s" % (rc, rep))
            rc, rep = run(name, "de.g2e3", {}, 10.0, True)
            check("stand-in refuses de.g2e3 -> exit 1", rc == EXIT_FAIL and rep and rep.get("ok") is False,
                  "rc=%d %s" % (rc, rep))
            dc = q = None
            took = False
            sec = S.Section.open(name)
            if sec is not None:
                dc = sec.hdr("devControl")
                q, _ = queued_commands(sec)
                took = se_took(sec, sec.u64(L.ring_head_offset(TO_SE)))
                sec.close()
            check("devControl bit 0 released after each run", dc is not None and not dc & S.HARNESS_BIT, "devControl=%s" % dc)
            check("C11a drained: nothing queued, tail at head", q == [] and took, "queued=%s" % q)
            # --to sn: an active stand-in host on its own section (it serves C11c/C11d while devControl bit 0 is set;
 # no SE needed). The passive host above never answers, which is what the SE path relies on.
            name_sn = name + "b"      # not "<name>_sn": that is the first host's mutex name (L.mutex_names), another object type
            host_sn = [py, "-I", os.path.join(_ROOT, "tests", "standins", "fake_sn_host.py"), "--name", name_sn,
                       "--no-token-check", "--hold-seconds", "60", "--quiet"]
            procs.append(subprocess.Popen(host_sn, stdout=hout, stderr=subprocess.STDOUT, cwd=_ROOT))
            rc, rep = run(name_sn, "de.ping", {}, 20.0, True, "sn")
            check("--to sn: stand-in host answers de.ping -> exit 0", rc == EXIT_OK and bool(rep) and rep.get("pong") is True,
                  "rc=%d %s" % (rc, rep))
            rc, rep = run(name_sn, "de.state", {}, 10.0, True, "sn")
            check("--to sn: de.state reply carries snPhase", rc == EXIT_OK and bool(rep) and "snPhase" in rep,
                  "rc=%d %s" % (rc, rep))
            rc, _ = run(name_sn, "de.state", {}, 3.0, True, "se")
            check("--to se on a host-only section -> exit 3 (no live SE)", rc == EXIT_UNREACHABLE, "rc=%d" % rc)
            sec = S.Section.open(name_sn)
            dc = sec.hdr("devControl") if sec is not None else None
            if sec is not None:
                sec.close()
            check("--to sn: bit 0 released", dc is not None and not dc & S.HARNESS_BIT, "devControl=%s" % dc)
    finally:
        for p in reversed(procs):
            if p.poll() is None:
                p.terminate()
            try:
                p.wait(10)
            except subprocess.TimeoutExpired:
                p.kill()
    ok = all(results)
    print("SE_CMD SELFTEST %s (%d/%d; stand-in logs %s)" % ("PASS" if ok else "FAIL", sum(results), len(results), logs))
    return 0 if ok else 1

def parse_args_tokens(tokens):
    """[] -> {}; ['{...}'] -> the JSON object; ['k=v',...] -> {k: v}. ValueError with the reason otherwise."""
    if not tokens:
        return {}
    if len(tokens) == 1 and tokens[0].lstrip().startswith("{"):
        try:
            obj = json.loads(tokens[0])
        except ValueError as e:
            raise ValueError("not JSON (%s): %r; under Windows PowerShell 5.1 use key=value tokens or escape the "
                             "inner quotes as \\\"" % (e, tokens[0]))
        if not isinstance(obj, dict):
            raise ValueError("the JSON argument must be an object")
        return obj
    out = {}
    for t in tokens:
        k, eq, v = t.partition("=")
        if not eq or not k:
            raise ValueError("expected key=value, got %r" % t)
        try:
            out[k] = json.loads(v)
        except ValueError:
            parts = v.split(",")
            try:
                out[k] = [float(x) for x in parts] if len(parts) > 1 else v
            except ValueError:
                out[k] = v
    return out

def main(argv=None) -> int:
    ap = argparse.ArgumentParser(description="Send one C11 command to the SE plugin (or --to sn: the Subnautica plugin) "
                                             "and print the reply.")
    ap.add_argument("cmd", nargs="?", help="command name, e.g. de.spawnplanet, de.g2e3, de.site, de.state")
    ap.add_argument("args", nargs="*", help="one JSON object, or key=value tokens (default: no arguments)")
    ap.add_argument("--to", choices=("se", "sn"), default="se",
                    help="the peer: se (C11a CtlToSe, default) or sn (C11c CtlToSn: de.status, de.capture, de.pixel, ...)")
    ap.add_argument("--timeout", type=float, default=30.0, help="seconds per phase: peer reachable, first pong, reply")
    ap.add_argument("--name", default=L.SECTION_NAME, help="section name (default %(default)s)")
    ap.add_argument("--json", action="store_true", help="print only the raw reply JSON")
    ap.add_argument("--quiet", action="store_true", help="no progress lines on stderr")
    ap.add_argument("--selftest", action="store_true", help="transport check against the Python stand-ins (no game)")
    ap.add_argument("--clear-stale-harness-bit", action="store_true",
                    help="clear devControl bit 0 left by a killed harness (only when no other harness runs), then exit")
    a = ap.parse_args(argv)
    if a.selftest:
        return selftest()
    if a.clear_stale_harness_bit:
        return clear_stale_harness_bit(a.name)
    if not a.cmd:
        ap.print_usage(sys.stderr)
        return EXIT_USAGE
    try:
        args = parse_args_tokens(a.args)
    except ValueError as e:
        say("bad arguments: %s" % e)
        return EXIT_USAGE
    if not (a.timeout > 0):
        say("--timeout must be positive")
        return EXIT_USAGE
    rc, rep = run(a.name, a.cmd, args, a.timeout, a.quiet, a.to)
    if rep is not None:
        if a.json:
            print(json.dumps(rep))
        else:
            show(a.cmd, rep)
    return rc

if __name__ == "__main__":
    sys.exit(main())
