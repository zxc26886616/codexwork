"""已消费批次的原桌一次救场。默认只读，--start 才可尝试现有原包 --resume。"""
import argparse
import ctypes
import json
import os
from pathlib import Path
import re
import shutil
import subprocess
import sys
from datetime import datetime,timezone
from ctypes import wintypes as w
import 监管 as watch
sys.path.insert(0,str(Path(__file__).resolve().parents[1]))
import official_results

def need(ok,msg):
    if not ok: raise ValueError(msg)

def raw_file(path):
    try:return watch.shared_read(path)
    except OSError as error:raise ValueError(f"缺少或无法读取原件 {path.name}") from error

def formal_root():
    path=ctypes.create_unicode_buffer(32768)
    need(ctypes.windll.shell32.SHGetFolderPathW(None,0x1c,None,0,path)==0,"无法确认 Windows 正式资料根")
    return (Path(path.value)/"SeaSaltDesk").resolve()

def exited(pid):
    need(type(pid) is int and pid>0,"原进程 ID 不明")
    h=watch.K.OpenProcess(0x100000,False,pid)
    if not h:
        error=ctypes.get_last_error()
        need(error==87,f"无法确认 PID {pid} 已退出，Windows {error}")
        return
    try:
        need(watch.K.WaitForSingleObject(h,0)==0,f"PID {pid} 仍存活或已被复用，禁止接席")
    finally:
        watch.K.CloseHandle(h)

def inspect_occupants(root):
    # 相同正式根的游戏、后台读窗及 WebView 均应已退出。只返回占用者名称/PID。
    ps="""
    $ErrorActionPreference='Stop'
    $r=$env:RESCUE_PROFILE_CHECK
    $me=Get-Process -Id $PID
    $proc=Get-CimInstance Win32_Process -Filter "Name='SeatControl.exe' OR Name='SeaSaltDesk.exe' OR Name='ReadOnly.exe' OR Name='msedgewebview2.exe'"
    $busy=@($proc | Where-Object { $_.Name -ne 'msedgewebview2.exe' -or
        ($_.CommandLine -and $_.CommandLine.IndexOf($r,[StringComparison]::OrdinalIgnoreCase) -ge 0) -or
        ($_.SessionId -eq $me.SessionId -and !$_.CommandLine) } | Select-Object Name,ProcessId)
    $os=Get-CimInstance Win32_OperatingSystem
    [pscustomobject]@{boot=$os.LastBootUpTime.ToUniversalTime().ToString('o');busy=$busy} | ConvertTo-Json -Depth 3 -Compress
    """
    exe=shutil.which("pwsh.exe") or shutil.which("powershell.exe")
    need(exe,"缺少 Windows 进程核验入口")
    result=subprocess.run([exe,"-NoProfile","-NonInteractive","-Command",ps],
        env={**os.environ,"RESCUE_PROFILE_CHECK":str(root)},capture_output=True,timeout=12,
        creationflags=subprocess.CREATE_NO_WINDOW)
    need(result.returncode==0,"进程清单或系统启动时刻不可读")
    view=json.loads(result.stdout.decode("utf-8-sig"))
    need(isinstance(view["busy"],list) and not view["busy"],"资料根被其他程序占用："+str(view["busy"]))
    return view["boot"]

def identity(d):
    get=lambda n:json.loads(raw_file(d/n))
    ticket=get("票据.json")
    watch.select_product(ticket["upgradeBatch"],ticket["pin"],ticket["upgradeReviewSha256"])
    watch.verify_files()
    worker=get("worker.json");receipt=get("消费.json");allow=get("E放行.json")
    watch.authorization(d,ticket)
    raw=lambda name:watch.sha(d/name)
    binding=receipt.get("launchBinding",{})
    need(ticket["batch"]==worker["batch"]==receipt["batch"]==d.name and
         ticket["pin"]==worker["pin"]==receipt["pin"]==watch.PIN and receipt["count"]==allow["count"]==ticket["count"]
         and allow["preflightSha256"]==raw("大厅预检.json") and binding.get("allowSha256")==raw("E放行.json")
         and Path(worker["exe"]).resolve()==watch.EXE.resolve(), "票据、原消费、放行或固定启动入口不符")
    if "refreshSha256" in binding:
        ref=watch.get(d/"E临启复核.json")
        need(re.fullmatch(r"临启预检-[0-9a-f]{32}\.json",ref["preflightFile"]) and
             raw("E临启复核.json")==binding["refreshSha256"] and ref["allowSha256"]==binding["allowSha256"] and
             raw(ref["preflightFile"])==binding["preflightSha256"]==ref["preflightSha256"],"已消费临启凭证身份不符")
    else:
        need(binding.get("preflightSha256")==allow["preflightSha256"],"缺少已消费的预检指纹")
    supervisor=get("发车意图.json")["supervisor"]
    guard=get("守护就绪.json")
    rows=[json.loads(s) for s in raw_file(d/"监督.jsonl").splitlines()]
    starts=[r for r in rows if r["kind"]=="worker"]
    need(len(starts)==1 and starts[0]["batch"]==d.name and starts[0]["pin"]==watch.PIN and
         starts[0]["pid"]==worker["pid"] and starts[0]["exeSHA256"]==watch.sha(watch.EXE)
         and guard["parent"]==supervisor and len({worker["pid"],supervisor,guard["pid"]})==3,
         "原 worker、监管和守护唯一启动身份不明")
    return ticket,worker,supervisor,guard["pid"]

