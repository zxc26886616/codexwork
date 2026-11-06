import argparse
from concurrent.futures import ThreadPoolExecutor, as_completed
import json
import os
from pathlib import Path
import shutil
import subprocess
import tempfile
import time
import sys
from build_fixture import HERE, PKG, PIN, BIN, save, sealed, inventory, sha

BLOCKED = ('--proxy-server=http://127.0.0.1:9 --proxy-bypass-list=<-loopback> --disable-quic '
           '--host-resolver-rules="MAP * ~NOTFOUND" --force-webrtc-ip-handling-policy=disable_non_proxied_udp '
           '--disable-background-networking --disable-component-update')
CASES = ("realtime-three","realtime-void","stop-queue-check","stop-inflight-check","unknown-end-check","stop-unresolved-check",
         "wrong-ranking","missing-map","wrong-map","wrong-table")
BOUND=CASES[6:]


def execute(run, case):
    cwd=run/case
    cwd.mkdir()
    scratch=cwd/"scratch"
    browser_temp=Path(tempfile.mkdtemp(prefix="SeaSalt-offline-fixture-"))
    env={k:v for k,v in os.environ.items() if not k.startswith("WEBVIEW2_")}
    env.update(TEMP=str(browser_temp),TMP=str(browser_temp),WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS=BLOCKED)
    args=["--bound-end",case] if case in BOUND else ["--"+case]
    start=time.monotonic()
    try:
        with (cwd/"out.raw").open("wb") as out,(cwd/"err.raw").open("wb") as err:
            proc=subprocess.Popen([BIN/"Checks.exe",*args],stdout=out,stderr=err,env=env,creationflags=subprocess.CREATE_NO_WINDOW)
            save(cwd/"process.json",dict(pid=proc.pid,command=[str(BIN/"Checks.exe"),*args],webviewArgs=BLOCKED,background=True))
            try: code=proc.wait(timeout=370)
            except subprocess.TimeoutExpired:
                proc.kill();proc.wait();code=None
    finally:
        # 逐桌证据归档，浏览器缓存仅在本次创建的临时目录内。WebView 子进程可能稍晚才释放文件。
        shutil.copytree(browser_temp,scratch,ignore=shutil.ignore_patterns("profile","*.tmp"))
        for attempt in range(20):
            try:
                shutil.rmtree(browser_temp)
                break
            except OSError as exc:
                if not browser_temp.exists():
                    break
                if attempt==19:
                    print(f"待浏览器释放临时目录 {browser_temp}：{exc}",file=sys.stderr)
                else:
                    time.sleep(0.5)
    logs={}
    for stream in ("out","err"):
        data=(cwd/(stream+".raw")).read_bytes()
        try: txt=data.decode("utf8")
        except UnicodeDecodeError: txt=data.decode("gb18030",errors="replace")
        (cwd/(stream+".txt")).write_text(txt,encoding="utf8")
        logs[stream]=txt
    identities=[json.loads(s) for s in logs["out"].splitlines() if s.startswith('{"process"')]
    identity={i["name"]:i for i in identities[0]["loaded"]} if len(identities)==1 else {}
    manifest=json.loads((PKG/"candidate.json").read_text("utf8"))
    actualProduct=all(identity.get(n,{}).get("sha256")==manifest["bin"][n+".dll"] and
                      Path(identity[n]["path"]).resolve()==BIN/(n+".dll") for n in ("SeaSaltDesk","SeatControl"))
    ledger=list(scratch.glob("SeaSalt-*/fixture-server.jsonl"))
    events=[json.loads(s) for s in ledger[0].read_text("utf-8-sig").splitlines()] if len(ledger)==1 else []
    def rows(k,v):
        return [e for e in events if e.get(k)==v]
    clocks=rows("signal","score-server")
    tables=["701","702","703"] if case=="realtime-three" else ["701"]
    checks=dict(originalChecks=code==0,loadedFixedProduct=actualProduct,
                originalApphost=len(identities)==1 and identities[0].get("processSha256")==manifest["bin"]["Checks.exe"],
                shadowChecks=identity.get("Checks",{}).get("sha256")==sha(BIN/"Checks.dll"),
                eachSeenAcceptedOnTime=[(r["table"],r["op"]) for r in clocks]==[(t,o) for t in tables for o in ("open","seen")] and
                    all(r["accepted"] and r["remainingMs"]>0 and r["seat"]=="10" for r in clocks),
                wireActions=[(r["table"],r["action"]) for r in events if "action" in r and r["accepted"]] ==
                    [(t,op) for t in tables for op in ("draw","keep","finish")] and not rows("accepted",False))
    last=events[-1] if events else {}
    if case in BOUND:
        checks.update(officialFinishedRead=last.get("terminalReads")==1 and len(rows("signal","terminal-authority"))==1,
                      noReentry=last.get("staleAccepts")==last.get("endedGameVisits")==0 and last.get("joins")==1,
                      negativePreservesTable=last.get("exit")==1 and last.get("stopped") is True and
                      last.get("Completed")==last.get("matches")==0 and last.get("CurrentTable")=="701")
        if case=="wrong-ranking": checks["revokeBeforeRelease"]=all(last.get(k) for k in ("observedPendingRead","heldUntilNavigation","releasedAfterRevocation"))
        rank=next(iter(rows("signal","served-ranking")), {})
        official=next(iter(rows("signal","terminal-authority")),{}).get("fieldsForEnd",{})
        official_ids=None if case=="missing-map" else ["10","99" if case=="wrong-map" else "20"]
        checks["participantEvidence"]=(rank.get("gameSeats")==["10","20"] and rank.get("login")=="10" and
            rank.get("rankingSeats")==["99" if case=="wrong-ranking" else "20","10"] and official.get("id")==("999" if case=="wrong-table" else "701") and
            (list(official["players"]) if "players" in official else None)==official_ids)
    result=dict(case=case,passed=all(checks.values()),exit=code,seconds=round(time.monotonic()-start,2),
        assertions=checks,scoreReceipts=clocks,identity=identities,ledger=str(ledger[0].relative_to(cwd)) if ledger else None)
    save(cwd/"result.json",result)
    return result


