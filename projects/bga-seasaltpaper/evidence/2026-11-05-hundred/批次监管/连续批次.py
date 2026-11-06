"""Observe an independently launched BGA batch; continue only a fully audited normal chain."""
import argparse
from contextlib import contextmanager, ExitStack
import ctypes
import ctypes.wintypes as win
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import time

import 监管 as watch


def demand(value, reason):
    if not value:
        raise ValueError(reason)


@contextmanager
def process_handle(pid, exe, earliest, recorded):
    # Pin the native process object for the observation lifetime. Reject recycled PIDs.
    handle=watch.K.OpenProcess(0x101000,False,pid)
    try:
        if handle:
            watch.K.GetProcessTimes.argtypes=[win.HANDLE]+[ctypes.POINTER(win.FILETIME)]*4
            stamps=[win.FILETIME() for _ in range(4)]
            demand(watch.K.GetProcessTimes(handle,*(ctypes.byref(x) for x in stamps)),"无法核验进程创建时刻")
            ft=stamps[0];created=((ft.dwHighDateTime<<32)+ft.dwLowDateTime)/10_000_000-11644473600
            demand(watch.timestamp(earliest)-1<=created<=watch.timestamp(recorded)+1,"PID已被复用或与原启动记录不符")
            watch.K.QueryFullProcessImageNameW.argtypes=[win.HANDLE,win.DWORD,win.LPWSTR,ctypes.POINTER(win.DWORD)]
            name=ctypes.create_unicode_buffer(32768);size=win.DWORD(len(name))
            demand(watch.K.QueryFullProcessImageNameW(handle,0,name,ctypes.byref(size)) and
                   Path(name.value).resolve()==exe.resolve(),"进程入口与原监管不符")
        yield lambda:bool(handle) and watch.K.WaitForSingleObject(handle,0)==258
    finally:
        if handle:watch.K.CloseHandle(handle)


def run_step(d, command, label, allowed=(0,)):
    out=d/(label+"-"+str(time.time_ns())+".log")
    with out.open("xb") as stream:
        code=subprocess.run(command,stdout=stream,stderr=subprocess.STDOUT,stdin=subprocess.DEVNULL,
                            creationflags=subprocess.CREATE_NO_WINDOW).returncode
    demand(code in allowed,f"{label}退出码{code}，原件 {out}")
    return code


def preflight(d):
    # The existing entry handles ordinary notices, known dialogs and verified cooldowns.
    # Exit 9 after the entry's own wait means the same account has a newly published official deadline.
    # Only re-inspect this ticket after that time. Unverified/personal cases are returned to E.
    previous=0
    while True:
        start=time.time()
        code=run_step(d,[shutil.which("pwsh.exe") or "powershell.exe","-NoProfile","-NonInteractive","-File",str(watch.CAMPAIGN/"大厅预检.ps1"),
                         "-Batch",d.name],"连续预检",allowed=(0,9))
        if code==0:break
        deadline=watch.get(watch.STORE.parent/"账号提醒/处置记录/当前冷却.json")
        due=watch.timestamp(deadline["localDue"])
        demand(deadline["source"]=="official-page" and deadline["released"] is False and
               watch.timestamp(deadline["at"])>=start-1 and due>max(previous,time.time()),"冷却新期限未经本次官方检查证实")
        previous=due
        time.sleep(max(0,due-time.time()))
    watch.preflight(d)


def audit(d):
    run_step(d,[sys.executable,"-B","-X","utf8",str(watch.CAMPAIGN/"核对台账.py"),d.name],"连续核账")