def local(d,table,worker,supervisor,sentinel):
    folder=d/"断电原桌救场"
    need(not (d/"结批.json").exists() and not folder.exists(),"已结批或已有救场痕迹，禁止补发")
    for pid in (worker["pid"],supervisor,sentinel):exited(pid)
    need(not any(watch.held(p) for p in (watch.data_file(d,".run.lock"),watch.DATA/"series-run.lock",
             watch.DATA/"turnbased-run.lock")),"原批/账号运行锁仍被占用")
    state_raw=raw_file(watch.data_file(d,".json"));status_raw=raw_file(watch.data_file(d,".status.json"))
    state,status=json.loads(state_raw),json.loads(status_raw)
    need(state["id"]==status["batchId"]==d.name and state["site"]=="BGA" and state["hostId"]==watch.HOST
         and state["realtime"] is True and state["finished"] is False and state["currentTable"]==status["table"]==table
         and not state["offerTable"] and state["queuePending"] is False and status["processId"]==worker["pid"]
         and state["planned"]==status["planned"]==watch.get(d/"票据.json")["count"]
         and status["status"] not in ("stopped","completed")
         and watch.table_id(state["opponentId"]) and state["opponentId"]!=watch.HOST
         and table not in [r["table"] for r in state["rounds"]], "原批没有准确绑定尚在玩的原桌双方")
    stopped=raw_file(watch.data_file(d,".stop-queue"))
    need(stopped.strip() and not watch.official_ended(d,table,state),"未持久停排或原桌已有官方终局")
    file=watch.DATA/"seat-actions"/f"BGA-{table}-{watch.HOST}.json"
    journal_raw=raw_file(file);journal=json.loads(journal_raw)
    seat=f"BGA/{table}/座位/{watch.HOST}"
    plan=journal["plan"]
    need(re.fullmatch(r"[0-9a-f]{32}",journal["id"]) and journal["table"]==seat and journal["opponent"]==state["opponentId"]
         and type(journal["generation"]) is int and journal["generation"]>0 and plan["documentId"]
         and plan["phase"] and plan["target"] and plan["actionKind"] and type(plan["move"]) is int and plan["move"]>=0
         and journal.get("phaseSource")=="last-server-state" and plan.get("officialOwn") is True,
         "待确认原始意图不完整或席位不符")
    progress=file.with_suffix(".progress.jsonl")
    if progress.exists():
        matches=[json.loads(line) for line in watch.shared_read(progress).splitlines() if line]
        need(not any(x.get("Table")==seat and x.get("Id")==journal["id"] for x in matches),
             "当前 journal 已有同桌进展回执，禁止按未决意图接席")
    return dict(state=state,statusRaw=status_raw,stateRaw=state_raw,journal=journal,journalRaw=journal_raw,
                stopSHA256=watch.sha(watch.data_file(d,".stop-queue")))

def check_site(table,opponent):
    view=official_results.query(table,active_seats=True)
    opts=view["options"];info=view["tableInfo"]
    need(view["table"]==table and view["game"]=="seasaltpaper" and view["officialStatus"]=="play"
         and view["url"]==f"https://boardgamearena.com/table?table={table}" and view["cancelled"]=="0"
         and view["activeSeatIds"]==sorted([watch.HOST,opponent]) and str(info["min_player"])==str(info["max_player"])=="2"
         and str(info["gamestart"]).isdecimal() and int(info["gamestart"])>0
         and all(str(opts[str(i)])=="1" for i in (101,102,110)) and str(opts["200"]) in ("0","1","2")
         and str(opts["201"]) in ("0","1"),"官方未明确确认本桌仍 play、同两席、双人即时制及规则")
    return view