if __name__=="__main__":
    parser=argparse.ArgumentParser()
    parser.add_argument("run")
    parser.add_argument("cases",nargs="*")
    parser.add_argument("--workers",type=int,default=3)
    args=parser.parse_args()
    run=HERE/args.run
    if run.parent.resolve()!=HERE or run.exists(): raise SystemExit("use a new direct subdirectory")
    chosen=args.cases or list(CASES)
    assert len(set(chosen))==len(chosen) and set(chosen)<=set(CASES)
    m=sealed(); before=inventory(PKG); shadow=inventory(BIN)
    assert shadow.keys()==m["bin"].keys() and [p for p in shadow if shadow[p]!=m["bin"][p]]==["Checks.dll"]
    run.mkdir()
    save(run/"固定包运行前.json",before)
    results=[]
    with ThreadPoolExecutor(max_workers=args.workers) as pool:
        for future in as_completed([pool.submit(execute,run,c) for c in chosen]):
            r=future.result();results.append(r);print(r["case"],r["passed"],flush=True)
            save(run/"进度.json",results)
    after=inventory(PKG);save(run/"固定包运行后.json",after)
    success=before==after and inventory(BIN)==shadow and all(r["passed"] for r in results)
    save(run/"结果.json",dict(passed=success,pin=PIN,fixedPackageUnchanged=before==after,shadowUnchanged=inventory(BIN)==shadow,
                           count=len(results),realGames=0,cases=[dict(case=r["case"],passed=r["passed"],exit=r["exit"]) for r in results]))
    sys.exit(0 if success else 1)