class Chain:
    def __init__(self, anchor, pin):
        ticket=watch.get(watch.batch_dir(anchor)/"票据.json")
        demand(ticket["batch"]==anchor and ticket["pin"]==pin,"起始批次与指定产品指纹不符")
        if ticket.get("upgradeBatch"):
            watch.select_product(ticket["upgradeBatch"],pin,ticket["upgradeReviewSha256"])
        demand(pin==watch.PIN,"指定指纹与当前固定包不符")
        self.anchor,self.pin=watch.batch_dir(anchor).name,pin
        self.folder=watch.STORE.parent/"连续监管"
        self.folder.mkdir(exist_ok=True)
        self.path=self.folder/(anchor+".json")
        self.events=self.folder/(anchor+".jsonl")
        self.current=anchor

    def emit(self,kind,**fields):
        if kind in ("release","finish") and self.events.exists():
            stamped=[json.loads(line) for line in self.events.read_text(encoding="utf8").splitlines() if line]
            if any(x["kind"]==kind and x["batch"]==self.current for x in stamped):
                return
        row=dict(at=watch.now(),kind=kind,anchor=self.anchor,batch=self.current,pin=self.pin,**fields)
        line=json.dumps(row,ensure_ascii=False)
        with self.events.open("a",encoding="utf8") as f:
            f.write(line+"\n");f.flush();os.fsync(f.fileno())
        print(line,flush=True)

    def save(self,phase,batch):
        self.current=batch
        watch.publish(self.path,dict(anchor=self.anchor,pin=self.pin,current=batch,phase=phase),replace=True)

    def verify_start(self,d,t,require_worker=True):
        demand(t["batch"]==d.name and t["pin"]==self.pin and t["count"]==watch.quota(t["baseline"]),"批号、pin或额度错误")
        watch.authorization(d,t)
        release=watch.get(d/"E放行.json")
        demand(release["preflightSha256"]==watch.sha(d/"大厅预检.json"),"放行的预检指纹不符")
        consumption=watch.get(d/"消费.json")
        demand((consumption["batch"],consumption["pin"],consumption["count"])==(d.name,self.pin,t["count"]),"原批缺少有效消费")
        if require_worker:
            worker=watch.get(d/"worker.json")
            demand(worker["batch"]==d.name and worker["pin"]==self.pin and worker["pid"]>0 and
                   Path(worker["exe"]).resolve()==watch.EXE.resolve(),"原worker身份不符")
            guard=watch.get(d/"守护就绪.json")
            supervisor=watch.get(d/"发车意图.json")["supervisor"]
            demand(guard["parent"]==supervisor and guard["pid"]>0,"守护与监管原件不符")
            return worker["pid"],supervisor,guard["pid"]

    def trouble(self,d):
        for f in ("需接管.json","需核实原桌.json","熔断.json","首次警报.json","监管异常.json"):
            if (d/f).exists():
                raise ValueError(f+" "+str(watch.get(d/f).get("reason","需 E 复核")))

    def observe(self,d,t):
        self.verify_start(d,t,False)
        launch_limit=time.monotonic()+15
        while not (d/"worker.json").exists():
            self.trouble(d)
            demand(not (d/"退出.json").exists() and time.monotonic()<launch_limit,
                   "已经消费但未取得原 worker，禁止再次发车")
            time.sleep(.25)
        worker, supervisor, sentinel=self.verify_start(d,t)
        if (d/"退出.json").exists():
            self.closed_worker(d)
            return
        consume=watch.get(d/"消费.json")["at"]
        intent=watch.get(d/"发车意图.json")["at"]
        with ExitStack() as stack:
            alive_worker=stack.enter_context(process_handle(worker,watch.EXE,intent,watch.get(d/"worker.json")["at"]))
            alive_super=stack.enter_context(process_handle(supervisor,Path(sys.executable),consume,intent))
            alive_guard=stack.enter_context(process_handle(sentinel,Path(sys.executable),intent,watch.get(d/"守护就绪.json")["at"]))
            self.wait_handles(d,alive_worker,alive_super,alive_guard)

    def closed_worker(self,d):
        exit_record=watch.get(d/"退出.json")
        demand(exit_record.get("exitCode")==0 and not exit_record.get("recovered"),"原worker异常退出")

    def wait_handles(self,d,alive_worker,alive_super,alive_guard):
        lost_worker=None
        while True:
            self.trouble(d)
            if (d/"退出.json").exists():
                self.closed_worker(d)
                return
            if not alive_super() or not alive_guard():
                if (d/"退出.json").exists():continue
                watch.fuse(d,"continuous-supervisor-or-sentinel-lost")
                raise ValueError("原监管或守护退出；停止续排并保原桌")
            if not alive_worker():
                if lost_worker is None:lost_worker=time.monotonic()
                demand(time.monotonic()-lost_worker<15,"原worker退出后未取得完整归档")
            time.sleep(.5)

    def finish(self,d,t):
        # The audit process verifies official tableinfos, scores, both human seats, decision
        # traces and progress, then atomically publishes the ledger. Re-entry reuses that record.
        self.trouble(d)
        state=watch.get(d/(d.name+".json"))
        ends=[json.loads(x) for x in (d/(d.name+".ends.jsonl")).read_text(encoding="utf-8-sig").splitlines() if x]
        demand(state.get("finished") is True and state.get("completed")==t["count"] and
               not any(state.get(k) for k in ("queuePending","offerTable","currentTable")) and
               len(ends)==len({e["table"] for e in ends})==t["count"] and
               all(e["site"]=="BGA" and e["batch"]==d.name and e["status"] in ("finished","archive") and
                   str(e["cancelled"])=="0" and e["endgame_reason"]=="normal_end" for e in ends),
               "原批次结束原因、桌数或在途状态需要人工核实")
        with watch.lock(watch.STORE/"监管.lock"):
            book=watch.ledger()
            if book["assessedBatch"]!=d.name:
                demand(book["assessedBatch"]==t["previous"] and book["total"]==t["baseline"],"独立台账不是本批前序")
                audit(d)
            book=watch.ledger()
        rows=[r for r in book["batches"] if r["batch"]==d.name]
        demand(book["assessedBatch"]==d.name and len(rows)==1 and rows[0]["clean"] is True and rows[0]["pin"]==self.pin
               and rows[0]["planned"]==t["count"] and not rows[0]["excluded"] and len(rows[0]["normal"])==t["count"]
               and book["total"]==t["baseline"]+t["count"]<=100,"独立核账未达到本批正常额度")
        if not (d/"结批.json").exists():watch.close(d,book["total"])
        closed=watch.get(d/"结批.json")
        demand(closed["fused"] is False and closed["accepted"]==t["count"] and closed["auditedTotal"]==book["total"]
               and closed["ledgerSha256"]==watch.sha(watch.CAMPAIGN/"百局台账.json"),"结批与独立账本不一致")
        self.emit("finish",normal=rows[0]["normal"],total=book["total"],remaining=100-book["total"])
        return book["total"]

    def open_successor(self,d):
        chain,_=watch.tickets()
        if chain[-1]["batch"]==d.name:
            t=watch.prepare()
        else:
            # Crash between creating a normal ticket and recording its ID. Only an unconsumed,
            # unapproved single direct successor can be adopted from the checkpointed prepare step.
            t=chain[-1]
            child=watch.batch_dir(t["batch"])
            demand(not (child/"消费.json").exists() and not (child/"E放行.json").exists(),"未登记的后继已有放行或消费，须人工核实")
        demand(t["previous"]==d.name and t["pin"]==self.pin and t["count"]==watch.quota(t["baseline"])
               and "cancelReviewSha256" not in t,"后继票据、额度或类型不符")
        self.save("open",t["batch"])

    def launch(self,d,t):
        if not (d/"消费.json").exists():
            if not (d/"大厅预检.json").exists():preflight(d)
            watch.preflight(d)
            if not (d/"E放行.json").exists():watch.release(d,self.pin)
            watch.authorization(d,t)
            watch.start(d)  # exclusive 消费.json; the underlying runner never starts twice
        self.verify_start(d,t,False)
        self.emit("release",count=t["count"],baseline=t["baseline"])
        self.save("watch",d.name)

    def run(self):
        # This lock is distinct from the watcher's per-batch monitor and the campaign ticket lock.
        with watch.lock(self.folder/"连续监管.lock"):
            if not self.path.exists():
                d=watch.batch_dir(self.anchor)
                self.verify_start(d,watch.get(d/"票据.json"))
                chain,_=watch.tickets()
                demand(chain[-1]["batch"]==self.anchor,"首次接手须为最新已启动的 E 放行批次")
                self.save("watch",self.anchor)
            while True:
                s=watch.get(self.path)
                demand(s["anchor"]==self.anchor and s["pin"]==self.pin,"连续监管入口身份不符")
                self.current=s["current"]
                d=watch.batch_dir(self.current);t=watch.get(d/"票据.json")
                self.trouble(d)
                if s["phase"]=="done":
                    demand(watch.ledger()["total"]==100,"完成标记与百局账本冲突")
                    return
                if s["phase"]=="prepare":
                    self.open_successor(d)
                    continue
                chain,_=watch.tickets()
                demand(chain[-1]["batch"]==d.name,"另有更新票据，禁止托管旧分支")
                if s["phase"]=="open":
                    self.launch(d,t)
                    continue
                demand(s["phase"]=="watch","未知监管阶段")
                self.observe(d,t)
                total=self.finish(d,t)
                self.save("done" if total==100 else "prepare",d.name)

    def block(self,exc):
        d=watch.batch_dir(self.current)
        try:
            view=watch.status(d);clock=view.get("ownClock") or {}
            alert=view.get("handoff") or view.get("verifyTable") or view.get("alert") or {}
            # 熔断先于接管文件落盘时，也能从同一份已核验的官方证据立即返回桌号。
            if not alert.get("officialEvidence"):
                first=watch.read_if(d/"熔断.json",{})
                if first.get("officialEvidence"):
                    alert=first
            if alert.get("reason")==watch.allocated_seat.CODE and alert.get("officialEvidence"):
                table=alert["table"]
                current=watch.snapshot(d,table)
                clock=current.get("ownClock")
                self.emit("block",reason=str(exc),table=table,remainingSeconds=(clock or {}).get("seconds"),
                          ownClock=clock,officialEvidence=alert["officialEvidence"],officialEnded=current["officialEnded"],
                          takeover=not current["officialEnded"],worker=view.get("worker"),siteEvidence=str(d))
                return
            self.emit("block",reason=str(exc),table=view.get("table") or alert.get("table"),
                      remainingSeconds=clock.get("seconds"),ownClock=clock,clockEvidence=alert.get("clockEvidence"),
                      officialEnded=view.get("officialEnded"),
                      worker=view.get("worker"),siteEvidence=str(d))
        except (OSError,ValueError,KeyError) as error:
            self.emit("block",reason=str(exc),diagnostic=str(error),siteEvidence=str(d))


def main():
    p=argparse.ArgumentParser(description=__doc__)
    p.add_argument("batch");p.add_argument("--pin",required=True)
    a=p.parse_args()
    runner=Chain(a.batch,a.pin.upper())
    try:
        runner.run()
    except (OSError,KeyError,ValueError,RuntimeError) as exc:
        runner.block(exc)
        return 1
    return 0


if __name__=="__main__":
    sys.exit(main())