def run(batch,table,launch=False):
    d=watch.batch_dir(batch)
    need(watch.table_id(table),"原桌号无效")
    need(watch.DATA.resolve()==formal_root() and watch.DATA.is_dir(),"正式资料根不符")
    need(not any(k.upper().startswith(("WEBVIEW2_","SEASALT_FIXTURE","ALLOCATED_FIXTURE","DOTNET_STARTUP_HOOKS","CORECLR_PROFILER",
             "CORECLR_ENABLE_PROFILING","COR_ENABLE_PROFILING")) for k in os.environ),"模拟浏览器或运行环境覆盖存在")
    ticket,worker,supervisor,sentinel=identity(d)
    def inspect():
        orig=local(d,table,worker,supervisor,sentinel)
        boot=inspect_occupants(watch.DATA)
        official=check_site(table,orig["state"]["opponentId"])
        # 网络读取期间若有另一进程取得锁、journal 或绑定变化，不沿用旧读数。
        current=local(d,table,worker,supervisor,sentinel)
        need(orig==current,"核验期间原桌状态、停排或待确认意图变化")
        return current,boot,official
    if not launch:
        orig,boot,official=inspect()
        return dict(eligible=True,started=False,batch=batch,table=table,pin=ticket["pin"],journalId=orig["journal"]["id"],
                    officialStatus=official["officialStatus"],boot=boot)
    # 使用既有批次监管互斥体；产品在自身进程中重新取得 mode / batch 独占锁。
    need((watch.STORE/"监管.lock").is_file(),"既有批次监管锁缺失")
    with watch.lock(watch.STORE/"监管.lock"):
        orig,boot,official=inspect()
        need(inspect_occupants(watch.DATA)==boot and 0 <=
             (datetime.now(timezone.utc)-datetime.fromisoformat(official["retrievedAt"])).total_seconds()<=15,
             "临启动环境有变化或官方原桌实读过期")
        need(local(d,table,worker,supervisor,sentinel)==orig,"临启动原桌证据变化")
        folder=d/"断电原桌救场"
        folder.mkdir()  # 独占且永久；从这里开始失败也不补发。
        args=["--resume",batch,"--root",str(watch.DATA)]
        reason="host-reboot" if datetime.fromisoformat(boot)>datetime.fromisoformat(worker["at"]) else "worker-exit"
        watch.publish(folder/"原进程末次状态.json",orig["statusRaw"])
        watch.publish(folder/"原进程末次批次.json",orig["stateRaw"])
        watch.publish(folder/"启动前官方原桌.json",official)
        watch.publish(folder/"启动前待确认意图.json",orig["journalRaw"])
        watch.publish(folder/"单次救场意图.json",dict(at=watch.now(),table=table,batch=batch,pin=ticket["pin"],
                      oldWorker=worker["pid"],oldSupervisor=supervisor,oldSentinel=sentinel,reason=reason,
                      rescueArgs=args,bootAt=boot,stopSha256=orig["stopSHA256"]))
        cmd=[str(watch.EXE),*args]
        proc=subprocess.Popen(cmd,stdin=subprocess.DEVNULL,stdout=subprocess.DEVNULL,stderr=subprocess.DEVNULL,
                              creationflags=subprocess.CREATE_NO_WINDOW|subprocess.CREATE_NEW_PROCESS_GROUP)
        record=dict(at=watch.now(),table=table,pid=proc.pid,exe=str(watch.EXE),sha256=watch.sha(watch.EXE),args=args)
        watch.publish(folder/"救场进程.json",record)
        return dict(eligible=True,started=True,batch=batch,table=table,rescuePid=proc.pid,reason=reason)

if __name__=="__main__":
    p=argparse.ArgumentParser(description=__doc__)
    p.add_argument("batch");p.add_argument("table");p.add_argument("--start",action="store_true")
    a=p.parse_args()
    try:print(json.dumps(run(a.batch,a.table,a.start),ensure_ascii=False))
    except (OSError,ValueError,KeyError,TypeError,subprocess.SubprocessError) as error:
        # 意图发布后中断有可能实际创建过进程，因此只报告未知，不能补发。
        consumed=bool(re.fullmatch(r"[0-9a-f]{32}",a.batch) and (watch.STORE/a.batch/"断电原桌救场").exists())
        print(json.dumps(dict(startConfirmed=None if consumed else False,rescueReserved=consumed,reason=str(error)),ensure_ascii=False))
        sys.exit(1)
